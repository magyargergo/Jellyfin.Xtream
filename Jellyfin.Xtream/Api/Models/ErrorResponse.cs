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

using System.Collections.Generic;

namespace Jellyfin.Xtream.Api.Models;

/// <summary>
/// Standardized error response for all API endpoints.
/// </summary>
public sealed class ErrorResponse
{
    /// <summary>
    /// Gets or sets the machine-readable error code.
    /// </summary>
    public string ErrorCode { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the human-readable error message.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the suggested action to resolve the error.
    /// </summary>
    public string? SuggestedAction { get; set; }

    /// <summary>
    /// Gets or sets additional context about the error.
    /// </summary>
    public Dictionary<string, string>? Context { get; init; }

    /// <summary>
    /// Gets or sets the number of seconds to wait before retrying (for rate limit errors).
    /// </summary>
    public int? RetryAfterSeconds { get; set; }
}

/// <summary>
/// Machine-readable error codes for agent/tool consumption.
/// </summary>
public static class ErrorCodes
{
    /// <summary>No provider is configured in the plugin.</summary>
    public const string ProviderNotConfigured = "PROVIDER_NOT_CONFIGURED";

    /// <summary>The specified provider ID was not found.</summary>
    public const string ProviderNotFound = "PROVIDER_NOT_FOUND";

    /// <summary>The specified stream ID was not found in active streams.</summary>
    public const string StreamNotFound = "STREAM_NOT_FOUND";

    /// <summary>Request validation failed (missing or invalid fields).</summary>
    public const string ValidationFailed = "VALIDATION_FAILED";

    /// <summary>Connection to the Xtream provider failed.</summary>
    public const string ConnectionFailed = "CONNECTION_FAILED";

    /// <summary>The operation would exceed the configured connection limit.</summary>
    public const string ConnectionLimitExceeded = "CONNECTION_LIMIT_EXCEEDED";

    /// <summary>Cannot delete the last enabled provider.</summary>
    public const string LastProviderDeletion = "LAST_PROVIDER_DELETION";

    /// <summary>The requested channel override was not found.</summary>
    public const string OverrideNotFound = "OVERRIDE_NOT_FOUND";

    /// <summary>The requested provider or stream health data is unavailable.</summary>
    public const string HealthDataUnavailable = "HEALTH_DATA_UNAVAILABLE";

    /// <summary>An internal server error occurred.</summary>
    public const string InternalError = "INTERNAL_ERROR";
}
