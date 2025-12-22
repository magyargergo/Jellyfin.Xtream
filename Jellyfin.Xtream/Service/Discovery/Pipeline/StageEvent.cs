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
using System.Runtime.InteropServices;
using System.Threading;

namespace Jellyfin.Xtream.Service.Discovery.Pipeline;

/// <summary>
/// Type of stage event.
/// </summary>
public enum StageEventType
{
    /// <summary>Item entered a stage.</summary>
    Entered,

    /// <summary>Item passed stage checks.</summary>
    Passed,

    /// <summary>Item failed stage checks.</summary>
    Failed,

    /// <summary>Stage completed all items.</summary>
    StageCompleted,
}

/// <summary>
/// Lightweight event struct for stage notifications.
/// Uses struct to avoid heap allocations in hot path.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly struct StageEvent : IEquatable<StageEvent>
{
    /// <summary>
    /// Gets the pipeline stage this event relates to.
    /// </summary>
    public PipelineStage Stage { get; init; }

    /// <summary>
    /// Gets the event type.
    /// </summary>
    public StageEventType Type { get; init; }

    /// <summary>
    /// Gets the pipeline item ID.
    /// </summary>
    public int ItemId { get; init; }

    /// <summary>
    /// Gets the UTC timestamp in ticks.
    /// </summary>
    public long TimestampTicks { get; init; }

    /// <summary>
    /// Gets the duration in milliseconds (for Passed/Failed events).
    /// </summary>
    public int DurationMs { get; init; }

    /// <summary>
    /// Gets the failure reason (for Failed events).
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Gets the count of items passed (for StageCompleted events).
    /// </summary>
    public int PassedCount { get; init; }

    /// <summary>
    /// Gets the count of items failed (for StageCompleted events).
    /// </summary>
    public int FailedCount { get; init; }

    /// <summary>
    /// Creates an Entered event.
    /// </summary>
    /// <param name="stage">The stage.</param>
    /// <param name="itemId">The item ID.</param>
    /// <returns>The event.</returns>
    public static StageEvent Entered(PipelineStage stage, int itemId) =>
        new()
        {
            Stage = stage,
            Type = StageEventType.Entered,
            ItemId = itemId,
            TimestampTicks = DateTime.UtcNow.Ticks,
        };

    /// <summary>
    /// Creates a Passed event.
    /// </summary>
    /// <param name="stage">The stage.</param>
    /// <param name="itemId">The item ID.</param>
    /// <param name="durationMs">Processing duration in ms.</param>
    /// <returns>The event.</returns>
    public static StageEvent Passed(PipelineStage stage, int itemId, int durationMs) =>
        new()
        {
            Stage = stage,
            Type = StageEventType.Passed,
            ItemId = itemId,
            TimestampTicks = DateTime.UtcNow.Ticks,
            DurationMs = durationMs,
        };

    /// <summary>
    /// Creates a Failed event.
    /// </summary>
    /// <param name="stage">The stage.</param>
    /// <param name="itemId">The item ID.</param>
    /// <param name="durationMs">Processing duration in ms.</param>
    /// <param name="reason">Failure reason.</param>
    /// <returns>The event.</returns>
    public static StageEvent Failed(PipelineStage stage, int itemId, int durationMs, string? reason) =>
        new()
        {
            Stage = stage,
            Type = StageEventType.Failed,
            ItemId = itemId,
            TimestampTicks = DateTime.UtcNow.Ticks,
            DurationMs = durationMs,
            Reason = reason,
        };

    /// <summary>
    /// Creates a StageCompleted event.
    /// </summary>
    /// <param name="stage">The stage.</param>
    /// <param name="passedCount">Items that passed.</param>
    /// <param name="failedCount">Items that failed.</param>
    /// <returns>The event.</returns>
    public static StageEvent Completed(PipelineStage stage, int passedCount, int failedCount) =>
        new()
        {
            Stage = stage,
            Type = StageEventType.StageCompleted,
            TimestampTicks = DateTime.UtcNow.Ticks,
            PassedCount = passedCount,
            FailedCount = failedCount,
        };

    /// <inheritdoc />
    public bool Equals(StageEvent other) =>
        Stage == other.Stage && Type == other.Type && ItemId == other.ItemId && TimestampTicks == other.TimestampTicks;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is StageEvent other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Stage, Type, ItemId, TimestampTicks);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(StageEvent left, StageEvent right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(StageEvent left, StageEvent right) => !left.Equals(right);
}

/// <summary>
/// Lock-free stage statistics using atomic operations.
/// </summary>
public sealed class StageStats
{
    private long _entered;
    private long _passed;
    private long _failed;
    private long _totalDurationMs;

    /// <summary>
    /// Gets the count of items that entered this stage.
    /// </summary>
    public long Entered => Interlocked.Read(ref _entered);

    /// <summary>
    /// Gets the count of items that passed this stage.
    /// </summary>
    public long Passed => Interlocked.Read(ref _passed);

    /// <summary>
    /// Gets the count of items that failed this stage.
    /// </summary>
    public long Failed => Interlocked.Read(ref _failed);

    /// <summary>
    /// Gets the count of items still being processed.
    /// </summary>
    public long InProgress => Entered - Passed - Failed;

    /// <summary>
    /// Gets the average duration in milliseconds.
    /// </summary>
    public double AverageDurationMs
    {
        get
        {
            var completed = Passed + Failed;
            return completed > 0 ? (double)Interlocked.Read(ref _totalDurationMs) / completed : 0;
        }
    }

    /// <summary>
    /// Gets the pass rate as a percentage.
    /// </summary>
    public double PassRate
    {
        get
        {
            var completed = Passed + Failed;
            return completed > 0 ? (double)Passed / completed * 100 : 0;
        }
    }

    /// <summary>
    /// Records an item entering the stage.
    /// </summary>
    public void RecordEntered() => Interlocked.Increment(ref _entered);

    /// <summary>
    /// Records an item passing the stage.
    /// </summary>
    /// <param name="durationMs">Processing duration.</param>
    public void RecordPassed(int durationMs)
    {
        Interlocked.Increment(ref _passed);
        Interlocked.Add(ref _totalDurationMs, durationMs);
    }

    /// <summary>
    /// Records an item failing the stage.
    /// </summary>
    /// <param name="durationMs">Processing duration.</param>
    public void RecordFailed(int durationMs)
    {
        Interlocked.Increment(ref _failed);
        Interlocked.Add(ref _totalDurationMs, durationMs);
    }

    /// <summary>
    /// Applies a stage event to update statistics.
    /// </summary>
    /// <param name="evt">The event to apply.</param>
    public void Apply(StageEvent evt)
    {
        switch (evt.Type)
        {
            case StageEventType.Entered:
                RecordEntered();
                break;
            case StageEventType.Passed:
                RecordPassed(evt.DurationMs);
                break;
            case StageEventType.Failed:
                RecordFailed(evt.DurationMs);
                break;
        }
    }

    /// <summary>
    /// Creates a snapshot of current statistics.
    /// </summary>
    /// <returns>Snapshot of stats.</returns>
    public StageStatsSnapshot ToSnapshot() => new(Entered, Passed, Failed, InProgress, AverageDurationMs, PassRate);
}

/// <summary>
/// Immutable snapshot of stage statistics.
/// </summary>
/// <param name="Entered">Items entered.</param>
/// <param name="Passed">Items passed.</param>
/// <param name="Failed">Items failed.</param>
/// <param name="InProgress">Items currently being processed.</param>
/// <param name="AverageDurationMs">Average processing time.</param>
/// <param name="PassRate">Pass rate percentage.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct StageStatsSnapshot(
    long Entered,
    long Passed,
    long Failed,
    long InProgress,
    double AverageDurationMs,
    double PassRate
);
