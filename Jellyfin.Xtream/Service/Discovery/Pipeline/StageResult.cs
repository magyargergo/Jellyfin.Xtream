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
using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Xtream.Service.Discovery.Pipeline;

/// <summary>
/// Result of processing an item through a pipeline stage.
/// </summary>
/// <typeparam name="T">The output type.</typeparam>
public readonly struct StageResult<T> : IEquatable<StageResult<T>>
{
    /// <summary>
    /// Gets a value indicating whether the item passed this stage.
    /// </summary>
    [MemberNotNullWhen(true, nameof(Value))]
    public bool Success { get; init; }

    /// <summary>
    /// Gets the output value (if passed).
    /// </summary>
    public T? Value { get; init; }

    /// <summary>
    /// Gets the failure reason (if failed).
    /// </summary>
    public string? FailureReason { get; init; }

    /// <summary>
    /// Gets the processing duration in milliseconds.
    /// </summary>
    public int DurationMs { get; init; }

    /// <inheritdoc />
    public bool Equals(StageResult<T> other) => Success == other.Success && DurationMs == other.DurationMs;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is StageResult<T> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Success, DurationMs);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(StageResult<T> left, StageResult<T> right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(StageResult<T> left, StageResult<T> right) => !left.Equals(right);
}

/// <summary>
/// Factory methods for creating stage results.
/// </summary>
public static class StageResult
{
    /// <summary>
    /// Creates a successful result.
    /// </summary>
    /// <typeparam name="T">Output type.</typeparam>
    /// <param name="value">Output value.</param>
    /// <param name="durationMs">Processing duration.</param>
    /// <returns>Success result.</returns>
    public static StageResult<T> Pass<T>(T value, int durationMs = 0) =>
        new()
        {
            Success = true,
            Value = value,
            DurationMs = durationMs,
        };

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    /// <typeparam name="T">Output type.</typeparam>
    /// <param name="reason">Failure reason.</param>
    /// <param name="durationMs">Processing duration.</param>
    /// <returns>Failure result.</returns>
    public static StageResult<T> Fail<T>(string reason, int durationMs = 0) =>
        new()
        {
            Success = false,
            FailureReason = reason,
            DurationMs = durationMs,
        };

    /// <summary>
    /// Creates a failed result from an item.
    /// </summary>
    /// <typeparam name="T">Output type.</typeparam>
    /// <param name="item">The failed item.</param>
    /// <param name="reason">Failure reason.</param>
    /// <param name="durationMs">Processing duration.</param>
    /// <returns>Failure result.</returns>
    public static StageResult<T> Fail<T>(PipelineItem item, string reason, int durationMs = 0) =>
        new()
        {
            Success = false,
            FailureReason = reason,
            DurationMs = durationMs,
        };
}
