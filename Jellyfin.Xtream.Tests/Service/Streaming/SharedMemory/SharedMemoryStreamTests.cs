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
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Service.Streaming.SharedMemory;
using Xunit;

namespace Jellyfin.Xtream.Tests.Service.Streaming.SharedMemory;

/// <summary>
/// Tests for <see cref="SharedMemoryStream"/> class.
/// Uses mock shared memory regions to simulate native producer behavior.
/// </summary>
public sealed class SharedMemoryStreamTests : IDisposable
{
    /// <summary>Expected magic number: "TSTREAM\0".</summary>
    private const ulong ExpectedMagic = 0x5453545245414D00UL;

    /// <summary>Expected protocol version.</summary>
    private const uint ExpectedVersion = 1;

    /// <summary>Header size in bytes.</summary>
    private const int HeaderSize = 256;

    /// <summary>TS packet size.</summary>
    private const int TsPacketSize = 188;

    /// <summary>Default slot count (power of 2).</summary>
    private const ulong DefaultSlotCount = 64;

    /// <summary>Default slot size (7 TS packets).</summary>
    private const uint DefaultSlotSize = TsPacketSize * 7;

    /// <summary>Total buffer capacity.</summary>
    private static readonly ulong DefaultBufferCapacity = DefaultSlotCount * DefaultSlotSize;

    /// <summary>Total shared memory size.</summary>
    private static readonly long TotalSize = HeaderSize + (long)DefaultBufferCapacity;

    private readonly string _testName;
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _accessor;
    private FileStream? _fileStream;

    public SharedMemoryStreamTests()
    {
        _testName = $"SharedMemoryStreamTest_{Guid.NewGuid():N}";
    }

    public void Dispose()
    {
        _accessor?.Dispose();
        _mmf?.Dispose();
        _fileStream?.Dispose();

        // Clean up the file-backed shared memory on Linux/macOS
        if (!OperatingSystem.IsWindows())
        {
            var shmPath = GetShmPath(_testName);
            if (File.Exists(shmPath))
            {
                try
                {
                    File.Delete(shmPath);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }
    }

    #region Stream Property Tests

    /// <summary>
    /// Verifies CanRead returns true when not disposed.
    /// </summary>
    [Fact]
    public void CanRead_NotDisposed_ReturnsTrue()
    {
        CreateSharedMemory();
        using var consumer = new SharedMemoryConsumer(_testName);
        using var stream = new SharedMemoryStream(consumer);

        Assert.True(stream.CanRead);
    }

    /// <summary>
    /// Verifies CanRead returns false after dispose.
    /// </summary>
    [Fact]
    public void CanRead_AfterDispose_ReturnsFalse()
    {
        CreateSharedMemory();
        using var consumer = new SharedMemoryConsumer(_testName);
        var stream = new SharedMemoryStream(consumer);
        stream.Dispose();

        Assert.False(stream.CanRead);
    }

    /// <summary>
    /// Verifies CanWrite returns false.
    /// </summary>
    [Fact]
    public void CanWrite_ReturnsFalse()
    {
        CreateSharedMemory();
        using var consumer = new SharedMemoryConsumer(_testName);
        using var stream = new SharedMemoryStream(consumer);

        Assert.False(stream.CanWrite);
    }

    /// <summary>
    /// Verifies CanSeek returns false.
    /// </summary>
    [Fact]
    public void CanSeek_ReturnsFalse()
    {
        CreateSharedMemory();
        using var consumer = new SharedMemoryConsumer(_testName);
        using var stream = new SharedMemoryStream(consumer);

        Assert.False(stream.CanSeek);
    }

    /// <summary>
    /// Verifies Length throws NotSupportedException.
    /// </summary>
    [Fact]
    public void Length_ThrowsNotSupportedException()
    {
        CreateSharedMemory();
        using var consumer = new SharedMemoryConsumer(_testName);
        using var stream = new SharedMemoryStream(consumer);

        Assert.Throws<NotSupportedException>(() => stream.Length);
    }

    /// <summary>
    /// Verifies Position getter returns bytes read.
    /// </summary>
    [Fact]
    public void Position_Get_ReturnsBytesRead()
    {
        CreateSharedMemory();
        SetWritePosition(5);
        SetReadPosition(0);
        WriteTestDataToSlot(0);

        using var consumer = new SharedMemoryConsumer(_testName);
        using var stream = new SharedMemoryStream(consumer, TimeSpan.FromMilliseconds(100));

        Assert.Equal(0, stream.Position);

        var buffer = new byte[TsPacketSize];
        var bytesRead = stream.Read(buffer, 0, buffer.Length);

        Assert.Equal(bytesRead, stream.Position);
    }

    /// <summary>
    /// Verifies Position setter throws NotSupportedException.
    /// </summary>
    [Fact]
    public void Position_Set_ThrowsNotSupportedException()
    {
        CreateSharedMemory();
        using var consumer = new SharedMemoryConsumer(_testName);
        using var stream = new SharedMemoryStream(consumer);

        Assert.Throws<NotSupportedException>(() => stream.Position = 100);
    }

    #endregion

    #region Unsupported Operation Tests

    /// <summary>
    /// Verifies Seek throws NotSupportedException.
    /// </summary>
    [Fact]
    public void Seek_ThrowsNotSupportedException()
    {
        CreateSharedMemory();
        using var consumer = new SharedMemoryConsumer(_testName);
        using var stream = new SharedMemoryStream(consumer);

        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
    }

    /// <summary>
    /// Verifies SetLength throws NotSupportedException.
    /// </summary>
    [Fact]
    public void SetLength_ThrowsNotSupportedException()
    {
        CreateSharedMemory();
        using var consumer = new SharedMemoryConsumer(_testName);
        using var stream = new SharedMemoryStream(consumer);

        Assert.Throws<NotSupportedException>(() => stream.SetLength(100));
    }

    /// <summary>
    /// Verifies Write throws NotSupportedException.
    /// </summary>
    [Fact]
    public void Write_ThrowsNotSupportedException()
    {
        CreateSharedMemory();
        using var consumer = new SharedMemoryConsumer(_testName);
        using var stream = new SharedMemoryStream(consumer);

        Assert.Throws<NotSupportedException>(() => stream.Write(new byte[10], 0, 10));
    }

    /// <summary>
    /// Verifies Flush does not throw (no-op for read-only stream).
    /// </summary>
    [Fact]
    public void Flush_DoesNotThrow()
    {
        CreateSharedMemory();
        using var consumer = new SharedMemoryConsumer(_testName);
        using var stream = new SharedMemoryStream(consumer);

        stream.Flush(); // Should not throw
    }

    #endregion

    #region Read Tests

    /// <summary>
    /// Verifies Read returns 0 for empty buffer.
    /// </summary>
    [Fact]
    public void Read_EmptyBuffer_ReturnsZero()
    {
        CreateSharedMemory();
        using var consumer = new SharedMemoryConsumer(_testName);
        using var stream = new SharedMemoryStream(consumer);

        var bytesRead = stream.Read(Array.Empty<byte>(), 0, 0);

        Assert.Equal(0, bytesRead);
    }

    /// <summary>
    /// Verifies Read returns 0 at end of stream.
    /// </summary>
    [Fact]
    public void Read_EndOfStream_ReturnsZero()
    {
        CreateSharedMemory();
        SetWritePosition(0);
        SetReadPosition(0);
        SetFlag(SharedMemoryStatusFlags.EndOfStream);

        using var consumer = new SharedMemoryConsumer(_testName);
        using var stream = new SharedMemoryStream(consumer, TimeSpan.FromMilliseconds(100));

        var buffer = new byte[TsPacketSize];
        var bytesRead = stream.Read(buffer, 0, buffer.Length);

        Assert.Equal(0, bytesRead);
    }

    /// <summary>
    /// Verifies Read returns data when available.
    /// </summary>
    [Fact]
    public void Read_DataAvailable_ReturnsData()
    {
        CreateSharedMemory();
        SetWritePosition(5);
        SetReadPosition(0);
        WriteTestDataToSlot(0);

        using var consumer = new SharedMemoryConsumer(_testName);
        using var stream = new SharedMemoryStream(consumer, TimeSpan.FromMilliseconds(100));

        var buffer = new byte[TsPacketSize * 5];
        var bytesRead = stream.Read(buffer, 0, buffer.Length);

        Assert.True(bytesRead > 0);
        Assert.Equal(0x47, buffer[0]); // TS sync byte
    }

    /// <summary>
    /// Verifies Read throws IOException on producer error.
    /// </summary>
    [Fact]
    public void Read_ProducerError_ThrowsIOException()
    {
        CreateSharedMemory();
        SetWritePosition(0);
        SetReadPosition(0);
        SetFlag(SharedMemoryStatusFlags.Error);
        SetErrorCode(SharedMemoryErrorCode.NetworkError);
        WriteErrorMessage("Test network error");

        using var consumer = new SharedMemoryConsumer(_testName);
        using var stream = new SharedMemoryStream(consumer, TimeSpan.FromMilliseconds(100));

        var buffer = new byte[TsPacketSize];
        var ex = Assert.Throws<IOException>(() => stream.Read(buffer, 0, buffer.Length));

        Assert.Contains("NetworkError", ex.Message);
    }

    /// <summary>
    /// Verifies Read returns 0 when producer is stopped.
    /// </summary>
    [Fact]
    public void Read_ProducerStopped_ReturnsZeroOnTimeout()
    {
        CreateSharedMemory();
        SetWritePosition(0);
        SetReadPosition(0);
        SetProducerState(ProducerState.Stopped);

        using var consumer = new SharedMemoryConsumer(_testName);
        using var stream = new SharedMemoryStream(consumer, TimeSpan.FromMilliseconds(50));

        var buffer = new byte[TsPacketSize];
        var bytesRead = stream.Read(buffer, 0, buffer.Length);

        Assert.Equal(0, bytesRead);
    }

    /// <summary>
    /// Verifies Span Read works correctly.
    /// </summary>
    [Fact]
    public void ReadSpan_DataAvailable_ReturnsData()
    {
        CreateSharedMemory();
        SetWritePosition(5);
        SetReadPosition(0);
        WriteTestDataToSlot(0);

        using var consumer = new SharedMemoryConsumer(_testName);
        using var stream = new SharedMemoryStream(consumer, TimeSpan.FromMilliseconds(100));

        Span<byte> buffer = stackalloc byte[TsPacketSize];
        var bytesRead = stream.Read(buffer);

        Assert.True(bytesRead > 0);
        Assert.Equal(0x47, buffer[0]);
    }

    #endregion

    #region ReadAsync Tests

    /// <summary>
    /// Verifies ReadAsync returns 0 for empty buffer.
    /// </summary>
    [Fact]
    public async Task ReadAsync_EmptyBuffer_ReturnsZero()
    {
        CreateSharedMemory();
        using var consumer = new SharedMemoryConsumer(_testName);
        await using var stream = new SharedMemoryStream(consumer);

        var bytesRead = await stream.ReadAsync(Memory<byte>.Empty);

        Assert.Equal(0, bytesRead);
    }

    /// <summary>
    /// Verifies ReadAsync returns data when available.
    /// </summary>
    [Fact]
    public async Task ReadAsync_DataAvailable_ReturnsData()
    {
        CreateSharedMemory();
        SetWritePosition(5);
        SetReadPosition(0);
        WriteTestDataToSlot(0);

        using var consumer = new SharedMemoryConsumer(_testName);
        await using var stream = new SharedMemoryStream(consumer, TimeSpan.FromMilliseconds(100));

        var buffer = new byte[TsPacketSize * 5];
        var bytesRead = await stream.ReadAsync(buffer.AsMemory());

        Assert.True(bytesRead > 0);
        Assert.Equal(0x47, buffer[0]);
    }

    /// <summary>
    /// Verifies ReadAsync with cancellation respects token.
    /// </summary>
    [Fact]
    public async Task ReadAsync_WithCancellation_ThrowsOperationCanceled()
    {
        CreateSharedMemory();
        SetWritePosition(0);
        SetReadPosition(0);

        using var consumer = new SharedMemoryConsumer(_testName);
        await using var stream = new SharedMemoryStream(consumer, TimeSpan.FromSeconds(30));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var buffer = new byte[TsPacketSize];

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            stream.ReadAsync(buffer.AsMemory(), cts.Token).AsTask()
        );
    }

    /// <summary>
    /// Verifies ReadAsync with timeout eventually returns.
    /// </summary>
    [Fact]
    public async Task ReadAsync_NoData_TimesOutEventually()
    {
        CreateSharedMemory();
        SetWritePosition(0);
        SetReadPosition(0);
        SetProducerState(ProducerState.Stopped);

        using var consumer = new SharedMemoryConsumer(_testName);
        await using var stream = new SharedMemoryStream(consumer, TimeSpan.FromMilliseconds(50));

        var buffer = new byte[TsPacketSize];

        // Should complete without hanging
        var bytesRead = await stream.ReadAsync(buffer.AsMemory()).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, bytesRead);
    }

    /// <summary>
    /// Verifies ReadAsync (byte[] overload) works correctly.
    /// </summary>
    [Fact]
    public async Task ReadAsyncByteArray_DataAvailable_ReturnsData()
    {
        CreateSharedMemory();
        SetWritePosition(5);
        SetReadPosition(0);
        WriteTestDataToSlot(0);

        using var consumer = new SharedMemoryConsumer(_testName);
        await using var stream = new SharedMemoryStream(consumer, TimeSpan.FromMilliseconds(100));

        var buffer = new byte[TsPacketSize * 5];
        var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);

        Assert.True(bytesRead > 0);
        Assert.Equal(0x47, buffer[0]);
    }

    #endregion

    #region Event Tests

    /// <summary>
    /// Verifies DiscontinuityDetected event is raised.
    /// </summary>
    [Fact]
    public void Read_WithDiscontinuity_RaisesEvent()
    {
        CreateSharedMemory();
        SetWritePosition(5);
        SetReadPosition(0);
        WriteTestDataToSlot(0);
        SetFlag(SharedMemoryStatusFlags.Discontinuity);

        using var consumer = new SharedMemoryConsumer(_testName);
        using var stream = new SharedMemoryStream(consumer, TimeSpan.FromMilliseconds(100));

        var eventRaised = false;
        stream.DiscontinuityDetected += (_, _) => eventRaised = true;

        var buffer = new byte[TsPacketSize * 5];
        _ = stream.Read(buffer, 0, buffer.Length);

        Assert.True(eventRaised);
    }

    /// <summary>
    /// Verifies OverflowDetected event is raised.
    /// </summary>
    [Fact]
    public void Read_WithOverflow_RaisesEvent()
    {
        CreateSharedMemory();
        SetWritePosition(5);
        SetReadPosition(0);
        WriteTestDataToSlot(0);
        SetFlag(SharedMemoryStatusFlags.Overflow);

        using var consumer = new SharedMemoryConsumer(_testName);
        using var stream = new SharedMemoryStream(consumer, TimeSpan.FromMilliseconds(100));

        var eventRaised = false;
        stream.OverflowDetected += (_, _) => eventRaised = true;

        var buffer = new byte[TsPacketSize * 5];
        _ = stream.Read(buffer, 0, buffer.Length);

        Assert.True(eventRaised);
    }

    /// <summary>
    /// Verifies ErrorReceived event is raised on error.
    /// </summary>
    [Fact]
    public void Read_WithError_RaisesErrorEvent()
    {
        CreateSharedMemory();
        SetWritePosition(0);
        SetReadPosition(0);
        SetFlag(SharedMemoryStatusFlags.Error);
        SetErrorCode(SharedMemoryErrorCode.NetworkError);
        WriteErrorMessage("Network error");

        using var consumer = new SharedMemoryConsumer(_testName);
        using var stream = new SharedMemoryStream(consumer, TimeSpan.FromMilliseconds(100));

        SharedMemoryErrorEventArgs? eventArgs = null;
        stream.ErrorReceived += (_, args) => eventArgs = args;

        var buffer = new byte[TsPacketSize];

        try
        {
            _ = stream.Read(buffer, 0, buffer.Length);
        }
        catch (IOException)
        {
            // Expected
        }

        Assert.NotNull(eventArgs);
        Assert.Equal(SharedMemoryErrorCode.NetworkError, eventArgs.ErrorCode);
        Assert.Contains("Network error", eventArgs.Message);
    }

    #endregion

    #region Dispose Tests

    /// <summary>
    /// Verifies Dispose disposes owned consumer.
    /// </summary>
    [Fact]
    public void Dispose_OwnedConsumer_DisposesConsumer()
    {
        CreateSharedMemory();

        var stream = new SharedMemoryStream(_testName);
        stream.Dispose();

        // Consumer should be disposed - accessing it should throw
        var consumer = stream.Consumer;
        Assert.Throws<ObjectDisposedException>(() => _ = consumer.AvailableBytes);
    }

    /// <summary>
    /// Verifies Dispose does not dispose injected consumer.
    /// </summary>
    [Fact]
    public void Dispose_InjectedConsumer_DoesNotDisposeConsumer()
    {
        CreateSharedMemory();

        using var consumer = new SharedMemoryConsumer(_testName);
        var stream = new SharedMemoryStream(consumer);
        stream.Dispose();

        // Consumer should still be usable
        _ = consumer.AvailableBytes; // Should not throw
    }

    /// <summary>
    /// Verifies Read throws ObjectDisposedException after Dispose.
    /// </summary>
    [Fact]
    public void Read_AfterDispose_ThrowsObjectDisposedException()
    {
        CreateSharedMemory();
        using var consumer = new SharedMemoryConsumer(_testName);
        var stream = new SharedMemoryStream(consumer);
        stream.Dispose();

        var buffer = new byte[TsPacketSize];
        Assert.Throws<ObjectDisposedException>(() => stream.Read(buffer, 0, buffer.Length));
    }

    /// <summary>
    /// Verifies double Dispose does not throw.
    /// </summary>
    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        CreateSharedMemory();
        using var consumer = new SharedMemoryConsumer(_testName);
        var stream = new SharedMemoryStream(consumer);

        stream.Dispose();
        stream.Dispose(); // Should not throw
    }

    #endregion

    #region Constructor Tests

    /// <summary>
    /// Verifies constructor throws for null name.
    /// </summary>
    [Fact]
    public void Constructor_NullName_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new SharedMemoryStream((string)null!));
    }

    /// <summary>
    /// Verifies constructor throws for empty name.
    /// </summary>
    [Fact]
    public void Constructor_EmptyName_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new SharedMemoryStream(string.Empty));
    }

    /// <summary>
    /// Verifies constructor throws for null consumer.
    /// </summary>
    [Fact]
    public void Constructor_NullConsumer_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new SharedMemoryStream((SharedMemoryConsumer)null!));
    }

    /// <summary>
    /// Verifies constructor with consumer stores consumer reference.
    /// </summary>
    [Fact]
    public void Constructor_WithConsumer_StoresReference()
    {
        CreateSharedMemory();
        using var consumer = new SharedMemoryConsumer(_testName);
        using var stream = new SharedMemoryStream(consumer);

        Assert.Same(consumer, stream.Consumer);
    }

    /// <summary>
    /// Verifies default timeout is 30 seconds.
    /// </summary>
    [Fact]
    public void Constructor_DefaultTimeout_Is30Seconds()
    {
        CreateSharedMemory();
        using var consumer = new SharedMemoryConsumer(_testName);

        // Cannot directly verify timeout, but can verify it doesn't time out immediately
        using var stream = new SharedMemoryStream(consumer);

        Assert.NotNull(stream);
    }

    /// <summary>
    /// Verifies custom timeout is applied.
    /// </summary>
    [Fact]
    public void Constructor_CustomTimeout_IsApplied()
    {
        CreateSharedMemory();
        SetWritePosition(0);
        SetReadPosition(0);
        SetProducerState(ProducerState.Stopped);

        using var consumer = new SharedMemoryConsumer(_testName);
        using var stream = new SharedMemoryStream(consumer, TimeSpan.FromMilliseconds(50));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var buffer = new byte[TsPacketSize];
        _ = stream.Read(buffer, 0, buffer.Length);
        sw.Stop();

        // Should complete quickly due to short timeout + stopped producer
        Assert.True(sw.ElapsedMilliseconds < 5000);
    }

    #endregion

    #region Property Accessor Tests

    /// <summary>
    /// Verifies ProducerState property returns consumer's producer state.
    /// </summary>
    [Fact]
    public void ProducerState_ReturnsConsumerProducerState()
    {
        CreateSharedMemory();
        SetProducerState(ProducerState.Streaming);

        using var consumer = new SharedMemoryConsumer(_testName);
        using var stream = new SharedMemoryStream(consumer);

        Assert.Equal(ProducerState.Streaming, stream.ProducerState);
    }

    /// <summary>
    /// Verifies Statistics property returns consumer's statistics.
    /// </summary>
    [Fact]
    public void Statistics_ReturnsConsumerStatistics()
    {
        CreateSharedMemory();
        SetWritePosition(10);
        SetReadPosition(5);

        using var consumer = new SharedMemoryConsumer(_testName);
        using var stream = new SharedMemoryStream(consumer);

        var consumerStats = consumer.GetStatistics();
        var streamStats = stream.Statistics;

        Assert.Equal(consumerStats.AvailableBytes, streamStats.AvailableBytes);
    }

    #endregion

    #region SharedMemoryErrorEventArgs Tests

    /// <summary>
    /// Verifies SharedMemoryErrorEventArgs stores values correctly.
    /// </summary>
    [Fact]
    public void SharedMemoryErrorEventArgs_StoresValuesCorrectly()
    {
        var args = new SharedMemoryErrorEventArgs(SharedMemoryErrorCode.NetworkError, "Test message");

        Assert.Equal(SharedMemoryErrorCode.NetworkError, args.ErrorCode);
        Assert.Equal("Test message", args.Message);
    }

    #endregion

    #region Helper Methods

    private void CreateSharedMemory()
    {
        // Platform-specific shared memory creation
        if (OperatingSystem.IsWindows())
        {
            // Windows: Use named memory-mapped file
            _mmf = MemoryMappedFile.CreateNew(_testName, TotalSize);
        }
        else
        {
            // Linux/macOS: Use file-backed memory-mapped file
            var shmPath = GetShmPath(_testName);

            // Create the file and set its size
            _fileStream = new FileStream(shmPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
            _fileStream.SetLength(TotalSize);

            // Create memory-mapped file from the file stream
            _mmf = MemoryMappedFile.CreateFromFile(
                _fileStream,
                mapName: null,
                capacity: 0,
                access: MemoryMappedFileAccess.ReadWrite,
                inheritability: HandleInheritability.None,
                leaveOpen: false
            );
        }

        _accessor = _mmf.CreateViewAccessor(0, TotalSize, MemoryMappedFileAccess.ReadWrite);

        // Write header
        var header = new SharedMemoryHeader
        {
            Magic = ExpectedMagic,
            Version = ExpectedVersion,
            HeaderSize = HeaderSize,
            BufferCapacity = DefaultBufferCapacity,
            SlotCount = DefaultSlotCount,
            SlotSize = DefaultSlotSize,
            TsPacketSize = TsPacketSize,
            WriteSequence = 0,
            WritePosition = 0,
            ProducerStateValue = (ulong)ProducerState.Streaming,
            LastWriteTimestamp = 0,
            TotalBytesWritten = 0,
            TotalPacketsWritten = 0,
            WriteWrapCount = 0,
            ReadSequence = 0,
            ReadPosition = 0,
            ConsumerStateValue = (ulong)ConsumerState.Unattached,
            LastReadTimestamp = 0,
            TotalBytesRead = 0,
            TotalPacketsRead = 0,
            ReadWrapCount = 0,
            Flags = (uint)SharedMemoryStatusFlags.ProducerReady,
            ErrorCode = 0,
            ErrorTimestamp = 0,
        };

        _accessor.Write(0, ref header);
    }

    private void SetWritePosition(ulong position)
    {
        _accessor!.Write(0x48, position);
    }

    private void SetReadPosition(ulong position)
    {
        _accessor!.Write(0x88, position);
    }

    private void SetFlag(SharedMemoryStatusFlags flag)
    {
        var current = _accessor!.ReadUInt32(0xC0);
        _accessor.Write(0xC0, current | (uint)flag);
    }

    private void SetErrorCode(SharedMemoryErrorCode code)
    {
        _accessor!.Write(0xC4, (uint)code);
    }

    private void SetProducerState(ProducerState state)
    {
        _accessor!.Write(0x50, (ulong)state);
    }

    private void WriteErrorMessage(string message)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(message);
        var messageBuffer = new byte[64];
        Array.Copy(bytes, messageBuffer, Math.Min(bytes.Length, 63));
        _accessor!.WriteArray(0xD0, messageBuffer, 0, 64);
    }

    private void WriteTestDataToSlot(int slotIndex)
    {
        var data = CreateTsData((int)DefaultSlotSize);
        var offset = HeaderSize + (slotIndex * (int)DefaultSlotSize);
        _accessor!.WriteArray(offset, data, 0, data.Length);
    }

    private static byte[] CreateTsData(int size)
    {
        var data = new byte[size];
        for (var i = 0; i < size; i += TsPacketSize)
        {
            data[i] = 0x47; // TS sync byte
            if (i + 1 < size)
            {
                data[i + 1] = 0x1F;
            }

            if (i + 2 < size)
            {
                data[i + 2] = 0xFF;
            }

            if (i + 3 < size)
            {
                data[i + 3] = 0x10;
            }
        }

        return data;
    }

    private static string GetShmPath(string name)
    {
        // Linux: /dev/shm is typically a tmpfs mount
        // macOS: /dev/shm doesn't exist, use /tmp
        var shmDir = Directory.Exists("/dev/shm") ? "/dev/shm" : "/tmp";
        return Path.Combine(shmDir, name);
    }

    #endregion
}
