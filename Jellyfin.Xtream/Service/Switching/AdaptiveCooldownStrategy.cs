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
using System.Runtime.CompilerServices;
using System.Threading;

namespace Jellyfin.Xtream.Service.Switching;

/// <summary>
/// Adaptive cooldown strategy that adjusts switch cooldown based on recent success rate.
/// Reduces cooldown when switches are successful, increases when they fail.
/// </summary>
internal sealed class AdaptiveCooldownStrategy
{
    private const int DefaultCooldownMs = 5000;
    private const int MinCooldownMs = 2000;
    private const int MaxCooldownMs = 30000;
    private const int HistorySize = 10;
    private const double SuccessRateThresholdHigh = 0.8;
    private const double SuccessRateThresholdLow = 0.5;
    private const int CooldownAdjustmentMs = 1000;

    private readonly bool[] _recentResults;
    private int _resultIndex;
    private int _resultCount;
    private int _currentCooldownMs;
    private long _lastAdjustmentTicks;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdaptiveCooldownStrategy"/> class.
    /// </summary>
    /// <param name="initialCooldownMs">Initial cooldown in milliseconds.</param>
    public AdaptiveCooldownStrategy(int initialCooldownMs = DefaultCooldownMs)
    {
        _recentResults = new bool[HistorySize];
        _currentCooldownMs = Math.Clamp(initialCooldownMs, MinCooldownMs, MaxCooldownMs);
        _lastAdjustmentTicks = Environment.TickCount64;
    }

    /// <summary>
    /// Gets the current cooldown duration in milliseconds.
    /// </summary>
    public int CurrentCooldownMs => Volatile.Read(ref _currentCooldownMs);

    /// <summary>
    /// Gets the current success rate (0.0 to 1.0).
    /// </summary>
    public double SuccessRate
    {
        get
        {
            int count = Volatile.Read(ref _resultCount);
            if (count == 0)
            {
                return 1.0;
            }

            int successes = 0;
            int samplesToCheck = Math.Min(count, HistorySize);
            for (int i = 0; i < samplesToCheck; i++)
            {
                if (Volatile.Read(ref _recentResults[i]))
                {
                    successes++;
                }
            }

            return (double)successes / samplesToCheck;
        }
    }

    /// <summary>
    /// Gets the number of samples recorded.
    /// </summary>
    public int SampleCount => Math.Min(Volatile.Read(ref _resultCount), HistorySize);

    /// <summary>
    /// Records a switch result and adjusts cooldown accordingly.
    /// </summary>
    /// <param name="success">Whether the switch was successful.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordResult(bool success)
    {
        int index = Interlocked.Increment(ref _resultIndex) % HistorySize;
        Volatile.Write(ref _recentResults[index], success);
        Interlocked.Increment(ref _resultCount);

        AdjustCooldown();
    }

    /// <summary>
    /// Checks if cooldown period has elapsed since the given time.
    /// </summary>
    /// <param name="lastSwitchTime">The time of the last switch.</param>
    /// <returns>True if cooldown has elapsed.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasCooldownElapsed(DateTime lastSwitchTime)
    {
        var elapsed = (DateTime.UtcNow - lastSwitchTime).TotalMilliseconds;
        return elapsed >= CurrentCooldownMs;
    }

    /// <summary>
    /// Gets the remaining cooldown time in milliseconds.
    /// </summary>
    /// <param name="lastSwitchTime">The time of the last switch.</param>
    /// <returns>Remaining cooldown in milliseconds, or 0 if elapsed.</returns>
    public int GetRemainingCooldownMs(DateTime lastSwitchTime)
    {
        var elapsed = (DateTime.UtcNow - lastSwitchTime).TotalMilliseconds;
        var remaining = CurrentCooldownMs - elapsed;
        return remaining > 0 ? (int)remaining : 0;
    }

    /// <summary>
    /// Resets the strategy to initial state.
    /// </summary>
    public void Reset()
    {
        Array.Clear(_recentResults, 0, HistorySize);
        _resultIndex = 0;
        _resultCount = 0;
        _currentCooldownMs = DefaultCooldownMs;
        _lastAdjustmentTicks = Environment.TickCount64;
    }

    private void AdjustCooldown()
    {
        long now = Environment.TickCount64;
        long lastAdjust = Interlocked.Read(ref _lastAdjustmentTicks);

        if (now - lastAdjust < 10000)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastAdjustmentTicks, now, lastAdjust) != lastAdjust)
        {
            return;
        }

        int count = Volatile.Read(ref _resultCount);
        if (count < 3)
        {
            return;
        }

        double rate = SuccessRate;
        int current = Volatile.Read(ref _currentCooldownMs);
        int newCooldown = current;

        if (rate >= SuccessRateThresholdHigh)
        {
            newCooldown = Math.Max(MinCooldownMs, current - CooldownAdjustmentMs);
        }
        else if (rate < SuccessRateThresholdLow)
        {
            newCooldown = Math.Min(MaxCooldownMs, current + (CooldownAdjustmentMs * 2));
        }

        if (newCooldown != current)
        {
            Volatile.Write(ref _currentCooldownMs, newCooldown);
        }
    }
}
