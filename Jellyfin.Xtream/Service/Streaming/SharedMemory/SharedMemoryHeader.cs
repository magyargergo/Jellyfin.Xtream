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
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Jellyfin.Xtream.Service.Streaming.SharedMemory;

/// <summary>
/// Shared memory header layout matching the C++ SharedMemoryHeader structure.
/// All fields must be at exact offsets to match native layout for lock-free IPC.
/// </summary>
/// <remarks>
/// Memory Layout (256 bytes total):
/// <list type="bullet">
/// <item>0x00-0x3F: Metadata (immutable after creation)</item>
/// <item>0x40-0x7F: Producer cache line (written by C++ producer)</item>
/// <item>0x80-0xBF: Consumer cache line (written by C# consumer)</item>
/// <item>0xC0-0xFF: Flags and error information</item>
/// </list>
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = 256)]
internal struct SharedMemoryHeader
{
    // === Metadata Section (0x00-0x3F) ===

    /// <summary>Magic number: "TSTREAM\0" = 0x5453545245414D00.</summary>
    [FieldOffset(0x00)]
    public ulong Magic;

    /// <summary>Protocol version (currently 1).</summary>
    [FieldOffset(0x08)]
    public uint Version;

    /// <summary>Size of header region (256).</summary>
    [FieldOffset(0x0C)]
    public uint HeaderSize;

    /// <summary>Total data region capacity in bytes.</summary>
    [FieldOffset(0x10)]
    public ulong BufferCapacity;

    /// <summary>Number of slots in the ring buffer.</summary>
    [FieldOffset(0x18)]
    public ulong SlotCount;

    /// <summary>Size of each slot in bytes.</summary>
    [FieldOffset(0x20)]
    public uint SlotSize;

    /// <summary>TS packet size (188).</summary>
    [FieldOffset(0x24)]
    public uint TsPacketSize;

    // === Producer Section (0x40-0x7F) ===

    /// <summary>Monotonic write sequence number.</summary>
    [FieldOffset(0x40)]
    public ulong WriteSequence;

    /// <summary>Current write slot index.</summary>
    [FieldOffset(0x48)]
    public ulong WritePosition;

    /// <summary>Producer state (ProducerState enum).</summary>
    [FieldOffset(0x50)]
    public ulong ProducerStateValue;

    /// <summary>Last write timestamp (nanoseconds since epoch).</summary>
    [FieldOffset(0x58)]
    public ulong LastWriteTimestamp;

    /// <summary>Total bytes written.</summary>
    [FieldOffset(0x60)]
    public ulong TotalBytesWritten;

    /// <summary>Total TS packets written.</summary>
    [FieldOffset(0x68)]
    public ulong TotalPacketsWritten;

    /// <summary>Buffer wrap count.</summary>
    [FieldOffset(0x70)]
    public ulong WriteWrapCount;

    // === Consumer Section (0x80-0xBF) ===

    /// <summary>Read sequence for validation.</summary>
    [FieldOffset(0x80)]
    public ulong ReadSequence;

    /// <summary>Current read slot index.</summary>
    [FieldOffset(0x88)]
    public ulong ReadPosition;

    /// <summary>Consumer state (ConsumerState enum).</summary>
    [FieldOffset(0x90)]
    public ulong ConsumerStateValue;

    /// <summary>Last read timestamp (nanoseconds since epoch).</summary>
    [FieldOffset(0x98)]
    public ulong LastReadTimestamp;

    /// <summary>Total bytes read.</summary>
    [FieldOffset(0xA0)]
    public ulong TotalBytesRead;

    /// <summary>Total TS packets read.</summary>
    [FieldOffset(0xA8)]
    public ulong TotalPacketsRead;

    /// <summary>Consumer wrap count.</summary>
    [FieldOffset(0xB0)]
    public ulong ReadWrapCount;

    // === Flags and Error Section (0xC0-0xFF) ===

    /// <summary>Status flags (SharedMemoryStatusFlags enum).</summary>
    [FieldOffset(0xC0)]
    public uint Flags;

    /// <summary>Error code (SharedMemoryErrorCode enum).</summary>
    [FieldOffset(0xC4)]
    public uint ErrorCode;

    /// <summary>Error timestamp (nanoseconds since epoch).</summary>
    [FieldOffset(0xC8)]
    public ulong ErrorTimestamp;

    // Error message at 0xD0 (64 bytes) - accessed via pointer
}

/// <summary>
/// Status flags for shared memory communication.
/// Multiple flags can be combined using bitwise OR.
/// </summary>
/// <remarks>
/// The underlying type (uint) is required to match the C++ memory layout for interop.
/// </remarks>
[Flags]
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "This is a legitimate Flags enum for shared memory status flags."
)]
[SuppressMessage(
    "Design",
    "CA1028:Enum storage should be Int32",
    Justification = "Must match C++ memory layout for shared memory interop."
)]
public enum SharedMemoryStatusFlags : uint
{
    /// <summary>No flags set.</summary>
    None = 0x00000000,

    /// <summary>Producer has initialized the region.</summary>
    ProducerReady = 0x00000001,

    /// <summary>Consumer has attached to the region.</summary>
    ConsumerReady = 0x00000002,

    /// <summary>No more data will be written.</summary>
    EndOfStream = 0x00000004,

    /// <summary>An error occurred (check ErrorCode).</summary>
    Error = 0x00000008,

    /// <summary>Stream discontinuity (e.g., URL switch).</summary>
    Discontinuity = 0x00000010,

    /// <summary>Buffer overflow occurred (data may be lost).</summary>
    Overflow = 0x00000020,

    /// <summary>Consumer requested unavailable data.</summary>
    Underflow = 0x00000040,

    /// <summary>URL switch is in progress.</summary>
    SwitchPending = 0x00000080,
}

/// <summary>
/// Producer state enumeration matching C++ ProducerState.
/// </summary>
/// <remarks>
/// The underlying type (ulong) is required to match the C++ memory layout for interop.
/// </remarks>
[SuppressMessage(
    "Design",
    "CA1028:Enum storage should be Int32",
    Justification = "Must match C++ memory layout for shared memory interop."
)]
public enum ProducerState : ulong
{
    /// <summary>Setting up shared memory.</summary>
    Initializing = 0,

    /// <summary>Connecting to data source.</summary>
    Connecting = 1,

    /// <summary>Actively writing data.</summary>
    Streaming = 2,

    /// <summary>Temporarily paused.</summary>
    Paused = 3,

    /// <summary>Switching data source (URL).</summary>
    Switching = 4,

    /// <summary>Gracefully stopped.</summary>
    Stopped = 5,

    /// <summary>Unrecoverable error.</summary>
    Failed = 6,
}

/// <summary>
/// Consumer state enumeration matching C++ ConsumerState.
/// </summary>
/// <remarks>
/// The underlying type (ulong) is required to match the C++ memory layout for interop.
/// </remarks>
[SuppressMessage(
    "Design",
    "CA1028:Enum storage should be Int32",
    Justification = "Must match C++ memory layout for shared memory interop."
)]
public enum ConsumerState : ulong
{
    /// <summary>Not yet connected.</summary>
    Unattached = 0,

    /// <summary>Connected but not reading.</summary>
    Attached = 1,

    /// <summary>Actively reading data.</summary>
    Reading = 2,

    /// <summary>Temporarily paused.</summary>
    Paused = 3,

    /// <summary>Disconnected from region.</summary>
    Detached = 4,
}

/// <summary>
/// Error codes for shared memory operations matching C++ SharedMemoryError.
/// </summary>
/// <remarks>
/// The underlying type (uint) is required to match the C++ memory layout for interop.
/// </remarks>
[SuppressMessage(
    "Design",
    "CA1028:Enum storage should be Int32",
    Justification = "Must match C++ memory layout for shared memory interop."
)]
public enum SharedMemoryErrorCode : uint
{
    /// <summary>No error.</summary>
    None = 0,

    /// <summary>Invalid magic number in header.</summary>
    InvalidMagic = 1,

    /// <summary>Protocol version mismatch.</summary>
    VersionMismatch = 2,

    /// <summary>Failed to map shared memory.</summary>
    MapFailed = 3,

    /// <summary>Failed to create/open semaphore.</summary>
    SemaphoreCreateFailed = 4,

    /// <summary>Producer disconnected unexpectedly.</summary>
    ProducerDisconnected = 5,

    /// <summary>Consumer disconnected unexpectedly.</summary>
    ConsumerDisconnected = 6,

    /// <summary>Buffer overflow occurred.</summary>
    BufferOverflow = 7,

    /// <summary>Network error in producer.</summary>
    NetworkError = 8,

    /// <summary>Internal error.</summary>
    InternalError = 9,
}

/// <summary>
/// Statistics from shared memory channel.
/// </summary>
/// <param name="TotalBytesWritten">Total bytes written by producer.</param>
/// <param name="TotalPacketsWritten">Total TS packets written.</param>
/// <param name="TotalBytesRead">Total bytes read by consumer.</param>
/// <param name="TotalPacketsRead">Total TS packets read.</param>
/// <param name="WriteWrapCount">Producer buffer wrap count.</param>
/// <param name="ReadWrapCount">Consumer buffer wrap count.</param>
/// <param name="AvailableBytes">Bytes currently available for reading.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct SharedMemoryStatistics(
    ulong TotalBytesWritten,
    ulong TotalPacketsWritten,
    ulong TotalBytesRead,
    ulong TotalPacketsRead,
    ulong WriteWrapCount,
    ulong ReadWrapCount,
    long AvailableBytes
)
{
    /// <summary>
    /// Gets the current buffer utilization as a percentage.
    /// </summary>
    public double BufferUtilization =>
        TotalBytesWritten > 0
            ? (double)AvailableBytes / ((long)(TotalBytesWritten - TotalBytesRead) + AvailableBytes) * 100.0
            : 0.0;

    /// <summary>
    /// Gets whether the consumer is keeping up with the producer.
    /// </summary>
    public bool IsKeepingUp => WriteWrapCount <= ReadWrapCount + 1;
}
