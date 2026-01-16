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
/// NAL unit types for H.264 and H.265 video streams.
/// </summary>
public enum NalUnitType
{
    /// <summary>Unknown or unrecognized NAL unit.</summary>
    Unknown = 0,

    /// <summary>H.264 IDR (Instantaneous Decoder Refresh) slice.</summary>
    H264Idr = 1,

    /// <summary>H.264 non-IDR slice.</summary>
    H264NonIdr = 2,

    /// <summary>H.264 Sequence Parameter Set.</summary>
    H264Sps = 3,

    /// <summary>H.264 Picture Parameter Set.</summary>
    H264Pps = 4,

    /// <summary>H.264 Access Unit Delimiter.</summary>
    H264Aud = 5,

    /// <summary>H.265 IDR slice (IDR_W_RADL or IDR_N_LP).</summary>
    H265Idr = 10,

    /// <summary>H.265 CRA (Clean Random Access) slice.</summary>
    H265Cra = 11,

    /// <summary>H.265 non-IDR slice.</summary>
    H265NonIdr = 12,

    /// <summary>H.265 Video Parameter Set.</summary>
    H265Vps = 13,

    /// <summary>H.265 Sequence Parameter Set.</summary>
    H265Sps = 14,

    /// <summary>H.265 Picture Parameter Set.</summary>
    H265Pps = 15,

    /// <summary>H.265 Access Unit Delimiter.</summary>
    H265Aud = 16,
}
