using Jellyfin.Xtream.Service.Streaming.Native;

namespace Jellyfin.Xtream.E2ETests.Infrastructure;

/// <summary>
/// Reusable test configurations for E2E health system testing.
/// </summary>
internal static class TestConfigs
{
    /// <summary>
    /// Fast isolation configuration for quick E2E testing.
    /// Uses short timeouts (100ms-1000ms) for rapid state transitions.
    /// </summary>
    public static TsDuckStreamerConfigNative FastIsolation =>
        new()
        {
            // Connection settings - short timeouts for quick testing
            ConnectTimeoutMs = 1000,
            ResponseTimeoutMs = 2000,
            StallTimeoutMs = 1500, // Faster stall detection for quick test cycles
            MaxRetries = 5,

            // Backoff settings - very short for testing
            InitialBackoffMs = 50,
            MaxBackoffMs = 500,
            BackoffMultiplier = 2.0,
            BackoffJitterMs = 20,

            // Output settings
            OutputFd = -1,
            AlignmentBufferPackets = 16,

            // Restamping
            EnableRestamp = 1,
            RestampMode = (int)RestampingMode.Correct,

            // Stall detection - fast for testing
            LowSpeedLimitBytes = 1000,
            LowSpeedTimeSec = 1, // Faster stall detection
            StallsBeforeSwitch = 4, // Must be > circuit breaker threshold (3) to allow ejection
            TimeoutImmediateSwitch = 0, // Disabled to allow multiple retries for circuit breaker testing

            // Quality-based switching - disabled for isolation testing
            EnableQualitySwitch = 0,
            QualityCheckIntervalMs = 500,
            QualityWindowSeconds = 5,
            MaxSyncErrorsPerWindow = 1,
            MaxContinuityErrorsPerSec = 10,
            MaxTransportErrorsPerSec = 5,
            MaxPcrErrorsPerSec = 3,

            // Health-based URL selection - very short for quick state transitions
            QuarantineDurationMs = 100, // 100ms initial isolation
            MaxQuarantineDurationMs = 1000, // 1s max isolation
            QuarantineBackoffMultiplier = 2.0,
            ScoreBoostOnSuccess = 2.0, // Faster recovery
            ScorePenaltyOnFailure = 10.0, // Aggressive penalty
            DefaultHealthScore = 50.0,

            // Circuit breaker - small windows for E2E testing (trip after 3 failures)
            CircuitBreakerShortWindowSize = 10,
            CircuitBreakerLongWindowSize = 20,
            CircuitBreakerShortWindowErrorPercent = 30, // Trip at 30% error rate (3 errors in 10 samples)
            CircuitBreakerLongWindowErrorPercent = 20,

            // DNS failure settings - short for testing
            DnsRetryCount = 3,
            DnsEjectionDurationMs = 1000, // 1 second ejection for fast recovery tests
        };

    /// <summary>
    /// Highly reactive configuration for immediate feedback testing.
    /// Circuit breaker trips on first failure, very short windows.
    /// </summary>
    public static TsDuckStreamerConfigNative HighlyReactive =>
        new()
        {
            // Connection settings - minimal timeouts
            ConnectTimeoutMs = 500,
            ResponseTimeoutMs = 1000,
            StallTimeoutMs = 1500,
            MaxRetries = 3,

            // Backoff settings - minimal
            InitialBackoffMs = 25,
            MaxBackoffMs = 200,
            BackoffMultiplier = 2.0,
            BackoffJitterMs = 10,

            // Output settings
            OutputFd = -1,
            AlignmentBufferPackets = 8,

            // Restamping
            EnableRestamp = 1,
            RestampMode = (int)RestampingMode.Correct,

            // Stall detection - configured to allow enough failures for circuit breaker
            LowSpeedLimitBytes = 100,
            LowSpeedTimeSec = 1,
            StallsBeforeSwitch = 3, // Must be > circuit breaker threshold (2) to allow ejection
            TimeoutImmediateSwitch = 0, // Disabled to allow multiple retries for circuit breaker testing

            // Quality-based switching - disabled
            EnableQualitySwitch = 0,
            QualityCheckIntervalMs = 250,
            QualityWindowSeconds = 2,
            MaxSyncErrorsPerWindow = 1,
            MaxContinuityErrorsPerSec = 5,
            MaxTransportErrorsPerSec = 3,
            MaxPcrErrorsPerSec = 2,

            // Health-based URL selection - ultra-short
            QuarantineDurationMs = 50, // 50ms initial isolation
            MaxQuarantineDurationMs = 500, // 500ms max isolation
            QuarantineBackoffMultiplier = 2.0,
            ScoreBoostOnSuccess = 5.0, // Very fast recovery
            ScorePenaltyOnFailure = 20.0, // Very aggressive penalty
            DefaultHealthScore = 50.0,

            // Circuit breaker - minimal windows for immediate feedback (trip after 2 failures)
            CircuitBreakerShortWindowSize = 5,
            CircuitBreakerLongWindowSize = 10,
            CircuitBreakerShortWindowErrorPercent = 40, // Trip at 40% error rate (2 errors in 5 samples)
            CircuitBreakerLongWindowErrorPercent = 30,

            // DNS failure settings - very short for reactive testing
            DnsRetryCount = 3,
            DnsEjectionDurationMs = 500, // 500ms ejection for immediate recovery
        };

    /// <summary>
    /// Production-like configuration with default values.
    /// Use this for realistic integration testing.
    /// </summary>
    public static TsDuckStreamerConfigNative Production => TsDuckStreamerConfigNative.Default;

    /// <summary>
    /// Configuration optimized for P2C load balancing tests.
    /// Longer durations to observe selection patterns over time.
    /// </summary>
    public static TsDuckStreamerConfigNative P2CLoadBalancing =>
        new()
        {
            // Connection settings
            ConnectTimeoutMs = 2000,
            ResponseTimeoutMs = 5000,
            StallTimeoutMs = 10000,
            MaxRetries = 10,

            // Backoff settings
            InitialBackoffMs = 100,
            MaxBackoffMs = 2000,
            BackoffMultiplier = 2.0,
            BackoffJitterMs = 50,

            // Output settings
            OutputFd = -1,
            AlignmentBufferPackets = 32,

            // Restamping
            EnableRestamp = 1,
            RestampMode = (int)RestampingMode.Correct,

            // Stall detection
            LowSpeedLimitBytes = 1000,
            LowSpeedTimeSec = 5,
            StallsBeforeSwitch = 2,
            TimeoutImmediateSwitch = 1,

            // Quality-based switching - enabled
            EnableQualitySwitch = 1,
            QualityCheckIntervalMs = 1000,
            QualityWindowSeconds = 10,
            MaxSyncErrorsPerWindow = 1,
            MaxContinuityErrorsPerSec = 20,
            MaxTransportErrorsPerSec = 10,
            MaxPcrErrorsPerSec = 5,

            // Health-based URL selection - moderate
            QuarantineDurationMs = 5000, // 5s initial isolation
            MaxQuarantineDurationMs = 60000, // 60s max isolation
            QuarantineBackoffMultiplier = 2.0,
            ScoreBoostOnSuccess = 0.5,
            ScorePenaltyOnFailure = 5.0,
            DefaultHealthScore = 50.0,
        };

    /// <summary>
    /// Configuration for outlier detection tests.
    /// Multiple providers with statistical analysis.
    /// </summary>
    public static TsDuckStreamerConfigNative OutlierDetection =>
        new()
        {
            // Connection settings
            ConnectTimeoutMs = 2000,
            ResponseTimeoutMs = 5000,
            StallTimeoutMs = 10000,
            MaxRetries = 10,

            // Backoff settings
            InitialBackoffMs = 100,
            MaxBackoffMs = 2000,
            BackoffMultiplier = 2.0,
            BackoffJitterMs = 50,

            // Output settings
            OutputFd = -1,
            AlignmentBufferPackets = 32,

            // Restamping
            EnableRestamp = 1,
            RestampMode = (int)RestampingMode.Correct,

            // Stall detection
            LowSpeedLimitBytes = 1000,
            LowSpeedTimeSec = 5,
            StallsBeforeSwitch = 2,
            TimeoutImmediateSwitch = 1,

            // Quality-based switching - disabled for pure health testing
            EnableQualitySwitch = 0,
            QualityCheckIntervalMs = 1000,
            QualityWindowSeconds = 10,
            MaxSyncErrorsPerWindow = 1,
            MaxContinuityErrorsPerSec = 20,
            MaxTransportErrorsPerSec = 10,
            MaxPcrErrorsPerSec = 5,

            // Health-based URL selection - moderate for outlier detection
            QuarantineDurationMs = 2000,
            MaxQuarantineDurationMs = 30000,
            QuarantineBackoffMultiplier = 2.0,
            ScoreBoostOnSuccess = 1.0,
            ScorePenaltyOnFailure = 5.0,
            DefaultHealthScore = 50.0,
        };
}
