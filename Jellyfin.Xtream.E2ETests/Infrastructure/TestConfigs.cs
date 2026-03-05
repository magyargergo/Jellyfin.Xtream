using Jellyfin.Xtream.Service.Streaming.Native;

namespace Jellyfin.Xtream.E2ETests.Infrastructure;

/// <summary>
/// Reusable test configurations for E2E testing.
/// Each preset returns a <see cref="StreamerTestSetup"/> that can be passed
/// to the <see cref="NativeE2ETestBase"/> constructor.
/// </summary>
internal static class TestConfigs
{
    // =====================================================================
    // Analyzer config (shared by presets that enable TR 101 290)
    // =====================================================================

    private static TsDuckConfigNative AnalyzerEnabled(RestampingMode restampMode = RestampingMode.Disabled) =>
        TsDuckConfigNative.FromManaged(
            new TsDuckConfiguration
            {
                EnableTr101290 = true,
                MetricsIntervalSeconds = 1,
                EnableAutoRestamp = restampMode != RestampingMode.Disabled,
                RestampMode = restampMode,
            }
        );

    // =====================================================================
    // Default E2E config (used by most streaming tests)
    // =====================================================================

    /// <summary>
    /// Default E2E streaming config: moderate timeouts, restamping enabled, no analyzer.
    /// </summary>
    public static StreamerTestSetup Default => new(DefaultStreamer);

    private static TsDuckStreamerConfigNative DefaultStreamer =>
        new()
        {
            ConnectTimeoutMs = 5000,
            ResponseTimeoutMs = 10000,
            StallTimeoutMs = 15000,
            MaxRetries = 3,
            InitialBackoffMs = 200,
            MaxBackoffMs = 5000,
            BackoffMultiplier = 2.0,
            BackoffJitterMs = 100,
            OutputFd = -1,
            AlignmentBufferPackets = 32,
            EnableRestamp = 1,
            RestampMode = (int)RestampingMode.Correct,
            LowSpeedLimitBytes = 100,
            LowSpeedTimeSec = 5,
            StallsBeforeSwitch = 2,
        };

    // =====================================================================
    // Analyzer presets (TR 101 290 monitoring)
    // =====================================================================

    /// <summary>
    /// Analyzer enabled, restamping disabled. For TR 101 290 monitoring tests.
    /// </summary>
    public static StreamerTestSetup Analyzer
    {
        get
        {
            var config = DefaultStreamer;
            config.EnableRestamp = 0;
            config.RestampMode = (int)RestampingMode.Disabled;
            return new StreamerTestSetup(config, AnalyzerEnabled());
        }
    }

    /// <summary>
    /// Restamp mode with analyzer for drift tracking.
    /// </summary>
    public static StreamerTestSetup Restamp(RestampingMode mode)
    {
        var config = DefaultStreamer;
        config.EnableRestamp = mode == RestampingMode.Disabled ? 0 : 1;
        config.RestampMode = (int)mode;
        return new StreamerTestSetup(config, AnalyzerEnabled(mode));
    }

    // =====================================================================
    // Circuit breaker / health system presets
    // =====================================================================

    /// <summary>
    /// Fast isolation: short timeouts (100ms-1000ms) for rapid circuit breaker state transitions.
    /// </summary>
    public static StreamerTestSetup FastIsolation => new(FastIsolationStreamer);

    internal static TsDuckStreamerConfigNative FastIsolationStreamer =>
        new()
        {
            ConnectTimeoutMs = 1000,
            ResponseTimeoutMs = 2000,
            StallTimeoutMs = 1500,
            MaxRetries = 5,
            InitialBackoffMs = 50,
            MaxBackoffMs = 500,
            BackoffMultiplier = 2.0,
            BackoffJitterMs = 20,
            OutputFd = -1,
            AlignmentBufferPackets = 16,
            EnableRestamp = 1,
            RestampMode = (int)RestampingMode.Correct,
            LowSpeedLimitBytes = 1000,
            LowSpeedTimeSec = 1,
            StallsBeforeSwitch = 4,
            TimeoutImmediateSwitch = 0,
            EnableQualitySwitch = 0,
            QualityCheckIntervalMs = 500,
            QualityWindowSeconds = 5,
            MaxSyncErrorsPerWindow = 1,
            MaxContinuityErrorsPerSec = 10,
            MaxTransportErrorsPerSec = 5,
            MaxPcrErrorsPerSec = 3,
            QuarantineDurationMs = 100,
            MaxQuarantineDurationMs = 1000,
            QuarantineBackoffMultiplier = 2.0,
            ScoreBoostOnSuccess = 2.0,
            ScorePenaltyOnFailure = 10.0,
            DefaultHealthScore = 50.0,
            CircuitBreakerShortWindowSize = 10,
            CircuitBreakerLongWindowSize = 20,
            CircuitBreakerShortWindowErrorPercent = 30,
            CircuitBreakerLongWindowErrorPercent = 20,
            DnsRetryCount = 3,
            DnsEjectionDurationMs = 1000,
        };

    /// <summary>
    /// Highly reactive: circuit breaker trips on first failure, very short windows.
    /// </summary>
    public static StreamerTestSetup HighlyReactive => new(HighlyReactiveStreamer);

    internal static TsDuckStreamerConfigNative HighlyReactiveStreamer =>
        new()
        {
            ConnectTimeoutMs = 500,
            ResponseTimeoutMs = 1000,
            StallTimeoutMs = 1500,
            MaxRetries = 3,
            InitialBackoffMs = 25,
            MaxBackoffMs = 200,
            BackoffMultiplier = 2.0,
            BackoffJitterMs = 10,
            OutputFd = -1,
            AlignmentBufferPackets = 8,
            EnableRestamp = 1,
            RestampMode = (int)RestampingMode.Correct,
            LowSpeedLimitBytes = 100,
            LowSpeedTimeSec = 1,
            StallsBeforeSwitch = 3,
            TimeoutImmediateSwitch = 0,
            EnableQualitySwitch = 0,
            QualityCheckIntervalMs = 250,
            QualityWindowSeconds = 2,
            MaxSyncErrorsPerWindow = 1,
            MaxContinuityErrorsPerSec = 5,
            MaxTransportErrorsPerSec = 3,
            MaxPcrErrorsPerSec = 2,
            QuarantineDurationMs = 50,
            MaxQuarantineDurationMs = 500,
            QuarantineBackoffMultiplier = 2.0,
            ScoreBoostOnSuccess = 5.0,
            ScorePenaltyOnFailure = 20.0,
            DefaultHealthScore = 50.0,
            CircuitBreakerShortWindowSize = 5,
            CircuitBreakerLongWindowSize = 10,
            CircuitBreakerShortWindowErrorPercent = 40,
            CircuitBreakerLongWindowErrorPercent = 30,
            DnsRetryCount = 3,
            DnsEjectionDurationMs = 500,
        };

    /// <summary>
    /// Production-like configuration with default values.
    /// </summary>
    public static StreamerTestSetup Production => new(TsDuckStreamerConfigNative.Default);

    /// <summary>
    /// P2C load balancing: longer durations to observe selection patterns.
    /// </summary>
    public static StreamerTestSetup P2CLoadBalancing =>
        new(
            new TsDuckStreamerConfigNative
            {
                ConnectTimeoutMs = 2000,
                ResponseTimeoutMs = 5000,
                StallTimeoutMs = 10000,
                MaxRetries = 10,
                InitialBackoffMs = 100,
                MaxBackoffMs = 2000,
                BackoffMultiplier = 2.0,
                BackoffJitterMs = 50,
                OutputFd = -1,
                AlignmentBufferPackets = 32,
                EnableRestamp = 1,
                RestampMode = (int)RestampingMode.Correct,
                LowSpeedLimitBytes = 1000,
                LowSpeedTimeSec = 5,
                StallsBeforeSwitch = 2,
                TimeoutImmediateSwitch = 1,
                EnableQualitySwitch = 1,
                QualityCheckIntervalMs = 1000,
                QualityWindowSeconds = 10,
                MaxSyncErrorsPerWindow = 1,
                MaxContinuityErrorsPerSec = 20,
                MaxTransportErrorsPerSec = 10,
                MaxPcrErrorsPerSec = 5,
                QuarantineDurationMs = 5000,
                MaxQuarantineDurationMs = 60000,
                QuarantineBackoffMultiplier = 2.0,
                ScoreBoostOnSuccess = 0.5,
                ScorePenaltyOnFailure = 5.0,
                DefaultHealthScore = 50.0,
            }
        );

    /// <summary>
    /// Outlier detection: moderate windows for statistical analysis.
    /// </summary>
    public static StreamerTestSetup OutlierDetection =>
        new(
            new TsDuckStreamerConfigNative
            {
                ConnectTimeoutMs = 2000,
                ResponseTimeoutMs = 5000,
                StallTimeoutMs = 10000,
                MaxRetries = 10,
                InitialBackoffMs = 100,
                MaxBackoffMs = 2000,
                BackoffMultiplier = 2.0,
                BackoffJitterMs = 50,
                OutputFd = -1,
                AlignmentBufferPackets = 32,
                EnableRestamp = 1,
                RestampMode = (int)RestampingMode.Correct,
                LowSpeedLimitBytes = 1000,
                LowSpeedTimeSec = 5,
                StallsBeforeSwitch = 2,
                TimeoutImmediateSwitch = 1,
                EnableQualitySwitch = 0,
                QualityCheckIntervalMs = 1000,
                QualityWindowSeconds = 10,
                MaxSyncErrorsPerWindow = 1,
                MaxContinuityErrorsPerSec = 20,
                MaxTransportErrorsPerSec = 10,
                MaxPcrErrorsPerSec = 5,
                QuarantineDurationMs = 2000,
                MaxQuarantineDurationMs = 30000,
                QuarantineBackoffMultiplier = 2.0,
                ScoreBoostOnSuccess = 1.0,
                ScorePenaltyOnFailure = 5.0,
                DefaultHealthScore = 50.0,
            }
        );

    // =====================================================================
    // Connection error / timeout presets
    // =====================================================================

    /// <summary>
    /// Short timeouts (2s) for connection error testing.
    /// </summary>
    public static StreamerTestSetup ShortTimeouts =>
        new(
            new TsDuckStreamerConfigNative
            {
                ConnectTimeoutMs = 2000,
                ResponseTimeoutMs = 2000,
                StallTimeoutMs = 2000,
                MaxRetries = 5,
                InitialBackoffMs = 100,
                MaxBackoffMs = 500,
                BackoffMultiplier = 1.5,
                BackoffJitterMs = 50,
                OutputFd = -1,
                AlignmentBufferPackets = 32,
                EnableRestamp = 0,
                RestampMode = 0,
                LowSpeedLimitBytes = 100,
                LowSpeedTimeSec = 1,
                StallsBeforeSwitch = 1,
            }
        );

    /// <summary>
    /// Very short timeouts (500ms) for extreme timeout testing.
    /// </summary>
    public static StreamerTestSetup VeryShortTimeouts =>
        new(
            new TsDuckStreamerConfigNative
            {
                ConnectTimeoutMs = 500,
                ResponseTimeoutMs = 500,
                StallTimeoutMs = 500,
                MaxRetries = 2,
                InitialBackoffMs = 100,
                MaxBackoffMs = 200,
                BackoffMultiplier = 1.0,
                BackoffJitterMs = 0,
                OutputFd = -1,
                AlignmentBufferPackets = 32,
                EnableRestamp = 0,
                RestampMode = 0,
                LowSpeedLimitBytes = 0,
                LowSpeedTimeSec = 0,
                StallsBeforeSwitch = 1,
            }
        );

    /// <summary>
    /// Stall detection: 2s stall timeout, switch on first stall.
    /// </summary>
    public static StreamerTestSetup StallDetection =>
        new(
            new TsDuckStreamerConfigNative
            {
                ConnectTimeoutMs = 3000,
                ResponseTimeoutMs = 3000,
                StallTimeoutMs = 2000,
                MaxRetries = 10,
                InitialBackoffMs = 100,
                MaxBackoffMs = 1000,
                BackoffMultiplier = 1.5,
                BackoffJitterMs = 50,
                OutputFd = -1,
                AlignmentBufferPackets = 32,
                EnableRestamp = 0,
                RestampMode = 0,
                LowSpeedLimitBytes = 100,
                LowSpeedTimeSec = 2,
                StallsBeforeSwitch = 1,
            }
        );

    // =====================================================================
    // Failover presets
    // =====================================================================

    /// <summary>
    /// Aggressive failover: short stall timeout, restamping enabled, quality switching disabled.
    /// </summary>
    public static StreamerTestSetup AggressiveFailover =>
        new(
            new TsDuckStreamerConfigNative
            {
                ConnectTimeoutMs = 3000,
                ResponseTimeoutMs = 5000,
                StallTimeoutMs = 3000,
                MaxRetries = 10,
                InitialBackoffMs = 100,
                MaxBackoffMs = 1000,
                BackoffMultiplier = 1.5,
                BackoffJitterMs = 50,
                OutputFd = -1,
                AlignmentBufferPackets = 32,
                EnableRestamp = 1,
                RestampMode = (int)RestampingMode.Correct,
                LowSpeedLimitBytes = 100,
                LowSpeedTimeSec = 2,
                StallsBeforeSwitch = 1,
                EnableQualitySwitch = 0,
            }
        );

    /// <summary>
    /// Failover with analyzer: short stall timeout, analyzer enabled, no restamping.
    /// Used by FailoverSwitchingTests for quality-aware failover testing.
    /// </summary>
    public static StreamerTestSetup AnalyzerFailover =>
        new(
            new TsDuckStreamerConfigNative
            {
                ConnectTimeoutMs = 3000,
                ResponseTimeoutMs = 5000,
                StallTimeoutMs = 3000,
                MaxRetries = 10,
                InitialBackoffMs = 100,
                MaxBackoffMs = 1000,
                BackoffMultiplier = 1.5,
                BackoffJitterMs = 50,
                OutputFd = -1,
                AlignmentBufferPackets = 32,
                EnableRestamp = 0,
                RestampMode = (int)RestampingMode.Disabled,
                LowSpeedLimitBytes = 100,
                LowSpeedTimeSec = 2,
                StallsBeforeSwitch = 1,
            },
            AnalyzerEnabled()
        );

    /// <summary>
    /// Standard failover: moderate timeouts with restamping.
    /// Used by FailoverTests for general failover behavior.
    /// </summary>
    public static StreamerTestSetup Failover =>
        new(
            new TsDuckStreamerConfigNative
            {
                ConnectTimeoutMs = 3000,
                ResponseTimeoutMs = 5000,
                StallTimeoutMs = 5000,
                MaxRetries = 10,
                InitialBackoffMs = 200,
                MaxBackoffMs = 2000,
                BackoffMultiplier = 1.5,
                BackoffJitterMs = 100,
                OutputFd = -1,
                AlignmentBufferPackets = 32,
                EnableRestamp = 1,
                RestampMode = (int)RestampingMode.Correct,
                LowSpeedLimitBytes = 100,
                LowSpeedTimeSec = 3,
                StallsBeforeSwitch = 1,
            }
        );

    /// <summary>
    /// Failover with analyzer for FailoverTests: moderate timeouts, no restamping, analyzer enabled.
    /// </summary>
    public static StreamerTestSetup FailoverAnalyzer =>
        new(
            new TsDuckStreamerConfigNative
            {
                ConnectTimeoutMs = 3000,
                ResponseTimeoutMs = 5000,
                StallTimeoutMs = 5000,
                MaxRetries = 10,
                InitialBackoffMs = 200,
                MaxBackoffMs = 2000,
                BackoffMultiplier = 1.5,
                BackoffJitterMs = 100,
                OutputFd = -1,
                AlignmentBufferPackets = 32,
                EnableRestamp = 0,
                RestampMode = (int)RestampingMode.Disabled,
                LowSpeedLimitBytes = 100,
                LowSpeedTimeSec = 3,
                StallsBeforeSwitch = 1,
            },
            AnalyzerEnabled()
        );

    // =====================================================================
    // Quality-based switching preset
    // =====================================================================

    /// <summary>
    /// Quality-based switching: analyzer enabled with quality switch thresholds.
    /// </summary>
    public static StreamerTestSetup QualitySwitch =>
        new(
            new TsDuckStreamerConfigNative
            {
                ConnectTimeoutMs = 3000,
                ResponseTimeoutMs = 5000,
                StallTimeoutMs = 3000,
                MaxRetries = 10,
                InitialBackoffMs = 100,
                MaxBackoffMs = 1000,
                BackoffMultiplier = 1.5,
                BackoffJitterMs = 50,
                OutputFd = -1,
                AlignmentBufferPackets = 32,
                EnableRestamp = 0,
                RestampMode = (int)RestampingMode.Disabled,
                LowSpeedLimitBytes = 100,
                LowSpeedTimeSec = 2,
                StallsBeforeSwitch = 1,
                EnableQualitySwitch = 1,
                QualityCheckIntervalMs = 1000,
                QualityWindowSeconds = 10,
                MaxSyncErrorsPerWindow = 1,
                MaxContinuityErrorsPerSec = 20,
                MaxTransportErrorsPerSec = 10,
                MaxPcrErrorsPerSec = 5,
            },
            AnalyzerEnabled()
        );
}
