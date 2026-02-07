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
using System.Collections.Generic;
using Jellyfin.Xtream.Service.Events;
using Jellyfin.Xtream.Utility;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.Streaming.Native;

/// <summary>
/// Singleton service owning the long-lived <see cref="NativeChannelRegistry"/>.
/// Provides build/rebuild lifecycle and publishes health events to SSE.
/// </summary>
public sealed class ChannelRegistryService : IDisposable
{
    private readonly ILogger<ChannelRegistryService> _logger;
    private readonly object _lock = new();
    private NativeChannelRegistry? _registry;
    private Dictionary<string, int> _providerIdToIndex = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelRegistryService"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    public ChannelRegistryService(ILogger<ChannelRegistryService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets the current registry, or null if not yet built.
    /// </summary>
    public NativeChannelRegistry? Registry
    {
        get
        {
            lock (_lock)
            {
                return _registry;
            }
        }
    }

    /// <summary>
    /// Gets whether a registry has been built.
    /// </summary>
    public bool IsBuilt
    {
        get
        {
            lock (_lock)
            {
                return _registry?.IsBuilt == true;
            }
        }
    }

    /// <summary>
    /// Creates a new registry, registers providers and streams via the setup action,
    /// then builds it. Replaces any existing registry.
    /// </summary>
    /// <param name="setup">Action to add providers and streams to the registry.
    /// The action receives the registry and a dictionary to populate with provider ID → index mappings.</param>
    /// <param name="postBuild">Optional action called after build succeeds but before the registry
    /// is published. Runs inside the lock, so GUID aliases registered here are immediately visible.
    /// Receives the new registry and provider ID → index mapping.</param>
    /// <returns>Build statistics.</returns>
    public RegistryStats Build(
        Action<NativeChannelRegistry, Dictionary<string, int>> setup,
        Action<NativeChannelRegistry, IReadOnlyDictionary<string, int>>? postBuild = null
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            var newRegistry = new NativeChannelRegistry(_logger);
            var newMapping = new Dictionary<string, int>(StringComparer.Ordinal);

            try
            {
                setup(newRegistry, newMapping);
                var stats = newRegistry.Build();

                // Run post-build actions inside the lock (e.g. GUID alias registration)
                postBuild?.Invoke(newRegistry, newMapping);

                // Wire up health callback for SSE events
                newRegistry.SetHealthCallback(OnHealthEvent);

                // Swap registries. Don't dispose old registry — active Restreams may
                // still hold references. Clear its callback to prevent stale SSE events;
                // SafeHandle finalizer will free native memory when all refs are gone.
                var oldRegistry = _registry;
#pragma warning disable IDISP003 // Intentional: old registry may still be referenced by active Restreams
                _registry = newRegistry;
#pragma warning restore IDISP003
                _providerIdToIndex = newMapping;
                oldRegistry?.SetHealthCallback(null);

                _logger.PluginLogInformation(
                    "ChannelRegistryService: built with {Channels} channels from {Providers} providers",
                    stats.ChannelCount,
                    stats.ProviderCount
                );

                return stats;
            }
            catch
            {
                newRegistry.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Rebuilds the current registry with new stream data while preserving health scores.
    /// Rebuild clears all channels and GUIDs (including aliases).
    /// </summary>
    /// <param name="addStreams">Action to add streams (providers are preserved from initial build).</param>
    /// <param name="postBuild">Optional action called after rebuild succeeds, inside the lock.
    /// Use to re-register GUID aliases atomically. Receives the registry and provider mapping.</param>
    /// <returns>Rebuild statistics.</returns>
    /// <exception cref="InvalidOperationException">Thrown if no registry exists.</exception>
    public RegistryStats Rebuild(
        Action<NativeChannelRegistry> addStreams,
        Action<NativeChannelRegistry, IReadOnlyDictionary<string, int>>? postBuild = null
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            if (_registry is null)
            {
                throw new InvalidOperationException("Cannot rebuild: no registry has been built yet");
            }

            _registry.BeginRebuild();
            addStreams(_registry);
            var stats = _registry.Rebuild();

            // Run post-build actions inside the lock (e.g. GUID alias re-registration)
            postBuild?.Invoke(_registry, _providerIdToIndex);

            _logger.PluginLogInformation(
                "ChannelRegistryService: rebuilt with {Channels} channels, {Streams} streams",
                stats.ChannelCount,
                stats.StreamCount
            );

            return stats;
        }
    }

    /// <summary>
    /// Reports a successful API call for a provider.
    /// Thread-safe; no-ops if no registry is built.
    /// Races with Build() are benign (swallows ObjectDisposedException).
    /// </summary>
    /// <param name="providerIndex">Provider index from the build phase.</param>
    /// <param name="latencyMs">API call latency in milliseconds.</param>
    public void ReportApiSuccess(int providerIndex, int latencyMs)
    {
        var reg = _registry;
        if (reg is null)
        {
            return;
        }

        try
        {
            reg.ReportApiSuccess(providerIndex, latencyMs);
        }
        catch (ObjectDisposedException)
        {
            // Race with Build() swapping registries — benign
        }
    }

    /// <summary>
    /// Reports a failed API call for a provider.
    /// Thread-safe; no-ops if no registry is built.
    /// Races with Build() are benign (swallows ObjectDisposedException).
    /// </summary>
    /// <param name="providerIndex">Provider index from the build phase.</param>
    /// <param name="errorType">Type of API error.</param>
    public void ReportApiFailure(int providerIndex, ApiErrorType errorType)
    {
        var reg = _registry;
        if (reg is null)
        {
            return;
        }

        try
        {
            reg.ReportApiFailure(providerIndex, errorType);
        }
        catch (ObjectDisposedException)
        {
            // Race with Build() swapping registries — benign
        }
    }

    /// <summary>
    /// Gets the provider index for a given provider ID.
    /// Returns -1 if registry is not built or provider not found.
    /// </summary>
    /// <param name="providerId">The provider ID to look up.</param>
    /// <returns>Provider index, or -1 if not found.</returns>
    public int GetProviderIndex(string providerId)
    {
        lock (_lock)
        {
            if (_providerIdToIndex.TryGetValue(providerId, out var index))
            {
                return index;
            }

            return -1;
        }
    }

    private void OnHealthEvent(int providerIndex, string eventType, string details)
    {
        _logger.LogDebugIfEnabled(
            "Health event: provider {Index} {EventType}: {Details}",
            providerIndex,
            eventType,
            details
        );

        PluginEventBus.Instance.Publish(
            $"provider.health.{eventType}",
            data: new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["providerIndex"] = providerIndex,
                ["eventType"] = eventType,
                ["details"] = details,
            }
        );
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (_lock)
        {
            _registry?.Dispose();
            _registry = null;
        }
    }
}
