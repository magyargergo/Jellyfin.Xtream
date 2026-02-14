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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Service.Streaming.SharedMemory;
using Xunit;

namespace Jellyfin.Xtream.Tests.Service.Streaming.SharedMemory;

/// <summary>
/// Tests for <see cref="SharedMemoryConsumer"/> class.
/// Uses mock shared memory regions to simulate native producer behavior.
/// </summary>
public sealed class SharedMemoryConsumerTests : IDisposable
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
    private SharedMemoryTestFixture? _fixture;

    public SharedMemoryConsumerTests()
    {
        _testName = $"SharedMemoryConsumerTest_{Guid.NewGuid():N}";
    }

    public void Dispose()
    {
        _fixture?.Dispose();
    }

    #region Constructor Tests

    /// <summary>
    /// Verifies constructor throws ArgumentNullException for null name.
    /// </summary>
    [Fact]
    public void Constructor_NullName_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new SharedMemoryConsumer(null!));
    }

    /// <summary>
    /// Verifies constructor throws FileNotFoundException for non-existent shared memory.
    /// </summary>
    [Fact]
    public void Constructor_NonExistentSharedMemory_ThrowsFileNotFoundException()
    {
        var nonExistentName = $"NonExistent_{Guid.NewGuid():N}";

        // On Windows, OpenExisting throws FileNotFoundException when shared memory doesn't exist
        var ex = Assert.ThrowsAny<Exception>(() => new SharedMemoryConsumer(nonExistentName));

        // Could be FileNotFoundException or other platform-specific exception
        Assert.True(
            ex is FileNotFoundException or IOException,
            $"Expected FileNotFoundException or IOException but got {ex.GetType().Name}: {ex.Message}"
        );
    }

    /// <summary>
    /// Verifies constructor throws InvalidOperationException for invalid magic number.
    /// </summary>
    [Fact]
    public void Constructor_InvalidMagic_ThrowsInvalidOperationException()
    {
        CreateSharedMemory(magic: 0xDEADBEEFDEADBEEFUL); // Invalid magic

        var ex = Assert.Throws<InvalidOperationException>(() => new SharedMemoryConsumer(_testName));

        Assert.Contains("Invalid shared memory magic", ex.Message);
        Assert.Contains("0xDEADBEEFDEADBEEF", ex.Message);
    }

    /// <summary>
    /// Verifies constructor throws InvalidOperationException for version mismatch.
    /// </summary>
    [Fact]
    public void Constructor_VersionMismatch_ThrowsInvalidOperationException()
    {
        CreateSharedMemory(version: 99); // Invalid version

        var ex = Assert.Throws<InvalidOperationException>(() => new SharedMemoryConsumer(_testName));

        Assert.Contains("Protocol version mismatch", ex.Message);
        Assert.Contains("99", ex.Message);
        Assert.Contains("1", ex.Message);
    }

    /// <summary>
    /// Verifies constructor succeeds with valid shared memory.
    /// </summary>
    [Fact]
    public void Constructor_ValidSharedMemory_Succeeds()
    {
        CreateSharedMemory();

        using var consumer = new SharedMemoryConsumer(_testName);

        Assert.Equal(_testName, consumer.Name);
        Assert.Equal(DefaultSlotCount, consumer.SlotCount);
        Assert.Equal(DefaultSlotSize, consumer.SlotSize);
    }

    /// <summary>
    /// Verifies constructor sets ConsumerReady flag.
    /// </summary>
    [Fact]
    public void Constructor_SetsConsumerReadyFlag()
    {
        CreateSharedMemory();

        using var consumer = new SharedMemoryConsumer(_testName);

        var flags = ReadFlags();
        Assert.True((flags & (uint)SharedMemoryStatusFlags.ConsumerReady) != 0);
    }

    #endregion

    #region Read Tests

    /// <summary>
    /// Verifies Read returns 0 when no data is available.
    /// </summary>
    [Fact]
    public void Read_NoDataAvailable_ReturnsZero()
    {
        CreateSharedMemory();
        SetWritePosition(0);
        SetReadPosition(0);

        using var consumer = new SharedMemoryConsumer(_testName);
        var buffer = new byte[TsPacketSize * 10];

        var bytesRead = consumer.Read(buffer);

        Assert.Equal(0, bytesRead);
    }

    /// <summary>
    /// Verifies Read returns correct data when available.
    /// </summary>
    [Fact]
    public void Read_DataAvailable_ReturnsData()
    {
        CreateSharedMemory();
        SetWritePosition(2); // 2 slots written
        SetReadPosition(0);

        // Write test data to first slot
        var testData = CreateTsData((int)DefaultSlotSize);
        WriteDataToSlot(0, testData);

        using var consumer = new SharedMemoryConsumer(_testName);
        var buffer = new byte[TsPacketSize * 10];

        var bytesRead = consumer.Read(buffer);

        Assert.True(bytesRead > 0);
        Assert.Equal(0, bytesRead % TsPacketSize); // Must be multiple of TS packet size
        Assert.Equal(0x47, buffer[0]); // TS sync byte
    }

    /// <summary>
    /// Verifies Read with empty buffer returns 0.
    /// </summary>
    [Fact]
    public void Read_EmptyBuffer_ReturnsZero()
    {
        CreateSharedMemory();
        SetWritePosition(2);
        SetReadPosition(0);

        using var consumer = new SharedMemoryConsumer(_testName);

        var bytesRead = consumer.Read(Span<byte>.Empty);

        Assert.Equal(0, bytesRead);
    }

    /// <summary>
    /// Verifies Read updates statistics correctly.
    /// </summary>
    [Fact]
    public void Read_UpdatesStatistics()
    {
        CreateSharedMemory();
        SetWritePosition(2);
        SetReadPosition(0);

        var testData = CreateTsData((int)DefaultSlotSize);
        WriteDataToSlot(0, testData);

        using var consumer = new SharedMemoryConsumer(_testName);
        var buffer = new byte[TsPacketSize * 10];

        var beforeStats = consumer.GetStatistics();
        _ = consumer.Read(buffer);
        var afterStats = consumer.GetStatistics();

        Assert.True(afterStats.TotalBytesRead > beforeStats.TotalBytesRead);
    }

    /// <summary>
    /// Verifies Read aligns to TS packet boundaries.
    /// </summary>
    [Fact]
    public void Read_AlignsToTsPacketBoundaries()
    {
        CreateSharedMemory();
        SetWritePosition(2);
        SetReadPosition(0);

        var testData = CreateTsData((int)DefaultSlotSize);
        WriteDataToSlot(0, testData);

        using var consumer = new SharedMemoryConsumer(_testName);

        // Request non-aligned buffer size
        var buffer = new byte[TsPacketSize + 50];
        var bytesRead = consumer.Read(buffer);

        // Should read exactly one TS packet (floor to packet boundary)
        Assert.Equal(TsPacketSize, bytesRead);
    }

    #endregion

    #region Property Tests

    /// <summary>
    /// Verifies AvailableBytes returns correct value.
    /// </summary>
    [Fact]
    public void AvailableBytes_ReturnsCorrectValue()
    {
        CreateSharedMemory();
        SetWritePosition(4);
        SetReadPosition(2);

        using var consumer = new SharedMemoryConsumer(_testName);

        var available = consumer.AvailableBytes;

        // 2 slots available, each slot is DefaultSlotSize bytes
        Assert.Equal((long)(2 * DefaultSlotSize), available);
    }

    /// <summary>
    /// Verifies IsEndOfStream returns correct value.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IsEndOfStream_ReturnsCorrectValue(bool endOfStream)
    {
        CreateSharedMemory();
        if (endOfStream)
        {
            SetFlag(SharedMemoryStatusFlags.EndOfStream);
        }

        using var consumer = new SharedMemoryConsumer(_testName);

        Assert.Equal(endOfStream, consumer.IsEndOfStream);
    }

    /// <summary>
    /// Verifies HasError returns correct value.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HasError_ReturnsCorrectValue(bool hasError)
    {
        CreateSharedMemory();
        if (hasError)
        {
            SetFlag(SharedMemoryStatusFlags.Error);
        }

        using var consumer = new SharedMemoryConsumer(_testName);

        Assert.Equal(hasError, consumer.HasError);
    }

    /// <summary>
    /// Verifies ErrorCode returns correct value.
    /// </summary>
    [Fact]
    public void ErrorCode_ReturnsCorrectValue()
    {
        CreateSharedMemory();
        SetErrorCode(SharedMemoryErrorCode.NetworkError);

        using var consumer = new SharedMemoryConsumer(_testName);

        Assert.Equal(SharedMemoryErrorCode.NetworkError, consumer.ErrorCode);
    }

    /// <summary>
    /// Verifies ProducerState returns correct value.
    /// </summary>
    [Theory]
    [InlineData(ProducerState.Initializing)]
    [InlineData(ProducerState.Streaming)]
    [InlineData(ProducerState.Stopped)]
    [InlineData(ProducerState.Failed)]
    public void ProducerState_ReturnsCorrectValue(ProducerState state)
    {
        CreateSharedMemory();
        SetProducerState(state);

        using var consumer = new SharedMemoryConsumer(_testName);

        Assert.Equal(state, consumer.ProducerState);
    }

    /// <summary>
    /// Verifies IsProducerReady returns correct value.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IsProducerReady_ReturnsCorrectValue(bool ready)
    {
        CreateSharedMemory();
        if (!ready)
        {
            ClearFlag(SharedMemoryStatusFlags.ProducerReady);
        }

        using var consumer = new SharedMemoryConsumer(_testName);

        Assert.Equal(ready, consumer.IsProducerReady);
    }

    #endregion

    #region Discontinuity and Overflow Tests

    /// <summary>
    /// Verifies ConsumeDiscontinuity returns true and clears flag when set.
    /// </summary>
    [Fact]
    public void ConsumeDiscontinuity_WhenSet_ReturnsTrueAndClearsFlag()
    {
        CreateSharedMemory();
        SetFlag(SharedMemoryStatusFlags.Discontinuity);

        using var consumer = new SharedMemoryConsumer(_testName);

        // First call should return true and clear
        Assert.True(consumer.ConsumeDiscontinuity());

        // Second call should return false (flag cleared)
        Assert.False(consumer.ConsumeDiscontinuity());
    }

    /// <summary>
    /// Verifies ConsumeDiscontinuity returns false when not set.
    /// </summary>
    [Fact]
    public void ConsumeDiscontinuity_WhenNotSet_ReturnsFalse()
    {
        CreateSharedMemory();

        using var consumer = new SharedMemoryConsumer(_testName);

        Assert.False(consumer.ConsumeDiscontinuity());
    }

    /// <summary>
    /// Verifies ConsumeOverflow returns true and clears flag when set.
    /// </summary>
    [Fact]
    public void ConsumeOverflow_WhenSet_ReturnsTrueAndClearsFlag()
    {
        CreateSharedMemory();
        SetFlag(SharedMemoryStatusFlags.Overflow);

        using var consumer = new SharedMemoryConsumer(_testName);

        // First call should return true and clear
        Assert.True(consumer.ConsumeOverflow());

        // Second call should return false (flag cleared)
        Assert.False(consumer.ConsumeOverflow());
    }

    /// <summary>
    /// Verifies ConsumeOverflow returns false when not set.
    /// </summary>
    [Fact]
    public void ConsumeOverflow_WhenNotSet_ReturnsFalse()
    {
        CreateSharedMemory();

        using var consumer = new SharedMemoryConsumer(_testName);

        Assert.False(consumer.ConsumeOverflow());
    }

    #endregion

    #region GetStatistics Tests

    /// <summary>
    /// Verifies GetStatistics returns valid data.
    /// </summary>
    [Fact]
    public void GetStatistics_ReturnsValidData()
    {
        CreateSharedMemory();
        SetStatistics(bytesWritten: 10000, packetsWritten: 53, writeWrap: 1);
        SetWritePosition(10);
        SetReadPosition(5);

        using var consumer = new SharedMemoryConsumer(_testName);

        var stats = consumer.GetStatistics();

        Assert.Equal(10000UL, stats.TotalBytesWritten);
        Assert.Equal(53UL, stats.TotalPacketsWritten);
        Assert.Equal(1UL, stats.WriteWrapCount);
        Assert.True(stats.AvailableBytes > 0);
    }

    /// <summary>
    /// Verifies GetStatistics AvailableBytes matches AvailableBytes property.
    /// </summary>
    [Fact]
    public void GetStatistics_AvailableBytes_MatchesProperty()
    {
        CreateSharedMemory();
        SetWritePosition(10);
        SetReadPosition(5);

        using var consumer = new SharedMemoryConsumer(_testName);

        var stats = consumer.GetStatistics();

        Assert.Equal(consumer.AvailableBytes, stats.AvailableBytes);
    }

    #endregion

    #region WaitForData Tests

    /// <summary>
    /// Verifies WaitForData returns true immediately when data is available.
    /// </summary>
    [Fact]
    public void WaitForData_DataAvailable_ReturnsTrueImmediately()
    {
        CreateSharedMemory();
        SetWritePosition(5);
        SetReadPosition(0);

        using var consumer = new SharedMemoryConsumer(_testName);

        var result = consumer.WaitForData(TimeSpan.FromMilliseconds(100));

        Assert.True(result);
    }

    /// <summary>
    /// Verifies WaitForData returns true when end of stream is signaled.
    /// </summary>
    [Fact]
    public void WaitForData_EndOfStream_ReturnsTrue()
    {
        CreateSharedMemory();
        SetWritePosition(0);
        SetReadPosition(0);
        SetFlag(SharedMemoryStatusFlags.EndOfStream);

        using var consumer = new SharedMemoryConsumer(_testName);

        var result = consumer.WaitForData(TimeSpan.FromMilliseconds(100));

        Assert.True(result);
    }

    /// <summary>
    /// Verifies WaitForData returns true when error is signaled.
    /// </summary>
    [Fact]
    public void WaitForData_Error_ReturnsTrue()
    {
        CreateSharedMemory();
        SetWritePosition(0);
        SetReadPosition(0);
        SetFlag(SharedMemoryStatusFlags.Error);

        using var consumer = new SharedMemoryConsumer(_testName);

        var result = consumer.WaitForData(TimeSpan.FromMilliseconds(100));

        Assert.True(result);
    }

    /// <summary>
    /// Verifies WaitForData returns false on timeout when no data.
    /// </summary>
    [Fact]
    public void WaitForData_NoData_ReturnsFalseOnTimeout()
    {
        CreateSharedMemory();
        SetWritePosition(0);
        SetReadPosition(0);

        using var consumer = new SharedMemoryConsumer(_testName);

        var result = consumer.WaitForData(TimeSpan.FromMilliseconds(50));

        Assert.False(result);
    }

    /// <summary>
    /// Verifies WaitForData respects cancellation token.
    /// </summary>
    [Fact]
    public void WaitForData_Cancelled_ReturnsFalse()
    {
        CreateSharedMemory();
        SetWritePosition(0);
        SetReadPosition(0);

        using var consumer = new SharedMemoryConsumer(_testName);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = consumer.WaitForData(TimeSpan.FromSeconds(10), cts.Token);

        Assert.False(result);
    }

    #endregion

    #region Dispose Tests

    /// <summary>
    /// Verifies Dispose clears ConsumerReady flag.
    /// </summary>
    [Fact]
    public void Dispose_ClearsConsumerReadyFlag()
    {
        CreateSharedMemory();

        using var consumer = new SharedMemoryConsumer(_testName);
        var flagsBefore = ReadFlags();
        Assert.True((flagsBefore & (uint)SharedMemoryStatusFlags.ConsumerReady) != 0);

        consumer.Dispose();

        var flagsAfter = ReadFlags();
        Assert.False((flagsAfter & (uint)SharedMemoryStatusFlags.ConsumerReady) != 0);
    }

    /// <summary>
    /// Verifies Dispose sets consumer state to Detached.
    /// </summary>
    [Fact]
    public void Dispose_SetsConsumerStateToDetached()
    {
        CreateSharedMemory();

        using var consumer = new SharedMemoryConsumer(_testName);
        consumer.Dispose();

        var state = ReadConsumerState();
        Assert.Equal(ConsumerState.Detached, state);
    }

    /// <summary>
    /// Verifies double Dispose does not throw.
    /// </summary>
    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        CreateSharedMemory();

        using var consumer = new SharedMemoryConsumer(_testName);
        consumer.Dispose();
        consumer.Dispose(); // Should not throw
    }

    /// <summary>
    /// Verifies operations throw ObjectDisposedException after Dispose.
    /// </summary>
    [Fact]
    public void Read_AfterDispose_ThrowsObjectDisposedException()
    {
        CreateSharedMemory();

        var consumer = new SharedMemoryConsumer(_testName);
        consumer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => consumer.Read(new byte[100]));
    }

    /// <summary>
    /// Verifies property access throws ObjectDisposedException after Dispose.
    /// </summary>
    [Fact]
    public void AvailableBytes_AfterDispose_ThrowsObjectDisposedException()
    {
        CreateSharedMemory();

        var consumer = new SharedMemoryConsumer(_testName);
        consumer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _ = consumer.AvailableBytes);
    }

    /// <summary>
    /// Verifies GetStatistics throws ObjectDisposedException after Dispose.
    /// </summary>
    [Fact]
    public void GetStatistics_AfterDispose_ThrowsObjectDisposedException()
    {
        CreateSharedMemory();

        var consumer = new SharedMemoryConsumer(_testName);
        consumer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => consumer.GetStatistics());
    }

    #endregion

    #region ErrorMessage Mapping Tests

    /// <summary>
    /// Verifies all error codes map to expected human-readable strings.
    /// </summary>
    [Theory]
    [InlineData(SharedMemoryErrorCode.None, "")]
    [InlineData(SharedMemoryErrorCode.InvalidMagic, "Invalid shared memory magic number")]
    [InlineData(SharedMemoryErrorCode.VersionMismatch, "Protocol version mismatch")]
    [InlineData(SharedMemoryErrorCode.MapFailed, "Failed to map shared memory")]
    [InlineData(SharedMemoryErrorCode.SemaphoreCreateFailed, "Failed to create semaphore")]
    [InlineData(SharedMemoryErrorCode.ProducerDisconnected, "Producer disconnected")]
    [InlineData(SharedMemoryErrorCode.ConsumerDisconnected, "Consumer disconnected")]
    [InlineData(SharedMemoryErrorCode.BufferOverflow, "Buffer overflow")]
    [InlineData(SharedMemoryErrorCode.NetworkError, "Network error")]
    [InlineData(SharedMemoryErrorCode.InternalError, "Internal error")]
    public void ErrorMessage_AllCodes_MapToExpectedStrings(SharedMemoryErrorCode code, string expected)
    {
        CreateSharedMemory();
        SetErrorCode(code);

        using var consumer = new SharedMemoryConsumer(_testName);

        Assert.Equal(expected, consumer.ErrorMessage);
    }

    /// <summary>
    /// Verifies unknown error code returns a descriptive message.
    /// </summary>
    [Fact]
    public void ErrorMessage_UnknownCode_ReturnsDescriptiveMessage()
    {
        CreateSharedMemory();
        SetErrorCode((SharedMemoryErrorCode)99);

        using var consumer = new SharedMemoryConsumer(_testName);

        Assert.StartsWith("Unknown error", consumer.ErrorMessage, StringComparison.Ordinal);
    }

    #endregion

    #region Concurrent Flag Consumption Tests

    /// <summary>
    /// Verifies that when multiple threads race to consume the overflow flag,
    /// exactly one succeeds due to atomic test-and-clear semantics.
    /// </summary>
    [Fact]
    public async Task ConsumeOverflow_ConcurrentChecks_ExactlyOneSucceeds()
    {
        CreateSharedMemory();
        SetFlag(SharedMemoryStatusFlags.Overflow);

        var consumer = new SharedMemoryConsumer(_testName);
        try
        {
            var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() => consumer.ConsumeOverflow())).ToArray();

            await Task.WhenAll(tasks);

            var trueCount = tasks.Count(t => t.Result);

            Assert.Equal(1, trueCount);
        }
        finally
        {
            consumer.Dispose();
        }
    }

    #endregion

    #region Disposal Guard Tests

    /// <summary>
    /// Verifies ConsumeOverflow throws ObjectDisposedException after Dispose.
    /// </summary>
    [Fact]
    public void ConsumeOverflow_AfterDisposal_ThrowsObjectDisposedException()
    {
#pragma warning disable IDISP016, IDISP017
        CreateSharedMemory();

        var consumer = new SharedMemoryConsumer(_testName);
        consumer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => consumer.ConsumeOverflow());
#pragma warning restore IDISP016, IDISP017
    }

    /// <summary>
    /// Verifies ConsumeDiscontinuity throws ObjectDisposedException after Dispose.
    /// </summary>
    [Fact]
    public void ConsumeDiscontinuity_AfterDisposal_ThrowsObjectDisposedException()
    {
#pragma warning disable IDISP016, IDISP017
        CreateSharedMemory();

        var consumer = new SharedMemoryConsumer(_testName);
        consumer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => consumer.ConsumeDiscontinuity());
#pragma warning restore IDISP016, IDISP017
    }

    /// <summary>
    /// Verifies ErrorMessage throws ObjectDisposedException after Dispose.
    /// </summary>
    [Fact]
    public void ErrorMessage_AfterDisposal_ThrowsObjectDisposedException()
    {
#pragma warning disable IDISP016, IDISP017
        CreateSharedMemory();

        var consumer = new SharedMemoryConsumer(_testName);
        consumer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _ = consumer.ErrorMessage);
#pragma warning restore IDISP016, IDISP017
    }

    #endregion

    #region Helper Methods

    private void CreateSharedMemory(ulong magic = ExpectedMagic, uint version = ExpectedVersion)
    {
        _fixture = new SharedMemoryTestFixture(_testName, TotalSize);

        // Write header
        var header = new SharedMemoryHeader
        {
            Magic = magic,
            Version = version,
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

        _fixture.Accessor.Write(0, ref header);
    }

    private void SetWritePosition(ulong position)
    {
        _fixture!.Accessor.Write(0x48, position);
    }

    private void SetReadPosition(ulong position)
    {
        _fixture!.Accessor.Write(0x88, position);
    }

    private void SetFlag(SharedMemoryStatusFlags flag)
    {
        var current = _fixture!.Accessor.ReadUInt32(0xC0);
        _fixture.Accessor.Write(0xC0, current | (uint)flag);
    }

    private void ClearFlag(SharedMemoryStatusFlags flag)
    {
        var current = _fixture!.Accessor.ReadUInt32(0xC0);
        _fixture.Accessor.Write(0xC0, current & ~(uint)flag);
    }

    private uint ReadFlags()
    {
        return _fixture!.Accessor.ReadUInt32(0xC0);
    }

    private void SetErrorCode(SharedMemoryErrorCode code)
    {
        _fixture!.Accessor.Write(0xC4, (uint)code);
    }

    private void SetProducerState(ProducerState state)
    {
        _fixture!.Accessor.Write(0x50, (ulong)state);
    }

    private ConsumerState ReadConsumerState()
    {
        return (ConsumerState)_fixture!.Accessor.ReadUInt64(0x90);
    }

    private void SetStatistics(ulong bytesWritten, ulong packetsWritten, ulong writeWrap)
    {
        _fixture!.Accessor.Write(0x60, bytesWritten);
        _fixture.Accessor.Write(0x68, packetsWritten);
        _fixture.Accessor.Write(0x70, writeWrap);
    }

    private void WriteDataToSlot(int slotIndex, byte[] data)
    {
        var offset = HeaderSize + (slotIndex * (int)DefaultSlotSize);
        _fixture!.Accessor.WriteArray(offset, data, 0, Math.Min(data.Length, (int)DefaultSlotSize));
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

    #endregion
}
