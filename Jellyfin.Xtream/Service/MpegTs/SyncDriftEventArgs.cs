using System;

namespace Jellyfin.Xtream.Service.MpegTs;

/// <summary>
/// Event arguments for A/V sync drift detection.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="SyncDriftEventArgs"/> class.
/// </remarks>
/// <param name="driftMs">The current drift in milliseconds.</param>
/// <param name="status">The current sync status.</param>
public sealed class SyncDriftEventArgs(double driftMs, SyncStatus status) : EventArgs
{
    /// <summary>
    /// Gets the drift in milliseconds. Positive = audio ahead.
    /// </summary>
    public double DriftMs { get; } = driftMs;

    /// <summary>
    /// Gets the current sync status.
    /// </summary>
    public SyncStatus Status { get; } = status;
}
