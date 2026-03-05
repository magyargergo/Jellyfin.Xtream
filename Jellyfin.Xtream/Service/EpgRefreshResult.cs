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

using System;
using System.Collections.Generic;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Represents the result of an EPG refresh operation.
/// </summary>
public sealed class EpgRefreshResult
{
    /// <summary>
    /// Gets the number of channels that successfully retrieved EPG data.
    /// </summary>
    public int SuccessCount { get; init; }

    /// <summary>
    /// Gets the total number of channels processed.
    /// </summary>
    public int TotalCount { get; init; }

    /// <summary>
    /// Gets the number of channels that succeeded after retry.
    /// </summary>
    public int RetriedSuccessCount { get; init; }

    /// <summary>
    /// Gets the duration of the refresh operation.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Gets the list of channels that failed to retrieve EPG data.
    /// </summary>
    public IReadOnlyList<string> FailedChannels { get; init; } = [];

    /// <summary>
    /// Gets an error message if the refresh operation failed entirely.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Gets the number of HTTP errors encountered.
    /// </summary>
    public int HttpErrorCount { get; init; }

    /// <summary>
    /// Gets the number of channels with no EPG data available.
    /// </summary>
    public int NoDataCount { get; init; }

    /// <summary>
    /// Gets a value indicating whether the refresh was fully successful.
    /// </summary>
    public bool IsSuccess => string.IsNullOrEmpty(ErrorMessage) && SuccessCount == TotalCount;

    /// <summary>
    /// Gets a value indicating whether the refresh was partially successful.
    /// </summary>
    public bool IsPartialSuccess => string.IsNullOrEmpty(ErrorMessage) && SuccessCount > 0 && SuccessCount < TotalCount;

    /// <summary>
    /// Gets a value indicating whether the refresh failed entirely.
    /// </summary>
    public bool IsFailure => !string.IsNullOrEmpty(ErrorMessage) || (TotalCount > 0 && SuccessCount == 0);
}
