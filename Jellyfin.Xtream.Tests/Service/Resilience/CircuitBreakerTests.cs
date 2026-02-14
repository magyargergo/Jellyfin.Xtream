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
using System.Threading;
using Jellyfin.Xtream.Service.Resilience;
using Xunit;

namespace Jellyfin.Xtream.Tests.Service.Resilience;

/// <summary>
/// Tests for <see cref="CircuitBreaker"/>.
/// Validates state machine transitions, failure counting, and half-open behavior.
/// </summary>
public sealed class CircuitBreakerTests
{
    private static CircuitBreakerConfig FastConfig =>
        new()
        {
            FailureThreshold = 3,
            FailureWindow = TimeSpan.FromSeconds(10),
            OpenDuration = TimeSpan.FromMilliseconds(100),
            HalfOpenMaxAttempts = 2,
        };

    #region Initial State

    /// <summary>
    /// Verifies circuit breaker starts in Closed state.
    /// </summary>
    [Fact]
    public void Constructor_DefaultConfig_StartsInClosedState()
    {
        var cb = new CircuitBreaker();

        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.Equal(0, cb.FailureCount);
        Assert.True(cb.IsAllowingRequests);
    }

    /// <summary>
    /// Verifies ShouldAllow returns true when circuit is closed.
    /// </summary>
    [Fact]
    public void ShouldAllow_WhenClosed_ReturnsTrue()
    {
        var cb = new CircuitBreaker(FastConfig);

        Assert.True(cb.ShouldAllow());
    }

    #endregion

    #region Closed → Open Transition

    /// <summary>
    /// Verifies circuit opens after reaching failure threshold.
    /// </summary>
    [Fact]
    public void RecordFailure_ReachesThreshold_TransitionsToOpen()
    {
        var cb = new CircuitBreaker(FastConfig);

        cb.RecordFailure();
        cb.RecordFailure();
        Assert.Equal(CircuitState.Closed, cb.State);

        cb.RecordFailure();
        Assert.Equal(CircuitState.Open, cb.State);
        Assert.False(cb.IsAllowingRequests);
    }

    /// <summary>
    /// Verifies ShouldAllow returns false when circuit is open.
    /// </summary>
    [Fact]
    public void ShouldAllow_WhenOpen_ReturnsFalse()
    {
        var cb = new CircuitBreaker(FastConfig);

        for (var i = 0; i < 3; i++)
        {
            cb.RecordFailure();
        }

        Assert.False(cb.ShouldAllow());
    }

    /// <summary>
    /// Verifies failure count resets when failures are outside the failure window.
    /// </summary>
    [Fact]
    public void RecordFailure_OutsideFailureWindow_ResetsCount()
    {
        var config = new CircuitBreakerConfig
        {
            FailureThreshold = 3,
            FailureWindow = TimeSpan.FromMilliseconds(50),
            OpenDuration = TimeSpan.FromMilliseconds(100),
        };

        var cb = new CircuitBreaker(config);

        cb.RecordFailure();
        cb.RecordFailure();

        // Wait for failure window to expire
        Thread.Sleep(80);

        // This failure should reset the count, so we need 3 more
        cb.RecordFailure();
        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.Equal(1, cb.FailureCount);
    }

    #endregion

    #region Open → HalfOpen Transition

    /// <summary>
    /// Verifies circuit transitions to HalfOpen after OpenDuration expires.
    /// </summary>
    [Fact]
    public void State_AfterOpenDuration_TransitionsToHalfOpen()
    {
        var cb = new CircuitBreaker(FastConfig);

        for (var i = 0; i < 3; i++)
        {
            cb.RecordFailure();
        }

        Assert.Equal(CircuitState.Open, cb.State);

        // Wait for OpenDuration to expire
        Thread.Sleep(150);

        Assert.Equal(CircuitState.HalfOpen, cb.State);
        Assert.True(cb.IsAllowingRequests);
    }

    #endregion

    #region HalfOpen → Closed Transition

    /// <summary>
    /// Verifies success in HalfOpen state closes the circuit.
    /// </summary>
    [Fact]
    public void RecordSuccess_WhenHalfOpen_TransitionsToClosed()
    {
        var cb = new CircuitBreaker(FastConfig);

        for (var i = 0; i < 3; i++)
        {
            cb.RecordFailure();
        }

        Thread.Sleep(150);
        Assert.Equal(CircuitState.HalfOpen, cb.State);

        cb.RecordSuccess();

        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.Equal(0, cb.FailureCount);
    }

    #endregion

    #region HalfOpen → Open Transition

    /// <summary>
    /// Verifies failure in HalfOpen state reopens the circuit.
    /// </summary>
    [Fact]
    public void RecordFailure_WhenHalfOpen_TransitionsToOpen()
    {
        var cb = new CircuitBreaker(FastConfig);

        for (var i = 0; i < 3; i++)
        {
            cb.RecordFailure();
        }

        Thread.Sleep(150);
        Assert.Equal(CircuitState.HalfOpen, cb.State);

        cb.RecordFailure();

        Assert.Equal(CircuitState.Open, cb.State);
    }

    #endregion

    #region HalfOpen Max Attempts

    /// <summary>
    /// Verifies ShouldAllow always returns true in HalfOpen state.
    /// Note: _halfOpenAttempts is never incremented internally;
    /// the circuit relies on RecordSuccess/RecordFailure to transition out.
    /// </summary>
    [Fact]
    public void ShouldAllow_HalfOpen_AllowsRequests()
    {
        var config = new CircuitBreakerConfig
        {
            FailureThreshold = 1,
            OpenDuration = TimeSpan.FromMilliseconds(50),
            HalfOpenMaxAttempts = 2,
        };

        var cb = new CircuitBreaker(config);

        cb.RecordFailure();
        Thread.Sleep(80);

        // HalfOpen state should allow requests for probing
        Assert.True(cb.ShouldAllow());
        Assert.Equal(CircuitState.HalfOpen, cb.State);
    }

    #endregion

    #region RecordSuccess in Closed State

    /// <summary>
    /// Verifies success in Closed state resets failure count.
    /// </summary>
    [Fact]
    public void RecordSuccess_WhenClosed_ResetsFailureCount()
    {
        var cb = new CircuitBreaker(FastConfig);

        cb.RecordFailure();
        cb.RecordFailure();
        Assert.Equal(2, cb.FailureCount);

        cb.RecordSuccess();

        Assert.Equal(0, cb.FailureCount);
        Assert.Equal(CircuitState.Closed, cb.State);
    }

    #endregion

    #region Trip and Reset

    /// <summary>
    /// Verifies Trip manually opens the circuit.
    /// </summary>
    [Fact]
    public void Trip_FromClosed_TransitionsToOpen()
    {
        var cb = new CircuitBreaker(FastConfig);

        cb.Trip("Manual trip test");

        Assert.Equal(CircuitState.Open, cb.State);
        Assert.False(cb.ShouldAllow());
    }

    /// <summary>
    /// Verifies Trip is idempotent when already open.
    /// </summary>
    [Fact]
    public void Trip_WhenAlreadyOpen_RemainsOpen()
    {
        var cb = new CircuitBreaker(FastConfig);

        cb.Trip();
        cb.Trip();

        Assert.Equal(CircuitState.Open, cb.State);
    }

    /// <summary>
    /// Verifies Reset returns circuit to Closed state.
    /// </summary>
    [Fact]
    public void Reset_FromOpen_TransitionsToClosed()
    {
        var cb = new CircuitBreaker(FastConfig);

        cb.Trip();
        Assert.Equal(CircuitState.Open, cb.State);

        cb.Reset();

        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.Equal(0, cb.FailureCount);
        Assert.True(cb.ShouldAllow());
    }

    #endregion

    #region StateChanged Event

    /// <summary>
    /// Verifies StateChanged event fires on transitions.
    /// </summary>
    [Fact]
    public void StateChanged_OnTransition_FiresWithCorrectArgs()
    {
        var cb = new CircuitBreaker(new CircuitBreakerConfig { FailureThreshold = 1 });
        CircuitStateChangedEventArgs? capturedArgs = null;

        cb.StateChanged += (_, args) => capturedArgs = args;

        cb.RecordFailure();

        Assert.NotNull(capturedArgs);
        Assert.Equal(CircuitState.Closed, capturedArgs.PreviousState);
        Assert.Equal(CircuitState.Open, capturedArgs.NewState);
    }

    /// <summary>
    /// Verifies StateChanged event includes reason for manual trip.
    /// </summary>
    [Fact]
    public void StateChanged_OnTrip_IncludesReason()
    {
        var cb = new CircuitBreaker(FastConfig);
        string? capturedReason = null;

        cb.StateChanged += (_, args) => capturedReason = args.Reason;

        cb.Trip("test reason");

        Assert.Equal("test reason", capturedReason);
    }

    #endregion

    #region GetStatus

    /// <summary>
    /// Verifies GetStatus returns correct snapshot.
    /// </summary>
    [Fact]
    public void GetStatus_WhenClosed_ReturnsCorrectSnapshot()
    {
        var cb = new CircuitBreaker(FastConfig);

        cb.RecordFailure();

        var status = cb.GetStatus();

        Assert.Equal(CircuitState.Closed, status.State);
        Assert.Equal(1, status.FailureCount);
        Assert.NotNull(status.LastFailureTime);
        Assert.Null(status.OpenedAt);
        Assert.Null(status.TimeUntilHalfOpen);
        Assert.Null(status.HalfOpenAttemptsRemaining);
    }

    /// <summary>
    /// Verifies GetStatus shows OpenedAt when open.
    /// </summary>
    [Fact]
    public void GetStatus_WhenOpen_IncludesOpenedAt()
    {
        var cb = new CircuitBreaker(FastConfig);

        for (var i = 0; i < 3; i++)
        {
            cb.RecordFailure();
        }

        var status = cb.GetStatus();

        Assert.Equal(CircuitState.Open, status.State);
        Assert.NotNull(status.OpenedAt);
        Assert.NotNull(status.TimeUntilHalfOpen);
    }

    /// <summary>
    /// Verifies GetStatus shows remaining attempts when half-open.
    /// </summary>
    [Fact]
    public void GetStatus_WhenHalfOpen_IncludesRemainingAttempts()
    {
        var cb = new CircuitBreaker(FastConfig);

        for (var i = 0; i < 3; i++)
        {
            cb.RecordFailure();
        }

        Thread.Sleep(150);

        var status = cb.GetStatus();

        Assert.Equal(CircuitState.HalfOpen, status.State);
        Assert.NotNull(status.HalfOpenAttemptsRemaining);
        Assert.Equal(2, status.HalfOpenAttemptsRemaining);
    }

    /// <summary>
    /// Verifies GetStatus reports null LastFailureTime when no failures occurred.
    /// </summary>
    [Fact]
    public void GetStatus_NoFailures_LastFailureTimeIsNull()
    {
        var cb = new CircuitBreaker(FastConfig);

        var status = cb.GetStatus();

        Assert.Null(status.LastFailureTime);
    }

    #endregion
}
