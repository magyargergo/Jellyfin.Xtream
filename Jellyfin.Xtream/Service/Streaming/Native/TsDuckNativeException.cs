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

namespace Jellyfin.Xtream.Service.Streaming.Native;

/// <summary>
/// Exception thrown when a native TsDuck operation fails.
/// </summary>
public sealed class TsDuckNativeException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TsDuckNativeException"/> class.
    /// </summary>
    public TsDuckNativeException() { }

    /// <summary>
    /// Initializes a new instance of the <see cref="TsDuckNativeException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    public TsDuckNativeException(string message)
        : base(message) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="TsDuckNativeException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception.</param>
    public TsDuckNativeException(string message, Exception innerException)
        : base(message, innerException) { }
}
