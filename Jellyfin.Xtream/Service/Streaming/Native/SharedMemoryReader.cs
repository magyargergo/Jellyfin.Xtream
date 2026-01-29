// Copyright (C) 2025  Gergo Magyar
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;

namespace Jellyfin.Xtream.Service.Streaming.Native;

/// <summary>
/// Reads TS data from a shared memory ring buffer written by native C++ code.
/// The ring buffer layout must match the C++ RingBuffer structure.
/// </summary>
/// <remarks>
/// <para>Ring buffer memory layout (must match C++ exactly):</para>
/// <code>
/// Offset 0:   uint64_t write_pos   - Write position (bytes written, wraps around buffer)
/// Offset 8:   uint64_t read_pos    - Read position (bytes read, wraps around buffer)
/// Offset 16:  uint32_t flags       - Bit flags: 0x1=EOS, 0x2=Error, 0x4=Discontinuity
/// Offset 20:  uint32_t reserved    - Padding for alignment
/// Offset 24:  uint8_t[] data       - Ring buffer data
/// </code>
/// </remarks>
public sealed class SharedMemoryReader : IDisposable
{
    // Ring buffer header layout offsets (must match C++)
    private const int WritePosOffset = 0;
    private const int ReadPosOffset = 8;
    private const int FlagsOffset = 16;
    private const int DataOffset = 24;

    // Flag bit masks
    private const uint FlagEndOfStream = 0x1;
    private const uint FlagError = 0x2;
    private const uint FlagDiscontinuity = 0x4;

    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly long _bufferSize;
    private readonly long _dataSize;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SharedMemoryReader"/> class.
    /// </summary>
    /// <param name="sharedMemoryName">Name of the shared memory region.</param>
    /// <param name="bufferSize">Total size of the shared memory region in bytes.</param>
    /// <exception cref="ArgumentException">Thrown if buffer size is too small.</exception>
    /// <exception cref="System.IO.FileNotFoundException">Thrown if shared memory doesn't exist.</exception>
    public SharedMemoryReader(string sharedMemoryName, long bufferSize)
    {
        ArgumentNullException.ThrowIfNull(sharedMemoryName);

        if (bufferSize <= DataOffset)
        {
            throw new ArgumentException("Buffer size must be larger than header size", nameof(bufferSize));
        }

        _bufferSize = bufferSize;
        _dataSize = bufferSize - DataOffset;

        // Open existing shared memory created by native code
        // Use platform-specific approach for cross-platform compatibility
        _mmf = OpenSharedMemory(sharedMemoryName, bufferSize);
        _accessor = _mmf.CreateViewAccessor(0, bufferSize);
    }

    /// <summary>
    /// Opens shared memory in a cross-platform way.
    /// </summary>
    private static MemoryMappedFile OpenSharedMemory(string name, long size)
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows: Use named shared memory
            return MemoryMappedFile.OpenExisting(name);
        }
        else
        {
            // Linux/macOS: Use file-backed memory map in /dev/shm (or tmp fallback)
            var shmPath = GetSharedMemoryPath(name);

            if (!System.IO.File.Exists(shmPath))
            {
                throw new System.IO.FileNotFoundException(
                    $"Shared memory file not found: {shmPath}. Ensure the native streamer has started.",
                    shmPath
                );
            }

            var fileStream = new System.IO.FileStream(
                shmPath,
                System.IO.FileMode.Open,
                System.IO.FileAccess.ReadWrite,
                System.IO.FileShare.ReadWrite
            );

            return MemoryMappedFile.CreateFromFile(
                fileStream,
                mapName: null,
                capacity: size,
                access: MemoryMappedFileAccess.ReadWrite,
                inheritability: System.IO.HandleInheritability.None,
                leaveOpen: false
            );
        }
    }

    /// <summary>
    /// Gets the file path for shared memory on Unix systems.
    /// </summary>
    private static string GetSharedMemoryPath(string name)
    {
        // Prefer /dev/shm (tmpfs) for better performance, fall back to /tmp
        var shmDir = System.IO.Directory.Exists("/dev/shm") ? "/dev/shm" : "/tmp";
        return System.IO.Path.Combine(shmDir, name);
    }

    /// <summary>
    /// Gets a value indicating whether the end of stream has been signaled by the writer.
    /// </summary>
    public bool IsEndOfStream
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var flags = _accessor.ReadUInt32(FlagsOffset);
            return (flags & FlagEndOfStream) != 0;
        }
    }

    /// <summary>
    /// Gets a value indicating whether an error has been signaled by the writer.
    /// </summary>
    public bool HasError
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var flags = _accessor.ReadUInt32(FlagsOffset);
            return (flags & FlagError) != 0;
        }
    }

    /// <summary>
    /// Gets a value indicating whether a discontinuity has been signaled (and clears the flag).
    /// </summary>
    public bool ConsumeDiscontinuityFlag()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var flags = _accessor.ReadUInt32(FlagsOffset);
        if ((flags & FlagDiscontinuity) != 0)
        {
            // Clear the discontinuity flag (atomic CAS would be better but this is simpler)
            _accessor.Write(FlagsOffset, flags & ~FlagDiscontinuity);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Gets the number of bytes available for reading.
    /// </summary>
    public long AvailableBytes
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var writePos = _accessor.ReadUInt64(WritePosOffset);
            var readPos = _accessor.ReadUInt64(ReadPosOffset);

            // Handle wrap-around: write position can be ahead by up to buffer size
            if (writePos >= readPos)
            {
                return (long)(writePos - readPos);
            }

            // This shouldn't happen with proper monotonic counters, but handle it gracefully
            return 0;
        }
    }

    /// <summary>
    /// Reads data from the ring buffer into the provided span.
    /// </summary>
    /// <param name="buffer">Buffer to read into.</param>
    /// <returns>Number of bytes read, 0 if no data available.</returns>
    public int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (buffer.IsEmpty)
        {
            return 0;
        }

        // Read current positions (volatile reads for memory ordering)
        var writePos = _accessor.ReadUInt64(WritePosOffset);
        var readPos = _accessor.ReadUInt64(ReadPosOffset);

        // Calculate available data
        var available = writePos >= readPos ? (long)(writePos - readPos) : 0L;
        if (available == 0)
        {
            return 0;
        }

        // Determine how much to read
        var toRead = (int)Math.Min(available, buffer.Length);

        // Calculate position within the circular buffer
        var bufferReadPos = (long)(readPos % (ulong)_dataSize);

        // Check if read wraps around
        var firstChunkSize = (int)Math.Min(toRead, _dataSize - bufferReadPos);
        var secondChunkSize = toRead - firstChunkSize;

        // Read first chunk
        ReadBytes(DataOffset + bufferReadPos, buffer[..firstChunkSize]);

        // Read second chunk if wrapping
        if (secondChunkSize > 0)
        {
            ReadBytes(DataOffset, buffer.Slice(firstChunkSize, secondChunkSize));
        }

        // Update read position (atomic would be better but C# doesn't support atomic 64-bit on 32-bit)
        _accessor.Write(ReadPosOffset, readPos + (ulong)toRead);

        return toRead;
    }

    /// <summary>
    /// Reads bytes from the memory-mapped file into a span.
    /// </summary>
    private unsafe void ReadBytes(long position, Span<byte> destination)
    {
        byte* ptr = null;
        try
        {
            _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            var source = new ReadOnlySpan<byte>(ptr + position, destination.Length);
            source.CopyTo(destination);
        }
        finally
        {
            if (ptr != null)
            {
                _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }
    }

    /// <summary>
    /// Waits for data to become available with timeout.
    /// </summary>
    /// <param name="timeoutMs">Maximum time to wait in milliseconds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if data is available, false if timeout expired.</returns>
    public bool WaitForData(int timeoutMs, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var deadline = Environment.TickCount64 + timeoutMs;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (AvailableBytes > 0 || IsEndOfStream || HasError)
            {
                return true;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }

            // Brief spin-wait then yield
            Thread.SpinWait(100);
            Thread.Sleep(1);
        }

        return false;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _accessor.Dispose();
        _mmf.Dispose();
    }
}
