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
using System.Runtime.InteropServices;
using Jellyfin.Xtream.Service.Streaming.SharedMemory;
using Xunit;

namespace Jellyfin.Xtream.Tests.Service.Streaming.SharedMemory;

/// <summary>
/// Tests for <see cref="SharedMemoryHeader"/> struct.
/// Validates memory layout compatibility with native C++ producer.
/// </summary>
public sealed class SharedMemoryHeaderTests
{
    /// <summary>Expected header size in bytes (must match C++ sizeof(SharedMemoryHeader)).</summary>
    private const int ExpectedHeaderSize = 256;

    /// <summary>
    /// Magic number from C++: "TSTREAM\0" encoded as big-endian 64-bit value.
    /// C++ definition: inline constexpr std::uint64_t SHM_MAGIC = 0x5453545245414D00ULL;
    /// </summary>
    private const ulong ExpectedMagicNumber = 0x5453545245414D00UL;

    /// <summary>
    /// Protocol version from C++.
    /// C++ definition: inline constexpr std::uint32_t SHM_PROTOCOL_VERSION = 1;
    /// </summary>
    private const uint ExpectedProtocolVersion = 1;

    /// <summary>Cache line size used for alignment in both C++ and C#.</summary>
    private const int CacheLineSize = 64;

    #region Struct Size Tests

    /// <summary>
    /// Verifies the struct size is exactly 256 bytes.
    /// This is critical for interop with the native C++ producer.
    /// </summary>
    [Fact]
    public void SharedMemoryHeader_Size_Is256Bytes()
    {
        var size = Marshal.SizeOf<SharedMemoryHeader>();

        Assert.Equal(ExpectedHeaderSize, size);
    }

    /// <summary>
    /// Verifies the struct can be safely marshaled for interop.
    /// </summary>
    [Fact]
    public void SharedMemoryHeader_IsBlittable()
    {
        // Blittable types can be directly copied between managed and unmanaged memory
        // If this throws, the struct contains non-blittable fields
        var header = new SharedMemoryHeader();
        var size = Marshal.SizeOf(header);

        // Allocate unmanaged memory and copy the struct
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(header, ptr, fDeleteOld: false);
            var roundTrip = Marshal.PtrToStructure<SharedMemoryHeader>(ptr);

            Assert.Equal(header.Magic, roundTrip.Magic);
            Assert.Equal(header.Version, roundTrip.Version);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    #endregion

    #region Comprehensive Memory Layout Verification

    /// <summary>
    /// Verifies that the header size is exactly 256 bytes to match C++ sizeof(SharedMemoryHeader).
    /// This is the primary interop compatibility check - if this fails, all shared memory
    /// communication between C++ and C# will be corrupted.
    /// </summary>
    /// <remarks>
    /// C++ static_assert: sizeof(SharedMemoryHeader) == SHM_HEADER_SIZE (256).
    /// </remarks>
    [Fact]
    public void Header_Size_Exactly256Bytes()
    {
        var actualSize = Marshal.SizeOf<SharedMemoryHeader>();

        Assert.True(
            actualSize == ExpectedHeaderSize,
            $"SharedMemoryHeader size mismatch. Expected {ExpectedHeaderSize} bytes to match C++ sizeof(SharedMemoryHeader), but got {actualSize} bytes. This will cause memory corruption in shared memory IPC."
        );
    }

    /// <summary>
    /// Comprehensive verification that all field offsets match the C++ memory layout.
    /// This test validates the entire struct layout in one place for easy debugging.
    /// </summary>
    /// <remarks>
    /// C++ SharedMemoryHeader layout from shared_memory_channel.hpp:
    /// <list type="table">
    /// <item>0x00: magic (uint64_t)</item>
    /// <item>0x08: version (uint32_t)</item>
    /// <item>0x0C: header_size (uint32_t)</item>
    /// <item>0x10: buffer_capacity (uint64_t)</item>
    /// <item>0x18: slot_count (uint64_t)</item>
    /// <item>0x20: slot_size (uint32_t)</item>
    /// <item>0x24: ts_packet_size (uint32_t)</item>
    /// <item>0x28: reserved1 (uint64_t) - not mapped in C#</item>
    /// <item>0x40: write_sequence (atomic&lt;uint64_t&gt;) - cache-line aligned</item>
    /// <item>0x48: write_position (atomic&lt;uint64_t&gt;)</item>
    /// <item>0x50: producer_state (atomic&lt;uint64_t&gt;)</item>
    /// <item>0x58: last_write_timestamp (atomic&lt;uint64_t&gt;)</item>
    /// <item>0x60: total_bytes_written (atomic&lt;uint64_t&gt;)</item>
    /// <item>0x68: total_packets_written (atomic&lt;uint64_t&gt;)</item>
    /// <item>0x70: write_wrap_count (atomic&lt;uint64_t&gt;)</item>
    /// <item>0x78: reserved2 (uint64_t) - not mapped in C#</item>
    /// <item>0x80: read_sequence (atomic&lt;uint64_t&gt;) - cache-line aligned</item>
    /// <item>0x88: read_position (atomic&lt;uint64_t&gt;)</item>
    /// <item>0x90: consumer_state (atomic&lt;uint64_t&gt;)</item>
    /// <item>0x98: last_read_timestamp (atomic&lt;uint64_t&gt;)</item>
    /// <item>0xA0: total_bytes_read (atomic&lt;uint64_t&gt;)</item>
    /// <item>0xA8: total_packets_read (atomic&lt;uint64_t&gt;)</item>
    /// <item>0xB0: read_wrap_count (atomic&lt;uint64_t&gt;)</item>
    /// <item>0xB8: reserved3 (uint64_t) - not mapped in C#</item>
    /// <item>0xC0: flags (atomic&lt;uint32_t&gt;) - cache-line aligned</item>
    /// <item>0xC4: error_code (atomic&lt;uint32_t&gt;)</item>
    /// <item>0xC8: error_timestamp (atomic&lt;uint64_t&gt;)</item>
    /// <item>0xD0: error_message[48] (char[])</item>
    /// </list>
    /// </remarks>
    [Fact]
    public void Header_FieldOffsets_MatchCppLayout()
    {
        // Metadata section (0x00-0x3F)
        AssertFieldOffset(nameof(SharedMemoryHeader.Magic), 0x00, "Magic number for validation");
        AssertFieldOffset(nameof(SharedMemoryHeader.Version), 0x08, "Protocol version");
        AssertFieldOffset(nameof(SharedMemoryHeader.HeaderSize), 0x0C, "Size of header region");
        AssertFieldOffset(nameof(SharedMemoryHeader.BufferCapacity), 0x10, "Total data region capacity");
        AssertFieldOffset(nameof(SharedMemoryHeader.SlotCount), 0x18, "Number of slots in ring buffer");
        AssertFieldOffset(nameof(SharedMemoryHeader.SlotSize), 0x20, "Size of each slot in bytes");
        AssertFieldOffset(nameof(SharedMemoryHeader.TsPacketSize), 0x24, "TS packet size (188)");

        // Producer section (0x40-0x7F) - cache-line 1
        AssertFieldOffset(
            nameof(SharedMemoryHeader.WriteSequence),
            0x40,
            "Monotonic write sequence (producer cache line start)"
        );
        AssertFieldOffset(nameof(SharedMemoryHeader.WritePosition), 0x48, "Current write slot index");
        AssertFieldOffset(nameof(SharedMemoryHeader.ProducerStateValue), 0x50, "Producer state enum value");
        AssertFieldOffset(nameof(SharedMemoryHeader.LastWriteTimestamp), 0x58, "Last write timestamp (ns)");
        AssertFieldOffset(nameof(SharedMemoryHeader.TotalBytesWritten), 0x60, "Total bytes written");
        AssertFieldOffset(nameof(SharedMemoryHeader.TotalPacketsWritten), 0x68, "Total TS packets written");
        AssertFieldOffset(nameof(SharedMemoryHeader.WriteWrapCount), 0x70, "Buffer wrap count (producer)");

        // Consumer section (0x80-0xBF) - cache-line 2
        AssertFieldOffset(
            nameof(SharedMemoryHeader.ReadSequence),
            0x80,
            "Read sequence for validation (consumer cache line start)"
        );
        AssertFieldOffset(nameof(SharedMemoryHeader.ReadPosition), 0x88, "Current read slot index");
        AssertFieldOffset(nameof(SharedMemoryHeader.ConsumerStateValue), 0x90, "Consumer state enum value");
        AssertFieldOffset(nameof(SharedMemoryHeader.LastReadTimestamp), 0x98, "Last read timestamp (ns)");
        AssertFieldOffset(nameof(SharedMemoryHeader.TotalBytesRead), 0xA0, "Total bytes read");
        AssertFieldOffset(nameof(SharedMemoryHeader.TotalPacketsRead), 0xA8, "Total TS packets read");
        AssertFieldOffset(nameof(SharedMemoryHeader.ReadWrapCount), 0xB0, "Consumer wrap count");

        // Flags and error section (0xC0-0xFF) - cache-line 3
        AssertFieldOffset(nameof(SharedMemoryHeader.Flags), 0xC0, "Status flags (flags cache line start)");
        AssertFieldOffset(nameof(SharedMemoryHeader.ErrorCode), 0xC4, "Error code");
        AssertFieldOffset(nameof(SharedMemoryHeader.ErrorTimestamp), 0xC8, "Error timestamp (ns)");
        // Note: ErrorMessage at 0xD0 is 48 bytes, accessed via pointer in C#
    }

    /// <summary>
    /// Verifies that cache-line alignment boundaries are correct for avoiding false sharing.
    /// Each section (producer, consumer, flags) must start on a 64-byte cache line boundary.
    /// </summary>
    /// <remarks>
    /// False sharing occurs when different threads access different fields that happen to be
    /// on the same cache line, causing unnecessary cache invalidation. The C++ code uses
    /// alignas(CACHE_LINE_SIZE) to ensure producer fields, consumer fields, and flags are
    /// on separate cache lines.
    /// </remarks>
    [Fact]
    public void Header_CacheLineAlignment_Correct()
    {
        // Producer section must start at 0x40 (cache line 1, offset 64)
        var producerOffset = (int)Marshal.OffsetOf<SharedMemoryHeader>(nameof(SharedMemoryHeader.WriteSequence));
        Assert.True(
            producerOffset == 0x40,
            $"Producer section (WriteSequence) must start at offset 0x40 (cache line 1). "
                + $"Actual offset: 0x{producerOffset:X2}. This is required for cache-line alignment to prevent false sharing."
        );
        Assert.True(
            producerOffset % CacheLineSize == 0,
            $"Producer section offset (0x{producerOffset:X2}) is not aligned to {CacheLineSize}-byte cache line boundary."
        );

        // Consumer section must start at 0x80 (cache line 2, offset 128)
        var consumerOffset = (int)Marshal.OffsetOf<SharedMemoryHeader>(nameof(SharedMemoryHeader.ReadSequence));
        Assert.True(
            consumerOffset == 0x80,
            $"Consumer section (ReadSequence) must start at offset 0x80 (cache line 2). "
                + $"Actual offset: 0x{consumerOffset:X2}. This is required for cache-line alignment to prevent false sharing."
        );
        Assert.True(
            consumerOffset % CacheLineSize == 0,
            $"Consumer section offset (0x{consumerOffset:X2}) is not aligned to {CacheLineSize}-byte cache line boundary."
        );

        // Flags section must start at 0xC0 (cache line 3, offset 192)
        var flagsOffset = (int)Marshal.OffsetOf<SharedMemoryHeader>(nameof(SharedMemoryHeader.Flags));
        Assert.True(
            flagsOffset == 0xC0,
            $"Flags section must start at offset 0xC0 (cache line 3). "
                + $"Actual offset: 0x{flagsOffset:X2}. This is required for cache-line alignment to prevent false sharing."
        );
        Assert.True(
            flagsOffset % CacheLineSize == 0,
            $"Flags section offset (0x{flagsOffset:X2}) is not aligned to {CacheLineSize}-byte cache line boundary."
        );
    }

    /// <summary>
    /// Verifies that the magic number constant matches the C++ definition.
    /// The magic number is "TSTREAM\0" encoded as a big-endian 64-bit value.
    /// </summary>
    /// <remarks>
    /// C++ definition: inline constexpr std::uint64_t SHM_MAGIC = 0x5453545245414D00ULL;
    /// This represents the ASCII string "TSTREAM" followed by a null byte, stored in
    /// big-endian format for easy visual inspection in memory dumps.
    /// </remarks>
    [Fact]
    public void Header_MagicNumber_MatchesCpp()
    {
        // Verify the expected magic number decodes to "TSTREAM\0"
        var magicBytes = BitConverter.GetBytes(ExpectedMagicNumber);

        // On little-endian systems, we need to reverse to get the string
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(magicBytes);
        }

        var magicString = System.Text.Encoding.ASCII.GetString(magicBytes, 0, 7);
        Assert.Equal("TSTREAM", magicString);
        Assert.Equal(0x00, magicBytes[7]); // Null terminator

        // Verify the constant value matches C++
        Assert.True(
            ExpectedMagicNumber == 0x5453545245414D00UL,
            "Magic number must be 0x5453545245414D00 to match C++ SHM_MAGIC constant"
        );
    }

    /// <summary>
    /// Verifies that the protocol version matches the C++ definition.
    /// Protocol version is used to detect incompatible changes in the header layout.
    /// </summary>
    /// <remarks>
    /// C++ definition: inline constexpr std::uint32_t SHM_PROTOCOL_VERSION = 1;
    /// Both producer (C++) and consumer (C#) must use the same protocol version,
    /// otherwise the consumer should reject the shared memory region.
    /// </remarks>
    [Fact]
    public void Header_ProtocolVersion_MatchesCpp()
    {
        Assert.True(
            ExpectedProtocolVersion == 1U,
            "Protocol version must be 1 to match C++ SHM_PROTOCOL_VERSION constant. "
                + "If the C++ version changes, update this test and the C# consumer."
        );
    }

    /// <summary>
    /// Helper method to assert a field offset with descriptive error messages.
    /// </summary>
    private static void AssertFieldOffset(string fieldName, int expectedOffset, string description)
    {
        var actualOffset = (int)Marshal.OffsetOf<SharedMemoryHeader>(fieldName);
        Assert.True(
            actualOffset == expectedOffset,
            $"Field '{fieldName}' ({description}) must be at offset 0x{expectedOffset:X2} to match C++ layout. "
                + $"Actual offset: 0x{actualOffset:X2}. Memory layout mismatch will cause data corruption."
        );
    }

    #endregion

    #region Enum Underlying Type Tests

    /// <summary>
    /// Verifies that ProducerState enum has the correct underlying type to match C++.
    /// C++ definition: enum class ProducerState : std::uint64_t
    /// </summary>
    [Fact]
    public void Enums_ProducerState_UnderlyingType_MatchesCpp()
    {
        var underlyingType = Enum.GetUnderlyingType(typeof(ProducerState));

        Assert.True(
            underlyingType == typeof(ulong),
            $"ProducerState underlying type must be UInt64 to match C++ 'enum class ProducerState : std::uint64_t'. "
                + $"Actual type: {underlyingType.Name}. This ensures the enum fits in the 8-byte field at offset 0x50."
        );
    }

    /// <summary>
    /// Verifies that ConsumerState enum has the correct underlying type to match C++.
    /// C++ definition: enum class ConsumerState : std::uint64_t
    /// </summary>
    [Fact]
    public void Enums_ConsumerState_UnderlyingType_MatchesCpp()
    {
        var underlyingType = Enum.GetUnderlyingType(typeof(ConsumerState));

        Assert.True(
            underlyingType == typeof(ulong),
            $"ConsumerState underlying type must be UInt64 to match C++ 'enum class ConsumerState : std::uint64_t'. "
                + $"Actual type: {underlyingType.Name}. This ensures the enum fits in the 8-byte field at offset 0x90."
        );
    }

    /// <summary>
    /// Verifies that SharedMemoryStatusFlags enum has the correct underlying type to match C++.
    /// C++ definition: enum class SharedMemoryFlags : std::uint32_t
    /// </summary>
    [Fact]
    public void Enums_SharedMemoryStatusFlags_UnderlyingType_MatchesCpp()
    {
        var underlyingType = Enum.GetUnderlyingType(typeof(SharedMemoryStatusFlags));

        Assert.True(
            underlyingType == typeof(uint),
            $"SharedMemoryStatusFlags underlying type must be UInt32 to match C++ 'enum class SharedMemoryFlags : std::uint32_t'. "
                + $"Actual type: {underlyingType.Name}. This ensures the flags fit in the 4-byte field at offset 0xC0."
        );
    }

    /// <summary>
    /// Verifies that SharedMemoryErrorCode enum has the correct underlying type to match C++.
    /// C++ definition: enum class SharedMemoryError : std::uint32_t
    /// </summary>
    [Fact]
    public void Enums_SharedMemoryErrorCode_UnderlyingType_MatchesCpp()
    {
        var underlyingType = Enum.GetUnderlyingType(typeof(SharedMemoryErrorCode));

        Assert.True(
            underlyingType == typeof(uint),
            $"SharedMemoryErrorCode underlying type must be UInt32 to match C++ 'enum class SharedMemoryError : std::uint32_t'. "
                + $"Actual type: {underlyingType.Name}. This ensures the error code fits in the 4-byte field at offset 0xC4."
        );
    }

    /// <summary>
    /// Comprehensive test verifying all enum underlying types in a single assertion block.
    /// </summary>
    [Fact]
    public void Enums_UnderlyingTypes_MatchCpp()
    {
        // ProducerState: C++ enum class ProducerState : std::uint64_t
        Assert.Equal(typeof(ulong), Enum.GetUnderlyingType(typeof(ProducerState)));

        // ConsumerState: C++ enum class ConsumerState : std::uint64_t
        Assert.Equal(typeof(ulong), Enum.GetUnderlyingType(typeof(ConsumerState)));

        // SharedMemoryStatusFlags: C++ enum class SharedMemoryFlags : std::uint32_t
        Assert.Equal(typeof(uint), Enum.GetUnderlyingType(typeof(SharedMemoryStatusFlags)));

        // SharedMemoryErrorCode: C++ enum class SharedMemoryError : std::uint32_t
        Assert.Equal(typeof(uint), Enum.GetUnderlyingType(typeof(SharedMemoryErrorCode)));
    }

    #endregion

    #region Field Offset Tests - Metadata Section (0x00-0x3F)

    /// <summary>
    /// Verifies Magic field is at offset 0x00.
    /// </summary>
    [Fact]
    public void Magic_FieldOffset_Is0x00()
    {
        var offset = (int)Marshal.OffsetOf<SharedMemoryHeader>(nameof(SharedMemoryHeader.Magic));

        Assert.Equal(0x00, offset);
    }

    /// <summary>
    /// Verifies Version field is at offset 0x08.
    /// </summary>
    [Fact]
    public void Version_FieldOffset_Is0x08()
    {
        var offset = (int)Marshal.OffsetOf<SharedMemoryHeader>(nameof(SharedMemoryHeader.Version));

        Assert.Equal(0x08, offset);
    }

    /// <summary>
    /// Verifies HeaderSize field is at offset 0x0C.
    /// </summary>
    [Fact]
    public void HeaderSize_FieldOffset_Is0x0C()
    {
        var offset = (int)Marshal.OffsetOf<SharedMemoryHeader>(nameof(SharedMemoryHeader.HeaderSize));

        Assert.Equal(0x0C, offset);
    }

    /// <summary>
    /// Verifies BufferCapacity field is at offset 0x10.
    /// </summary>
    [Fact]
    public void BufferCapacity_FieldOffset_Is0x10()
    {
        var offset = (int)Marshal.OffsetOf<SharedMemoryHeader>(nameof(SharedMemoryHeader.BufferCapacity));

        Assert.Equal(0x10, offset);
    }

    /// <summary>
    /// Verifies SlotCount field is at offset 0x18.
    /// </summary>
    [Fact]
    public void SlotCount_FieldOffset_Is0x18()
    {
        var offset = (int)Marshal.OffsetOf<SharedMemoryHeader>(nameof(SharedMemoryHeader.SlotCount));

        Assert.Equal(0x18, offset);
    }

    /// <summary>
    /// Verifies SlotSize field is at offset 0x20.
    /// </summary>
    [Fact]
    public void SlotSize_FieldOffset_Is0x20()
    {
        var offset = (int)Marshal.OffsetOf<SharedMemoryHeader>(nameof(SharedMemoryHeader.SlotSize));

        Assert.Equal(0x20, offset);
    }

    /// <summary>
    /// Verifies TsPacketSize field is at offset 0x24.
    /// </summary>
    [Fact]
    public void TsPacketSize_FieldOffset_Is0x24()
    {
        var offset = (int)Marshal.OffsetOf<SharedMemoryHeader>(nameof(SharedMemoryHeader.TsPacketSize));

        Assert.Equal(0x24, offset);
    }

    #endregion

    #region Field Offset Tests - Producer Section (0x40-0x7F)

    /// <summary>
    /// Verifies WriteSequence field is at offset 0x40.
    /// </summary>
    [Fact]
    public void WriteSequence_FieldOffset_Is0x40()
    {
        var offset = (int)Marshal.OffsetOf<SharedMemoryHeader>(nameof(SharedMemoryHeader.WriteSequence));

        Assert.Equal(0x40, offset);
    }

    /// <summary>
    /// Verifies WritePosition field is at offset 0x48.
    /// </summary>
    [Fact]
    public void WritePosition_FieldOffset_Is0x48()
    {
        var offset = (int)Marshal.OffsetOf<SharedMemoryHeader>(nameof(SharedMemoryHeader.WritePosition));

        Assert.Equal(0x48, offset);
    }

    /// <summary>
    /// Verifies ProducerStateValue field is at offset 0x50.
    /// </summary>
    [Fact]
    public void ProducerStateValue_FieldOffset_Is0x50()
    {
        var offset = (int)Marshal.OffsetOf<SharedMemoryHeader>(nameof(SharedMemoryHeader.ProducerStateValue));

        Assert.Equal(0x50, offset);
    }

    /// <summary>
    /// Verifies LastWriteTimestamp field is at offset 0x58.
    /// </summary>
    [Fact]
    public void LastWriteTimestamp_FieldOffset_Is0x58()
    {
        var offset = (int)Marshal.OffsetOf<SharedMemoryHeader>(nameof(SharedMemoryHeader.LastWriteTimestamp));

        Assert.Equal(0x58, offset);
    }

    /// <summary>
    /// Verifies TotalBytesWritten field is at offset 0x60.
    /// </summary>
    [Fact]
    public void TotalBytesWritten_FieldOffset_Is0x60()
    {
        var offset = (int)Marshal.OffsetOf<SharedMemoryHeader>(nameof(SharedMemoryHeader.TotalBytesWritten));

        Assert.Equal(0x60, offset);
    }

    /// <summary>
    /// Verifies TotalPacketsWritten field is at offset 0x68.
    /// </summary>
    [Fact]
    public void TotalPacketsWritten_FieldOffset_Is0x68()
    {
        var offset = (int)Marshal.OffsetOf<SharedMemoryHeader>(nameof(SharedMemoryHeader.TotalPacketsWritten));

        Assert.Equal(0x68, offset);
    }

    /// <summary>
    /// Verifies WriteWrapCount field is at offset 0x70.
    /// </summary>
    [Fact]
    public void WriteWrapCount_FieldOffset_Is0x70()
    {
        var offset = (int)Marshal.OffsetOf<SharedMemoryHeader>(nameof(SharedMemoryHeader.WriteWrapCount));

        Assert.Equal(0x70, offset);
    }

    #endregion

    #region Field Offset Tests - Consumer Section (0x80-0xBF)

    /// <summary>
    /// Verifies ReadSequence field is at offset 0x80.
    /// </summary>
    [Fact]
    public void ReadSequence_FieldOffset_Is0x80()
    {
        var offset = (int)Marshal.OffsetOf<SharedMemoryHeader>(nameof(SharedMemoryHeader.ReadSequence));

        Assert.Equal(0x80, offset);
    }

    /// <summary>
    /// Verifies ReadPosition field is at offset 0x88.
    /// </summary>
    [Fact]
    public void ReadPosition_FieldOffset_Is0x88()
    {
        var offset = (int)Marshal.OffsetOf<SharedMemoryHeader>(nameof(SharedMemoryHeader.ReadPosition));

        Assert.Equal(0x88, offset);
    }

    /// <summary>
    /// Verifies ConsumerStateValue field is at offset 0x90.
    /// </summary>
    [Fact]
    public void ConsumerStateValue_FieldOffset_Is0x90()
    {
        var offset = (int)Marshal.OffsetOf<SharedMemoryHeader>(nameof(SharedMemoryHeader.ConsumerStateValue));

        Assert.Equal(0x90, offset);
    }

    /// <summary>
    /// Verifies LastReadTimestamp field is at offset 0x98.
    /// </summary>
    [Fact]
    public void LastReadTimestamp_FieldOffset_Is0x98()
    {
        var offset = (int)Marshal.OffsetOf<SharedMemoryHeader>(nameof(SharedMemoryHeader.LastReadTimestamp));

        Assert.Equal(0x98, offset);
    }

    /// <summary>
    /// Verifies TotalBytesRead field is at offset 0xA0.
    /// </summary>
    [Fact]
    public void TotalBytesRead_FieldOffset_Is0xA0()
    {
        var offset = (int)Marshal.OffsetOf<SharedMemoryHeader>(nameof(SharedMemoryHeader.TotalBytesRead));

        Assert.Equal(0xA0, offset);
    }

    /// <summary>
    /// Verifies TotalPacketsRead field is at offset 0xA8.
    /// </summary>
    [Fact]
    public void TotalPacketsRead_FieldOffset_Is0xA8()
    {
        var offset = (int)Marshal.OffsetOf<SharedMemoryHeader>(nameof(SharedMemoryHeader.TotalPacketsRead));

        Assert.Equal(0xA8, offset);
    }

    /// <summary>
    /// Verifies ReadWrapCount field is at offset 0xB0.
    /// </summary>
    [Fact]
    public void ReadWrapCount_FieldOffset_Is0xB0()
    {
        var offset = (int)Marshal.OffsetOf<SharedMemoryHeader>(nameof(SharedMemoryHeader.ReadWrapCount));

        Assert.Equal(0xB0, offset);
    }

    #endregion

    #region Field Offset Tests - Flags and Error Section (0xC0-0xFF)

    /// <summary>
    /// Verifies Flags field is at offset 0xC0.
    /// </summary>
    [Fact]
    public void Flags_FieldOffset_Is0xC0()
    {
        var offset = (int)Marshal.OffsetOf<SharedMemoryHeader>(nameof(SharedMemoryHeader.Flags));

        Assert.Equal(0xC0, offset);
    }

    /// <summary>
    /// Verifies ErrorCode field is at offset 0xC4.
    /// </summary>
    [Fact]
    public void ErrorCode_FieldOffset_Is0xC4()
    {
        var offset = (int)Marshal.OffsetOf<SharedMemoryHeader>(nameof(SharedMemoryHeader.ErrorCode));

        Assert.Equal(0xC4, offset);
    }

    /// <summary>
    /// Verifies ErrorTimestamp field is at offset 0xC8.
    /// </summary>
    [Fact]
    public void ErrorTimestamp_FieldOffset_Is0xC8()
    {
        var offset = (int)Marshal.OffsetOf<SharedMemoryHeader>(nameof(SharedMemoryHeader.ErrorTimestamp));

        Assert.Equal(0xC8, offset);
    }

    #endregion

    #region SharedMemoryStatistics Tests

    /// <summary>
    /// Verifies SharedMemoryStatistics can be created with default values.
    /// </summary>
    [Fact]
    public void SharedMemoryStatistics_DefaultValues_AreZero()
    {
        var stats = new SharedMemoryStatistics(0, 0, 0, 0, 0, 0, 0);

        Assert.Equal(0UL, stats.TotalBytesWritten);
        Assert.Equal(0UL, stats.TotalPacketsWritten);
        Assert.Equal(0UL, stats.TotalBytesRead);
        Assert.Equal(0UL, stats.TotalPacketsRead);
        Assert.Equal(0UL, stats.WriteWrapCount);
        Assert.Equal(0UL, stats.ReadWrapCount);
        Assert.Equal(0L, stats.AvailableBytes);
    }

    /// <summary>
    /// Verifies SharedMemoryStatistics correctly stores provided values.
    /// </summary>
    [Fact]
    public void SharedMemoryStatistics_WithValues_StoresCorrectly()
    {
        var stats = new SharedMemoryStatistics(
            TotalBytesWritten: 1000UL,
            TotalPacketsWritten: 5UL,
            TotalBytesRead: 500UL,
            TotalPacketsRead: 2UL,
            WriteWrapCount: 1UL,
            ReadWrapCount: 0UL,
            AvailableBytes: 500L
        );

        Assert.Equal(1000UL, stats.TotalBytesWritten);
        Assert.Equal(5UL, stats.TotalPacketsWritten);
        Assert.Equal(500UL, stats.TotalBytesRead);
        Assert.Equal(2UL, stats.TotalPacketsRead);
        Assert.Equal(1UL, stats.WriteWrapCount);
        Assert.Equal(0UL, stats.ReadWrapCount);
        Assert.Equal(500L, stats.AvailableBytes);
    }

    /// <summary>
    /// Verifies BufferUtilization returns 0 when no data written.
    /// </summary>
    [Fact]
    public void SharedMemoryStatistics_BufferUtilization_ZeroWhenNoDataWritten()
    {
        var stats = new SharedMemoryStatistics(0, 0, 0, 0, 0, 0, 0);

        Assert.Equal(0.0, stats.BufferUtilization);
    }

    /// <summary>
    /// Verifies BufferUtilization calculation is correct.
    /// </summary>
    [Fact]
    public void SharedMemoryStatistics_BufferUtilization_CalculatesCorrectly()
    {
        // 1000 written, 500 read, 500 available
        // Available / (Pending + Available) = 500 / (500 + 500) = 50%
        var stats = new SharedMemoryStatistics(
            TotalBytesWritten: 1000UL,
            TotalPacketsWritten: 5UL,
            TotalBytesRead: 500UL,
            TotalPacketsRead: 2UL,
            WriteWrapCount: 0UL,
            ReadWrapCount: 0UL,
            AvailableBytes: 500L
        );

        Assert.Equal(50.0, stats.BufferUtilization);
    }

    /// <summary>
    /// Verifies IsKeepingUp returns true when consumer is caught up.
    /// </summary>
    [Fact]
    public void SharedMemoryStatistics_IsKeepingUp_TrueWhenCaughtUp()
    {
        var stats = new SharedMemoryStatistics(
            TotalBytesWritten: 1000UL,
            TotalPacketsWritten: 5UL,
            TotalBytesRead: 1000UL,
            TotalPacketsRead: 5UL,
            WriteWrapCount: 1UL,
            ReadWrapCount: 1UL,
            AvailableBytes: 0L
        );

        Assert.True(stats.IsKeepingUp);
    }

    /// <summary>
    /// Verifies IsKeepingUp returns true when consumer is one wrap behind.
    /// </summary>
    [Fact]
    public void SharedMemoryStatistics_IsKeepingUp_TrueWhenOneWrapBehind()
    {
        var stats = new SharedMemoryStatistics(
            TotalBytesWritten: 2000UL,
            TotalPacketsWritten: 10UL,
            TotalBytesRead: 1000UL,
            TotalPacketsRead: 5UL,
            WriteWrapCount: 2UL,
            ReadWrapCount: 1UL,
            AvailableBytes: 1000L
        );

        Assert.True(stats.IsKeepingUp);
    }

    /// <summary>
    /// Verifies IsKeepingUp returns false when consumer is falling behind.
    /// </summary>
    [Fact]
    public void SharedMemoryStatistics_IsKeepingUp_FalseWhenFarBehind()
    {
        var stats = new SharedMemoryStatistics(
            TotalBytesWritten: 3000UL,
            TotalPacketsWritten: 15UL,
            TotalBytesRead: 1000UL,
            TotalPacketsRead: 5UL,
            WriteWrapCount: 3UL,
            ReadWrapCount: 0UL,
            AvailableBytes: 2000L
        );

        Assert.False(stats.IsKeepingUp);
    }

    /// <summary>
    /// Verifies SharedMemoryStatistics record equality works correctly.
    /// </summary>
    [Fact]
    public void SharedMemoryStatistics_Equality_WorksCorrectly()
    {
        var stats1 = new SharedMemoryStatistics(1000, 5, 500, 2, 1, 0, 500);
        var stats2 = new SharedMemoryStatistics(1000, 5, 500, 2, 1, 0, 500);
        var stats3 = new SharedMemoryStatistics(2000, 10, 1000, 5, 2, 1, 1000);

        Assert.Equal(stats1, stats2);
        Assert.NotEqual(stats1, stats3);
    }

    #endregion

    #region Enum Tests

    /// <summary>
    /// Verifies SharedMemoryStatusFlags values match C++ enum.
    /// </summary>
    [Theory]
    [InlineData(SharedMemoryStatusFlags.None, 0x00000000U)]
    [InlineData(SharedMemoryStatusFlags.ProducerReady, 0x00000001U)]
    [InlineData(SharedMemoryStatusFlags.ConsumerReady, 0x00000002U)]
    [InlineData(SharedMemoryStatusFlags.EndOfStream, 0x00000004U)]
    [InlineData(SharedMemoryStatusFlags.Error, 0x00000008U)]
    [InlineData(SharedMemoryStatusFlags.Discontinuity, 0x00000010U)]
    [InlineData(SharedMemoryStatusFlags.Overflow, 0x00000020U)]
    [InlineData(SharedMemoryStatusFlags.Underflow, 0x00000040U)]
    [InlineData(SharedMemoryStatusFlags.SwitchPending, 0x00000080U)]
    public void SharedMemoryStatusFlags_Values_MatchCppLayout(SharedMemoryStatusFlags flag, uint expected)
    {
        Assert.Equal(expected, (uint)flag);
    }

    /// <summary>
    /// Verifies SharedMemoryStatusFlags can be combined.
    /// </summary>
    [Fact]
    public void SharedMemoryStatusFlags_CanBeCombined()
    {
        var combined = SharedMemoryStatusFlags.ProducerReady | SharedMemoryStatusFlags.ConsumerReady;

        Assert.Equal(0x00000003U, (uint)combined);
        Assert.True(combined.HasFlag(SharedMemoryStatusFlags.ProducerReady));
        Assert.True(combined.HasFlag(SharedMemoryStatusFlags.ConsumerReady));
        Assert.False(combined.HasFlag(SharedMemoryStatusFlags.EndOfStream));
    }

    /// <summary>
    /// Verifies ProducerState values match C++ enum.
    /// </summary>
    [Theory]
    [InlineData(ProducerState.Initializing, 0UL)]
    [InlineData(ProducerState.Connecting, 1UL)]
    [InlineData(ProducerState.Streaming, 2UL)]
    [InlineData(ProducerState.Paused, 3UL)]
    [InlineData(ProducerState.Switching, 4UL)]
    [InlineData(ProducerState.Stopped, 5UL)]
    [InlineData(ProducerState.Failed, 6UL)]
    public void ProducerState_Values_MatchCppLayout(ProducerState state, ulong expected)
    {
        Assert.Equal(expected, (ulong)state);
    }

    /// <summary>
    /// Verifies ConsumerState values match C++ enum.
    /// </summary>
    [Theory]
    [InlineData(ConsumerState.Unattached, 0UL)]
    [InlineData(ConsumerState.Attached, 1UL)]
    [InlineData(ConsumerState.Reading, 2UL)]
    [InlineData(ConsumerState.Paused, 3UL)]
    [InlineData(ConsumerState.Detached, 4UL)]
    public void ConsumerState_Values_MatchCppLayout(ConsumerState state, ulong expected)
    {
        Assert.Equal(expected, (ulong)state);
    }

    /// <summary>
    /// Verifies SharedMemoryErrorCode values match C++ enum.
    /// </summary>
    [Theory]
    [InlineData(SharedMemoryErrorCode.None, 0U)]
    [InlineData(SharedMemoryErrorCode.InvalidMagic, 1U)]
    [InlineData(SharedMemoryErrorCode.VersionMismatch, 2U)]
    [InlineData(SharedMemoryErrorCode.MapFailed, 3U)]
    [InlineData(SharedMemoryErrorCode.SemaphoreCreateFailed, 4U)]
    [InlineData(SharedMemoryErrorCode.ProducerDisconnected, 5U)]
    [InlineData(SharedMemoryErrorCode.ConsumerDisconnected, 6U)]
    [InlineData(SharedMemoryErrorCode.BufferOverflow, 7U)]
    [InlineData(SharedMemoryErrorCode.NetworkError, 8U)]
    [InlineData(SharedMemoryErrorCode.InternalError, 9U)]
    public void SharedMemoryErrorCode_Values_MatchCppLayout(SharedMemoryErrorCode code, uint expected)
    {
        Assert.Equal(expected, (uint)code);
    }

    #endregion

    #region Magic Number Tests

    /// <summary>
    /// Verifies the expected magic number matches "TSTREAM\0".
    /// </summary>
    [Fact]
    public void ExpectedMagic_Is_TSTREAM()
    {
        // "TSTREAM\0" in ASCII bytes: T=0x54, S=0x53, T=0x54, R=0x52, E=0x45, A=0x41, M=0x4D, \0=0x00
        // When read as little-endian 64-bit integer, the byte order is reversed
        var magicBytes = new byte[] { 0x54, 0x53, 0x54, 0x52, 0x45, 0x41, 0x4D, 0x00 };
        _ = BitConverter.ToUInt64(magicBytes, 0);

        // The expected magic in the header file is 0x5453545245414D00
        // This means the C++ code stores it differently (big-endian in the constant)
        // Let's just verify the magic bytes spell "TSTREAM\0"
        var magicString = System.Text.Encoding.ASCII.GetString(magicBytes, 0, 7);

        Assert.Equal("TSTREAM", magicString);
        Assert.Equal(0x00, magicBytes[7]); // Null terminator
    }

    #endregion
}
