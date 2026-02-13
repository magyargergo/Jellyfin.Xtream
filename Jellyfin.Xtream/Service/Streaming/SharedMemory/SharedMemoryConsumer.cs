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
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Jellyfin.Xtream.Service.Streaming.SharedMemory;

/// <summary>
/// High-performance consumer for reading TS streaming data from shared memory.
/// Uses lock-free SPSC ring buffer protocol matching native C++ producer.
/// </summary>
/// <remarks>
/// <para>
/// This consumer is designed for zero-copy streaming data transfer from native
/// C++ code to managed C# code. It uses memory-mapped files for shared memory
/// access and volatile reads/writes for lock-free synchronization.
/// </para>
/// <para>
/// Thread safety: Single consumer thread should call Read/WaitForData.
/// GetStatistics and property accessors are safe from any thread.
/// </para>
/// </remarks>
public sealed class SharedMemoryConsumer : IDisposable
{
    /// <summary>Expected magic number: "TSTREAM\0".</summary>
    private const ulong ExpectedMagic = 0x5453545245414D00UL;

    /// <summary>Expected protocol version.</summary>
    private const uint ExpectedVersion = 1;

    /// <summary>Size of the header region.</summary>
    private const int HeaderSize = 256;

    /// <summary>TS packet size.</summary>
    private const int TsPacketSize = 188;

    // Field offsets in shared memory (must match C++ layout exactly)
    private const int WritePositionOffset = 0x48;
    private const int ProducerStateOffset = 0x50;
    private const int ReadPositionOffset = 0x88;
    private const int ConsumerStateOffset = 0x90;
    private const int LastReadTimestampOffset = 0x98;
    private const int TotalBytesReadOffset = 0xA0;
    private const int TotalPacketsReadOffset = 0xA8;
    private const int FlagsOffset = 0xC0;
    private const int ErrorCodeOffset = 0xC4;
    private const int ErrorMessageOffset = 0xD0;

    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly Semaphore? _semaphore;
    private readonly string _name;
    private readonly ulong _slotCount;
    private readonly uint _slotSize;
    private readonly ulong _slotMask;
    private readonly long _dataOffset;
    private readonly long _totalSize;

    // Cached base pointer - acquired once and held for the lifetime of the consumer.
    // This is safe because the MemoryMappedViewAccessor keeps the mapping alive.
    private readonly unsafe byte* _basePtr;

    private bool _disposed;

    /// <summary>
    /// Creates a consumer attached to existing shared memory created by native producer.
    /// </summary>
    /// <param name="name">Shared memory name (must match producer).</param>
    /// <exception cref="ArgumentNullException">Thrown if name is null.</exception>
    /// <exception cref="FileNotFoundException">Thrown if shared memory doesn't exist.</exception>
    /// <exception cref="InvalidOperationException">Thrown if shared memory has invalid format.</exception>
    public SharedMemoryConsumer(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        _name = name;

        // Open shared memory (platform-specific)
        _mmf = OpenSharedMemory(name);

        try
        {
            // Create view accessor for the header first to read configuration
            using var headerView = _mmf.CreateViewAccessor(0, HeaderSize, MemoryMappedFileAccess.ReadWrite);

            // Read and validate header
            headerView.Read(0, out SharedMemoryHeader header);

            if (header.Magic != ExpectedMagic)
            {
                throw new InvalidOperationException(
                    $"Invalid shared memory magic: 0x{header.Magic:X16}, expected 0x{ExpectedMagic:X16}"
                );
            }

            if (header.Version != ExpectedVersion)
            {
                throw new InvalidOperationException(
                    $"Protocol version mismatch: got {header.Version}, expected {ExpectedVersion}"
                );
            }

            _slotCount = header.SlotCount;
            _slotSize = header.SlotSize;
            _slotMask = _slotCount - 1;
            _dataOffset = HeaderSize;
            _totalSize = HeaderSize + (long)header.BufferCapacity;

            // Create full view accessor
            _accessor = _mmf.CreateViewAccessor(0, _totalSize, MemoryMappedFileAccess.ReadWrite);

            // Acquire and cache the base pointer for the lifetime of this consumer.
            // This eliminates repeated AcquirePointer/ReleasePointer overhead in hot paths.
            unsafe
            {
                byte* ptr = null;
                _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                _basePtr = ptr;
            }

            // Open signaling semaphore (optional - may not exist on all platforms)
            _semaphore = TryOpenSemaphore(name);

            // Set consumer ready flag
            SetFlag(SharedMemoryStatusFlags.ConsumerReady);
            WriteConsumerState(ConsumerState.Attached);
        }
        catch
        {
            _mmf.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Gets the shared memory name.
    /// </summary>
    public string Name => _name;

    /// <summary>
    /// Gets the slot count.
    /// </summary>
    public ulong SlotCount => _slotCount;

    /// <summary>
    /// Gets the slot size.
    /// </summary>
    public uint SlotSize => _slotSize;

    /// <summary>
    /// Gets the number of bytes available for reading.
    /// </summary>
    public long AvailableBytes
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ulong writePos = ReadWritePosition();
            ulong readPos = ReadReadPosition();
            return (long)((writePos - readPos) * _slotSize);
        }
    }

    /// <summary>
    /// Gets whether end of stream has been signaled.
    /// </summary>
    public bool IsEndOfStream
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return HasFlag(SharedMemoryStatusFlags.EndOfStream);
        }
    }

    /// <summary>
    /// Gets whether an error has occurred.
    /// </summary>
    public bool HasError
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return HasFlag(SharedMemoryStatusFlags.Error);
        }
    }

    /// <summary>
    /// Gets the current error code.
    /// </summary>
    public SharedMemoryErrorCode ErrorCode
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return (SharedMemoryErrorCode)ReadUInt32Volatile(ErrorCodeOffset);
        }
    }

    /// <summary>
    /// Gets the current error message.
    /// </summary>
    public string ErrorMessage
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Buffer size matches C++ error_message[48] in SharedMemoryHeader
            Span<byte> buffer = stackalloc byte[48];
            ReadBytes(ErrorMessageOffset, buffer);
            int nullIndex = buffer.IndexOf((byte)0);
            if (nullIndex >= 0)
            {
                buffer = buffer[..nullIndex];
            }

            return Encoding.UTF8.GetString(buffer);
        }
    }

    /// <summary>
    /// Gets the current producer state.
    /// </summary>
    public ProducerState ProducerState
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return (ProducerState)ReadUInt64Volatile(ProducerStateOffset);
        }
    }

    /// <summary>
    /// Gets whether the producer is ready.
    /// </summary>
    public bool IsProducerReady
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return HasFlag(SharedMemoryStatusFlags.ProducerReady);
        }
    }

    /// <summary>
    /// Reads available data into the provided buffer.
    /// </summary>
    /// <param name="buffer">Destination buffer.</param>
    /// <returns>Number of bytes read (always multiple of 188).</returns>
    public unsafe int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (buffer.IsEmpty)
        {
            return 0;
        }

        // Use cached pointer - no acquire/release overhead
        byte* ptr = _basePtr;

        // Update consumer state using cached pointer
        Volatile.Write(ref Unsafe.AsRef<ulong>(ptr + ConsumerStateOffset), (ulong)ConsumerState.Reading);

        // Read positions with proper memory ordering using cached pointer
        ulong writePos = Volatile.Read(ref Unsafe.AsRef<ulong>(ptr + WritePositionOffset));
        ulong readPos = Volatile.Read(ref Unsafe.AsRef<ulong>(ptr + ReadPositionOffset));

        // Calculate available slots
        ulong availableSlots = writePos - readPos;
        if (availableSlots == 0)
        {
            return 0;
        }

        // Calculate how many bytes we can read (aligned to TS packets)
        uint slotSize = _slotSize;
        int bytesToRead = (int)Math.Min((long)availableSlots * slotSize, buffer.Length);
        bytesToRead = (bytesToRead / TsPacketSize) * TsPacketSize;

        if (bytesToRead == 0)
        {
            return 0;
        }

        int totalRead = 0;
        ulong currentSlot = readPos & _slotMask;
        long dataOffset = _dataOffset;

        long totalSize = _totalSize;
        while (totalRead < bytesToRead)
        {
            int chunkSize = Math.Min(bytesToRead - totalRead, (int)slotSize);
            long slotOffset = dataOffset + (long)(currentSlot * slotSize);

            // Bounds check: ensure we don't read past the mapped memory region
            if (slotOffset < dataOffset || slotOffset + chunkSize > totalSize)
            {
                // Corrupted shared memory state - stop reading to prevent access violation
                break;
            }

            // Copy directly from cached pointer
            new ReadOnlySpan<byte>(ptr + slotOffset, chunkSize).CopyTo(buffer.Slice(totalRead, chunkSize));
            totalRead += chunkSize;
            currentSlot = (currentSlot + 1) & _slotMask;
            readPos++;
        }

        // Update read position with release semantics using cached pointer
        Volatile.Write(ref Unsafe.AsRef<ulong>(ptr + ReadPositionOffset), readPos);

        // Update statistics inline
        ref ulong totalBytesRef = ref Unsafe.AsRef<ulong>(ptr + TotalBytesReadOffset);
        Volatile.Write(ref totalBytesRef, Volatile.Read(ref totalBytesRef) + (ulong)totalRead);

        ref ulong totalPacketsRef = ref Unsafe.AsRef<ulong>(ptr + TotalPacketsReadOffset);
        Volatile.Write(ref totalPacketsRef, Volatile.Read(ref totalPacketsRef) + (ulong)(totalRead / TsPacketSize));

        // Update timestamp less frequently
        if ((readPos & 0xF) == 0)
        {
            ref ulong timestampRef = ref Unsafe.AsRef<ulong>(ptr + LastReadTimestampOffset);
            Volatile.Write(ref timestampRef, (ulong)DateTime.UtcNow.Ticks * 100);
        }

        return totalRead;
    }

    /// <summary>
    /// Reads available data directly to a destination stream (zero-copy from shared memory).
    /// This is more efficient than Read() + Write() as it avoids an intermediate buffer.
    /// </summary>
    /// <param name="destination">Destination stream to write to.</param>
    /// <param name="maxBytes">Maximum bytes to read (0 = unlimited).</param>
    /// <returns>Number of bytes written to destination (always multiple of 188).</returns>
    public unsafe int ReadTo(Stream destination, int maxBytes = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(destination);

        // Use cached pointer - no acquire/release overhead
        byte* ptr = _basePtr;

        // Update consumer state using cached pointer
        Volatile.Write(ref Unsafe.AsRef<ulong>(ptr + ConsumerStateOffset), (ulong)ConsumerState.Reading);

        // Read positions with proper memory ordering using cached pointer
        ulong writePos = Volatile.Read(ref Unsafe.AsRef<ulong>(ptr + WritePositionOffset));
        ulong readPos = Volatile.Read(ref Unsafe.AsRef<ulong>(ptr + ReadPositionOffset));

        // Calculate available slots
        ulong availableSlots = writePos - readPos;
        if (availableSlots == 0)
        {
            return 0;
        }

        // Calculate how many bytes we can read (aligned to TS packets)
        // Cache slot size as local for faster access
        uint slotSize = _slotSize;
        long availableBytes = (long)availableSlots * slotSize;
        int bytesToRead =
            maxBytes > 0 ? (int)Math.Min(availableBytes, maxBytes) : (int)Math.Min(availableBytes, int.MaxValue);
        bytesToRead = (bytesToRead / TsPacketSize) * TsPacketSize;

        if (bytesToRead == 0)
        {
            return 0;
        }

        int totalWritten = 0;
        ulong currentSlot = readPos & _slotMask;
        long dataOffset = _dataOffset;

        // Batch write: accumulate contiguous slots when possible
        long totalSize = _totalSize;
        while (totalWritten < bytesToRead)
        {
            // Calculate contiguous region from current slot
            int remainingBytes = bytesToRead - totalWritten;
            int chunkSize = Math.Min(remainingBytes, (int)slotSize);
            long slotOffset = dataOffset + (long)(currentSlot * slotSize);

            // Bounds check: ensure we don't read past the mapped memory region
            if (slotOffset < dataOffset || slotOffset + chunkSize > totalSize)
            {
                // Corrupted shared memory state - stop reading to prevent access violation
                break;
            }

            // Write directly from shared memory to destination stream
            var span = new ReadOnlySpan<byte>(ptr + slotOffset, chunkSize);
            destination.Write(span);

            totalWritten += chunkSize;
            currentSlot = (currentSlot + 1) & _slotMask;
            readPos++;
        }

        // Update read position with release semantics using cached pointer
        Volatile.Write(ref Unsafe.AsRef<ulong>(ptr + ReadPositionOffset), readPos);

        // Update statistics inline to avoid method call overhead
        ref ulong totalBytesRef = ref Unsafe.AsRef<ulong>(ptr + TotalBytesReadOffset);
        Volatile.Write(ref totalBytesRef, Volatile.Read(ref totalBytesRef) + (ulong)totalWritten);

        ref ulong totalPacketsRef = ref Unsafe.AsRef<ulong>(ptr + TotalPacketsReadOffset);
        Volatile.Write(ref totalPacketsRef, Volatile.Read(ref totalPacketsRef) + (ulong)(totalWritten / TsPacketSize));

        // Update timestamp less frequently (every ~10 reads) to reduce overhead
        // Timestamp is informational, not critical for correctness
        if ((readPos & 0xF) == 0)
        {
            ref ulong timestampRef = ref Unsafe.AsRef<ulong>(ptr + LastReadTimestampOffset);
            Volatile.Write(ref timestampRef, (ulong)DateTime.UtcNow.Ticks * 100);
        }

        return totalWritten;
    }

    /// <summary>
    /// Waits for data to become available.
    /// </summary>
    /// <param name="timeout">Maximum time to wait.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if data is available, false on timeout.</returns>
    public unsafe bool WaitForData(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        byte* ptr = _basePtr;

        // Quick check first using cached pointer - avoid property overhead
        ulong writePos = Volatile.Read(ref Unsafe.AsRef<ulong>(ptr + WritePositionOffset));
        ulong readPos = Volatile.Read(ref Unsafe.AsRef<ulong>(ptr + ReadPositionOffset));
        if (writePos != readPos)
        {
            return true;
        }

        // Check flags inline
        uint flags = Volatile.Read(ref Unsafe.AsRef<uint>(ptr + FlagsOffset));
        if ((flags & ((uint)SharedMemoryStatusFlags.EndOfStream | (uint)SharedMemoryStatusFlags.Error)) != 0)
        {
            return true;
        }

        if (_semaphore != null)
        {
            return WaitOnSemaphore(timeout, cancellationToken);
        }

        // Fall back to spin wait if no semaphore
        return SpinWaitForData(timeout, cancellationToken);
    }

    /// <summary>
    /// Checks and clears the discontinuity flag.
    /// Call this after handling a stream discontinuity.
    /// </summary>
    /// <returns>True if discontinuity was signaled, false otherwise.</returns>
    public bool ConsumeDiscontinuity()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (HasFlag(SharedMemoryStatusFlags.Discontinuity))
        {
            ClearFlag(SharedMemoryStatusFlags.Discontinuity);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks and clears the overflow flag.
    /// When overflow is detected, syncs the consumer read position to catch up with the producer.
    /// Call this to detect if data was lost due to slow consumption.
    /// </summary>
    /// <returns>True if overflow occurred, false otherwise.</returns>
    public unsafe bool ConsumeOverflow()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (HasFlag(SharedMemoryStatusFlags.Overflow))
        {
            // Overflow occurred - producer has overwritten old data.
            // We need to sync our read position to catch up with the producer.
            // Leave a small margin (quarter of buffer) to avoid immediate re-overflow.
            byte* ptr = _basePtr;
            ulong writePos = Volatile.Read(ref Unsafe.AsRef<ulong>(ptr + WritePositionOffset));
            ulong currentReadPos = Volatile.Read(ref Unsafe.AsRef<ulong>(ptr + ReadPositionOffset));

            // Calculate how far behind we are
            ulong slotsUsed = writePos - currentReadPos;
            ulong bufferCapacity = _slotCount - 1; // One slot reserved for full/empty distinction

            // If we're behind by more than the buffer, sync to (writePos - quarter buffer)
            // This leaves room for continued streaming without immediate overflow
            if (slotsUsed >= bufferCapacity)
            {
                ulong margin = _slotCount / 4; // Keep 25% buffer margin
                ulong newReadPos = writePos > margin ? writePos - margin : 0;

                // Only advance, never go backwards
                if (newReadPos > currentReadPos)
                {
                    Volatile.Write(ref Unsafe.AsRef<ulong>(ptr + ReadPositionOffset), newReadPos);
                }
            }

            ClearFlag(SharedMemoryStatusFlags.Overflow);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Gets statistics about the shared memory channel.
    /// </summary>
    /// <returns>Statistics snapshot.</returns>
    public SharedMemoryStatistics GetStatistics()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ulong bytesWritten = ReadUInt64Volatile(0x60);
        ulong packetsWritten = ReadUInt64Volatile(0x68);
        ulong bytesRead = ReadUInt64Volatile(0xA0);
        ulong packetsRead = ReadUInt64Volatile(0xA8);
        ulong writeWrap = ReadUInt64Volatile(0x70);
        ulong readWrap = ReadUInt64Volatile(0xB0);

        return new SharedMemoryStatistics(
            TotalBytesWritten: bytesWritten,
            TotalPacketsWritten: packetsWritten,
            TotalBytesRead: bytesRead,
            TotalPacketsRead: packetsRead,
            WriteWrapCount: writeWrap,
            ReadWrapCount: readWrap,
            AvailableBytes: AvailableBytes
        );
    }

    /// <inheritdoc/>
    public unsafe void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Update consumer state before detaching (using cached pointer)
        if (_basePtr != null)
        {
            Volatile.Write(ref Unsafe.AsRef<ulong>(_basePtr + ConsumerStateOffset), (ulong)ConsumerState.Detached);

            // Clear ConsumerReady flag
            ref int flagsRef = ref Unsafe.AsRef<int>(_basePtr + FlagsOffset);
            Interlocked.And(ref flagsRef, ~(int)SharedMemoryStatusFlags.ConsumerReady);

            // Release the cached pointer
            _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        }

        _semaphore?.Dispose();
        _accessor.Dispose();
        _mmf.Dispose();
    }

    // ========================================================================
    // Private Implementation - Memory Access
    // ========================================================================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong ReadWritePosition()
    {
        return ReadUInt64Volatile(WritePositionOffset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong ReadReadPosition()
    {
        return ReadUInt64Volatile(ReadPositionOffset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteReadPosition(ulong value)
    {
        WriteUInt64Volatile(ReadPositionOffset, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteConsumerState(ConsumerState state)
    {
        WriteUInt64Volatile(ConsumerStateOffset, (ulong)state);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong ReadUInt64Volatile(int offset)
    {
        // Use Volatile.Read for acquire semantics
        unsafe
        {
            byte* ptr = null;
            try
            {
                _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                return Volatile.Read(ref Unsafe.AsRef<ulong>(ptr + offset));
            }
            finally
            {
                if (ptr != null)
                {
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteUInt64Volatile(int offset, ulong value)
    {
        // Use Volatile.Write for release semantics
        unsafe
        {
            byte* ptr = null;
            try
            {
                _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                Volatile.Write(ref Unsafe.AsRef<ulong>(ptr + offset), value);
            }
            finally
            {
                if (ptr != null)
                {
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint ReadUInt32Volatile(int offset)
    {
        unsafe
        {
            byte* ptr = null;
            try
            {
                _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                return Volatile.Read(ref Unsafe.AsRef<uint>(ptr + offset));
            }
            finally
            {
                if (ptr != null)
                {
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }
        }
    }

    private void UpdateReadStatistics(ulong bytesRead, ulong packetsRead)
    {
        // Update statistics (non-atomic, best-effort)
        unsafe
        {
            byte* ptr = null;
            try
            {
                _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);

                // Update total bytes read
                ref ulong totalBytesRef = ref Unsafe.AsRef<ulong>(ptr + TotalBytesReadOffset);
                Volatile.Write(ref totalBytesRef, Volatile.Read(ref totalBytesRef) + bytesRead);

                // Update total packets read
                ref ulong totalPacketsRef = ref Unsafe.AsRef<ulong>(ptr + TotalPacketsReadOffset);
                Volatile.Write(ref totalPacketsRef, Volatile.Read(ref totalPacketsRef) + packetsRead);

                // Update last read timestamp
                ref ulong timestampRef = ref Unsafe.AsRef<ulong>(ptr + LastReadTimestampOffset);
                Volatile.Write(ref timestampRef, (ulong)DateTime.UtcNow.Ticks * 100); // Convert to ns
            }
            finally
            {
                if (ptr != null)
                {
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }
        }
    }

    private bool HasFlag(SharedMemoryStatusFlags flag)
    {
        uint flags = ReadUInt32Volatile(FlagsOffset);
        return (flags & (uint)flag) != 0;
    }

    private void SetFlag(SharedMemoryStatusFlags flag)
    {
        unsafe
        {
            byte* ptr = null;
            try
            {
                _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                ref int flagsRef = ref Unsafe.AsRef<int>(ptr + FlagsOffset);
                Interlocked.Or(ref flagsRef, (int)flag);
            }
            finally
            {
                if (ptr != null)
                {
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }
        }
    }

    private void ClearFlag(SharedMemoryStatusFlags flag)
    {
        unsafe
        {
            byte* ptr = null;
            try
            {
                _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                ref int flagsRef = ref Unsafe.AsRef<int>(ptr + FlagsOffset);
                Interlocked.And(ref flagsRef, ~(int)flag);
            }
            finally
            {
                if (ptr != null)
                {
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }
        }
    }

    private unsafe void ReadBytes(long position, Span<byte> destination)
    {
        byte* ptr = null;
        try
        {
            _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            new ReadOnlySpan<byte>(ptr + position, destination.Length).CopyTo(destination);
        }
        finally
        {
            if (ptr != null)
            {
                _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }
    }

    // ========================================================================
    // Private Implementation - Wait Operations
    // ========================================================================

    private unsafe bool WaitOnSemaphore(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        byte* ptr = _basePtr;
        const uint terminalFlags = (uint)SharedMemoryStatusFlags.EndOfStream | (uint)SharedMemoryStatusFlags.Error;

        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                // Try to acquire semaphore with short timeout
                if (_semaphore!.WaitOne(100))
                {
                    return true;
                }

                // Check for data/completion between waits using cached pointer
                ulong writePos = Volatile.Read(ref Unsafe.AsRef<ulong>(ptr + WritePositionOffset));
                ulong readPos = Volatile.Read(ref Unsafe.AsRef<ulong>(ptr + ReadPositionOffset));
                if (writePos != readPos)
                {
                    return true;
                }

                uint flags = Volatile.Read(ref Unsafe.AsRef<uint>(ptr + FlagsOffset));
                if ((flags & terminalFlags) != 0)
                {
                    return true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation
        }

        return false;
    }

    private unsafe bool SpinWaitForData(TimeSpan timeout, CancellationToken cancellationToken)
    {
        // Use Stopwatch for high-precision timing instead of DateTime.UtcNow
        long timeoutTicks = (long)(timeout.TotalMilliseconds * Stopwatch.Frequency / 1000);
        long startTicks = Stopwatch.GetTimestamp();
        var spinner = new SpinWait();

        byte* ptr = _basePtr;

        // Cache flag masks for fast checking
        const uint terminalFlags = (uint)SharedMemoryStatusFlags.EndOfStream | (uint)SharedMemoryStatusFlags.Error;

        while (!cancellationToken.IsCancellationRequested)
        {
            // Check for data available - inline volatile reads
            ulong writePos = Volatile.Read(ref Unsafe.AsRef<ulong>(ptr + WritePositionOffset));
            ulong readPos = Volatile.Read(ref Unsafe.AsRef<ulong>(ptr + ReadPositionOffset));

            if (writePos != readPos)
            {
                return true;
            }

            // Check terminal flags
            uint flags = Volatile.Read(ref Unsafe.AsRef<uint>(ptr + FlagsOffset));
            if ((flags & terminalFlags) != 0)
            {
                return true;
            }

            // Check timeout using Stopwatch (faster than DateTime.UtcNow)
            if (Stopwatch.GetTimestamp() - startTicks >= timeoutTicks)
            {
                return false;
            }

            spinner.SpinOnce();

            if (spinner.NextSpinWillYield)
            {
                Thread.Sleep(1);
            }
        }

        return false;
    }

    // ========================================================================
    // Private Implementation - Platform-Specific
    // ========================================================================

    private static MemoryMappedFile OpenSharedMemory(string name)
    {
        if (OperatingSystem.IsWindows())
        {
            return MemoryMappedFile.OpenExisting(name);
        }

        // Linux/macOS: Use file-backed memory in /dev/shm or /tmp
        var shmPath = GetShmPath(name);
        if (!File.Exists(shmPath))
        {
            throw new FileNotFoundException(
                $"Shared memory not found: {shmPath}. Ensure the native producer has started.",
                shmPath
            );
        }

        var fs = new FileStream(shmPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        try
        {
            return MemoryMappedFile.CreateFromFile(
                fs,
                mapName: null,
                capacity: 0,
                access: MemoryMappedFileAccess.ReadWrite,
                inheritability: HandleInheritability.None,
                leaveOpen: false
            );
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    private static Semaphore? TryOpenSemaphore(string name)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return Semaphore.OpenExisting(name + "_sem");
            }

            // On Linux/macOS, named semaphores work differently
            // For now, fall back to spin-wait on non-Windows
            return null;
        }
        catch
        {
            // Semaphore may not exist - fall back to spin-wait
            return null;
        }
    }

    private static string GetShmPath(string name)
    {
        // Linux: /dev/shm is typically a tmpfs mount
        // macOS: /dev/shm doesn't exist, use /tmp
        var shmDir = Directory.Exists("/dev/shm") ? "/dev/shm" : "/tmp";
        return Path.Combine(shmDir, name);
    }
}
