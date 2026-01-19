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
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Service.MpegTs.Core;
using Jellyfin.Xtream.Service.MpegTs.Infrastructure;
using Jellyfin.Xtream.Service.MpegTs.Infrastructure.Pooling;
using Jellyfin.Xtream.Service.MpegTs.TsDuck;
using Jellyfin.Xtream.Service.MpegTs.UseCases;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.ProviderManagement;

/// <summary>
/// Configuration for the provider switch service.
/// </summary>
public sealed class ProviderSwitchConfiguration
{
    /// <summary>Gets the cooldown between switch attempts in milliseconds.</summary>
    public int CooldownMs { get; init; } = 3000;

    /// <summary>Gets the maximum switch attempts per stream session.</summary>
    public int MaxAttemptsPerSession { get; init; } = 10;

    /// <summary>Gets the timeout for a single switch attempt in milliseconds.</summary>
    public int TimeoutMs { get; init; } = 5000;

    /// <summary>Gets whether to use MPEG-TS byte alignment during switching.</summary>
    public bool UseByteAlignment { get; init; } = true;

    /// <summary>Gets whether to wait for keyframes during aligned switching.</summary>
    public bool WaitForKeyframe { get; init; } = true;

    /// <summary>Gets whether to activate timestamp remapping for seamless A/V sync.</summary>
    public bool UseTimestampRemapping { get; init; } = true;

    /// <summary>Gets whether to validate PSI (PAT/PMT) presence before committing to switch.</summary>
    public bool ValidatePsiBeforeSwitch { get; init; } = true;

    /// <summary>Gets the timeout in milliseconds for PSI validation (matches keyframe search timeout).</summary>
    public int PsiValidationTimeoutMs { get; init; } = 3000;

    /// <summary>Default configuration.</summary>
    public static readonly ProviderSwitchConfiguration Default = new();
}

/// <summary>
/// Unified service for provider switching during active streams.
/// Combines URL resolution, MPEG-TS alignment, and FFmpeg-based A/V synchronization.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="ProviderSwitchService"/> class.
/// </remarks>
public sealed class ProviderSwitchService(
    IProviderUrlResolver urlResolver,
    IHttpClientFactory httpClientFactory,
    IProviderAvailabilityService availabilityService,
    IPluginConfigurationProvider configProvider,
    ILogger<ProviderSwitchService> logger,
    ILoggerFactory loggerFactory,
    IProviderMetricsTracker? metricsTracker = null,
    ProviderSwitchConfiguration? config = null,
    IFFmpegContext? ffmpegContext = null,
    IFFmpegProcessorPool? demuxerPool = null,
    IFFmpegProcessorPool? remuxerPool = null
) : IProviderSwitchService
{
    private readonly IProviderUrlResolver _urlResolver =
        urlResolver ?? throw new ArgumentNullException(nameof(urlResolver));
    private readonly IHttpClientFactory _httpClientFactory =
        httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    private readonly IProviderAvailabilityService _availabilityService =
        availabilityService ?? throw new ArgumentNullException(nameof(availabilityService));
    private readonly IPluginConfigurationProvider _configProvider =
        configProvider ?? throw new ArgumentNullException(nameof(configProvider));
    private readonly ILogger<ProviderSwitchService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IProviderMetricsTracker? _metricsTracker = metricsTracker;
    private readonly ProviderSwitchConfiguration _config = config ?? ProviderSwitchConfiguration.Default;
    private readonly IFFmpegContext _ffmpegContext = ffmpegContext ?? FFmpegContextAdapter.Instance;
    private readonly IFFmpegProcessorPool? _demuxerPool = demuxerPool;
    private readonly IFFmpegProcessorPool? _remuxerPool = remuxerPool;

    // Consume loggerFactory to avoid unused parameter error (kept for API compatibility)
#pragma warning disable CA1823, IDE0052
    private readonly ILoggerFactory? _loggerFactory = loggerFactory;
#pragma warning restore CA1823, IDE0052

    private readonly ConcurrentDictionary<string, StreamState> _streams = new(StringComparer.Ordinal);

    private volatile bool _disposed;
    private long _totalSwitches;
    private long _successfulSwitches;
    private long _failedSwitches;
    private long _totalSwitchLatencyMs;

    /// <inheritdoc />
    public int ActiveStreamCount => _streams.Count;

    /// <inheritdoc />
    public SwitchStatistics Statistics =>
        new()
        {
            ActiveStreams = _streams.Count,
            TotalSwitches = Interlocked.Read(ref _totalSwitches),
            SuccessfulSwitches = Interlocked.Read(ref _successfulSwitches),
            FailedSwitches = Interlocked.Read(ref _failedSwitches),
            AverageSwitchLatencyMs =
                _successfulSwitches > 0 ? (double)Interlocked.Read(ref _totalSwitchLatencyMs) / _successfulSwitches : 0,
        };

    /// <inheritdoc />
    public void RegisterStream(string streamId, string currentUrl)
    {
        var state = new StreamState
        {
            StreamId = streamId,
            CurrentUrl = currentUrl,
            StreamSwitcher = new AlignedStreamSwitcher(_logger, _ffmpegContext, _demuxerPool),
            QualityMonitor = new StreamQualityMonitor(),
        };

        // Get a remuxer from the pool for A/V sync (non-blocking background processing)
        if (_config.UseTimestampRemapping && _remuxerPool != null)
        {
            state.RemuxerPool = _remuxerPool;

            // Fire-and-forget async initialization - pool handles lifecycle
            _ = Task.Run(async () =>
            {
                try
                {
                    var remuxer = await _remuxerPool
                        .GetPreparedProcessorAsync(cancellationToken: default)
                        .ConfigureAwait(false);
                    if (remuxer != null)
                    {
                        remuxer.SetStreamId(streamId);
                        state.PooledRemuxer = remuxer;
                        state.RemuxerAssignedTicks = Environment.TickCount64;
                        _logger.PluginLogInformation(
                            "Acquired pooled remuxer {RemuxerId} for stream {StreamId}",
                            remuxer.InstanceId,
                            streamId
                        );
                    }
                    else
                    {
                        _logger.PluginLogWarning(
                            "Failed to acquire pooled remuxer for stream {StreamId}, A/V sync may be degraded",
                            streamId
                        );
                    }
                }
                catch (Exception ex)
                {
                    _logger.PluginLogError(ex, "Error acquiring pooled remuxer for stream {StreamId}", streamId);
                }
            });
        }

        if (_streams.TryAdd(streamId, state))
        {
            _logger.LogDebugIfEnabled("Registered stream {StreamId} for provider switching", streamId);
        }
    }

    /// <inheritdoc />
    public void UnregisterStream(string streamId)
    {
        if (_streams.TryRemove(streamId, out var state))
        {
            var switchCount = state.SwitchCount;
            var remuxer = state.PooledRemuxer;
            var remuxerStats =
                remuxer?.IsRunning == true
                    ? $", remuxer: {remuxer.Statistics.BytesWritten / 1024}KB in, {remuxer.Statistics.BytesRead / 1024}KB out"
                    : string.Empty;

            state.Dispose();

            _logger.LogDebugIfEnabled(
                "Unregistered stream {StreamId}, total switches: {SwitchCount}{RemuxerStats}",
                streamId,
                switchCount,
                remuxerStats
            );
        }
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool CanSwitch(string streamId)
    {
        return _streams.TryGetValue(streamId, out var state)
            && !state.IsCooldownActive(_config.CooldownMs)
            && !state.IsMaxAttemptsReached(_config.MaxAttemptsPerSession);
    }

    /// <inheritdoc />
    public async Task<ProviderSwitchResult> TrySwitchAsync(
        string streamId,
        string currentUrl,
        SwitchReason reason,
        CancellationToken cancellationToken = default
    )
    {
        if (!_streams.TryGetValue(streamId, out var state))
        {
            return ProviderSwitchResult.StreamNotRegistered;
        }

        // Fast-path checks
        if (state.IsCooldownActive(_config.CooldownMs))
        {
            return ProviderSwitchResult.CooldownActive;
        }

        if (state.IsMaxAttemptsReached(_config.MaxAttemptsPerSession))
        {
            return ProviderSwitchResult.MaxAttemptsReached;
        }

        // Acquire per-stream lock to prevent concurrent switches
        if (!await state.SwitchLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return ProviderSwitchResult.Failed(SwitchFailureReason.Error, "Switch already in progress");
        }

        var stopwatch = Stopwatch.StartNew();
        _ = Interlocked.Increment(ref _totalSwitches);
        state.RecordAttempt();

        try
        {
            _logger.PluginLogInformation(
                "Provider switch #{Attempt} for {StreamId} (reason: {Reason})",
                state.SwitchCount,
                streamId,
                reason
            );

            // Apply timeout
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_config.TimeoutMs);

            // Get ALL alternative URLs from resolver (ordered by health score)
            var alternativeUrls = await _urlResolver
                .GetAllAlternativeUrlsAsync(streamId, currentUrl, reason, timeoutCts.Token)
                .ConfigureAwait(false);

            if (alternativeUrls.Count == 0)
            {
                return CompleteFailedSwitch(state, SwitchFailureReason.NoAlternativeProvider);
            }

            // Try each alternative provider until one succeeds
            SwitchFailureReason lastFailureReason = SwitchFailureReason.NoAlternativeProvider;
            string? lastFailureMessage = null;

            for (var i = 0; i < alternativeUrls.Count; i++)
            {
                var newUrl = alternativeUrls[i];

                // Check if we're still within timeout
                if (timeoutCts.Token.IsCancellationRequested)
                {
                    _logger.PluginLogWarning(
                        "Provider switch timeout reached after trying {AttemptCount}/{TotalCount} providers for {StreamId}",
                        i,
                        alternativeUrls.Count,
                        streamId
                    );
                    break;
                }

                _logger.LogDebugIfEnabled(
                    "Trying alternative provider {Index}/{Total} for {StreamId}",
                    i + 1,
                    alternativeUrls.Count,
                    streamId
                );

                // Try aligned switching if enabled
                if (_config.UseByteAlignment)
                {
                    var alignedResult = await TrySwitchWithAlignmentAsync(state, newUrl, stopwatch, timeoutCts.Token)
                        .ConfigureAwait(false);

                    if (alignedResult.HasValue)
                    {
                        if (alignedResult.Value.Success)
                        {
                            // Success! Return the result
                            _logger.PluginLogInformation(
                                "Provider switch succeeded on attempt {Index}/{Total} for {StreamId}",
                                i + 1,
                                alternativeUrls.Count,
                                streamId
                            );
                            return alignedResult.Value;
                        }

                        // This provider failed - record the reason and try the next one
                        lastFailureReason = alignedResult.Value.FailureReason;
                        lastFailureMessage = alignedResult.Value.FailureMessage;

                        _logger.PluginLogWarning(
                            "Alternative provider {Index}/{Total} failed for {StreamId}: {Reason} - {Message}. Trying next...",
                            i + 1,
                            alternativeUrls.Count,
                            streamId,
                            lastFailureReason,
                            lastFailureMessage ?? "No details"
                        );

                        continue;
                    }

                    // alignedResult is null means alignment failed but we can fall through to simple switch
                }

                // Simple URL switch (no alignment) - if we reach here, alignment was disabled or failed softly
                return CompleteSuccessfulSwitch(
                    state,
                    newUrl,
                    stopwatch.ElapsedMilliseconds,
                    timestampRemappingActive: false,
                    alignedToKeyframe: false
                );
            }

            // All providers failed
            _logger.PluginLogError(
                "All {Count} alternative providers failed for {StreamId}. Last error: {Reason} - {Message}",
                alternativeUrls.Count,
                streamId,
                lastFailureReason,
                lastFailureMessage ?? "No details"
            );

            return CompleteFailedSwitch(
                state,
                lastFailureReason,
                lastFailureMessage ?? "All alternative providers failed"
            );
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CompleteFailedSwitch(state, SwitchFailureReason.Timeout, "Switch timed out");
        }
        catch (Exception ex)
        {
            _logger.PluginLogError(ex, "Provider switch error for {StreamId}", streamId);
            return CompleteFailedSwitch(state, SwitchFailureReason.Error, ex.Message);
        }
        finally
        {
            _ = state.SwitchLock.Release();
        }
    }

    /// <inheritdoc />
    public void RecordThroughput(string streamId, long bytesWritten)
    {
        if (_streams.TryGetValue(streamId, out var state))
        {
            state.QualityMonitor.RecordSample(bytesWritten);
        }
    }

    /// <inheritdoc />
    public void RecordTsDuckMetrics(string streamId, TsDuckMetrics metrics)
    {
        if (_streams.TryGetValue(streamId, out var state))
        {
            // Use the event handler method which properly sets both properties
            state.QualityMonitor.OnTsDuckMetricsUpdated(this, new TsDuckMetricsEventArgs(metrics));

            // Forward TsDuck quality score to provider-level metrics tracker
            if (_metricsTracker != null)
            {
                var providerId = ExtractProviderId(state.CurrentUrl);
                if (!string.IsNullOrEmpty(providerId))
                {
                    var qualityScore = metrics.CalculateQualityScore();
                    _metricsTracker.RecordTsDuckQuality(providerId, qualityScore);
                }
            }
        }
    }

    /// <summary>
    /// Attempts aligned switching with MPEG-TS byte alignment and timestamp remapping.
    /// </summary>
    private async Task<ProviderSwitchResult?> TrySwitchWithAlignmentAsync(
        StreamState state,
        string newUrl,
        Stopwatch stopwatch,
        CancellationToken cancellationToken
    )
    {
        try
        {
            // Connect to new provider
            var httpClient = _httpClientFactory.CreateClient("XtreamClient");
            using var request = new HttpRequestMessage(HttpMethod.Get, newUrl);
            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                _logger.PluginLogWarning(
                    "Aligned switch connection failed for {StreamId}: HTTP {StatusCode}",
                    state.StreamId,
                    statusCode
                );

                // Record failure for blacklisting - providers returning HTTP errors should be blacklisted
                var providerId = ExtractProviderId(newUrl);
                if (!string.IsNullOrEmpty(providerId))
                {
                    var failureReason = statusCode switch
                    {
                        >= 400 and < 500 => ProviderFailureReason.ClientError,
                        >= 500 => ProviderFailureReason.ServerError,
                        _ => ProviderFailureReason.Unknown,
                    };

                    var circuitOpened = _availabilityService.RecordFailure(providerId, failureReason);
                    _logger.PluginLogInformation(
                        "Recorded {FailureReason} failure for provider {ProviderId} (circuit opened: {CircuitOpened})",
                        failureReason,
                        providerId,
                        circuitOpened
                    );
                }

                // CRITICAL FIX: If the HTTP connection failed, don't fall back to simple URL switch
                // with the same URL - it will also fail. Return a failed switch instead.
                // The main HTTP loop will try to reconnect to the original provider or trigger
                // another switch attempt with a different provider.
                var failReason = statusCode is >= 400 and < 500
                    ? SwitchFailureReason.ConnectionFailed
                    : SwitchFailureReason.Error;

                return CompleteFailedSwitch(
                    state,
                    failReason,
                    string.Create(CultureInfo.InvariantCulture, $"New provider returned HTTP {statusCode}")
                );
            }

            var newStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var streamDisposer = newStream.ConfigureAwait(false);

            // Validate PSI presence before committing to switch (per TR 101 290)
            // CRITICAL: Store raw PSI data - ffmpeg demuxer needs PAT/PMT to identify streams
            byte[]? psiRawData = null;

            if (_config.ValidatePsiBeforeSwitch)
            {
                var psiValidation = await ValidatePsiPresenceAsync(
                        newStream,
                        _config.PsiValidationTimeoutMs,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                if (!psiValidation.HasPat)
                {
                    _logger.PluginLogWarning(
                        "Provider B stream has no PAT within {TimeoutMs}ms, aborting aligned switch for {StreamId}",
                        _config.PsiValidationTimeoutMs,
                        state.StreamId
                    );
                    return null; // Fall back to simple URL switch
                }

                // Capture PSI data for prepending to aligned stream
                psiRawData = psiValidation.RawData;

                _logger.LogDebugIfEnabled(
                    "PSI validation passed for {StreamId}: PAT={HasPat}, PMT={HasPmt}, VideoPid={VideoPid}, AudioPid={AudioPid}, RawDataBytes={RawBytes}",
                    state.StreamId,
                    psiValidation.HasPat,
                    psiValidation.HasPmt,
                    psiValidation.VideoPid,
                    psiValidation.AudioPid,
                    psiRawData?.Length ?? 0
                );

                // Check for PID layout mismatches between providers
                if (
                    state.LastVideoPid > 0
                    && psiValidation.VideoPid > 0
                    && state.LastVideoPid != psiValidation.VideoPid
                )
                {
                    _logger.PluginLogWarning(
                        "PID mismatch detected for {StreamId}: Video PID changed from 0x{OldPid:X4} to 0x{NewPid:X4}. "
                            + "Some decoders may experience errors until new PSI is processed.",
                        state.StreamId,
                        state.LastVideoPid,
                        psiValidation.VideoPid
                    );
                }

                if (
                    state.LastAudioPid > 0
                    && psiValidation.AudioPid > 0
                    && state.LastAudioPid != psiValidation.AudioPid
                )
                {
                    _logger.PluginLogWarning(
                        "PID mismatch detected for {StreamId}: Audio PID changed from 0x{OldPid:X4} to 0x{NewPid:X4}. "
                            + "Some decoders may experience errors until new PSI is processed.",
                        state.StreamId,
                        state.LastAudioPid,
                        psiValidation.AudioPid
                    );
                }

                // Update state with new PIDs for future comparisons
                if (psiValidation.VideoPid > 0)
                {
                    state.LastVideoPid = psiValidation.VideoPid;
                }

                if (psiValidation.AudioPid > 0)
                {
                    state.LastAudioPid = psiValidation.AudioPid;
                }

                if (psiValidation.PmtPid > 0)
                {
                    state.LastPmtPid = psiValidation.PmtPid;
                }
            }

            // Align to MPEG-TS packet boundaries
            var (alignResult, alignedData) = await state
                .StreamSwitcher.AlignStreamAsync(newStream, _config.WaitForKeyframe, cancellationToken)
                .ConfigureAwait(false);

            if (!alignResult.Success)
            {
                _logger.PluginLogWarning(
                    "Stream alignment failed for {StreamId}: {Error}",
                    state.StreamId,
                    alignResult.Error
                );
                return null; // Fall back to simple URL switch
            }

            // P0 FIX: Activate timestamp remapping - ALWAYS activate with best available offset to prevent desync
            var timestampRemappingActive = false;
            var alignedDataArray = alignedData.ToArray();

            // CRITICAL: Prepend PSI data (PAT/PMT) to aligned stream
            // FFmpeg demuxer needs PAT/PMT BEFORE any elementary stream data to identify streams.
            // Without this, ffmpeg reports "Invalid data found when processing input" or fails to find streams.
            if (psiRawData is { Length: > 0 })
            {
                var combinedData = new byte[psiRawData.Length + alignedDataArray.Length];
                Buffer.BlockCopy(psiRawData, 0, combinedData, 0, psiRawData.Length);
                Buffer.BlockCopy(alignedDataArray, 0, combinedData, psiRawData.Length, alignedDataArray.Length);
                alignedDataArray = combinedData;

                _logger.LogDebugIfEnabled(
                    "Prepended {PsiBytes} bytes of PSI data to aligned stream for {StreamId} (total: {TotalBytes} bytes)",
                    psiRawData.Length,
                    state.StreamId,
                    alignedDataArray.Length
                );
            }

            if (_config.UseTimestampRemapping)
            {
                // Get the new stream's first PTS
                long newFirstPts;
                if (alignResult.FirstVideoPts >= 0)
                {
                    newFirstPts = alignResult.FirstVideoPts;
                }
                else if (alignResult.FirstAudioPts >= 0)
                {
                    _logger.LogDebugIfEnabled(
                        "No video PTS found for {StreamId}, using audio PTS {AudioPts} for remapping",
                        state.StreamId,
                        alignResult.FirstAudioPts
                    );
                    newFirstPts = alignResult.FirstAudioPts;
                }
                else
                {
                    _logger.PluginLogWarning(
                        "No PTS found in aligned data for {StreamId}, using zero for remapping",
                        state.StreamId
                    );
                    newFirstPts = 0;
                }

                // Notify pooled remuxer of provider switch
                // FFmpeg handles A/V sync via genpts/igndts flags - no manual timestamp patching needed
                var remuxer = state.PooledRemuxer;
                if (remuxer?.IsRunning == true)
                {
                    remuxer.NotifyProviderSwitch(state.CurrentUrl, newUrl);
                    timestampRemappingActive = true;
                }
                else
                {
                    // Pipeline not running - inject discontinuity markers as fallback
                    var discontinuityResult = DiscontinuityInjector.InjectDiscontinuity(alignedDataArray);
                    if (discontinuityResult.Success)
                    {
                        _logger.LogDebugIfEnabled(
                            "Injected discontinuity indicators for {StreamId} (pipeline not running)",
                            state.StreamId
                        );
                    }
                }
            }
            else
            {
                // When timestamp remapping is disabled, we still need to inject discontinuity indicators
                // to signal decoders that PCR/timestamps may have jumped and they should resync.
                // Per ISO/IEC 13818-1, the discontinuity_indicator in adaptation field signals:
                // - PCR may have an unexpected discontinuity
                // - Continuity counter may reset
                // - Decoder should resynchronize its system clock
                var discontinuityResult = DiscontinuityInjector.InjectDiscontinuity(alignedDataArray);
                if (discontinuityResult.Success)
                {
                    _logger.LogDebugIfEnabled(
                        "Injected discontinuity indicators in {PacketCount} packets for {StreamId} (first at offset {Offset})",
                        discontinuityResult.PacketsModified,
                        state.StreamId,
                        discontinuityResult.FirstModifiedOffset
                    );
                }
            }

            // Log detailed keyframe information for debugging
            if (_config.WaitForKeyframe)
            {
                if (alignResult.IsIdrFrame)
                {
                    if (alignResult.HasRequiredParameterSets)
                    {
                        _logger.LogDebugIfEnabled(
                            "Found IDR frame for {StreamId} with all parameter sets (NAL: {NalType}, SPS: {HasSps}, PPS: {HasPps}, VPS: {HasVps})",
                            state.StreamId,
                            alignResult.NalType,
                            alignResult.HasSps,
                            alignResult.HasPps,
                            alignResult.HasVps
                        );
                    }
                    else
                    {
                        // Attempt to inject cached parameter sets to prevent decoder errors
                        var injected = TryInjectCachedParameterSets(state, alignResult, ref alignedDataArray);

                        if (injected)
                        {
                            _logger.PluginLogInformation(
                                "Injected cached parameter sets for {StreamId} (NAL: {NalType}). Decoder should initialize cleanly.",
                                state.StreamId,
                                alignResult.NalType
                            );
                        }
                        else
                        {
                            _logger.PluginLogWarning(
                                "Found IDR frame for {StreamId} but missing parameter sets (NAL: {NalType}, SPS: {HasSps}, PPS: {HasPps}, VPS: {HasVps}). "
                                    + "No cached parameters available. Decoder may show 'non-existing PPS/SPS referenced' errors.",
                                state.StreamId,
                                alignResult.NalType,
                                alignResult.HasSps,
                                alignResult.HasPps,
                                alignResult.HasVps
                            );
                        }
                    }
                }
                else if (alignResult.AlignedToKeyframe)
                {
                    _logger.PluginLogWarning(
                        "Found RAI keyframe but no IDR for {StreamId} (NAL type: {NalType}). "
                            + "Some decoders may show errors until next IDR frame.",
                        state.StreamId,
                        alignResult.NalType
                    );
                }
                else
                {
                    _logger.PluginLogWarning(
                        "No keyframe found for {StreamId} within timeout. "
                            + "Visual artifacts may occur until next keyframe.",
                        state.StreamId
                    );
                }
            }

            // P0 FIX: Return aligned data with the result so caller can inject it into the buffer
            // This is critical - without writing aligned data, consumers receive unaligned/un-remapped data
            var result = CompleteSuccessfulSwitch(
                state,
                newUrl,
                stopwatch.ElapsedMilliseconds,
                timestampRemappingActive,
                alignResult.AlignedToKeyframe,
                alignedDataArray // Pass the processed aligned data for buffer injection
            );

            _logger.PluginLogInformation(
                "Aligned provider switch successful for {StreamId} in {DurationMs}ms (keyframe: {Keyframe}, IDR: {IsIdr}, NAL: {NalType}, remapping: {Remapping}, alignedData: {AlignedBytes} bytes)",
                state.StreamId,
                stopwatch.ElapsedMilliseconds,
                alignResult.AlignedToKeyframe,
                alignResult.IsIdrFrame,
                alignResult.NalType,
                timestampRemappingActive,
                alignedDataArray.Length
            );

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogDebugIfEnabled(
                ex,
                "Aligned switch failed for {StreamId}, will fall back to URL-only switch",
                state.StreamId
            );
            return null; // Fall back to simple URL switch
        }
    }

    /// <summary>
    /// Validates that a new stream contains valid PSI (PAT/PMT) before committing to switch.
    /// Uses FFmpeg demuxer (pooled when available) to detect programs and streams.
    /// Per TR 101 290, PAT must be present within 500ms. We use a configurable timeout.
    /// </summary>
    /// <param name="stream">The stream to validate (will be read from).</param>
    /// <param name="timeoutMs">Maximum time to wait for PSI in milliseconds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Validation result with PSI information.</returns>
    private async Task<PsiValidationResult> ValidatePsiPresenceAsync(
        Stream stream,
        int timeoutMs,
        CancellationToken cancellationToken
    )
    {
        const int PacketSize = TsConstants.PacketSize;
        const int MaxPacketsToScan = 500; // ~10Mbps for 500ms = ~625KB = ~3300 packets, but 500 is enough for PAT
        const int BufferSize = PacketSize * MaxPacketsToScan;

        // Use ArrayPool to avoid ~94KB allocation per provider switch
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeoutMs);

        var totalRead = 0;
        var hasProgram = false;
        var pmtPid = -1;
        var videoPid = -1;
        var audioPid = -1;

        // Get prepared demuxer from pool (avoids SIGSEGV during rapid switches)
        IPooledFFmpegProcessor? demuxer = null;

        if (_demuxerPool != null)
        {
            demuxer = await _demuxerPool.GetPreparedProcessorAsync(cancellationToken: cts.Token).ConfigureAwait(false);
        }

        try
        {
            // Read data until FFmpeg detects programs or timeout
            while (totalRead < BufferSize && !cts.Token.IsCancellationRequested && !hasProgram)
            {
                var bytesRead = await stream
                    .ReadAsync(buffer.AsMemory(totalRead, Math.Min(PacketSize * 50, BufferSize - totalRead)), cts.Token)
                    .ConfigureAwait(false);

                if (bytesRead == 0)
                {
                    break;
                }

                var previousTotal = totalRead;
                totalRead += bytesRead;

                // Feed data to FFmpeg demuxer for program detection
                if (demuxer != null)
                {
                    demuxer.FeedData(buffer.AsSpan(previousTotal, bytesRead));

                    // Process available packets to trigger program detection
                    while (demuxer.Process()) { }

                    // Check if FFmpeg detected any programs
                    if (demuxer.ProgramCount > 0)
                    {
                        hasProgram = true;

                        // Get program info from first detected program
                        foreach (var programNumber in demuxer.GetProgramNumbers())
                        {
                            videoPid = demuxer.GetVideoPid(programNumber);
                            var audioPids = demuxer.GetAudioPids(programNumber);
                            if (audioPids.Length > 0)
                            {
                                audioPid = audioPids[0];
                            }

                            pmtPid = demuxer.GetPmtPid(programNumber);
                            break; // Use first program
                        }
                    }
                }
                else
                {
                    // Fallback: simple PAT detection (just check for PID 0 packets)
                    for (var i = previousTotal; i <= totalRead - PacketSize; i += PacketSize)
                    {
                        if (buffer[i] == TsConstants.SyncByte)
                        {
                            var pid = ((buffer[i + 1] & 0x1F) << 8) | buffer[i + 2];
                            if (pid == TsConstants.PatPid)
                            {
                                hasProgram = true;
                                break;
                            }
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout reached
        }
        finally
        {
            // Return pooled demuxer
            if (demuxer != null)
            {
                _demuxerPool?.ReturnProcessor(demuxer);
            }
        }

        // Return raw data for prepending to aligned stream - critical for ffmpeg demuxer
        // Note: We must copy data before returning the rented buffer
        byte[]? rawData = null;
        if (totalRead > 0)
        {
            rawData = new byte[totalRead];
            Buffer.BlockCopy(buffer, 0, rawData, 0, totalRead);
        }

        // Return buffer to pool after copying data
        ArrayPool<byte>.Shared.Return(buffer);

        // hasPat = true if we found any program (FFmpeg requires PAT to detect programs)
        // hasPmt = true if we found video/audio PIDs
        var hasPat = hasProgram;
        var hasPmt = videoPid > 0 || audioPid > 0;

        return new PsiValidationResult(hasPat, hasPmt, pmtPid, videoPid, audioPid, rawData);
    }

    /// <summary>
    /// Result of PSI (Program Specific Information) validation.
    /// </summary>
    /// <remarks>
    /// Includes the raw data read during validation so it can be prepended to aligned data.
    /// This is critical for ffmpeg: without PAT/PMT, the demuxer cannot identify elementary streams.
    /// </remarks>
    [StructLayout(LayoutKind.Auto)]
    private readonly record struct PsiValidationResult(
        bool HasPat,
        bool HasPmt,
        int PmtPid = -1,
        int VideoPid = -1,
        int AudioPid = -1,
        byte[]? RawData = null
    );

    /// <summary>
    /// Parameter set injection is no longer supported with RAI-based keyframe detection.
    /// Decoders receive SPS/PPS naturally from the stream.
    /// </summary>
    /// <param name="state">The stream state (unused).</param>
    /// <param name="alignResult">The alignment result (unused).</param>
    /// <param name="alignedData">The aligned data (not modified).</param>
    /// <returns>Always returns false as parameter set injection is disabled.</returns>
    private static bool TryInjectCachedParameterSets(
        StreamState state,
        AlignedSwitchResult alignResult,
        ref byte[] alignedData
    )
    {
        // With RAI-based detection, we don't parse NAL units and can't cache parameter sets.
        // Decoders will receive SPS/PPS from the stream naturally.
        _ = state;
        _ = alignResult;
        _ = alignedData;
        return false;
    }

    /// <inheritdoc />
    [Obsolete("Parameter set caching is no longer supported with RAI-based keyframe detection.")]
    public void UpdateParameterSetCache(string streamId, ReadOnlySpan<byte> data)
    {
        // With RAI-based detection, we don't parse NAL units and can't cache parameter sets.
        // This method is kept for interface compatibility but is a no-op.
        _ = streamId;
        _ = data;
    }

    /// <inheritdoc />
    public void UpdateTimingState(string streamId, long videoPts, long audioPts, long pcr)
    {
        // Update last known PTS for provider switch calculations
        // With FFmpeg pipeline, we just track the PTS in stream state
        // The pipeline handles A/V sync internally
        var pts = videoPts > 0 ? videoPts : audioPts;
        if (pts > 0 && _streams.TryGetValue(streamId, out var state))
        {
            state.LastKnownPts = pts;
        }

        _ = pcr; // PCR tracking handled by pipeline
    }

    /// <inheritdoc />
    public bool ActivateReconnectionRemapping(string streamId, ReadOnlySpan<byte> firstDataChunk)
    {
        if (!_streams.TryGetValue(streamId, out var state))
        {
            return false;
        }

        var remuxer = state.PooledRemuxer;

        // With pooled remuxer, we notify it of the reconnection
        // FFmpeg handles A/V sync internally via genpts/igndts flags
        if (remuxer?.IsRunning == true)
        {
            remuxer.NotifyProviderSwitch(state.CurrentUrl, state.CurrentUrl);

            _logger.PluginLogInformation(
                "Activated reconnection remapping for stream {StreamId} via pooled remuxer",
                streamId
            );

            return true;
        }

        // Remuxer was just reset or is still initializing - consider activation complete.
        // The remuxer will reinitialize from scratch with the new stream's data.
        // Returning true here prevents endless retries of activation and allows
        // normal data flow to resume (ProcessDataForRemapping will feed the remuxer).
        if (remuxer != null)
        {
            var firstVideoPts = ExtractFirstVideoPts(firstDataChunk);
            _logger.PluginLogInformation(
                "Reconnection activation for stream {StreamId} (firstPts={FirstPts}) - remuxer will reinitialize from new stream",
                streamId,
                firstVideoPts
            );
            return true;
        }

        // No remuxer configured
        _logger.LogDebugIfEnabled("Reconnection for stream {StreamId} without remuxer configured", streamId);

        return false;
    }

    /// <inheritdoc />
    public void ActivateDiscontinuityRemapping(string streamId, long previousPts, long newPts)
    {
        if (previousPts <= 0 || newPts <= 0)
        {
            _logger.LogDebugIfEnabled(
                "Cannot activate discontinuity remapping for stream {StreamId}: invalid PTS values (prev={PrevPts}, new={NewPts})",
                streamId,
                previousPts,
                newPts
            );
            return;
        }

        // With FFmpeg pipeline, discontinuities are handled automatically
        // Just log the event for diagnostics
        var deltaMs = (newPts - previousPts) / 90.0;
        _logger.PluginLogInformation(
            "Discontinuity detected for stream {StreamId}: PTS jump {DeltaMs:F1}ms (prev={PrevPts}, new={NewPts}) - FFmpeg pipeline handles sync",
            streamId,
            deltaMs,
            previousPts,
            newPts
        );
    }

    /// <summary>
    /// Extracts the first video PTS from MPEG-TS data.
    /// Scans packets for a video PES header with PTS.
    /// </summary>
    private static long ExtractFirstVideoPts(ReadOnlySpan<byte> data)
    {
        const int PacketSize = 188;
        const byte SyncByte = 0x47;

        var offset = 0;
        while (offset + PacketSize <= data.Length)
        {
            if (data[offset] != SyncByte)
            {
                offset++;
                continue;
            }

            var packet = data.Slice(offset, PacketSize);
            var pid = ((packet[1] & 0x1F) << 8) | packet[2];

            // Skip non-video PIDs (PAT, PMT, audio, etc.)
            // Video PIDs are typically in the range 0x100-0x1FFF
            // Common video PIDs: 0x100, 0x101, 0x1011, 0x44, etc.
            // We check for PUSI and PES header to identify video
            var hasPusi = (packet[1] & 0x40) != 0;
            if (!hasPusi || pid == 0 || pid == 0x11 || pid == 0x12)
            {
                offset += PacketSize;
                continue;
            }

            // Get payload offset
            var adaptationControl = (packet[3] >> 4) & 0x03;
            var payloadOffset = 4;
            if (adaptationControl >= 2 && packet[4] < 183)
            {
                payloadOffset += 1 + packet[4]; // Skip adaptation field
            }

            if (payloadOffset >= PacketSize - 9)
            {
                offset += PacketSize;
                continue;
            }

            var payload = packet[payloadOffset..];

            // Check for PES header (starts with 0x00 0x00 0x01)
            if (payload.Length < 14 || payload[0] != 0x00 || payload[1] != 0x00 || payload[2] != 0x01)
            {
                offset += PacketSize;
                continue;
            }

            // Check stream ID for video (0xE0-0xEF)
            if (payload[3] is < 0xE0 or > 0xEF)
            {
                offset += PacketSize;
                continue;
            }

            // Check PTS flags (bits 7-6 of byte 7)
            var ptsFlags = (payload[7] >> 6) & 0x03;
            if (ptsFlags < 2 || payload.Length < 14)
            {
                offset += PacketSize;
                continue;
            }

            // Extract PTS (33 bits)
            return (((long)(payload[9] >> 1) & 0x07) << 30)
                | (((long)payload[10]) << 22)
                | (((long)(payload[11] >> 1)) << 15)
                | (((long)payload[12]) << 7)
                | ((long)(payload[13] >> 1));
        }

        return 0;
    }

    private ProviderSwitchResult CompleteSuccessfulSwitch(
        StreamState state,
        string newUrl,
        long durationMs,
        bool timestampRemappingActive,
        bool alignedToKeyframe,
        byte[]? alignedData = null
    )
    {
        state.CurrentUrl = newUrl;
        state.LastSwitchTime = DateTime.UtcNow;

        _ = Interlocked.Increment(ref _successfulSwitches);
        _ = Interlocked.Add(ref _totalSwitchLatencyMs, durationMs);

        _logger.PluginLogInformation(
            "Provider switch successful for {StreamId} in {DurationMs}ms (aligned data: {AlignedDataSize} bytes)",
            state.StreamId,
            durationMs,
            alignedData?.Length ?? 0
        );

        return ProviderSwitchResult.Succeeded(
            newUrl,
            durationMs,
            timestampRemappingActive,
            alignedToKeyframe,
            alignedData
        );
    }

    private ProviderSwitchResult CompleteFailedSwitch(
        StreamState state,
        SwitchFailureReason reason,
        string? message = null
    )
    {
        _ = Interlocked.Increment(ref _failedSwitches);

        _logger.PluginLogWarning(
            "Provider switch failed for {StreamId}: {Reason} - {Message}",
            state.StreamId,
            reason,
            message ?? "No details"
        );

        return ProviderSwitchResult.Failed(reason, message);
    }

    /// <summary>
    /// Extracts the provider ID from a stream URL by matching against known providers.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private string? ExtractProviderId(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        try
        {
            var uri = new Uri(url);
            var pluginConfig = _configProvider.GetConfiguration();
            if (pluginConfig == null)
            {
                return null;
            }

            // Match URL host against known providers
            foreach (var provider in pluginConfig.Providers)
            {
                if (string.IsNullOrEmpty(provider.BaseUrl))
                {
                    continue;
                }

                try
                {
                    var providerUri = new Uri(provider.BaseUrl);
                    if (
                        uri.Host.Equals(providerUri.Host, StringComparison.OrdinalIgnoreCase)
                        && uri.Port == providerUri.Port
                    )
                    {
                        return provider.Id;
                    }
                }
                catch
                {
                    // Skip invalid provider URLs
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ProcessDataForRemapping(string streamId, ReadOnlySpan<byte> input, out byte[]? output)
    {
        output = null;

        if (input.IsEmpty || !_streams.TryGetValue(streamId, out var state))
        {
            return false;
        }

        // If remuxer was disabled due to repeated failures, bypass it entirely
        if (state.RemuxerDisabled)
        {
            return false;
        }

        var remuxer = state.PooledRemuxer;
        if (remuxer == null)
        {
            return false;
        }

        // With the pooled remuxer, data is queued for background processing.
        // This is NON-BLOCKING - the background worker handles FFmpeg initialization
        // and processing, avoiding the 0.5 second blocking during avformat_find_stream_info.
        if (!remuxer.TryQueueData(input))
        {
            // Queue full or remuxer unhealthy - log periodically
            _logger.LogDebugIfEnabled(
                "Failed to queue data for stream {StreamId} (queue: {QueuedBytes}KB, healthy: {IsHealthy})",
                streamId,
                remuxer.QueuedBytes / 1024,
                remuxer.IsHealthy
            );

            // CRITICAL FIX: Even though we couldn't queue the input, the pipeline IS active.
            // We must return true with null output so the caller skips writing (continues).
            // Returning false would cause the caller to write RAW (un-remuxed) data to the
            // buffer, which has source timestamps and causes playback loops/glitches.
            // Try to return any available output from previous processing.
            _ = remuxer.TryReadOutput(out output);
            return true;
        }

        // Try to read any available output (also non-blocking).
        // Always return true because the pipeline IS active - the remuxer is configured
        // and data was successfully queued. The caller will check if output has data
        // and skip writing if there's no output yet (still initializing or buffering).
        _ = remuxer.TryReadOutput(out output);
        return true;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsRemappingActive(string streamId)
    {
        return _streams.TryGetValue(streamId, out var state) && state.PooledRemuxer?.IsRunning == true;
    }

    /// <inheritdoc />
    public void ResetRemuxer(string streamId)
    {
        if (!_streams.TryGetValue(streamId, out var state))
        {
            return;
        }

        var remuxer = state.PooledRemuxer;
        if (remuxer == null)
        {
            return;
        }

        var stats = remuxer.Statistics;

        // PrepareForReuse clears queues and resets the inner remuxer
        remuxer.PrepareForReuse();

        // Reset the assignment time so health check gives the remuxer fresh time to initialize
        state.RemuxerAssignedTicks = Environment.TickCount64;

        _logger.PluginLogInformation(
            "Reset pooled remuxer for stream {StreamId} (was running: {WasRunning}, bytes: {BytesWritten}KB in / {BytesRead}KB out, switches: {Switches})",
            streamId,
            stats.IsRunning,
            stats.BytesWritten / 1024,
            stats.BytesRead / 1024,
            stats.ProviderSwitches
        );
    }

    /// <inheritdoc />
    public bool IsRemuxerHealthy(string streamId)
    {
        if (!_streams.TryGetValue(streamId, out var state))
        {
            return true; // No stream registered - considered healthy
        }

        var remuxer = state.PooledRemuxer;
        if (remuxer is not { IsRunning: true })
        {
            return true; // No remuxer or not yet initialized - considered healthy
        }

        // TIME-BASED CHECK: FFmpeg initialization requires wall-clock time regardless of bitrate.
        // At high bitrates (e.g., 9 Mbps), bytes flow fast but FFmpeg still needs CPU time to:
        // - Parse PAT/PMT tables
        // - Detect and initialize codec contexts
        // - Fill internal buffers before producing output
        // Require at least 5 seconds before declaring a remuxer stuck.
        const long MinInitTimeMs = 5000; // 5 seconds minimum
        var elapsedMs = Environment.TickCount64 - state.RemuxerAssignedTicks;
        if (elapsedMs < MinInitTimeMs)
        {
            return true; // Not enough time elapsed - give FFmpeg more time
        }

        var stats = remuxer.Statistics;

        // FFmpeg stream detection requirements (from ffmpeg-formats documentation):
        // - probesize: default 5,000,000 bytes (5MB) - bytes needed for stream detection
        // We use FFmpeg's probesize as the minimum data threshold before judging remuxer health.
        // If the remuxer has consumed >= 5MB with output ratio < 5%, it's stuck.

        // FFmpeg-based constants
        const long FfmpegDefaultProbeSize = 5 * 1024 * 1024; // 5MB - FFmpeg's default probesize
        const double MinOutputRatio = 0.05; // 5% minimum output ratio

        // PRIORITY 1: Check if remuxer is stuck BEFORE stall detection.
        // If we've consumed significant data (>5MB) with terrible output ratio (<5%),
        // the remuxer is stuck regardless of current throughput. This catches the case where
        // the remuxer was already stuck before a stall occurred.
        if (stats.BytesWritten >= FfmpegDefaultProbeSize)
        {
            if (stats.BytesRead == 0)
            {
                return false; // Stuck - consumed 5MB+ but producing nothing
            }

            var outputRatio = (double)stats.BytesRead / stats.BytesWritten;
            if (outputRatio < MinOutputRatio)
            {
                return false; // Stuck - output ratio below 5% after consuming 5MB+
            }
        }

        // If we reach here, either:
        // - BytesWritten < 5MB (not enough data to judge yet), or
        // - BytesWritten >= 5MB with acceptable ratio (passed PRIORITY 1)
        // In both cases, the remuxer is considered healthy.
        return true;
    }

    /// <inheritdoc />
    public void DisableRemuxer(string streamId)
    {
        if (!_streams.TryGetValue(streamId, out var state))
        {
            return;
        }

        state.RemuxerDisabled = true;
        _logger.PluginLogWarning(
            "Remuxer DISABLED for stream {StreamId} - raw data will be used for this session",
            streamId
        );
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var kvp in _streams)
        {
            kvp.Value.Dispose();
        }

        _streams.Clear();
    }

    /// <summary>
    /// Per-stream state for switch management.
    /// </summary>
    private sealed class StreamState : IDisposable
    {
        private long _lastAttemptTicks;
        private IPooledFFmpegProcessor? _pooledRemuxer;

        public required string StreamId { get; init; }
        public string CurrentUrl { get; set; } = string.Empty;
        public DateTime LastSwitchTime { get; set; } = DateTime.MinValue;
        public int SwitchCount { get; private set; }
        public SemaphoreSlim SwitchLock { get; } = new(1, 1);
        public required AlignedStreamSwitcher StreamSwitcher { get; init; }
        public required StreamQualityMonitor QualityMonitor { get; init; }

        /// <summary>Gets or sets the pooled processor for A/V sync (non-blocking background processing).</summary>
        public IPooledFFmpegProcessor? PooledRemuxer
        {
            get => Volatile.Read(ref _pooledRemuxer);
            set => Volatile.Write(ref _pooledRemuxer, value);
        }

        /// <summary>Gets or sets the remuxer pool reference for returning the processor on dispose.</summary>
        public IFFmpegProcessorPool? RemuxerPool { get; set; }

        /// <summary>Gets or sets the last known video PID from PSI validation.</summary>
        public int LastVideoPid { get; set; } = -1;

        /// <summary>Gets or sets the last known audio PID from PSI validation.</summary>
        public int LastAudioPid { get; set; } = -1;

        /// <summary>Gets or sets the last known PMT PID from PSI validation.</summary>
        public int LastPmtPid { get; set; } = -1;

        /// <summary>Gets or sets the last known PTS for timestamp offset calculation.</summary>
        public long LastKnownPts { get; set; }

        /// <summary>Gets or sets whether the remuxer has been disabled for this stream session due to failures.</summary>
        public bool RemuxerDisabled { get; set; }

        /// <summary>Gets or sets the tick count when the remuxer was assigned (for time-based health checks).</summary>
        public long RemuxerAssignedTicks { get; set; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsCooldownActive(int cooldownMs)
        {
            var lastTicks = Interlocked.Read(ref _lastAttemptTicks);
            return lastTicks != 0 && (Environment.TickCount64 - lastTicks) < cooldownMs;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsMaxAttemptsReached(int maxAttempts) => SwitchCount >= maxAttempts;

        public void RecordAttempt()
        {
            _ = Interlocked.Exchange(ref _lastAttemptTicks, Environment.TickCount64);
            SwitchCount++;
        }

        public void Dispose()
        {
            SwitchLock.Dispose();

            // Return pooled processor to pool instead of disposing directly
            var remuxer = Interlocked.Exchange(ref _pooledRemuxer, null);
            if (remuxer != null)
            {
                RemuxerPool?.ReturnProcessor(remuxer);
            }
        }
    }
}
