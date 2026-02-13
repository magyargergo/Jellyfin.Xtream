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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Xtream.Service.Streaming.Native;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Manages the global registry of active <see cref="Restream"/> instances.
/// Provides stream lookup, snapshot collection, and administrative operations
/// (kill, switch, eject, reset) for the streaming API surface.
/// </summary>
internal static class RestreamActiveStreamRegistry
{
    /// <summary>
    /// Global registry of all active Restream instances for monitoring and management.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Restream> _activeStreams = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers a stream in the active registry.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="stream">The Restream instance to register.</param>
    /// <returns>True if the stream was added, false if a stream with the same ID already exists.</returns>
    internal static bool Register(string streamId, Restream stream) => _activeStreams.TryAdd(streamId, stream);

    /// <summary>
    /// Unregisters a stream from the active registry.
    /// </summary>
    /// <param name="streamId">The stream identifier to remove.</param>
    /// <returns>True if the stream was removed, false if it was not found.</returns>
    internal static bool Unregister(string streamId) => _activeStreams.TryRemove(streamId, out _);

    /// <summary>
    /// Gets the count of active Restream instances.
    /// </summary>
    /// <returns>The number of active streams.</returns>
    public static int GetActiveStreamCount() => _activeStreams.Count;

    /// <summary>
    /// Gets information about all active streams for API/UI purposes.
    /// </summary>
    /// <returns>A list of stream information objects.</returns>
    public static IReadOnlyList<StreamInfoSnapshot> GetActiveStreamSnapshots()
    {
        List<StreamInfoSnapshot> result = [];

        foreach (var activeStream in _activeStreams)
        {
            var stream = activeStream.Value;
            try
            {
                result.Add(stream.CreateSnapshot());
            }
            catch
            {
                // Ignore errors for individual streams
            }
        }

        return result;
    }

    /// <summary>
    /// Kills (disposes) a stream by its ID.
    /// </summary>
    /// <param name="streamId">The stream ID to kill.</param>
    /// <param name="reason">Optional reason for killing the stream (for notifications).</param>
    /// <returns>True if the stream was found and killed, false otherwise.</returns>
    public static bool KillStream(string streamId, string? reason = null)
    {
        if (_activeStreams.TryGetValue(streamId, out var stream))
        {
            var killReason = reason ?? "Manual termination";
            stream.SetKillReason(killReason);
            stream.Dispose();

            Events.PluginEventBus.Instance.Publish(
                "stream.killed",
                streamId,
                new Dictionary<string, object>(StringComparer.Ordinal) { ["reason"] = killReason }
            );

            return true;
        }

        return false;
    }

    /// <summary>
    /// Gets the provider health snapshot for a specific provider in a stream.
    /// </summary>
    /// <param name="streamId">The stream ID.</param>
    /// <param name="providerIndex">The provider index (0-based).</param>
    /// <returns>The health snapshot, or null if not found.</returns>
    public static ProviderHealthSnapshot? GetProviderHealth(string streamId, int providerIndex)
    {
        if (!_activeStreams.TryGetValue(streamId, out var stream))
        {
            return null;
        }

        return stream.NativeStreamer?.GetProviderHealth(providerIndex);
    }

    /// <summary>
    /// Gets all provider health snapshots for a stream.
    /// </summary>
    /// <param name="streamId">The stream ID.</param>
    /// <returns>List of provider health snapshots, or null if stream not found.</returns>
    public static IReadOnlyList<ProviderHealthSnapshot>? GetAllProviderHealth(string streamId)
    {
        if (!_activeStreams.TryGetValue(streamId, out var stream))
        {
            return null;
        }

        var nativeStreamer = stream.NativeStreamer;
        if (nativeStreamer == null)
        {
            return null;
        }

        var count = nativeStreamer.GetProviderCount();
        var results = new List<ProviderHealthSnapshot>(count);
        for (int i = 0; i < count; i++)
        {
            var health = nativeStreamer.GetProviderHealth(i);
            if (health != null)
            {
                results.Add(health.Value);
            }
        }

        return results;
    }

    /// <summary>
    /// Requests a URL switch (force reconnect) for a stream.
    /// </summary>
    /// <param name="streamId">The stream ID.</param>
    /// <returns>True if the switch was requested, false if stream not found.</returns>
    public static bool RequestSwitch(string streamId)
    {
        if (!_activeStreams.TryGetValue(streamId, out var stream))
        {
            return false;
        }

        stream.NativeStreamer?.RequestSwitch();
        return true;
    }

    /// <summary>
    /// Gets detailed TR 101 290 metrics for a stream.
    /// </summary>
    /// <param name="streamId">The stream ID.</param>
    /// <returns>The metrics, or null if not available.</returns>
    public static TsDuckMetrics? GetStreamMetrics(string streamId)
    {
        if (!_activeStreams.TryGetValue(streamId, out var stream))
        {
            return null;
        }

        return stream.NativeStreamer?.GetMetrics();
    }

    /// <summary>
    /// Gets A/V sync analysis for a stream.
    /// </summary>
    /// <param name="streamId">The stream ID.</param>
    /// <returns>The A/V sync analysis, or null if not available.</returns>
    public static AvSyncAnalysis? GetStreamAvSync(string streamId)
    {
        if (!_activeStreams.TryGetValue(streamId, out var stream))
        {
            return null;
        }

        return stream.NativeStreamer?.GetAvSyncAnalysis();
    }

    /// <summary>
    /// Gets PCR analysis for a stream.
    /// </summary>
    /// <param name="streamId">The stream ID.</param>
    /// <returns>The PCR analysis, or null if not available.</returns>
    public static PcrAnalysis? GetStreamPcrAnalysis(string streamId)
    {
        if (!_activeStreams.TryGetValue(streamId, out var stream))
        {
            return null;
        }

        return stream.NativeStreamer?.GetPcrAnalysis();
    }

    /// <summary>
    /// Force ejects a provider from a stream's health system.
    /// </summary>
    /// <param name="streamId">The stream ID.</param>
    /// <param name="providerIndex">The provider index (0-based).</param>
    /// <param name="durationMs">Ejection duration in milliseconds.</param>
    /// <returns>True if the provider was ejected, false if stream not found.</returns>
    public static bool ForceEjectProvider(string streamId, int providerIndex, int durationMs)
    {
        if (!_activeStreams.TryGetValue(streamId, out var stream))
        {
            return false;
        }

        stream.NativeStreamer?.ForceEjectProvider(providerIndex, durationMs);
        return true;
    }

    /// <summary>
    /// Resets all providers in a stream to Active state.
    /// </summary>
    /// <param name="streamId">The stream ID.</param>
    /// <returns>True if reset, false if stream not found.</returns>
    public static bool ResetAllProviders(string streamId)
    {
        if (!_activeStreams.TryGetValue(streamId, out var stream))
        {
            return false;
        }

        stream.NativeStreamer?.ResetAllProviders();
        return true;
    }

    /// <summary>
    /// Kills all active streams.
    /// </summary>
    /// <param name="reason">Optional reason for killing the streams (for notifications).</param>
    /// <returns>The number of streams killed.</returns>
    public static int KillAllStreams(string? reason = null)
    {
        var count = 0;

        foreach (var stream in _activeStreams.Values.ToList())
        {
            try
            {
                stream.SetKillReason(reason ?? "Bulk termination");
                stream.Dispose();
                count++;
            }
            catch
            {
                // Ignore disposal errors
            }
        }

        return count;
    }
}
