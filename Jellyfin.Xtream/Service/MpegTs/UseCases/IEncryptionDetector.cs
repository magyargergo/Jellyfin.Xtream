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

using System.Collections.Generic;

namespace Jellyfin.Xtream.Service.MpegTs.UseCases;

/// <summary>
/// Detects encryption and scrambling in MPEG-TS streams.
/// This is a focused interface following the Interface Segregation Principle.
/// </summary>
public interface IEncryptionDetector
{
    /// <summary>
    /// Gets the PIDs that are currently scrambled (encrypted).
    /// </summary>
    int[] ScrambledPids { get; }

    /// <summary>
    /// Gets the detected Conditional Access System IDs from CAT.
    /// </summary>
    IReadOnlyDictionary<int, string> CaSystemIds { get; }

    /// <summary>
    /// Gets a value indicating whether the stream is encrypted.
    /// </summary>
    bool IsEncrypted { get; }
}
