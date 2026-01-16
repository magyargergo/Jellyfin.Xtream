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

namespace Jellyfin.Xtream.Service.MpegTs.UseCases;

/// <summary>
/// Monitors CRC-32 validation errors for MPEG-TS tables (PAT, PMT, CAT).
/// This is a focused interface following the Interface Segregation Principle.
/// </summary>
public interface ICrcMonitor
{
    /// <summary>
    /// Gets the number of PAT CRC-32 validation failures.
    /// </summary>
    long PatCrcErrors { get; }

    /// <summary>
    /// Gets the number of PMT CRC-32 validation failures.
    /// </summary>
    long PmtCrcErrors { get; }

    /// <summary>
    /// Gets the number of CAT CRC-32 validation failures.
    /// </summary>
    long CatCrcErrors { get; }
}
