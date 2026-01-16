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
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Utility;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.Epg;

/// <summary>
/// Composite EPG provider that tries multiple providers in priority order.
/// Implements the Composite Pattern - treats a group of providers as a single provider.
/// Supports pre-warming and skips unavailable providers for performance.
/// </summary>
public class CompositeEpgProvider : IEpgProviderWithPrewarm
{
    private readonly List<IEpgProvider> _providers;
    private readonly ILogger<CompositeEpgProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CompositeEpgProvider"/> class.
    /// </summary>
    /// <param name="providers">Collection of EPG providers to use.</param>
    /// <param name="logger">Logger.</param>
    public CompositeEpgProvider(IEnumerable<IEpgProvider> providers, ILogger<CompositeEpgProvider> logger)
    {
        // Sort by priority (lower = higher priority)
        _providers = [.. providers.OrderBy(p => p.Priority)];
        _logger = logger;

        _logger.PluginLogInformation(
            "CompositeEpgProvider initialized with {Count} providers: {Providers}",
            _providers.Count,
            string.Join(", ", _providers.Select(p => $"{p.Name} (priority {p.Priority})"))
        );
    }

    /// <inheritdoc />
    public string Name => "Composite";

    /// <inheritdoc />
    public int Priority => 0; // Composite is the primary entry point

    /// <inheritdoc />
    public bool IsAvailable => _providers.Exists(p => p.IsAvailable);

    /// <inheritdoc />
    public async Task PrewarmAsync(CancellationToken cancellationToken)
    {
        _logger.PluginLogInformation("Pre-warming EPG providers...");

        // Pre-warm all providers that support it (in parallel)
        var prewarmTasks = _providers.OfType<IEpgProviderWithPrewarm>().Select(p => p.PrewarmAsync(cancellationToken));

        await Task.WhenAll(prewarmTasks).ConfigureAwait(false);

        _logger.PluginLogInformation("EPG provider pre-warming complete");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EpgProgram>> GetProgramsAsync(int streamId, CancellationToken cancellationToken)
    {
        foreach (var provider in _providers)
        {
            // Skip unavailable providers (circuit breaker pattern)
            if (!provider.IsAvailable)
            {
                _logger.PluginLogInformation(
                    "Skipping unavailable EPG provider '{Provider}' for stream {StreamId}",
                    provider.Name,
                    streamId
                );
                continue;
            }

            try
            {
                _logger.PluginLogInformation(
                    "Trying EPG provider '{Provider}' for stream {StreamId}",
                    provider.Name,
                    streamId
                );

                var programs = await provider.GetProgramsAsync(streamId, cancellationToken).ConfigureAwait(false);

                if (programs.Count > 0)
                {
                    _logger.PluginLogInformation(
                        "EPG provider '{Provider}' returned {Count} programs for stream {StreamId}",
                        provider.Name,
                        programs.Count,
                        streamId
                    );
                    return programs;
                }

                _logger.PluginLogInformation(
                    "EPG provider '{Provider}' returned no programs for stream {StreamId}, trying next",
                    provider.Name,
                    streamId
                );
            }
            catch (OperationCanceledException)
            {
                throw; // Don't catch cancellation
            }
            catch (Exception ex)
            {
                _logger.PluginLogWarning(
                    ex,
                    "EPG provider '{Provider}' failed for stream {StreamId}: {Error}",
                    provider.Name,
                    streamId,
                    ex.Message
                );
                // Continue to next provider
            }
        }

        _logger.PluginLogInformation("No EPG provider returned programs for stream {StreamId}", streamId);

        return [];
    }
}
