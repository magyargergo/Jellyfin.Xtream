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
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Jellyfin.Xtream.Service.Resilience;

/// <summary>
/// Circuit breaker state machine for provider resilience.
/// Implements the standard circuit breaker pattern with configurable thresholds.
/// </summary>
public sealed class CircuitBreaker
{
    private readonly CircuitBreakerConfig _config;
    private readonly object _lock = new();

    private CircuitState _state = CircuitState.Closed;
    private int _failureCount;
    private DateTime _lastFailureTime = DateTime.MinValue;
    private DateTime _openedAt = DateTime.MinValue;
    private int _halfOpenAttempts;

    /// <summary>
    /// Initializes a new instance of the <see cref="CircuitBreaker"/> class.
    /// </summary>
    /// <param name="config">Circuit breaker configuration.</param>
    public CircuitBreaker(CircuitBreakerConfig? config = null)
    {
        _config = config ?? CircuitBreakerConfig.Default;
    }

    /// <summary>
    /// Gets the current state of the circuit breaker.
    /// </summary>
    public CircuitState State
    {
        get
        {
            lock (_lock)
            {
                UpdateState();
                return _state;
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the circuit is allowing requests.
    /// </summary>
    public bool IsAllowingRequests => State != CircuitState.Open;

    /// <summary>
    /// Gets the current failure count.
    /// </summary>
    public int FailureCount => Volatile.Read(ref _failureCount);

    /// <summary>
    /// Occurs when the circuit breaker state changes.
    /// </summary>
    public event EventHandler<CircuitStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Determines if a request should be allowed based on current circuit state.
    /// </summary>
    /// <returns>True if the request should proceed, false if it should be blocked.</returns>
    public bool ShouldAllow()
    {
        lock (_lock)
        {
            UpdateState();

            return _state switch
            {
                CircuitState.Closed => true,
                CircuitState.Open => false,
                CircuitState.HalfOpen => _halfOpenAttempts < _config.HalfOpenMaxAttempts,
                _ => true,
            };
        }
    }

    /// <summary>
    /// Records a successful operation.
    /// </summary>
    public void RecordSuccess()
    {
        lock (_lock)
        {
            if (_state == CircuitState.HalfOpen)
            {
                // Success in half-open state closes the circuit
                TransitionTo(CircuitState.Closed);
            }

            // Reset failure count on success
            _failureCount = 0;
        }
    }

    /// <summary>
    /// Records a failed operation.
    /// </summary>
    /// <param name="exception">Optional exception that caused the failure.</param>
    public void RecordFailure(Exception? exception = null)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;

            // Reset failure count if outside the failure window
            if (now - _lastFailureTime > _config.FailureWindow)
            {
                _failureCount = 0;
            }

            _lastFailureTime = now;
            _failureCount++;

            if (_state == CircuitState.HalfOpen)
            {
                // Failure in half-open state reopens the circuit
                TransitionTo(CircuitState.Open);
            }
            else if (_state == CircuitState.Closed && _failureCount >= _config.FailureThreshold)
            {
                // Threshold exceeded - open the circuit
                TransitionTo(CircuitState.Open);
            }
        }
    }

    /// <summary>
    /// Manually opens the circuit breaker.
    /// </summary>
    /// <param name="reason">Reason for manual opening.</param>
    public void Trip(string? reason = null)
    {
        lock (_lock)
        {
            if (_state != CircuitState.Open)
            {
                TransitionTo(CircuitState.Open, reason ?? "Manual trip");
            }
        }
    }

    /// <summary>
    /// Manually resets the circuit breaker to closed state.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _failureCount = 0;
            _halfOpenAttempts = 0;
            TransitionTo(CircuitState.Closed, "Manual reset");
        }
    }

    /// <summary>
    /// Gets a snapshot of the current circuit breaker status.
    /// </summary>
    /// <returns>Status snapshot.</returns>
    public CircuitBreakerStatus GetStatus()
    {
        lock (_lock)
        {
            UpdateState();
            return new CircuitBreakerStatus
            {
                State = _state,
                FailureCount = _failureCount,
                LastFailureTime = _lastFailureTime == DateTime.MinValue ? null : _lastFailureTime,
                OpenedAt = _state == CircuitState.Open ? _openedAt : null,
                TimeUntilHalfOpen =
                    _state == CircuitState.Open ? _openedAt + _config.OpenDuration - DateTime.UtcNow : null,
                HalfOpenAttemptsRemaining =
                    _state == CircuitState.HalfOpen ? _config.HalfOpenMaxAttempts - _halfOpenAttempts : null,
            };
        }
    }

    private void UpdateState()
    {
        // Check if open circuit should transition to half-open
        if (_state == CircuitState.Open)
        {
            var now = DateTime.UtcNow;
            if (now - _openedAt >= _config.OpenDuration)
            {
                TransitionTo(CircuitState.HalfOpen);
            }
        }
    }

    private void TransitionTo(CircuitState newState, string? reason = null)
    {
        var previousState = _state;
        if (previousState == newState)
        {
            return;
        }

        _state = newState;

        switch (newState)
        {
            case CircuitState.Open:
                _openedAt = DateTime.UtcNow;
                _halfOpenAttempts = 0;
                break;
            case CircuitState.HalfOpen:
                _halfOpenAttempts = 0;
                break;
            case CircuitState.Closed:
                _failureCount = 0;
                _halfOpenAttempts = 0;
                break;
        }

        StateChanged?.Invoke(this, new CircuitStateChangedEventArgs(previousState, newState, reason));
    }
}

/// <summary>
/// Circuit breaker state.
/// </summary>
public enum CircuitState
{
    /// <summary>
    /// Circuit is closed, requests flow normally.
    /// </summary>
    Closed,

    /// <summary>
    /// Circuit is open, requests are blocked.
    /// </summary>
    Open,

    /// <summary>
    /// Circuit is testing if service has recovered.
    /// </summary>
    HalfOpen,
}

/// <summary>
/// Configuration for the circuit breaker.
/// </summary>
public sealed record CircuitBreakerConfig
{
    /// <summary>
    /// Gets the default configuration.
    /// </summary>
    public static CircuitBreakerConfig Default { get; } = new();

    /// <summary>
    /// Gets the number of failures required to open the circuit.
    /// </summary>
    public int FailureThreshold { get; init; } = 3;

    /// <summary>
    /// Gets the window during which failures are counted.
    /// </summary>
    public TimeSpan FailureWindow { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Gets the duration the circuit stays open before transitioning to half-open.
    /// </summary>
    public TimeSpan OpenDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets the maximum number of test requests in half-open state.
    /// </summary>
    public int HalfOpenMaxAttempts { get; init; } = 2;
}

/// <summary>
/// Event args for circuit state changes.
/// </summary>
public sealed class CircuitStateChangedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CircuitStateChangedEventArgs"/> class.
    /// </summary>
    /// <param name="previousState">The previous state.</param>
    /// <param name="newState">The new state.</param>
    /// <param name="reason">Optional reason for the transition.</param>
    public CircuitStateChangedEventArgs(CircuitState previousState, CircuitState newState, string? reason)
    {
        PreviousState = previousState;
        NewState = newState;
        Reason = reason;
    }

    /// <summary>
    /// Gets the previous state.
    /// </summary>
    public CircuitState PreviousState { get; }

    /// <summary>
    /// Gets the new state.
    /// </summary>
    public CircuitState NewState { get; }

    /// <summary>
    /// Gets the optional reason for the transition.
    /// </summary>
    public string? Reason { get; }
}

/// <summary>
/// Circuit breaker status snapshot.
/// </summary>
public sealed record CircuitBreakerStatus
{
    /// <summary>
    /// Gets the current state.
    /// </summary>
    public required CircuitState State { get; init; }

    /// <summary>
    /// Gets the current failure count.
    /// </summary>
    public required int FailureCount { get; init; }

    /// <summary>
    /// Gets the time of the last failure.
    /// </summary>
    public required DateTime? LastFailureTime { get; init; }

    /// <summary>
    /// Gets the time when the circuit was opened.
    /// </summary>
    public required DateTime? OpenedAt { get; init; }

    /// <summary>
    /// Gets the time until the circuit transitions to half-open.
    /// </summary>
    public required TimeSpan? TimeUntilHalfOpen { get; init; }

    /// <summary>
    /// Gets the remaining test attempts in half-open state.
    /// </summary>
    public required int? HalfOpenAttemptsRemaining { get; init; }
}

/// <summary>
/// Registry for managing multiple circuit breakers.
/// </summary>
public sealed class CircuitBreakerRegistry
{
    private readonly ConcurrentDictionary<string, CircuitBreaker> _breakers = new(StringComparer.Ordinal);
    private readonly CircuitBreakerConfig _defaultConfig;

    /// <summary>
    /// Initializes a new instance of the <see cref="CircuitBreakerRegistry"/> class.
    /// </summary>
    /// <param name="defaultConfig">Default configuration for new circuit breakers.</param>
    public CircuitBreakerRegistry(CircuitBreakerConfig? defaultConfig = null)
    {
        _defaultConfig = defaultConfig ?? CircuitBreakerConfig.Default;
    }

    /// <summary>
    /// Occurs when any circuit breaker in the registry changes state.
    /// </summary>
    public event EventHandler<RegistryCircuitStateChangedEventArgs>? CircuitStateChanged;

    /// <summary>
    /// Gets the circuit breaker for a given key, creating one if it doesn't exist.
    /// </summary>
    /// <param name="key">The circuit breaker key (e.g., provider ID).</param>
    /// <returns>The circuit breaker.</returns>
    public CircuitBreaker GetOrCreate(string key)
    {
        return _breakers.GetOrAdd(
            key,
            _ =>
            {
                var breaker = new CircuitBreaker(_defaultConfig);
                breaker.StateChanged += (_, args) =>
                    CircuitStateChanged?.Invoke(this, new RegistryCircuitStateChangedEventArgs(key, args));
                return breaker;
            }
        );
    }

    /// <summary>
    /// Checks if a circuit is open (blocking requests).
    /// </summary>
    /// <param name="key">The circuit breaker key.</param>
    /// <returns>True if the circuit is open.</returns>
    public bool IsOpen(string key)
    {
        return _breakers.TryGetValue(key, out var breaker) && breaker.State == CircuitState.Open;
    }

    /// <summary>
    /// Checks if a circuit is allowing requests.
    /// </summary>
    /// <param name="key">The circuit breaker key.</param>
    /// <returns>True if requests are allowed.</returns>
    public bool IsAllowing(string key)
    {
        return !_breakers.TryGetValue(key, out var breaker) || breaker.IsAllowingRequests;
    }

    /// <summary>
    /// Records a success for a circuit breaker.
    /// </summary>
    /// <param name="key">The circuit breaker key.</param>
    public void RecordSuccess(string key)
    {
        if (_breakers.TryGetValue(key, out var breaker))
        {
            breaker.RecordSuccess();
        }
    }

    /// <summary>
    /// Records a failure for a circuit breaker.
    /// </summary>
    /// <param name="key">The circuit breaker key.</param>
    /// <param name="exception">Optional exception that caused the failure.</param>
    public void RecordFailure(string key, Exception? exception = null)
    {
        GetOrCreate(key).RecordFailure(exception);
    }

    /// <summary>
    /// Gets all circuit breakers with their status.
    /// </summary>
    /// <returns>Dictionary of key to status.</returns>
    public IReadOnlyDictionary<string, CircuitBreakerStatus> GetAllStatus()
    {
        return _breakers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.GetStatus(), StringComparer.Ordinal);
    }

    /// <summary>
    /// Gets all open circuit breakers.
    /// </summary>
    /// <returns>List of keys with open circuits.</returns>
    public IReadOnlyList<string> GetOpenCircuits()
    {
        return _breakers.Where(kvp => kvp.Value.State == CircuitState.Open).Select(kvp => kvp.Key).ToList();
    }

    /// <summary>
    /// Resets all circuit breakers.
    /// </summary>
    public void ResetAll()
    {
        foreach (var breaker in _breakers.Values)
        {
            breaker.Reset();
        }
    }

    /// <summary>
    /// Removes a circuit breaker from the registry.
    /// </summary>
    /// <param name="key">The circuit breaker key.</param>
    /// <returns>True if removed, false if not found.</returns>
    public bool Remove(string key) => _breakers.TryRemove(key, out _);
}

/// <summary>
/// Event args for registry-level circuit state changes.
/// </summary>
public sealed class RegistryCircuitStateChangedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RegistryCircuitStateChangedEventArgs"/> class.
    /// </summary>
    /// <param name="key">The circuit breaker key.</param>
    /// <param name="stateChange">The state change details.</param>
    public RegistryCircuitStateChangedEventArgs(string key, CircuitStateChangedEventArgs stateChange)
    {
        Key = key;
        StateChange = stateChange;
    }

    /// <summary>
    /// Gets the circuit breaker key.
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// Gets the state change details.
    /// </summary>
    public CircuitStateChangedEventArgs StateChange { get; }
}
