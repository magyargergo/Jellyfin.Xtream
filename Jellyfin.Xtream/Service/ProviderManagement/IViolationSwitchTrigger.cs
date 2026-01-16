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

namespace Jellyfin.Xtream.Service.ProviderManagement;

/// <summary>
/// Evaluates stream quality violations and determines if a provider switch should be triggered.
/// Follows ISP: focused interface for violation evaluation only.
/// </summary>
public interface IViolationSwitchTrigger
{
    /// <summary>
    /// Records a violation and returns whether a switch should be triggered.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="violationType">The type of violation (e.g., "PAT", "PCR").</param>
    /// <returns>A result indicating whether to switch and the reason.</returns>
    ViolationEvaluationResult RecordViolation(string streamId, string violationType);

    /// <summary>
    /// Records an A/V drift measurement and returns whether a switch should be triggered.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="driftMs">The drift in milliseconds.</param>
    /// <returns>A result indicating whether to switch and the reason.</returns>
    ViolationEvaluationResult RecordDrift(string streamId, double driftMs);

    /// <summary>
    /// Resets violation counters for a stream (call after successful switch).
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    void ResetCounters(string streamId);

    /// <summary>
    /// Acknowledges a successful switch, resetting the consecutive failure counter.
    /// Call this when a switch succeeds and the new provider is stable.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    void AcknowledgeSuccess(string streamId);
}

/// <summary>
/// Result of evaluating a violation for switch triggering.
/// </summary>
/// <param name="ShouldSwitch">Whether a provider switch should be triggered.</param>
/// <param name="Reason">The reason for the switch (if applicable).</param>
public readonly record struct ViolationEvaluationResult(bool ShouldSwitch, string? Reason = null)
{
    /// <summary>No switch needed.</summary>
    public static readonly ViolationEvaluationResult NoSwitch = new(ShouldSwitch: false);

    /// <summary>Switch is needed but cooldown is active.</summary>
    public static readonly ViolationEvaluationResult CooldownActive = new(ShouldSwitch: false, "Cooldown active");

    /// <summary>Switch is disabled due to max consecutive failures reached.</summary>
    public static readonly ViolationEvaluationResult MaxFailuresReached = new(
        ShouldSwitch: false,
        "Max consecutive failures reached"
    );
}
