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

    private readonly IProviderTester _tester;
    private readonly ICredentialParser _parser;
    private readonly IHttpClientFactory _httpClientFactory;
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
    /// <param name="tester">The provider tester.</param>
    /// <param name="parser">The credential parser.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="sourceLogger">The credential source logger.</param>
    public ProviderDiscoveryService(
        IProviderTester tester,
        ICredentialParser parser,
        IHttpClientFactory httpClientFactory,
        IApplicationPaths applicationPaths,
        ILogger<ProviderDiscoveryService> logger,
        ILogger<WebCredentialSource> sourceLogger
    )
    {
        _tester = tester;
        _parser = parser;
        _httpClientFactory = httpClientFactory;
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
                return _currentCts != null && !_currentCts.IsCancellationRequested;
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
            if (_currentCts != null && !_currentCts.IsCancellationRequested)
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

        // Create progress reporter that writes to channel
        var progress = new Progress<DiscoveryProgress>(p =>
        {
            lock (_lock)
            {
                _currentProgress = p;
            }

            // Try to write, ignore if channel is full (DropOldest will handle it)
            writer.TryWrite(p);
        });

        try
        {
            _logger.LogInformation(
                "Starting discovery and test operation: MaxPages={MaxPages}, DiscoveryWorkers={DiscoveryWorkers}, TestWorkers={TestWorkers}",
                options.MaxPages,
                options.MaxDiscoveryWorkers,
                options.MaxTestWorkers
            );

            // Phase 1: Discover credentials using the web credential source
            var httpClient = _httpClientFactory.CreateClient("XtreamClient");
            using var source = new WebCredentialSource(httpClient, _parser, _sourceLogger);

            var discoveryResult = await source
                .DiscoverAsync(options.MaxPages, options.MaxDiscoveryWorkers, progress, cancellationToken)
                .ConfigureAwait(false);

            result.DiscoveryResult = discoveryResult;

            if (!discoveryResult.Success || discoveryResult.Credentials.Count == 0)
            {
                result.ErrorMessage = discoveryResult.ErrorMessage ?? "No credentials found";
                var failProgress = new DiscoveryProgress
                {
                    Phase = DiscoveryPhase.Failed,
                    Message = result.ErrorMessage,
                };
                writer.TryWrite(failProgress);
                lock (_lock)
                {
                    _currentProgress = failProgress;
                    _lastResult = result;
                }

                return;
            }

            _logger.LogInformation(
                "Discovered {Count} credentials from {Pages} pages",
                discoveryResult.Credentials.Count,
                discoveryResult.PagesProcessed
            );

            // Phase 2: Test credentials
            var testResults = await _tester
                .TestCredentialsAsync(
                    discoveryResult.Credentials,
                    options.MaxTestWorkers,
                    options.TestStream,
                    options.TestEpg,
                    progress,
                    cancellationToken
                )
                .ConfigureAwait(false);

            result.TestResults = testResults.ToList();

            // Categorize results as a funnel: Working -> Working+EPG -> Working+EPG+Polish
            // Order by: Polish channel count (descending), days until expiration (descending), total channels (descending)
            result.WorkingProviders = OrderByPreference(
                    testResults.Where(r => r.Status == ProviderStatus.Active && r.StreamWorks)
                )
                .ToList();

            result.WorkingWithEpgProviders = OrderByPreference(
                    testResults.Where(r => r.Status == ProviderStatus.Active && r.StreamWorks && r.HasEpg)
                )
                .ToList();

            result.FullyWorkingProviders = OrderByPreference(testResults.Where(r => r.IsFullyWorking)).ToList();

            result.Success = true;

            // Save to cache
            SaveCachedResult(result);

            // Report completion
            var completeProgress = new DiscoveryProgress
            {
                Phase = DiscoveryPhase.Completed,
                CurrentItem = testResults.Count,
                TotalItems = testResults.Count,
                CredentialsFound = discoveryResult.Credentials.Count,
                WorkingProviders = result.WorkingProviders.Count,
                WorkingWithEpg = result.WorkingWithEpgProviders.Count,
                FullyWorking = result.FullyWorkingProviders.Count,
                Message =
                    $"Complete: {result.WorkingProviders.Count} working, {result.WorkingWithEpgProviders.Count} with EPG, {result.FullyWorkingProviders.Count} with Polish",
            };
            writer.TryWrite(completeProgress);

            lock (_lock)
            {
                _currentProgress = completeProgress;
                _lastResult = result;
            }

            _logger.LogInformation(
                "Discovery and test complete: {Total} tested, {Working} working, {WithEpg} with EPG, {Full} fully working",
                testResults.Count,
                result.WorkingProviders.Count,
                result.WorkingWithEpgProviders.Count,
                result.FullyWorkingProviders.Count
            );
        }
        catch (OperationCanceledException)
        {
            result.ErrorMessage = "Operation was cancelled";
            var cancelProgress = new DiscoveryProgress
            {
                Phase = DiscoveryPhase.Failed,
                Message = "Operation cancelled",
            };
            writer.TryWrite(cancelProgress);
            lock (_lock)
            {
                _currentProgress = cancelProgress;
                _lastResult = result;
            }
        }
        catch (Exception ex)
        {
            // Log at debug level for expected failures to reduce noise
            if (ex is HttpRequestException httpEx && IsExpectedConnectionFailure(httpEx))
            {
                _logger.LogDebug("Discovery and test operation failed: {Message}", ex.Message);
            }
            else
            {
                _logger.LogError(ex, "Discovery and test operation failed");
            }

            result.ErrorMessage = ex.Message;
            var errorProgress = new DiscoveryProgress
            {
                Phase = DiscoveryPhase.Failed,
                Message = $"Error: {ex.Message}",
            };
            writer.TryWrite(errorProgress);
            lock (_lock)
            {
                _currentProgress = errorProgress;
                _lastResult = result;
            }
        }
        finally
        {
            // Complete the channel
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
    public async Task<DiscoveryTestResult> DiscoverAndTestAsync(
        DiscoveryOptions options,
        IProgress<DiscoveryProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        var result = new DiscoveryTestResult();

        // Create linked cancellation token
        CancellationTokenSource cts;
        lock (_lock)
        {
            if (_currentCts != null && !_currentCts.IsCancellationRequested)
            {
                result.ErrorMessage = "A discovery operation is already in progress";
                return result;
            }

            _currentCts?.Dispose();
            _currentCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts = _currentCts;
        }

        try
        {
            _logger.LogInformation(
                "Starting discovery and test operation: MaxPages={MaxPages}, DiscoveryWorkers={DiscoveryWorkers}, TestWorkers={TestWorkers}",
                options.MaxPages,
                options.MaxDiscoveryWorkers,
                options.MaxTestWorkers
            );

            // Phase 1: Discover credentials using the web credential source
            var httpClient = _httpClientFactory.CreateClient("XtreamClient");
            using var source = new WebCredentialSource(httpClient, _parser, _sourceLogger);

            var discoveryResult = await source
                .DiscoverAsync(options.MaxPages, options.MaxDiscoveryWorkers, progress, cts.Token)
                .ConfigureAwait(false);

            result.DiscoveryResult = discoveryResult;

            if (!discoveryResult.Success || discoveryResult.Credentials.Count == 0)
            {
                result.ErrorMessage = discoveryResult.ErrorMessage ?? "No credentials found";
                return result;
            }

            _logger.LogInformation(
                "Discovered {Count} credentials from {Pages} pages",
                discoveryResult.Credentials.Count,
                discoveryResult.PagesProcessed
            );

            // Phase 2: Test credentials
            var testResults = await _tester
                .TestCredentialsAsync(
                    discoveryResult.Credentials,
                    options.MaxTestWorkers,
                    options.TestStream,
                    options.TestEpg,
                    progress,
                    cts.Token
                )
                .ConfigureAwait(false);

            result.TestResults = testResults.ToList();

            // Categorize results as a funnel: Working -> Working+EPG -> Working+EPG+Polish
            // Order by: Polish channel count (descending), days until expiration (descending), total channels (descending)
            result.WorkingProviders = OrderByPreference(
                    testResults.Where(r => r.Status == ProviderStatus.Active && r.StreamWorks)
                )
                .ToList();

            result.WorkingWithEpgProviders = OrderByPreference(
                    testResults.Where(r => r.Status == ProviderStatus.Active && r.StreamWorks && r.HasEpg)
                )
                .ToList();

            result.FullyWorkingProviders = OrderByPreference(testResults.Where(r => r.IsFullyWorking)).ToList();

            result.Success = true;

            // Save to cache
            SaveCachedResult(result);

            // Report completion
            progress?.Report(
                new DiscoveryProgress
                {
                    Phase = DiscoveryPhase.Completed,
                    CurrentItem = testResults.Count,
                    TotalItems = testResults.Count,
                    CredentialsFound = discoveryResult.Credentials.Count,
                    WorkingProviders = result.WorkingProviders.Count,
                    WorkingWithEpg = result.WorkingWithEpgProviders.Count,
                    FullyWorking = result.FullyWorkingProviders.Count,
                    Message =
                        $"Complete: {result.WorkingProviders.Count} working, {result.WorkingWithEpgProviders.Count} with EPG, {result.FullyWorkingProviders.Count} with Polish",
                }
            );

            _logger.LogInformation(
                "Discovery and test complete: {Total} tested, {Working} working, {WithEpg} with EPG, {Full} fully working",
                testResults.Count,
                result.WorkingProviders.Count,
                result.WorkingWithEpgProviders.Count,
                result.FullyWorkingProviders.Count
            );
        }
        catch (OperationCanceledException)
        {
            result.ErrorMessage = "Operation was cancelled";
            progress?.Report(new DiscoveryProgress { Phase = DiscoveryPhase.Failed, Message = "Operation cancelled" });
            throw;
        }
        catch (Exception ex)
        {
            // Log at debug level for expected failures to reduce noise
            if (ex is HttpRequestException httpEx && IsExpectedConnectionFailure(httpEx))
            {
                _logger.LogDebug("Discovery and test operation failed: {Message}", ex.Message);
            }
            else
            {
                _logger.LogError(ex, "Discovery and test operation failed");
            }

            result.ErrorMessage = ex.Message;
            progress?.Report(new DiscoveryProgress { Phase = DiscoveryPhase.Failed, Message = $"Error: {ex.Message}" });
        }
        finally
        {
            lock (_lock)
            {
                if (_currentCts == cts)
                {
                    _currentCts.Dispose();
                    _currentCts = null;
                }
            }
        }

        return result;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            _currentCts?.Cancel();
            _currentCts?.Dispose();
            _currentCts = null;
            _progressChannel?.Writer.TryComplete();
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
    /// Orders providers by preference:
    /// 1. Polish channel count (descending - more Polish channels first)
    /// 2. Days until expiration (descending - longer validity first)
    /// 3. Total channel count (descending - more channels first).
    /// </summary>
    private static IOrderedEnumerable<ProviderTestResult> OrderByPreference(IEnumerable<ProviderTestResult> providers)
    {
        return providers
            .OrderByDescending(r => r.PolishChannelCount)
            .ThenByDescending(r => GetDaysUntilExpiration(r.ExpirationDate))
            .ThenByDescending(r => r.TotalChannelCount);
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

                _logger.LogInformation(
                    "Loaded cached discovery results: {Working} working, {WithEpg} with EPG, {Full} fully working",
                    cached.WorkingProviders.Count,
                    cached.WorkingWithEpgProviders.Count,
                    cached.FullyWorkingProviders.Count
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load cached discovery results from {Path}", _cachePath);
        }
    }

    private void SaveCachedResult(DiscoveryTestResult result)
    {
        try
        {
            var directory = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(result, JsonOptions);
            File.WriteAllText(_cachePath, json);

            _logger.LogDebug("Saved discovery results to cache: {Path}", _cachePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save discovery results to cache: {Path}", _cachePath);
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
                _logger.LogInformation("Cleared discovery cache: {Path}", _cachePath);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete discovery cache: {Path}", _cachePath);
            return false;
        }
    }
}
