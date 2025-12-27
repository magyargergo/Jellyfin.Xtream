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
using Jellyfin.Xtream.Configuration;
using Jellyfin.Xtream.Service;
using Jellyfin.Xtream.Service.ProviderManagement;
using Xunit;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Tests for the streaming timeout policy and failover behavior.
/// Validates industry-standard timeout values based on RFC 8216 (HLS),
/// Apple HLS Best Practices, Google Media CDN, and hls.js configuration.
/// </summary>
public sealed class FailoverTimeoutTests
{
    /// <summary>
    /// Verifies default connection timeout is 5 seconds (industry standard: 1-5s).
    /// </summary>
    [Fact]
    public void DefaultConnectTimeout_ShouldBe5Seconds()
    {
        Assert.Equal(5000, StreamingTimeoutPolicy.DefaultConnectTimeoutMs);
    }

    /// <summary>
    /// Verifies default first byte timeout is 5 seconds.
    /// </summary>
    [Fact]
    public void DefaultFirstByteTimeout_ShouldBe5Seconds()
    {
        Assert.Equal(5000, StreamingTimeoutPolicy.DefaultFirstByteTimeoutMs);
    }

    /// <summary>
    /// Verifies default data stall timeout is 10 seconds (industry standard: 10-20s).
    /// </summary>
    [Fact]
    public void DefaultDataStallTimeout_ShouldBe10Seconds()
    {
        Assert.Equal(10000, StreamingTimeoutPolicy.DefaultDataStallTimeoutMs);
    }

    /// <summary>
    /// Verifies default failover budget is 15 seconds (suitable for IPTV live TV scenarios).
    /// </summary>
    [Fact]
    public void DefaultFailoverBudget_ShouldBe15Seconds()
    {
        Assert.Equal(15000, StreamingTimeoutPolicy.DefaultFailoverBudgetMs);
    }

    /// <summary>
    /// Verifies default blacklist duration is 30 seconds (reduced from 2 minutes).
    /// </summary>
    [Fact]
    public void DefaultBlacklistDuration_ShouldBe30Seconds()
    {
        Assert.Equal(30000, StreamingTimeoutPolicy.DefaultBlacklistDurationMs);
    }

    /// <summary>
    /// Verifies extended blacklist duration is 60 seconds (reduced from 5 minutes).
    /// </summary>
    [Fact]
    public void ExtendedBlacklistDuration_ShouldBe60Seconds()
    {
        Assert.Equal(60000, StreamingTimeoutPolicy.ExtendedBlacklistDurationMs);
    }

    /// <summary>
    /// Verifies quick blacklist duration is 10 seconds for transient errors.
    /// </summary>
    [Fact]
    public void QuickBlacklistDuration_ShouldBe10Seconds()
    {
        Assert.Equal(10000, StreamingTimeoutPolicy.QuickBlacklistDurationMs);
    }

    /// <summary>
    /// Verifies failover backoff uses fast exponential progression.
    /// Expected: 0ms, 200ms, 400ms, 800ms, 1000ms (capped).
    /// </summary>
    [Theory]
    [InlineData(1, 0)] // First attempt: no delay
    [InlineData(2, 200)] // Second attempt: 200ms
    [InlineData(3, 400)] // Third attempt: 400ms
    [InlineData(4, 800)] // Fourth attempt: 800ms
    [InlineData(5, 1000)] // Fifth attempt: capped at 1000ms
    [InlineData(10, 1000)] // High attempt: still capped at 1000ms
    public void CalculateFailoverBackoff_ShouldUseFastExponential(int attemptNumber, int expectedMs)
    {
        int actual = StreamingTimeoutPolicy.CalculateFailoverBackoff(attemptNumber);
        Assert.Equal(expectedMs, actual);
    }

    /// <summary>
    /// Verifies stream open timeout combines connect and first byte timeouts.
    /// </summary>
    [Fact]
    public void GetStreamOpenTimeoutMs_ShouldCombineConnectAndFirstByte()
    {
        int expected =
            StreamingTimeoutPolicy.DefaultConnectTimeoutMs + StreamingTimeoutPolicy.DefaultFirstByteTimeoutMs;
        var config = new PluginConfiguration();
        int actual = StreamingTimeoutPolicy.GetStreamOpenTimeoutMs(config);
        Assert.Equal(expected, actual);
        Assert.Equal(10000, actual); // 5s + 5s = 10s
    }

    /// <summary>
    /// Verifies configuration overrides default timeout values.
    /// </summary>
    [Fact]
    public void GetConnectTimeoutMs_WithConfig_ShouldUseConfigValue()
    {
        var config = new PluginConfiguration { StreamConnectTimeoutSeconds = 7 };
        int actual = StreamingTimeoutPolicy.GetConnectTimeoutMs(config);
        Assert.Equal(7000, actual);
    }

    /// <summary>
    /// Verifies configuration with zero value falls back to default.
    /// </summary>
    [Fact]
    public void GetConnectTimeoutMs_WithZeroConfig_ShouldUseDefault()
    {
        var config = new PluginConfiguration { StreamConnectTimeoutSeconds = 0 };
        int actual = StreamingTimeoutPolicy.GetConnectTimeoutMs(config);
        Assert.Equal(StreamingTimeoutPolicy.DefaultConnectTimeoutMs, actual);
    }

    /// <summary>
    /// Verifies first byte timeout respects configuration.
    /// </summary>
    [Fact]
    public void GetFirstByteTimeoutMs_WithConfig_ShouldUseConfigValue()
    {
        var config = new PluginConfiguration { StreamFirstByteTimeoutSeconds = 3 };
        int actual = StreamingTimeoutPolicy.GetFirstByteTimeoutMs(config);
        Assert.Equal(3000, actual);
    }

    /// <summary>
    /// Verifies data stall timeout respects configuration.
    /// </summary>
    [Fact]
    public void GetDataStallTimeoutMs_WithConfig_ShouldUseConfigValue()
    {
        var config = new PluginConfiguration { StreamDataStallTimeoutSeconds = 15 };
        int actual = StreamingTimeoutPolicy.GetDataStallTimeoutMs(config);
        Assert.Equal(15000, actual);
    }

    /// <summary>
    /// Verifies failover budget respects configuration.
    /// </summary>
    [Fact]
    public void GetFailoverBudgetMs_WithConfig_ShouldUseConfigValue()
    {
        var config = new PluginConfiguration { FailoverBudgetSeconds = 8 };
        int actual = StreamingTimeoutPolicy.GetFailoverBudgetMs(config);
        Assert.Equal(8000, actual);
    }

    /// <summary>
    /// Verifies blacklist duration respects configuration.
    /// </summary>
    [Fact]
    public void GetBlacklistDuration_WithConfig_ShouldUseConfigValue()
    {
        var config = new PluginConfiguration { ProviderBlacklistSeconds = 45 };
        var actual = StreamingTimeoutPolicy.GetBlacklistDuration(config);
        Assert.Equal(TimeSpan.FromSeconds(45), actual);
    }

    /// <summary>
    /// Verifies max failover attempts respects configuration.
    /// </summary>
    [Fact]
    public void GetMaxFailoverAttempts_WithConfig_ShouldUseConfigValue()
    {
        var config = new PluginConfiguration { MaxFailoverAttempts = 6 };
        int actual = StreamingTimeoutPolicy.GetMaxFailoverAttempts(config);
        Assert.Equal(6, actual);
    }

    /// <summary>
    /// Verifies max failover attempts uses default when config is zero.
    /// </summary>
    [Fact]
    public void GetMaxFailoverAttempts_WithZeroConfig_ShouldUseDefault()
    {
        var config = new PluginConfiguration { MaxFailoverAttempts = 0 };
        int actual = StreamingTimeoutPolicy.GetMaxFailoverAttempts(config);
        Assert.Equal(StreamingTimeoutPolicy.DefaultMaxFailoverAttempts, actual);
    }

    /// <summary>
    /// Verifies empty config falls back to defaults.
    /// </summary>
    [Fact]
    public void GetConnectTimeoutMs_WithEmptyConfig_ShouldUseDefault()
    {
        var config = new PluginConfiguration();
        int actual = StreamingTimeoutPolicy.GetConnectTimeoutMs(config);
        Assert.Equal(StreamingTimeoutPolicy.DefaultConnectTimeoutMs, actual);
    }

    /// <summary>
    /// Verifies extended blacklist duration getter.
    /// </summary>
    [Fact]
    public void GetExtendedBlacklistDuration_ShouldReturn60Seconds()
    {
        var actual = StreamingTimeoutPolicy.GetExtendedBlacklistDuration();
        Assert.Equal(TimeSpan.FromSeconds(60), actual);
    }

    /// <summary>
    /// Verifies quick blacklist duration getter.
    /// </summary>
    [Fact]
    public void GetQuickBlacklistDuration_ShouldReturn10Seconds()
    {
        var actual = StreamingTimeoutPolicy.GetQuickBlacklistDuration();
        Assert.Equal(TimeSpan.FromSeconds(10), actual);
    }

    /// <summary>
    /// Verifies blacklist threshold is 3 consecutive failures.
    /// </summary>
    [Fact]
    public void BlacklistThreshold_ShouldBe3()
    {
        Assert.Equal(3, StreamingTimeoutPolicy.BlacklistThreshold);
    }

    /// <summary>
    /// Verifies default max failover attempts is 4.
    /// </summary>
    [Fact]
    public void DefaultMaxFailoverAttempts_ShouldBe4()
    {
        Assert.Equal(4, StreamingTimeoutPolicy.DefaultMaxFailoverAttempts);
    }

    /// <summary>
    /// Verifies reconnect delay constant.
    /// </summary>
    [Fact]
    public void ReconnectDelayMs_ShouldBe500()
    {
        Assert.Equal(500, StreamingTimeoutPolicy.ReconnectDelayMs);
    }

    /// <summary>
    /// Verifies failover delay base constant.
    /// </summary>
    [Fact]
    public void FailoverDelayBaseMs_ShouldBe200()
    {
        Assert.Equal(200, StreamingTimeoutPolicy.FailoverDelayBaseMs);
    }

    /// <summary>
    /// Verifies failover delay max constant.
    /// </summary>
    [Fact]
    public void FailoverDelayMaxMs_ShouldBe1000()
    {
        Assert.Equal(1000, StreamingTimeoutPolicy.FailoverDelayMaxMs);
    }

    /// <summary>
    /// Verifies minimum per-attempt timeout constant.
    /// IPTV providers often have slow initial response times (3-5s is common).
    /// </summary>
    [Fact]
    public void MinPerAttemptTimeoutMs_ShouldBe5000()
    {
        Assert.Equal(5000, StreamingTimeoutPolicy.MinPerAttemptTimeoutMs);
    }

    /// <summary>
    /// Verifies per-attempt timeout divides budget fairly among attempts.
    /// With 15s budget and 4 attempts remaining, each gets ~3750ms (but min 5000ms applies).
    /// </summary>
    [Theory]
    [InlineData(15000, 4, 5000)] // 15000/4=3750 but min is 5000
    [InlineData(15000, 3, 5000)] // 15000/3=5000
    [InlineData(15000, 2, 7500)] // 15000/2=7500
    [InlineData(15000, 1, 10000)] // 15000/1=15000 but capped at stream open timeout (10s)
    [InlineData(8000, 4, 5000)] // 8000/4=2000 but min is 5000
    [InlineData(3000, 4, 3000)] // Below min threshold, capped at remaining budget
    public void CalculatePerAttemptTimeout_ShouldDivideBudgetFairly(
        int remainingBudgetMs,
        int attemptsRemaining,
        int expectedMs
    )
    {
        var config = new PluginConfiguration();
        int actual = StreamingTimeoutPolicy.CalculatePerAttemptTimeout(remainingBudgetMs, attemptsRemaining, config);
        Assert.Equal(expectedMs, actual);
    }

    /// <summary>
    /// Verifies per-attempt timeout is capped at stream open timeout.
    /// Even with large budget, per-attempt should not exceed connect + first byte timeout.
    /// </summary>
    [Fact]
    public void CalculatePerAttemptTimeout_ShouldNotExceedStreamOpenTimeout()
    {
        var config = new PluginConfiguration();
        int streamOpenTimeout = StreamingTimeoutPolicy.GetStreamOpenTimeoutMs(config);

        // With 30s budget and 1 attempt, would get 30s but capped at 10s
        int actual = StreamingTimeoutPolicy.CalculatePerAttemptTimeout(30000, 1, config);

        Assert.Equal(streamOpenTimeout, actual);
        Assert.Equal(10000, actual);
    }

    /// <summary>
    /// Verifies per-attempt timeout returns 0 when budget is exhausted.
    /// </summary>
    [Theory]
    [InlineData(0, 4)]
    [InlineData(-100, 4)]
    public void CalculatePerAttemptTimeout_WithNoBudget_ShouldReturnZero(int remainingBudgetMs, int attemptsRemaining)
    {
        var config = new PluginConfiguration();
        int actual = StreamingTimeoutPolicy.CalculatePerAttemptTimeout(remainingBudgetMs, attemptsRemaining, config);
        Assert.Equal(0, actual);
    }

    /// <summary>
    /// Verifies per-attempt timeout handles zero attempts remaining gracefully.
    /// </summary>
    [Fact]
    public void CalculatePerAttemptTimeout_WithZeroAttempts_ShouldUseFullBudget()
    {
        var config = new PluginConfiguration();
        int actual = StreamingTimeoutPolicy.CalculatePerAttemptTimeout(5000, 0, config);
        // With 0 attempts remaining, uses full budget (capped at stream open timeout)
        Assert.Equal(5000, actual);
    }

    /// <summary>
    /// Integration test: verifies worst-case failover scenario timing.
    /// With 4 attempts and 15s budget, backoff consumes 1.4s, leaving 13.6s for attempts.
    /// </summary>
    [Fact]
    public void WorstCaseFailover_ShouldCompleteWithin15Seconds()
    {
        // Calculate theoretical worst case:
        // - Attempt 1: 0ms backoff + up to 10s stream open timeout (capped by stream open timeout)
        // - Attempt 2: 200ms backoff
        // - Attempt 3: 400ms backoff
        // - Attempt 4: 800ms backoff
        // Total backoff: 1400ms
        // Remaining for actual attempts: 15000 - 1400 = 13600ms
        // Each attempt timeout capped by stream open timeout (10s)

        int budget = StreamingTimeoutPolicy.DefaultFailoverBudgetMs;
        int maxAttempts = StreamingTimeoutPolicy.DefaultMaxFailoverAttempts;
        int totalBackoff = 0;

        for (int i = 1; i <= maxAttempts; i++)
        {
            totalBackoff += StreamingTimeoutPolicy.CalculateFailoverBackoff(i);
        }

        // Total backoff should be manageable (1400ms = 0 + 200 + 400 + 800)
        Assert.Equal(1400, totalBackoff);

        // Budget should accommodate backoff plus ample attempt time
        Assert.True(budget > totalBackoff, "Budget should be greater than total backoff time");
        Assert.True(budget - totalBackoff >= 10000, "Should have at least 10s for actual connection attempts");
    }

    /// <summary>
    /// Integration test: simulates realistic failover scenario with per-attempt timeouts.
    /// Verifies multiple providers can be tried within the 15s budget.
    /// </summary>
    [Fact]
    public void RealisticFailover_ShouldAllowMultipleAttempts()
    {
        var config = new PluginConfiguration();
        int budget = StreamingTimeoutPolicy.DefaultFailoverBudgetMs;
        int maxAttempts = StreamingTimeoutPolicy.DefaultMaxFailoverAttempts;

        // Simulate failover loop
        int remainingBudget = budget;
        int attemptsMade = 0;
        int totalTimeUsed = 0;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (remainingBudget <= 0)
            {
                break;
            }

            // Add backoff delay (not on first attempt)
            if (attempt > 1)
            {
                int backoff = StreamingTimeoutPolicy.CalculateFailoverBackoff(attempt);
                remainingBudget -= backoff;
                totalTimeUsed += backoff;
            }

            if (remainingBudget <= 0)
            {
                break;
            }

            // Calculate per-attempt timeout
            int attemptsRemaining = maxAttempts - attempt + 1;
            int perAttemptTimeout = StreamingTimeoutPolicy.CalculatePerAttemptTimeout(
                remainingBudget,
                attemptsRemaining,
                config
            );

            // Simulate worst case: provider times out
            remainingBudget -= perAttemptTimeout;
            totalTimeUsed += perAttemptTimeout;
            attemptsMade++;
        }

        // With fair allocation, we should be able to try at least 3 providers
        Assert.True(attemptsMade >= 3, $"Should try at least 3 providers, but only tried {attemptsMade}");

        // Total time should not exceed budget (with some tolerance for rounding)
        Assert.True(totalTimeUsed <= budget + 100, $"Total time {totalTimeUsed}ms should not exceed budget {budget}ms");
    }
}
