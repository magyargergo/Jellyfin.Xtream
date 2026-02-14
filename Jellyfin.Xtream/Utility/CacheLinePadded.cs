// Copyright (C) 2025  Gergo Magyar

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System.Runtime.InteropServices;

namespace Jellyfin.Xtream.Utility;

/// <summary>
/// A cache-line-padded long value (128 bytes total) to prevent false sharing
/// between atomically accessed fields on different CPU cores.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 128)]
internal struct CacheLinePadded
{
    /// <summary>
    /// The padded long value.
    /// </summary>
    [FieldOffset(0)]
    public long Value;
}

/// <summary>
/// A cache-line-padded int value (128 bytes total) to prevent false sharing
/// between atomically accessed fields on different CPU cores.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 128)]
internal struct CacheLinePaddedInt
{
    /// <summary>
    /// The padded int value.
    /// </summary>
    [FieldOffset(0)]
    public int Value;
}
