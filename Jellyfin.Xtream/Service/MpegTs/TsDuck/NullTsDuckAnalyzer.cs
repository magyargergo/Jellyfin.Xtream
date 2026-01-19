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
using Jellyfin.Xtream.Service.MpegTs.UseCases;

namespace Jellyfin.Xtream.Service.MpegTs.TsDuck;

/// <summary>
/// No-op implementation of ITsDuckAnalyzer for graceful degradation.
/// Used when the native TsDuck library is unavailable.
/// </summary>
/// <remarks>
/// This analyzer accepts data but performs no analysis. It allows the application
/// to function without TsDuck, just without TR 101 290 metrics. Use this when:
/// <list type="bullet">
/// <item>The native library (libtsduck_interop) is not installed</item>
/// <item>TsDuck runtime dependencies are missing</item>
/// <item>Testing without TsDuck integration</item>
/// </list>
/// </remarks>
public sealed class NullTsDuckAnalyzer : ITsDuckAnalyzer
{
    /// <inheritdoc/>
    public bool IsAvailable => false;

    /// <inheritdoc/>
    public bool IsInitialized => true; // Always "initialized" but does nothing

    /// <inheritdoc/>
    public TsDuckProcessStatus ProcessStatus => TsDuckProcessStatus.Unavailable;

    /// <inheritdoc/>
    public event EventHandler<StreamQualityViolationEventArgs>? StreamQualityViolation
    {
        add
        { /* No-op */
        }
        remove
        { /* No-op */
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Not raised. For A/V sync drift monitoring, use <c>TsIndexer.TimestampTracker.DriftDetected</c>.
    /// </remarks>
    public event EventHandler<SyncDriftEventArgs>? SyncDriftDetected
    {
        add
        { /* No-op - use TsIndexer.TimestampTracker.DriftDetected instead */
        }
        remove
        { /* No-op - use TsIndexer.TimestampTracker.DriftDetected instead */
        }
    }

    /// <inheritdoc/>
    public event EventHandler<TsDuckMetricsEventArgs>? MetricsUpdated
    {
        add
        { /* No-op */
        }
        remove
        { /* No-op */
        }
    }

    /// <inheritdoc/>
    /// <returns>Always returns null since no analysis is performed.</returns>
    public TsDuckMetrics? GetMetrics() => null;

    /// <inheritdoc/>
    /// <returns>Always returns null since no metrics are available.</returns>
    public TimeSpan? MetricsAge => null;

    /// <inheritdoc/>
    /// <returns>Always returns false since no metrics are collected.</returns>
    public bool HasFreshMetrics => false;

    /// <inheritdoc/>
    /// <remarks>Data is discarded - no analysis performed.</remarks>
    public void FeedData(ReadOnlySpan<byte> data)
    {
        // No-op: data is discarded
    }

    /// <inheritdoc/>
    /// <remarks>No state to reset.</remarks>
    public void Reset()
    {
        // No-op: nothing to reset
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // No resources to dispose
    }
}
