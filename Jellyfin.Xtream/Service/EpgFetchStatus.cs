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

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Represents the result of an EPG fetch attempt for a single channel.
/// </summary>
public enum EpgFetchStatus
{
    /// <summary>
    /// EPG data was successfully retrieved and cached.
    /// </summary>
    Success,

    /// <summary>
    /// No EPG data available for this channel.
    /// </summary>
    NoData,

    /// <summary>
    /// HTTP error occurred (retryable).
    /// </summary>
    HttpError,

    /// <summary>
    /// Other error occurred (not retryable).
    /// </summary>
    Error,
}
