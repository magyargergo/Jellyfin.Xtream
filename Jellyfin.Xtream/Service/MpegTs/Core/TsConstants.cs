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

namespace Jellyfin.Xtream.Service.MpegTs.Core;

/// <summary>
/// Constants for MPEG-TS parsing per ISO/IEC 13818-1.
/// </summary>
public static class TsConstants
{
    /// <summary>
    /// The standard size of an MPEG-TS packet (ISO/IEC 13818-1 Section 2.4.3).
    /// </summary>
    public const int PacketSize = 188;

    /// <summary>
    /// The DVB-ASI packet size including 16-byte Reed-Solomon FEC (ISO/IEC 13818-1 Annex B).
    /// Used in professional broadcast equipment and satellite/cable headends.
    /// </summary>
    public const int DvbAsiPacketSize = 204;

    /// <summary>
    /// The sync byte value (0x47) found at the start of every MPEG-TS packet.
    /// </summary>
    public const byte SyncByte = 0x47;

    /// <summary>
    /// The PID for the Program Association Table (PAT).
    /// </summary>
    public const int PatPid = 0x0000;

    /// <summary>
    /// The PID for the Conditional Access Table (CAT).
    /// </summary>
    public const int CatPid = 0x0001;

    /// <summary>
    /// The PID for the Transport Stream Description Table (TSDT).
    /// </summary>
    public const int TsdtPid = 0x0002;

    /// <summary>
    /// The null packet PID used for CBR padding.
    /// </summary>
    public const int NullPid = 0x1FFF;

    /// <summary>
    /// The table ID for CAT (Conditional Access Table).
    /// </summary>
    public const byte CatTableId = 0x01;

    /// <summary>
    /// Reed-Solomon FEC bytes appended to DVB-ASI packets.
    /// </summary>
    public const int ReedSolomonFecSize = 16;
}
