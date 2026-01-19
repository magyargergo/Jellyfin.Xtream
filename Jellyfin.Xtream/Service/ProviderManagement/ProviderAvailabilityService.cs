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
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Utility;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;

#pragma warning disable CA1859 // Use concrete types for better performance - interface needed for flexibility

namespace Jellyfin.Xtream.Service.ProviderManagement;

/// <summary>
/// Manages provider availability using Polly v8 circuit breakers with weighted health scoring.
/// Also handles provider connection status refreshes via HTTP API calls.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="ProviderAvailabilityService"/> class.
/// </remarks>
/// <param name="httpClientFactory">HTTP client factory for API calls.</param>
/// <param name="loggerFactory">Logger factory for creating loggers.</param>
/// <param name="discordService">Optional Discord notification service.</param>
/// <param name="configurationProvider">Provider for retrieving plugin configuration.</param>
public sealed class ProviderAvailabilityService(
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory,
    IDiscordNotificationService? discordService,
    IPluginConfigurationProvider configurationProvider
) : IProviderAvailabilityService, IDisposable
{
    // Scoring weights (must sum to 100 for base score)
    private const double SuccessRateWeight = 0.40;
    private const double CapacityWeight = 0.40;
    private const double HealthWeight = 0.20;

    // Bonus/penalty modifiers
    private const int RecentSuccessBonus = 20;
    private const int RecentFailurePenalty = -15;
    private const int ConsecutiveFailurePenalty = -15;
    private const int MaxConsecutiveFailurePenalty = -60;

    // Time thresholds
    private static readonly TimeSpan RecentSuccessWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RecentFailureWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CacheExpiry = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, ProviderState> _states = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _previousAtLimitState = new(StringComparer.Ordinal);
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly ILogger<ProviderAvailabilityService> _logger =
        loggerFactory.CreateLogger<ProviderAvailabilityService>();
    private readonly IDiscordNotificationService? _discordService = discordService;
    private readonly IPluginConfigurationProvider _configurationProvider = configurationProvider;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    // High-performance provider indexing for O(1) score lookups
    private FastProviderIndex? _providerIndex;
    private DateTime _lastFullRefresh = DateTime.MinValue;

    private FastProviderIndex ProviderIndex => _providerIndex ??= new FastProviderIndex();

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsAvailable(string providerId)
    {
        var state = GetOrCreateState(providerId);
        var circuitState = state.CircuitStateProvider.CircuitState;

        // Available if circuit is closed or half-open (testing)
        var circuitAvailable = circuitState is not CircuitState.Open and not CircuitState.Isolated;

        // Also check capacity if tracked
        var hasCapacity = state.MaxConnections == 0 || state.AvailableSlots > 0;

        // Provider must be online (API reachable, no 4xx/5xx errors like 452)
        // For newly created state with no status update yet, assume online
        var isOnline = state.LastStatusUpdate == default || state.IsOnline;

        return circuitAvailable && hasCapacity && isOnline;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ProviderCircuitState GetCircuitState(string providerId) =>
        GetOrCreateState(providerId).CircuitStateProvider.CircuitState.ToProviderCircuitState();

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsCircuitAvailable(string providerId)
    {
        var state = GetOrCreateState(providerId);
        var circuitState = state.CircuitStateProvider.CircuitState;
        return circuitState is not CircuitState.Open and not CircuitState.Isolated;
    }

    /// <inheritdoc />
    public Task IsolateCircuitAsync(string providerId) => RecordConnectionLimitAsync(providerId);

    /// <inheritdoc />
    public int GetSelectionScore(string providerId)
    {
        var state = GetOrCreateState(providerId);
        var score = CalculateSelectionScore(state);

        // Update the fast index for cached lookups
        if (_providerIndex != null)
        {
            var index = ProviderIndex.GetOrRegisterIndex(providerId);
            ProviderIndex.SetScore(index, score);
        }

        return score;
    }

    /// <inheritdoc />
    public IReadOnlyList<ProviderStreamInfo> GetSortedProviders(
        IEnumerable<ProviderStreamInfo> providers,
        bool forceIncludeAll = false
    )
    {
        // Use array pooling to avoid allocations for small provider counts
        var providerList = providers as IList<ProviderStreamInfo> ?? [.. providers];
        var count = providerList.Count;

        if (count == 0)
        {
            return [];
        }

        // For single provider, skip sorting overhead
        if (count == 1)
        {
            var single = providerList[0];
            return forceIncludeAll || IsAvailable(single.Provider.Id) ? [single] : [];
        }

        // Rent arrays for zero-allocation scoring
        var scoredArray = ArrayPool<(ProviderStreamInfo Provider, int Score, bool Available)>.Shared.Rent(count);
        try
        {
            var validCount = 0;
            for (var i = 0; i < count; i++)
            {
                var provider = providerList[i];
                var providerId = provider.Provider.Id;
                var available = IsAvailable(providerId);

                if (forceIncludeAll || available)
                {
                    scoredArray[validCount++] = (provider, GetSelectionScore(providerId), available);
                }
            }

            if (validCount == 0)
            {
                return [];
            }

            // Sort in place - descending by score, then by name
            Array.Sort(scoredArray, 0, validCount, ScoredProviderComparer.Instance);

            // Build result list
            var result = new ProviderStreamInfo[validCount];
            for (var i = 0; i < validCount; i++)
            {
                result[i] = scoredArray[i].Provider;
            }

            return result;
        }
        finally
        {
            ArrayPool<(ProviderStreamInfo Provider, int Score, bool Available)>.Shared.Return(scoredArray);
        }
    }

    /// <summary>
    /// Comparer for scored providers - sorts by score descending, then name ascending.
    /// </summary>
    private sealed class ScoredProviderComparer : IComparer<(ProviderStreamInfo Provider, int Score, bool Available)>
    {
        public static readonly ScoredProviderComparer Instance = new();

        public int Compare(
            (ProviderStreamInfo Provider, int Score, bool Available) x,
            (ProviderStreamInfo Provider, int Score, bool Available) y
        )
        {
            // Sort by score descending
            var scoreCompare = y.Score.CompareTo(x.Score);
            if (scoreCompare != 0)
            {
                return scoreCompare;
            }

            // Then by name ascending
            return StringComparer.Ordinal.Compare(x.Provider.Provider.Name, y.Provider.Provider.Name);
        }
    }

    /// <inheritdoc />
    public void RecordSuccess(string providerId)
    {
        var state = GetOrCreateState(providerId);

        // Reset consecutive failures
        state.ConsecutiveFailures = 0;
        state.LastSuccessTicks = DateTime.UtcNow.Ticks;

        // Update success rate (exponential moving average)
        UpdateSuccessRate(state, success: true);

        // Execute through pipeline to update circuit breaker
        try
        {
            state.Pipeline.Execute(() => { });
        }
        catch (BrokenCircuitException)
        {
            // Circuit is open, will transition to half-open on next check
        }

        var newScore = CalculateSelectionScore(state);

        // Notify sorted cache of score change
        NotifyScoreChanged(providerId, newScore);

        _logger.LogDebugIfEnabled(
            "Provider {ProviderId} success recorded. Score: {Score}, Circuit: {State}",
            providerId,
            newScore,
            state.CircuitStateProvider.CircuitState
        );
    }

    /// <inheritdoc />
    public bool RecordFailure(string providerId, ProviderFailureReason reason, string? providerName = null)
    {
        var state = GetOrCreateState(providerId);

        // Increment consecutive failures
        var failures = Interlocked.Increment(ref state.ConsecutiveFailures);
        state.LastFailureTicks = DateTime.UtcNow.Ticks;
        state.LastFailureReason = reason;

        // Update success rate
        UpdateSuccessRate(state, success: false);

        // Execute failing operation through pipeline to trigger circuit breaker
        var circuitOpened = false;
        try
        {
            _ = state.Pipeline.Execute<object?>(() =>
                throw new InvalidOperationException($"Provider {providerName ?? providerId} failed: {reason}")
            );
        }
        catch (BrokenCircuitException)
        {
            circuitOpened = true;
        }
        catch (InvalidOperationException)
        {
            // Expected - our thrown exception
        }

        // Check if circuit just opened
        var newScore = CalculateSelectionScore(state);

        // Notify sorted cache of score change
        NotifyScoreChanged(providerId, newScore);

        if (state.CircuitStateProvider.CircuitState == CircuitState.Open)
        {
            circuitOpened = true;
            var duration = GetBlacklistDuration(reason);

            _logger.PluginLogWarning(
                "Provider {ProviderId} circuit OPENED after {Failures} failures ({Reason}). Duration: {Duration}s",
                providerId,
                failures,
                reason,
                duration.TotalSeconds
            );

            // Send Discord notification
            NotifyProviderBlacklisted(providerId, providerName, reason, duration, failures);
        }
        else
        {
            _logger.LogDebugIfEnabled(
                "Provider {ProviderId} failure #{Count}: {Reason}. Score: {Score}",
                providerId,
                failures,
                reason,
                newScore
            );
        }

        return circuitOpened;
    }

    /// <inheritdoc />
    public async Task RecordConnectionLimitAsync(string providerId)
    {
        var state = GetOrCreateState(providerId);
        await state.ManualControl.IsolateAsync().ConfigureAwait(false);

        state.AvailableSlots = 0;
        state.LastFailureReason = ProviderFailureReason.ConnectionLimit;

        _logger.PluginLogInformation("Provider {ProviderId} circuit ISOLATED due to connection limit", providerId);
    }

    /// <inheritdoc />
    public async Task ResetCircuitAsync(string providerId)
    {
        if (_states.TryGetValue(providerId, out var state))
        {
            await state.ManualControl.CloseAsync().ConfigureAwait(false);
            state.ConsecutiveFailures = 0;

            _logger.LogDebugIfEnabled("Provider {ProviderId} circuit manually reset", providerId);
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, ProviderResilienceState> GetSnapshot()
    {
        var snapshot = new Dictionary<string, ProviderResilienceState>(StringComparer.Ordinal);
        var now = DateTime.UtcNow;

        foreach (var kvp in _states)
        {
            var state = kvp.Value;
            snapshot[kvp.Key] = new ProviderResilienceState
            {
                ProviderId = kvp.Key,
                CircuitState = state.CircuitStateProvider.CircuitState.ToProviderCircuitState(),
                SelectionScore = CalculateSelectionScore(state),
                IsAvailable = IsAvailable(kvp.Key),
                ConsecutiveFailures = state.ConsecutiveFailures,
                AvailableSlots = state.AvailableSlots,
                MaxConnections = state.MaxConnections,
                Timestamp = now,
                IsOnline = state.IsOnline,
                ActiveConnections = state.ActiveConnections,
                Status = state.AccountStatus,
                ExpirationDate = state.ExpirationDate,
                IsTrial = state.IsTrial,
                ErrorMessage = state.ErrorMessage,
            };
        }

        return snapshot;
    }

    /// <inheritdoc />
    public ProviderResilienceState? GetStatus(string providerId)
    {
        if (!_states.TryGetValue(providerId, out var state))
        {
            return null;
        }

        // Check if status is still valid
        return DateTime.UtcNow - state.LastStatusUpdate > CacheExpiry
            ? null
            : new ProviderResilienceState
            {
                ProviderId = providerId,
                CircuitState = state.CircuitStateProvider.CircuitState.ToProviderCircuitState(),
                SelectionScore = CalculateSelectionScore(state),
                IsAvailable = IsAvailable(providerId),
                ConsecutiveFailures = state.ConsecutiveFailures,
                AvailableSlots = state.AvailableSlots,
                MaxConnections = state.MaxConnections,
                Timestamp = state.LastStatusUpdate,
                IsOnline = state.IsOnline,
                ActiveConnections = state.ActiveConnections,
                Status = state.AccountStatus,
                ExpirationDate = state.ExpirationDate,
                IsTrial = state.IsTrial,
                ErrorMessage = state.ErrorMessage,
            };
    }

    /// <inheritdoc />
    public bool NeedsRefresh() => DateTime.UtcNow - _lastFullRefresh > CacheExpiry;

    /// <inheritdoc />
    public async Task RefreshAsync(IEnumerable<XtreamProvider> providers, CancellationToken cancellationToken = default)
    {
        // Avoid concurrent refreshes
        if (!await _refreshLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var providerList = providers.ToList();
            _logger.LogDebugIfEnabled("Refreshing connection status for {Count} providers", providerList.Count);

            var tasks = providerList.Select(provider => RefreshProviderAsync(provider, cancellationToken));
            await Task.WhenAll(tasks).ConfigureAwait(false);

            _lastFullRefresh = DateTime.UtcNow;
        }
        finally
        {
            _ = _refreshLock.Release();
        }
    }

    /// <summary>
    /// Refreshes the connection status for a single provider.
    /// </summary>
    private async Task RefreshProviderAsync(XtreamProvider provider, CancellationToken cancellationToken)
    {
        var state = GetOrCreateState(provider.Id);

        try
        {
            using var client = new XtreamClient(_httpClientFactory, _loggerFactory.CreateLogger<XtreamClient>());
            var playerApi = await client
                .GetUserAndServerInfoAsync(provider.ToConnectionInfo(), cancellationToken)
                .ConfigureAwait(false);

            if (playerApi?.UserInfo != null)
            {
                var userInfo = playerApi.UserInfo;
                state.MaxConnections = userInfo.MaxConnections;
                state.ActiveConnections = userInfo.ActiveCons;
                state.AvailableSlots = Math.Max(0, userInfo.MaxConnections - userInfo.ActiveCons);
                state.AccountStatus = userInfo.Status ?? "Unknown";
                state.ExpirationDate = userInfo.ExpDate;
                state.IsTrial = userInfo.IsTrial;
                state.IsOnline = true;
                state.ErrorMessage = null;
                state.LastStatusUpdate = DateTime.UtcNow;

                // Check for connection limit state change and notify
                await CheckAndNotifyLimitChangeAsync(provider, state, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                state.ErrorMessage = "Failed to get user info";
                state.IsOnline = false;
                state.LastStatusUpdate = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebugIfEnabled(ex, "Failed to get connection status for provider {ProviderId}", provider.Id);
            state.ErrorMessage = ex.Message;
            state.IsOnline = false;
            state.AvailableSlots = 0; // Reset slots when provider is offline
            state.LastStatusUpdate = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Checks if the connection limit state has changed and sends a Discord notification if so.
    /// </summary>
    private async Task CheckAndNotifyLimitChangeAsync(
        XtreamProvider provider,
        ProviderState state,
        CancellationToken cancellationToken
    )
    {
        if (state.MaxConnections <= 0)
        {
            return; // Unknown max, can't track state
        }

        var currentlyAtLimit = state.ActiveConnections >= state.MaxConnections;
        var previouslyAtLimit = _previousAtLimitState.TryGetValue(provider.Id, out var wasAtLimit) && wasAtLimit;

        // Detect state transition
        if (currentlyAtLimit != previouslyAtLimit)
        {
            _logger.LogDebugIfEnabled(
                "Provider {Provider} connection limit state changed: {Previous} -> {Current} ({Active}/{Max})",
                provider.Name,
                previouslyAtLimit ? "AtLimit" : "Available",
                currentlyAtLimit ? "AtLimit" : "Available",
                state.ActiveConnections,
                state.MaxConnections
            );

            // Send Discord notification
            if (_discordService != null)
            {
                await _discordService
                    .NotifyConnectionLimitChangeAsync(
                        provider.Name,
                        state.ActiveConnections,
                        state.MaxConnections,
                        currentlyAtLimit,
                        state.ActiveConnections, // external connections estimate
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
        }

        // Update tracked state
        _previousAtLimitState[provider.Id] = currentlyAtLimit;
    }

    /// <inheritdoc />
    public void UpdateCapacity(string providerId, int availableSlots, int maxConnections)
    {
        var state = GetOrCreateState(providerId);
        state.AvailableSlots = availableSlots;
        state.MaxConnections = maxConnections;

        _logger.LogDebugIfEnabled(
            "Provider {ProviderId} capacity updated: {Available}/{Max} slots",
            providerId,
            availableSlots,
            maxConnections
        );
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasCapacity(string providerId)
    {
        if (!_states.TryGetValue(providerId, out var state))
        {
            return true; // Unknown providers assumed to have capacity
        }

        return state.MaxConnections == 0 || state.AvailableSlots > 0;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetAvailableSlots(string providerId)
    {
        if (!_states.TryGetValue(providerId, out var state))
        {
            return -1; // Unknown provider
        }

        return state.AvailableSlots;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetMaxConnections(string providerId)
    {
        if (!_states.TryGetValue(providerId, out var state))
        {
            return 0; // Unknown provider - unlimited/unknown
        }

        return state.MaxConnections;
    }

    /// <summary>
    /// Updates the fast provider index with a new score.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <param name="newScore">The new score value.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void NotifyScoreChanged(string providerId, int newScore)
    {
        if (_providerIndex == null)
        {
            return;
        }

        var index = ProviderIndex.GetOrRegisterIndex(providerId);
        ProviderIndex.SetScore(index, newScore);
    }

    private ProviderState GetOrCreateState(string providerId) => _states.GetOrAdd(providerId, CreateProviderState);

    private ProviderState CreateProviderState(string providerId)
    {
        var config = _configurationProvider.GetConfiguration();
        var breakDuration =
            config?.ProviderBlacklistSeconds > 0
                ? TimeSpan.FromSeconds(config.ProviderBlacklistSeconds)
                : TimeSpan.FromMilliseconds(StreamingTimeoutPolicy.DefaultBlacklistDurationMs);

        var stateProvider = new CircuitBreakerStateProvider();
        var manualControl = new CircuitBreakerManualControl();

        var options = new CircuitBreakerStrategyOptions
        {
            FailureRatio = 0.5,
            SamplingDuration = TimeSpan.FromSeconds(30),
            MinimumThroughput = StreamingTimeoutPolicy.BlacklistThreshold,
            BreakDuration = breakDuration,
            ShouldHandle = new PredicateBuilder().Handle<Exception>(),
            StateProvider = stateProvider,
            ManualControl = manualControl,
            OnOpened = args =>
            {
                _logger.PluginLogInformation(
                    "Provider {ProviderId} circuit OPENED for {Duration}s",
                    providerId,
                    args.BreakDuration.TotalSeconds
                );
                return default;
            },
            OnClosed = _ =>
            {
                _logger.LogDebugIfEnabled("Provider {ProviderId} circuit CLOSED", providerId);
                return default;
            },
            OnHalfOpened = _ =>
            {
                _logger.LogDebugIfEnabled("Provider {ProviderId} circuit HALF-OPEN (testing)", providerId);
                return default;
            },
        };

        var pipeline = new ResiliencePipelineBuilder().AddCircuitBreaker(options).Build();

        return new ProviderState(pipeline, stateProvider, manualControl);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CalculateSelectionScore(ProviderState state)
    {
        // Circuit open = immediate exclusion
        var circuitState = state.CircuitStateProvider.CircuitState;
        if (circuitState is CircuitState.Open or CircuitState.Isolated)
        {
            return 0;
        }

        // Calculate base score from success rate (0-100)
        var successRateScore = state.RecentTotal > 0 ? state.RecentSuccesses * 100 / state.RecentTotal : 50;

        // Calculate capacity score (0-100)
        var capacityScore =
            state.MaxConnections > 0 ? Math.Min(100, state.AvailableSlots * 100 / state.MaxConnections) : 100;

        // Combine weighted scores
        var score = (successRateScore * SuccessRateWeight) + (capacityScore * CapacityWeight) + (50 * HealthWeight); // Base health score

        // Apply consecutive failure penalty
        if (state.ConsecutiveFailures > 0)
        {
            var penalty = Math.Min(MaxConsecutiveFailurePenalty, state.ConsecutiveFailures * ConsecutiveFailurePenalty);
            score += penalty;
        }

        // Apply recency bonuses/penalties
        var now = DateTime.UtcNow.Ticks;
        if (state.LastSuccessTicks > 0 && (now - state.LastSuccessTicks) < RecentSuccessWindow.Ticks)
        {
            score += RecentSuccessBonus;
        }

        if (state.LastFailureTicks > 0 && (now - state.LastFailureTicks) < RecentFailureWindow.Ticks)
        {
            score += RecentFailurePenalty;
        }

        // Half-open gets slight penalty (still testing)
        if (circuitState == CircuitState.HalfOpen)
        {
            score -= 10;
        }

        // Clamp to 0-100
        return Math.Max(0, Math.Min(100, (int)score));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void UpdateSuccessRate(ProviderState state, bool success)
    {
        // Exponential moving average with cap at 20 samples
        var total = state.RecentTotal;
        var successes = state.RecentSuccesses;

        if (total >= 20)
        {
            // Decay old data by 10%
            total = total * 9 / 10;
            successes = successes * 9 / 10;
        }

        state.RecentTotal = total + 1;
        state.RecentSuccesses = success ? successes + 1 : successes;
    }

    private TimeSpan GetBlacklistDuration(ProviderFailureReason reason)
    {
        return reason switch
        {
            // Severe errors - extended blacklist (60s)
            ProviderFailureReason.ConnectionLimit => TimeSpan.FromMilliseconds(
                StreamingTimeoutPolicy.ExtendedBlacklistDurationMs
            ),
            ProviderFailureReason.ClientError => TimeSpan.FromMilliseconds(
                StreamingTimeoutPolicy.ExtendedBlacklistDurationMs
            ),

            // Transient errors - quick recovery (10s)
            ProviderFailureReason.Timeout => TimeSpan.FromMilliseconds(StreamingTimeoutPolicy.QuickBlacklistDurationMs),
            ProviderFailureReason.PrematureEof => TimeSpan.FromMilliseconds(
                StreamingTimeoutPolicy.QuickBlacklistDurationMs
            ),
            ProviderFailureReason.RateLimited => TimeSpan.FromMilliseconds(
                StreamingTimeoutPolicy.QuickBlacklistDurationMs
            ),

            // Provider infrastructure issues - moderate blacklist (30s)
            // 407 errors are caused by provider's misconfigured origin proxy, may self-heal
            ProviderFailureReason.ProxyAuthenticationError => TimeSpan.FromMilliseconds(
                StreamingTimeoutPolicy.DefaultBlacklistDurationMs
            ),

            // Zombie backend - moderate blacklist (30s)
            // Load balancer may be routing to multiple backends, some dead
            // Moderate duration allows re-routing to healthy backend on retry
            ProviderFailureReason.ZombieBackend => TimeSpan.FromMilliseconds(
                StreamingTimeoutPolicy.DefaultBlacklistDurationMs
            ),

            // Default for other errors (30s)
            _ => GetDefaultBlacklistDuration(),
        };
    }

    private TimeSpan GetDefaultBlacklistDuration()
    {
        var config = _configurationProvider.GetConfiguration();
        var seconds = config?.ProviderBlacklistSeconds ?? 0;
        return seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromMilliseconds(StreamingTimeoutPolicy.DefaultBlacklistDurationMs);
    }

    private void NotifyProviderBlacklisted(
        string providerId,
        string? providerName,
        ProviderFailureReason reason,
        TimeSpan duration,
        int consecutiveFailures
    )
    {
        if (_discordService == null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _discordService
                    .NotifyProviderBlacklistedAsync(
                        providerId,
                        providerName ?? providerId,
                        reason,
                        duration,
                        consecutiveFailures
                    )
                    .ConfigureAwait(false);
            }
            catch
            {
                // Ignore notification failures
            }
        });
    }

    /// <inheritdoc />
    public void Dispose() => _refreshLock.Dispose();

    /// <summary>
    /// Internal state for a single provider.
    /// </summary>
    private sealed class ProviderState(
        ResiliencePipeline pipeline,
        CircuitBreakerStateProvider stateProvider,
        CircuitBreakerManualControl manualControl
    )
    {
        public readonly ResiliencePipeline Pipeline = pipeline;
        public readonly CircuitBreakerStateProvider CircuitStateProvider = stateProvider;
        public readonly CircuitBreakerManualControl ManualControl = manualControl;

        // Health metrics (use volatile for 32-bit, interlocked for 64-bit)
        public int ConsecutiveFailures;
        public long LastSuccessTicks;
        public long LastFailureTicks;
        public int RecentSuccesses = 5;
        public int RecentTotal = 10;
        public ProviderFailureReason LastFailureReason;

        // Capacity tracking
        public int AvailableSlots;
        public int MaxConnections;
        public int ActiveConnections;

        // Account status (from API refresh)
        public bool IsOnline;
        public string? AccountStatus;
        public DateTime? ExpirationDate;
        public bool IsTrial;
        public string? ErrorMessage;
        public DateTime LastStatusUpdate;
    }
}
