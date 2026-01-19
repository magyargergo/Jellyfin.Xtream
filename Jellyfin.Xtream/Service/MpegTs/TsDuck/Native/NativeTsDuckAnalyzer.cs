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
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Jellyfin.Xtream.Service.MpegTs.UseCases;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.MpegTs.TsDuck.Native;

/// <summary>
/// Native TSDuck analyzer implementation using P/Invoke to libtsduck_interop.
/// Provides broadcast-grade TR 101 290 monitoring with minimal overhead.
/// </summary>
public sealed unsafe class NativeTsDuckAnalyzer : ITsDuckAnalyzer
{
    private readonly TsDuckConfiguration _config;
    private readonly ILogger? _logger;

    private readonly TsDuckContextSafeHandle _context;
    private readonly TsDuckAnalyzerSafeHandle _analyzer;

    // Keep delegates alive to prevent GC
    private readonly TsDuckNativeMethods.MetricsCallbackDelegate? _metricsDelegate;
    private readonly TsDuckNativeMethods.ViolationCallbackDelegate? _violationDelegate;

    private volatile TsDuckMetrics? _latestMetrics;
    private TsDuckMetrics? _previousMetrics;
    private bool _disposed;

    // Event deduplication state
    private int _lastReportedQualityScore = 100;
    private DateTime _lastMetricsEventTime = DateTime.MinValue;
    private readonly ConcurrentDictionary<string, DateTime> _recentViolations = new(StringComparer.Ordinal);

    /// <summary>
    /// Minimum interval between metrics events in milliseconds.
    /// Events are suppressed if metrics haven't changed significantly within this window.
    /// </summary>
    private const int MinMetricsChangeIntervalMs = 500;

    /// <summary>
    /// Time window for violation deduplication in milliseconds.
    /// Same violation type won't fire again within this window.
    /// </summary>
    private const int ViolationDedupeWindowMs = 5000;

    /// <summary>
    /// Quality score change threshold that triggers an event regardless of time.
    /// </summary>
    private const int SignificantScoreChange = 5;

    /// <summary>
    /// Initializes a new instance of the <see cref="NativeTsDuckAnalyzer"/> class.
    /// </summary>
    /// <param name="config">Configuration options. Uses defaults if null.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <exception cref="TsDuckNativeException">Thrown if native library initialization fails.</exception>
    public NativeTsDuckAnalyzer(TsDuckConfiguration? config = null, ILogger? logger = null)
    {
        _config = config ?? TsDuckConfiguration.Default;
        _logger = logger;

        // Create context using SafeHandle factory
        _context = TsDuckContextSafeHandle.Create();
        if (_context.IsInvalid)
        {
            throw new TsDuckNativeException("Failed to create TsDuck native context");
        }

        // Create analyzer with configuration using SafeHandle factory
        var nativeConfig = TsDuckConfigNative.FromManaged(_config);
        _analyzer = TsDuckAnalyzerSafeHandle.Create(_context, nativeConfig);
        if (_analyzer.IsInvalid)
        {
            _context.Dispose();
            throw new TsDuckNativeException("Failed to create TsDuck native analyzer");
        }

        // Set up callbacks
        _metricsDelegate = OnNativeMetricsCallback;
        _violationDelegate = OnNativeViolationCallback;

        TsDuckNativeMethods.AnalyzerSetMetricsCallback(_analyzer.DangerousGetHandle(), _metricsDelegate, 0);
        TsDuckNativeMethods.AnalyzerSetViolationCallback(_analyzer.DangerousGetHandle(), _violationDelegate, 0);

        var version = TsDuckNativeMethods.GetVersion();
        _logger?.PluginLogInformation(
            "NativeTsDuckAnalyzer initialized (TSDuck version: {Version})",
            version ?? "unknown"
        );
    }

    /// <inheritdoc/>
    public bool IsAvailable =>
        !_disposed && !_context.IsInvalid && TsDuckNativeMethods.ContextIsAvailable(_context.DangerousGetHandle());

    /// <inheritdoc/>
    public bool IsInitialized =>
        !_disposed && !_analyzer.IsInvalid && TsDuckNativeMethods.AnalyzerIsInitialized(_analyzer.DangerousGetHandle());

    /// <inheritdoc/>
    public TsDuckProcessStatus ProcessStatus
    {
        get
        {
            if (_disposed)
            {
                return TsDuckProcessStatus.Stopped;
            }

            if (_context.IsInvalid || !TsDuckNativeMethods.ContextIsAvailable(_context.DangerousGetHandle()))
            {
                return TsDuckProcessStatus.Unavailable;
            }

            return !_analyzer.IsInvalid && TsDuckNativeMethods.AnalyzerIsInitialized(_analyzer.DangerousGetHandle())
                ? TsDuckProcessStatus.Running
                : TsDuckProcessStatus.Failed;
        }
    }

    /// <inheritdoc/>
    public event EventHandler<StreamQualityViolationEventArgs>? StreamQualityViolation;

    /// <inheritdoc/>
    public event EventHandler<TsDuckMetricsEventArgs>? MetricsUpdated;

    /// <summary>
    /// Event raised when A/V synchronization drift is detected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Note:</strong> This event is NOT raised by NativeTsDuckAnalyzer.
    /// Native TsDuck analysis does not track PTS/DTS correlation needed for A/V sync detection.
    /// </para>
    /// <para>
    /// For A/V sync drift monitoring, use <c>TsIndexer.TimestampTracker.DriftDetected</c> instead,
    /// which provides real-time drift detection with 20ms professional broadcast threshold (EBU R37).
    /// </para>
    /// </remarks>
    public event EventHandler<SyncDriftEventArgs>? SyncDriftDetected
    {
        add
        { /* Not implemented - use TsIndexer.TimestampTracker.DriftDetected instead */
        }
        remove
        { /* Not implemented - use TsIndexer.TimestampTracker.DriftDetected instead */
        }
    }

    /// <inheritdoc/>
    public TsDuckMetrics? GetMetrics() => _latestMetrics;

    /// <inheritdoc/>
    public TimeSpan? MetricsAge => _latestMetrics?.Age;

    /// <inheritdoc/>
    public bool HasFreshMetrics => _latestMetrics is not null && !_latestMetrics.IsStale;

    /// <inheritdoc/>
    public void FeedData(ReadOnlySpan<byte> data)
    {
        if (_disposed || data.IsEmpty || !IsInitialized)
        {
            return;
        }

        var result = TsDuckNativeMethods.AnalyzerFeed(_analyzer.DangerousGetHandle(), data);
        if (result < 0)
        {
            _logger?.LogDebugIfEnabled("Native TsDuck feed error: {Error}", (TsDuckNativeError)result);
        }
    }

    /// <inheritdoc/>
    public void Reset()
    {
        if (_disposed || _analyzer.IsInvalid)
        {
            return;
        }

        _logger?.LogDebugIfEnabled("NativeTsDuckAnalyzer.Reset() - clearing state for new stream");

        TsDuckNativeMethods.AnalyzerReset(_analyzer.DangerousGetHandle());

        _latestMetrics = null;
        _previousMetrics = null;

        // Reset deduplication state
        _lastReportedQualityScore = 100;
        _lastMetricsEventTime = DateTime.MinValue;
        _recentViolations.Clear();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Clear callbacks before disposing handles
        if (!_analyzer.IsInvalid)
        {
            TsDuckNativeMethods.AnalyzerSetMetricsCallback(_analyzer.DangerousGetHandle(), null, 0);
            TsDuckNativeMethods.AnalyzerSetViolationCallback(_analyzer.DangerousGetHandle(), null, 0);
        }

        // Dispose in reverse order of creation - SafeHandles handle the native cleanup
        _analyzer.Dispose();
        _context.Dispose();

        _logger?.LogDebugIfEnabled("NativeTsDuckAnalyzer disposed");
    }

    private void OnNativeMetricsCallback(TsDuckMetricsNative* nativeMetrics, nint userData)
    {
        if (_disposed || nativeMetrics == null)
        {
            return;
        }

        try
        {
            var metrics = nativeMetrics->ToManaged();

            // Populate Phase 2a enhanced metrics
            PopulateEnhancedMetrics(metrics);

            _previousMetrics = _latestMetrics;
            _latestMetrics = metrics;

            // Only raise event if there's a significant change (deduplication)
            if (ShouldRaiseMetricsEvent(metrics))
            {
                MetricsUpdated?.Invoke(this, new TsDuckMetricsEventArgs(metrics));
                _lastMetricsEventTime = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebugIfEnabled(ex, "Error processing native metrics callback");
        }
    }

    /// <summary>
    /// Determines whether a metrics event should be raised based on deduplication rules.
    /// </summary>
    /// <param name="metrics">The current metrics.</param>
    /// <returns>True if the event should be raised; false to suppress.</returns>
    private bool ShouldRaiseMetricsEvent(TsDuckMetrics metrics)
    {
        // Always fire the first event
        if (_previousMetrics is null)
        {
            return true;
        }

        var now = DateTime.UtcNow;
        var timeSinceLast = (now - _lastMetricsEventTime).TotalMilliseconds;

        // Calculate current quality score
        var currentScore = metrics.CalculateQualityScore();
        var scoreDelta = Math.Abs(currentScore - _lastReportedQualityScore);

        // Fire on significant score change regardless of time
        if (scoreDelta >= SignificantScoreChange)
        {
            _lastReportedQualityScore = currentScore;
            return true;
        }

        // Throttle if within minimum interval and no significant change
        if (timeSinceLast < MinMetricsChangeIntervalMs)
        {
            return false;
        }

        // Fire if new errors appeared
        if (HasNewErrors(metrics, _previousMetrics))
        {
            _lastReportedQualityScore = currentScore;
            return true;
        }

        // Fire periodically even without changes (every 5 seconds) for freshness
        if (timeSinceLast >= 5000)
        {
            _lastReportedQualityScore = currentScore;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if new TR 101 290 errors have appeared since the previous metrics.
    /// </summary>
    private static bool HasNewErrors(TsDuckMetrics current, TsDuckMetrics previous)
    {
        // Check Priority 1 error increases
        if (current.Priority1.TotalErrors > previous.Priority1.TotalErrors)
        {
            return true;
        }

        // Check Priority 2 error increases
        if (current.Priority2.TotalErrors > previous.Priority2.TotalErrors)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Populates enhanced metrics (PCR, IAT, Bitrate, Extended PID) from native analyzer.
    /// </summary>
    private void PopulateEnhancedMetrics(TsDuckMetrics metrics)
    {
        var handle = _analyzer.DangerousGetHandle();

        // PCR Analysis
        if (TsDuckNativeMethods.AnalyzerGetPcrAnalysis(handle, out var pcrNative))
        {
            metrics.PcrAnalysis = pcrNative.ToManaged();
        }

        // IAT Analysis
        if (TsDuckNativeMethods.AnalyzerGetIatAnalysis(handle, out var iatNative))
        {
            metrics.IatAnalysis = iatNative.ToManaged();
        }

        // Bitrate Analysis
        if (TsDuckNativeMethods.AnalyzerGetBitrateAnalysis(handle, out var bitrateNative))
        {
            metrics.BitrateAnalysis = bitrateNative.ToManaged();
        }

        // Extended PID Information (Phase 2b)
        var pidsExtended = TsDuckNativeMethods.GetPidInfoExtended(handle);
        if (pidsExtended.Length > 0)
        {
            metrics.PidsExtended = pidsExtended;
        }
    }

    private void OnNativeViolationCallback(nint violationTypePtr, nint detailsPtr, nint userData)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var violationType = Marshal.PtrToStringAnsi(violationTypePtr) ?? "Unknown Violation";
            var details = Marshal.PtrToStringAnsi(detailsPtr) ?? string.Empty;

            _logger?.LogDebugIfEnabled("Native TsDuck violation: {Type} - {Details}", violationType, details);

            // Deduplicate violations within time window
            if (ShouldRaiseViolationEvent(violationType))
            {
                StreamQualityViolation?.Invoke(this, new StreamQualityViolationEventArgs(violationType, details));
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebugIfEnabled(ex, "Error processing native violation callback");
        }
    }

    /// <summary>
    /// Determines whether a violation event should be raised based on deduplication rules.
    /// Same violation type won't fire again within the deduplication window.
    /// </summary>
    /// <param name="violationType">The violation type string.</param>
    /// <returns>True if the event should be raised; false to suppress.</returns>
    private bool ShouldRaiseViolationEvent(string violationType)
    {
        var now = DateTime.UtcNow;

        if (_recentViolations.TryGetValue(violationType, out var lastTime))
        {
            if ((now - lastTime).TotalMilliseconds < ViolationDedupeWindowMs)
            {
                return false;
            }
        }

        _recentViolations[violationType] = now;

        // Periodically clean up old entries to prevent memory growth
        if (_recentViolations.Count > 50)
        {
            CleanupOldViolations(now);
        }

        return true;
    }

    /// <summary>
    /// Removes old violation entries to prevent unbounded memory growth.
    /// Thread-safe cleanup using ConcurrentDictionary.TryRemove.
    /// </summary>
    private void CleanupOldViolations(DateTime now)
    {
        foreach (var kvp in _recentViolations)
        {
            if ((now - kvp.Value).TotalMilliseconds > ViolationDedupeWindowMs * 2)
            {
                _recentViolations.TryRemove(kvp.Key, out _);
            }
        }
    }
}

/// <summary>
/// Exception thrown when native TSDuck initialization fails.
/// </summary>
[Serializable]
public sealed class TsDuckNativeException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TsDuckNativeException"/> class.
    /// </summary>
    public TsDuckNativeException()
        : base() { }

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
