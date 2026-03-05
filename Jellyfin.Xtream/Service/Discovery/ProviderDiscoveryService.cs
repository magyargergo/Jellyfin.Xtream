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
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Xtream.Service.Discovery.Pipeline;
using Jellyfin.Xtream.Utility;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.Discovery;

/// <summary>
/// Service that orchestrates credential discovery and testing.
/// Supports both synchronous and async (streaming) modes for progress updates.
/// </summary>
public sealed class ProviderDiscoveryService : IProviderDiscoveryService, IDisposable
{
    private const string CacheFileName = "discovery-cache.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ICredentialParser _parser;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ProviderDiscoveryService> _logger;
    private readonly ILogger<WebCredentialSource> _sourceLogger;
    private readonly string _cachePath;

    private CancellationTokenSource? _currentCts;
    private readonly object _lock = new();

    // Progress channel for streaming updates to clients
    private Channel<DiscoveryProgress>? _progressChannel;
    private DiscoveryProgress? _currentProgress;
    private DiscoveryTestResult? _lastResult;
    private Task? _backgroundTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderDiscoveryService"/> class.
    /// </summary>
    /// <param name="parser">The credential parser.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="sourceLogger">The credential source logger.</param>
    public ProviderDiscoveryService(
        ICredentialParser parser,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        IApplicationPaths applicationPaths,
        ILogger<ProviderDiscoveryService> logger,
        ILogger<WebCredentialSource> sourceLogger
    )
    {
        _parser = parser;
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _sourceLogger = sourceLogger;
        _cachePath = Path.Combine(applicationPaths.PluginConfigurationsPath, "Jellyfin.Xtream", CacheFileName);

        // Try to load cached results on startup
        LoadCachedResult();
    }

    /// <inheritdoc />
    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _currentCts?.IsCancellationRequested == false;
            }
        }
    }

    /// <inheritdoc />
    public void Cancel()
    {
        lock (_lock)
        {
            _currentCts?.Cancel();
        }
    }

    /// <inheritdoc />
    public DiscoveryProgress? GetCurrentProgress()
    {
        lock (_lock)
        {
            return _currentProgress;
        }
    }

    /// <inheritdoc />
    public DiscoveryTestResult? GetLastResult()
    {
        lock (_lock)
        {
            return _lastResult;
        }
    }

    /// <inheritdoc />
    public bool StartDiscoveryAsync(DiscoveryOptions options)
    {
        lock (_lock)
        {
            if (_currentCts?.IsCancellationRequested == false)
            {
                return false; // Already running
            }

            _currentCts?.Dispose();
            _currentCts = new CancellationTokenSource();

            // Create a bounded channel to prevent memory issues if client is slow
            _progressChannel = Channel.CreateBounded<DiscoveryProgress>(
                new BoundedChannelOptions(100)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = false,
                    SingleWriter = true,
                }
            );

            _currentProgress = new DiscoveryProgress { Phase = DiscoveryPhase.Discovering, Message = "Starting..." };

            _lastResult = null;

            // Start the background task
            var cts = _currentCts;
            var channel = _progressChannel;
            _backgroundTask = Task.Run(async () =>
                await RunDiscoveryAsync(options, channel.Writer, cts.Token).ConfigureAwait(false)
            );

            return true;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<DiscoveryProgress> GetProgressUpdatesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        Channel<DiscoveryProgress>? channel;
        lock (_lock)
        {
            channel = _progressChannel;
        }

        if (channel == null)
        {
            // No operation running, return current state
            var progress = GetCurrentProgress();
            if (progress != null)
            {
                yield return progress;
            }

            yield break;
        }

        // Read from the channel until it completes or is cancelled
        await foreach (var progress in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return progress;
        }
    }

    private async Task RunDiscoveryAsync(
        DiscoveryOptions options,
        ChannelWriter<DiscoveryProgress> writer,
        CancellationToken cancellationToken
    )
    {
        var result = new DiscoveryTestResult();

        // Helper to update progress
        void UpdateProgress(DiscoveryProgress p)
        {
            lock (_lock)
            {
                _currentProgress = p;
            }

            _ = writer.TryWrite(p);
        }

        try
        {
            var startDate = options.GetStartDate();
            var endDate = options.GetEndDate();

            _logger.PluginLogInformation(
                "Starting pipeline discovery: TimeRange={TimeRange} ({StartDate:yyyy-MM-dd} to {EndDate:yyyy-MM-dd}), DiscoveryWorkers={DiscoveryWorkers}",
                options.TimeRange,
                startDate,
                endDate,
                options.MaxDiscoveryWorkers
            );

            // Phase 1: Discover credentials using the web credential source
            var httpClient = _httpClientFactory.CreateClient("XtreamClient");
            using var source = new WebCredentialSource(httpClient, _parser, _sourceLogger);

            // Report discovery phase progress
            var discoveryProgress = new Progress<DiscoveryProgress>(p =>
            {
                p.Phase = DiscoveryPhase.Discovering;
                UpdateProgress(p);
            });

            var discoveryResult = await source
                .DiscoverAsync(startDate, endDate, options.MaxDiscoveryWorkers, discoveryProgress, cancellationToken)
                .ConfigureAwait(false);

            result.DiscoveryResult = discoveryResult;

            if (!discoveryResult.Success || discoveryResult.Credentials.Count == 0)
            {
                result.ErrorMessage = discoveryResult.ErrorMessage ?? "No credentials found";
                UpdateProgress(new DiscoveryProgress { Phase = DiscoveryPhase.Failed, Message = result.ErrorMessage });
                lock (_lock)
                {
                    _lastResult = result;
                }

                return;
            }

            _logger.PluginLogInformation(
                "Discovered {Count} credentials from {Pages} pages, starting pipeline",
                discoveryResult.Credentials.Count,
                discoveryResult.PagesProcessed
            );

            // Phase 2: Run the pipeline
            UpdateProgress(
                new DiscoveryProgress
                {
                    Phase = DiscoveryPhase.Testing,
                    Message = "Starting pipeline...",
                    CredentialsFound = discoveryResult.Credentials.Count,
                    TotalItems = discoveryResult.Credentials.Count,
                }
            );

            var pipeline = DiscoveryPipeline.Create(_httpClientFactory, _loggerFactory, options.CountryCode);
            try
            {
                // Start the pipeline
                var pipelineTask = pipeline.RunAsync(discoveryResult.Credentials, cancellationToken);

                // Stream progress updates while pipeline runs
                await foreach (
                    var pipelineProgress in pipeline.StreamProgressAsync(cancellationToken).ConfigureAwait(false)
                )
                {
                    // Map pipeline progress to discovery progress
                    var dp = new DiscoveryProgress
                    {
                        Phase = DiscoveryPhase.Testing,
                        CurrentItem = pipelineProgress.CompletedItems,
                        TotalItems = pipelineProgress.TotalItems,
                        CredentialsFound = discoveryResult.Credentials.Count,
                        ConnectivityPassed = (int)pipelineProgress.ConnectivityPassed,
                        AuthenticationPassed = (int)pipelineProgress.AuthenticationPassed,
                        WorkingProviders = (int)pipelineProgress.StreamTestPassed,
                        WorkingWithEpg = (int)pipelineProgress.EpgCheckPassed,
                        FullyWorking = (int)pipelineProgress.CountryFilterPassed,
                        ExcellentProviders = (int)pipelineProgress.QualityScoringPassed,
                        InProgress = (int)pipelineProgress.TotalInProgress,
                        IsComplete = pipelineProgress.IsComplete,
                        Message =
                            $"Pipeline: {pipelineProgress.CompletedItems}/{pipelineProgress.TotalItems} processed",
                    };

                    UpdateProgress(dp);
                }

                // Wait for pipeline to complete
                await pipelineTask.ConfigureAwait(false);

                // Collect results from pipeline
                var testResults = pipeline.GetCompletedResults().ToList();

                result.TestResults = testResults;

                // Categorize results as a funnel
                result.WorkingProviders =
                [
                    .. OrderByPreference(testResults.Where(r => r.Status == ProviderStatus.Active && r.StreamWorks)),
                ];

                result.WorkingWithEpgProviders =
                [
                    .. OrderByPreference(
                        testResults.Where(r => r.Status == ProviderStatus.Active && r.StreamWorks && r.HasEpg)
                    ),
                ];

                result.FullyWorkingProviders = [.. OrderByPreference(testResults.Where(r => r.IsFullyWorking))];

                result.ExcellentProviders = [.. OrderByPreference(testResults.Where(r => r.IsExcellent))];

                result.Success = true;

                // Save to cache
                SaveCachedResult(result);

                // Report completion
                var completeProgress = new DiscoveryProgress
                {
                    Phase = DiscoveryPhase.Completed,
                    CurrentItem = testResults.Count,
                    TotalItems = discoveryResult.Credentials.Count,
                    CredentialsFound = discoveryResult.Credentials.Count,
                    ConnectivityPassed = (int)(pipeline.GetStageStats(PipelineStage.Connectivity)?.Passed ?? 0),
                    AuthenticationPassed = (int)(pipeline.GetStageStats(PipelineStage.Authentication)?.Passed ?? 0),
                    WorkingProviders = result.WorkingProviders.Count,
                    WorkingWithEpg = result.WorkingWithEpgProviders.Count,
                    FullyWorking = result.FullyWorkingProviders.Count,
                    ExcellentProviders = result.ExcellentProviders.Count,
                    InProgress = 0,
                    IsComplete = true,
                    Message =
                        $"Complete: {result.WorkingProviders.Count} working, {result.FullyWorkingProviders.Count} with country channels, {result.ExcellentProviders.Count} excellent",
                };
                UpdateProgress(completeProgress);

                lock (_lock)
                {
                    _lastResult = result;
                }

                _logger.PluginLogInformation(
                    "Pipeline complete: {Total} processed, {Working} working, {WithEpg} with EPG, {Full} fully working, {Excellent} excellent",
                    testResults.Count,
                    result.WorkingProviders.Count,
                    result.WorkingWithEpgProviders.Count,
                    result.FullyWorkingProviders.Count,
                    result.ExcellentProviders.Count
                );
            }
            finally
            {
                await pipeline.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            result.ErrorMessage = "Operation was cancelled";
            UpdateProgress(new DiscoveryProgress { Phase = DiscoveryPhase.Failed, Message = "Operation cancelled" });
            lock (_lock)
            {
                _lastResult = result;
            }
        }
        catch (Exception ex)
        {
            if (ex is HttpRequestException httpEx && IsExpectedConnectionFailure(httpEx))
            {
                _logger.LogDebugIfEnabled("Discovery pipeline failed: {Message}", ex.Message);
            }
            else
            {
                _logger.PluginLogError(ex, "Discovery pipeline failed");
            }

            result.ErrorMessage = ex.Message;
            UpdateProgress(new DiscoveryProgress { Phase = DiscoveryPhase.Failed, Message = $"Error: {ex.Message}" });
            lock (_lock)
            {
                _lastResult = result;
            }
        }
        finally
        {
            writer.Complete();

            lock (_lock)
            {
                _currentCts?.Dispose();
                _currentCts = null;
                _progressChannel = null;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            _currentCts?.Cancel();
            _currentCts?.Dispose();
            _currentCts = null;
            _ = (_progressChannel?.Writer.TryComplete());
            _progressChannel = null;
        }
    }

    private static bool IsExpectedConnectionFailure(HttpRequestException ex)
    {
        // Check for common expected failures that don't need stack traces
        var message = ex.Message.ToUpperInvariant();

        // Connection refused, host not found, network unreachable
        if (
            message.Contains("CONNECTION REFUSED", StringComparison.Ordinal)
            || message.Contains("NO SUCH HOST", StringComparison.Ordinal)
            || message.Contains("HOST NOT FOUND", StringComparison.Ordinal)
            || message.Contains("NAME OR SERVICE NOT KNOWN", StringComparison.Ordinal)
            || message.Contains("NETWORK IS UNREACHABLE", StringComparison.Ordinal)
            || message.Contains("NODENAME NOR SERVNAME", StringComparison.Ordinal)
            || message.Contains("ACTIVELY REFUSED", StringComparison.Ordinal)
        )
        {
            return true;
        }

        // Check HTTP status codes that are expected failures
        if (ex.StatusCode.HasValue)
        {
            var code = (int)ex.StatusCode.Value;
            // 404 Not Found, 401 Unauthorized, 403 Forbidden are expected for invalid providers
            return code is 404 or 401 or 403 or 406;
        }

        return false;
    }

    /// <summary>
    /// Calculates days until expiration for sorting purposes.
    /// Returns int.MaxValue for null expiration (never expires).
    /// Returns 0 for already expired.
    /// </summary>
    private static int GetDaysUntilExpiration(DateTime? expirationDate)
    {
        if (!expirationDate.HasValue)
        {
            return int.MaxValue; // Never expires, sort last
        }

        var days = (expirationDate.Value - DateTime.UtcNow).Days;
        return days < 0 ? 0 : days;
    }

    /// <summary>
    /// Orders providers by preference using comprehensive trust score:
    /// 1. Trust score (descending - higher trust first)
    /// 2. Quality score (descending - higher quality first)
    /// 3. Country channel count (descending - more country channels first)
    /// 4. Days until expiration (descending - longer validity first).
    /// </summary>
    private static IOrderedEnumerable<ProviderTestResult> OrderByPreference(IEnumerable<ProviderTestResult> providers)
    {
        return providers
            .OrderByDescending(r => r.TrustScore?.Score ?? 0)
            .ThenByDescending(r => r.StreamQuality?.QualityScore ?? 0)
            .ThenByDescending(r => r.CountryChannelCount)
            .ThenByDescending(r => GetDaysUntilExpiration(r.ExpirationDate));
    }

    private void LoadCachedResult()
    {
        try
        {
            if (!File.Exists(_cachePath))
            {
                return;
            }

            var json = File.ReadAllText(_cachePath);
            var cached = JsonSerializer.Deserialize<DiscoveryTestResult>(json);

            if (cached != null)
            {
                lock (_lock)
                {
                    _lastResult = cached;
                }

                _logger.PluginLogInformation(
                    "Loaded cached discovery results: {Working} working, {WithEpg} with EPG, {Full} fully working",
                    cached.WorkingProviders.Count,
                    cached.WorkingWithEpgProviders.Count,
                    cached.FullyWorkingProviders.Count
                );
            }
        }
        catch (Exception ex)
        {
            _logger.PluginLogWarning(ex, "Failed to load cached discovery results from {Path}", _cachePath);
        }
    }

    private void SaveCachedResult(DiscoveryTestResult result)
    {
        try
        {
            var directory = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                _ = Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(result, JsonOptions);
            File.WriteAllText(_cachePath, json);

            _logger.LogDebugIfEnabled("Saved discovery results to cache: {Path}", _cachePath);
        }
        catch (Exception ex)
        {
            _logger.PluginLogWarning(ex, "Failed to save discovery results to cache: {Path}", _cachePath);
        }
    }

    /// <inheritdoc />
    public bool ClearCache()
    {
        lock (_lock)
        {
            _lastResult = null;
            _currentProgress = null;
        }

        try
        {
            if (File.Exists(_cachePath))
            {
                File.Delete(_cachePath);
                _logger.PluginLogInformation("Cleared discovery cache: {Path}", _cachePath);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.PluginLogWarning(ex, "Failed to delete discovery cache: {Path}", _cachePath);
            return false;
        }
    }
}
