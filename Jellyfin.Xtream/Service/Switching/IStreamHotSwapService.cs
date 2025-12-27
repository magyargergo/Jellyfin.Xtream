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
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Xtream.Service.Switching;

/// <summary>
/// Provides hot-swap functionality for switching stream providers mid-playback.
/// </summary>
public interface IStreamHotSwapService
{
    /// <summary>
    /// Attempts to switch to a new provider URL.
    /// </summary>
    /// <param name="context">The hot-swap context containing stream state.</param>
    /// <param name="reason">The reason for the switch request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the hot-swap attempt.</returns>
    Task<HotSwapResult> TrySwitchAsync(
        HotSwapContext context,
        SwitchReason reason,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Gets a value indicating whether hot-swap is available for the given context.
    /// </summary>
    /// <param name="context">The hot-swap context.</param>
    /// <returns>True if hot-swap can be attempted.</returns>
    bool CanSwitch(HotSwapContext context);
}

/// <summary>
/// Context for hot-swap operations containing stream state information.
/// </summary>
public readonly struct HotSwapContext : IEquatable<HotSwapContext>
{
    /// <summary>
    /// Gets the stream/channel identifier.
    /// </summary>
    public required string StreamId { get; init; }

    /// <summary>
    /// Gets the current source URL.
    /// </summary>
    public required string CurrentUrl { get; init; }

    /// <summary>
    /// Gets the number of consecutive failures.
    /// </summary>
    public int ConsecutiveFailures { get; init; }

    /// <summary>
    /// Gets the total bytes transferred in current session.
    /// </summary>
    public long BytesTransferred { get; init; }

    /// <summary>Equality operator.</summary>
    public static bool operator ==(HotSwapContext left, HotSwapContext right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(HotSwapContext left, HotSwapContext right) => !left.Equals(right);

    /// <inheritdoc />
    public bool Equals(HotSwapContext other) => StreamId == other.StreamId && CurrentUrl == other.CurrentUrl;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is HotSwapContext other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(StreamId, CurrentUrl);
}

/// <summary>
/// Result of a hot-swap attempt.
/// </summary>
public readonly struct HotSwapResult : IEquatable<HotSwapResult>
{
    /// <summary>
    /// Gets a value indicating whether the switch succeeded.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Gets the new URL to use (if successful).
    /// </summary>
    public string? NewUrl { get; init; }

    /// <summary>
    /// Gets the time taken for the switch in milliseconds.
    /// </summary>
    public long ElapsedMs { get; init; }

    /// <summary>
    /// Gets the failure reason (if unsuccessful).
    /// </summary>
    public HotSwapFailureReason FailureReason { get; init; }

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static HotSwapResult Succeeded(string newUrl, long elapsedMs) =>
        new()
        {
            Success = true,
            NewUrl = newUrl,
            ElapsedMs = elapsedMs,
            FailureReason = HotSwapFailureReason.None,
        };

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    public static HotSwapResult Failed(HotSwapFailureReason reason) =>
        new()
        {
            Success = false,
            NewUrl = null,
            ElapsedMs = 0,
            FailureReason = reason,
        };

    /// <summary>
    /// A cached instance for when hot-swap is not available.
    /// </summary>
    public static readonly HotSwapResult NotAvailable = Failed(HotSwapFailureReason.NotAvailable);

    /// <summary>
    /// A cached instance for cooldown active.
    /// </summary>
    public static readonly HotSwapResult CooldownActive = Failed(HotSwapFailureReason.CooldownActive);

    /// <summary>
    /// A cached instance for max attempts reached.
    /// </summary>
    public static readonly HotSwapResult MaxAttemptsReached = Failed(HotSwapFailureReason.MaxAttemptsReached);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(HotSwapResult left, HotSwapResult right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(HotSwapResult left, HotSwapResult right) => !left.Equals(right);

    /// <inheritdoc />
    public bool Equals(HotSwapResult other) => Success == other.Success && FailureReason == other.FailureReason;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is HotSwapResult other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Success, FailureReason);
}

/// <summary>
/// Reasons for hot-swap failure.
/// </summary>
public enum HotSwapFailureReason
{
    /// <summary>No failure.</summary>
    None,

    /// <summary>Hot-swap is not available/configured.</summary>
    NotAvailable,

    /// <summary>Cooldown period is active.</summary>
    CooldownActive,

    /// <summary>Maximum attempts reached.</summary>
    MaxAttemptsReached,

    /// <summary>Provider returned no alternative.</summary>
    NoAlternativeProvider,

    /// <summary>Switch timed out.</summary>
    Timeout,

    /// <summary>Switch failed with error.</summary>
    Error,
}

/// <summary>
/// Reasons for initiating a provider switch.
/// </summary>
public enum SwitchReason
{
    /// <summary>Current provider health is degraded.</summary>
    HealthDegraded,

    /// <summary>Connection to current provider failed.</summary>
    ConnectionFailed,

    /// <summary>Current provider capacity reached.</summary>
    CapacityReached,

    /// <summary>A better provider became available.</summary>
    BetterProviderAvailable,

    /// <summary>Manual switch requested.</summary>
    Manual,
}

/// <summary>
/// Result of a provider switch operation.
/// </summary>
public readonly struct SwitchResult : IEquatable<SwitchResult>
{
    /// <summary>
    /// Gets a value indicating whether the switch was successful.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Gets the previous provider (before switch).
    /// </summary>
    public ProviderStreamInfo? PreviousProvider { get; init; }

    /// <summary>
    /// Gets the new provider (after switch).
    /// </summary>
    public ProviderStreamInfo? NewProvider { get; init; }

    /// <summary>
    /// Gets the switch duration in milliseconds.
    /// </summary>
    public int DurationMs { get; init; }

    /// <summary>
    /// Gets a value indicating whether there was a stream discontinuity.
    /// </summary>
    public bool HadDiscontinuity { get; init; }

    /// <summary>
    /// Gets the failure reason if not successful.
    /// </summary>
    public string? FailureReason { get; init; }

    /// <summary>
    /// Gets the exception that caused the failure (if any).
    /// </summary>
    public Exception? Exception { get; init; }

    /// <summary>
    /// Creates a successful switch result.
    /// </summary>
    public static SwitchResult Succeeded(
        ProviderStreamInfo previousProvider,
        ProviderStreamInfo newProvider,
        int durationMs,
        bool hadDiscontinuity
    ) =>
        new()
        {
            Success = true,
            PreviousProvider = previousProvider,
            NewProvider = newProvider,
            DurationMs = durationMs,
            HadDiscontinuity = hadDiscontinuity,
        };

    /// <summary>
    /// Creates a failed switch result.
    /// </summary>
    public static SwitchResult Failed(ProviderStreamInfo provider, string reason) =>
        new()
        {
            Success = false,
            PreviousProvider = provider,
            FailureReason = reason,
        };

    /// <summary>
    /// Creates a failed switch result with exception.
    /// </summary>
    public static SwitchResult Failed(ProviderStreamInfo provider, string reason, Exception? exception) =>
        new()
        {
            Success = false,
            PreviousProvider = provider,
            FailureReason = reason,
            Exception = exception,
        };

    /// <summary>Equality operator.</summary>
    public static bool operator ==(SwitchResult left, SwitchResult right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(SwitchResult left, SwitchResult right) => !left.Equals(right);

    /// <inheritdoc />
    public bool Equals(SwitchResult other) =>
        Success == other.Success && Equals(PreviousProvider, other.PreviousProvider);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SwitchResult other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Success, PreviousProvider);
}
