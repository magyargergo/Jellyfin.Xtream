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

using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Represents a channel with multiple provider options, ordered by quality.
/// Uses array for optimal memory layout and cache locality.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="ChannelWithProviders"/> class.
/// </remarks>
/// <param name="normalizedName">The normalized channel name used for deduplication.</param>
/// <param name="displayName">The cleaned display name for UI.</param>
/// <param name="providers">The providers offering this channel, ordered by quality (best first).</param>
public sealed class ChannelWithProviders(string normalizedName, string displayName, ProviderStreamInfo[] providers)
{
    private readonly ProviderStreamInfo[] _providers = providers;

    /// <summary>
    /// Gets the normalized channel name used for deduplication (uppercase, no prefixes/quality indicators).
    /// </summary>
    public string NormalizedName { get; } = normalizedName;

    /// <summary>
    /// Gets the cleaned display name for UI (readable, no prefixes/quality indicators).
    /// </summary>
    public string DisplayName { get; } = displayName;

    /// <summary>
    /// Gets the best available image URL across all providers.
    /// Returns null if no provider has an image.
    /// </summary>
    public string? BestImageUrl { get; } = FindFirstImage(providers);

    /// <summary>
    /// Gets a value indicating whether any provider has an image for this channel.
    /// </summary>
    public bool HasImage => BestImageUrl != null;

    /// <summary>
    /// Gets the best (highest quality) provider for this channel.
    /// </summary>
    public ProviderStreamInfo? Best
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _providers.Length > 0 ? _providers[0] : null;
    }

    /// <summary>
    /// Gets all providers for this channel, ordered by quality (best first).
    /// </summary>
    public IReadOnlyList<ProviderStreamInfo> Providers
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _providers;
    }

    /// <summary>
    /// Gets the number of providers offering this channel.
    /// </summary>
    public int ProviderCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _providers.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string? FindFirstImage(ProviderStreamInfo[] providers)
    {
        for (var i = 0; i < providers.Length; i++)
        {
            var icon = providers[i].Stream.StreamIcon;
            if (!string.IsNullOrEmpty(icon))
            {
                return icon;
            }
        }

        return null;
    }
}
