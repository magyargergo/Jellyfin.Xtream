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
using Jellyfin.Xtream.Service.Streaming.Native;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Provides configuration helpers for <see cref="Restream"/>:
/// stream quality detection, buffer sizing, and native streamer/network configuration building.
/// </summary>
internal static class RestreamConfiguration
{
    // Buffer sizes based on stream quality
    private const int SdBufferSize = 33554432;
    private const int HdBufferSize = 67108864;
    private const int UhdBufferSize = 134217728;

    /// <summary>
    /// Detects stream quality (SD/HD/FHD/UHD) based on channel name and video metadata.
    /// </summary>
    /// <param name="mediaSource">The media source to inspect.</param>
    /// <returns>A quality label: "UHD/4K", "Full HD", "HD", or "SD".</returns>
    internal static string DetectStreamQuality(MediaSourceInfo mediaSource)
    {
        var name = mediaSource.Name?.ToLowerInvariant() ?? string.Empty;

        if (
            name.Contains("4k", StringComparison.Ordinal)
            || name.Contains("uhd", StringComparison.Ordinal)
            || name.Contains("ultra hd", StringComparison.Ordinal)
            || name.Contains("2160", StringComparison.Ordinal)
        )
        {
            return "UHD/4K";
        }

        if (
            name.Contains("fhd", StringComparison.Ordinal)
            || name.Contains("full hd", StringComparison.Ordinal)
            || name.Contains("1080", StringComparison.Ordinal)
        )
        {
            return "Full HD";
        }

        if (name.Contains("hd", StringComparison.Ordinal) || name.Contains("720", StringComparison.Ordinal))
        {
            return "HD";
        }

        if (mediaSource.MediaStreams != null)
        {
            foreach (var stream in mediaSource.MediaStreams)
            {
                if (stream.Type == MediaStreamType.Video)
                {
                    // Width/Height are nullable and may be null before FFprobe runs.
                    // Only classify as SD if we have confirmed low resolution.
                    // When resolution is unknown (null/0), default to HD to avoid
                    // undersized buffers that cause underruns on HD+ streams.
                    if (stream.Width >= 3840 || stream.Height >= 2160)
                    {
                        return "UHD/4K";
                    }

                    if (stream.Width >= 1920 || stream.Height >= 1080)
                    {
                        return "Full HD";
                    }

                    if (stream.Width >= 1280 || stream.Height >= 720)
                    {
                        return "HD";
                    }

                    // Only classify as SD if we have actual resolution data confirming it
                    return stream.Width > 0 && stream.Height > 0 ? "SD" : "HD";
                }
            }
        }

        return "HD";
    }

    /// <summary>
    /// Gets the buffer size in bytes based on detected stream quality.
    /// </summary>
    /// <param name="quality">The quality label from <see cref="DetectStreamQuality"/>.</param>
    /// <returns>Buffer size in bytes.</returns>
    internal static int GetBufferSize(string quality)
    {
        return quality switch
        {
            "UHD/4K" => UhdBufferSize,
            "Full HD" => HdBufferSize,
            "HD" => HdBufferSize,
            "SD" => SdBufferSize,
            _ => HdBufferSize,
        };
    }

    /// <summary>
    /// Safely retrieves the plugin configuration, returning null if the plugin is not yet initialized.
    /// This allows Restream to be used in test environments where the plugin may not be registered.
    /// </summary>
    /// <returns>The current plugin configuration, or null if unavailable.</returns>
    internal static PluginConfiguration? GetPluginConfiguration()
    {
        try
        {
            return Plugin.Instance.Configuration;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds a <see cref="TsDuckStreamerConfigNative"/> from the plugin configuration,
    /// mapping user-configured timeouts and resilience settings to native struct fields.
    /// Falls back to <see cref="TsDuckStreamerConfigNative.Default"/> values when the
    /// plugin instance is not available.
    /// </summary>
    /// <returns>A configured native streamer configuration struct.</returns>
    internal static TsDuckStreamerConfigNative BuildStreamerConfig()
    {
        var streamerConfig = TsDuckStreamerConfigNative.Default;
        var pluginConfig = GetPluginConfiguration();

        if (pluginConfig == null)
        {
            return streamerConfig;
        }

        // Map user-configured timeouts (seconds to milliseconds)
        streamerConfig.ConnectTimeoutMs = pluginConfig.StreamConnectTimeoutSeconds * 1000;
        streamerConfig.ResponseTimeoutMs = pluginConfig.StreamResponseHeadersTimeoutSeconds * 1000;
        streamerConfig.StallTimeoutMs = pluginConfig.StreamDataStallTimeoutSeconds * 1000;

        // Map resilience settings
        streamerConfig.MaxRetries = pluginConfig.MaxFailoverAttempts;
        streamerConfig.QuarantineDurationMs = pluginConfig.ProviderBlacklistSeconds * 1000;

        // Map load balancer settings
        streamerConfig.EnableP2C = pluginConfig.EnableP2CLoadBalancing ? 1 : 0;
        streamerConfig.EnableOutlierDetection = pluginConfig.EnableOutlierDetection ? 1 : 0;
        streamerConfig.OutlierStddevFactor = pluginConfig.OutlierStddevFactor;
        streamerConfig.ProbationSuccessThreshold = pluginConfig.ProbationSuccessThreshold;

        return streamerConfig;
    }

    /// <summary>
    /// Builds a <see cref="NetworkConfig"/> from the plugin configuration,
    /// mapping user-configured network and timeout settings to the native network layer.
    /// </summary>
    /// <returns>A configured network configuration, or null if plugin is unavailable.</returns>
    internal static NetworkConfig? BuildNetworkConfig()
    {
        var pluginConfig = GetPluginConfiguration();
        if (pluginConfig == null)
        {
            return null;
        }

        var networkConfig = new NetworkConfig
        {
            TcpConnectTimeoutMs = pluginConfig.StreamConnectTimeoutSeconds * 1000,
            FirstByteTimeoutMs = pluginConfig.StreamFirstByteTimeoutSeconds * 1000,
            DnsTimeoutMs = pluginConfig.DnsTimeoutSeconds * 1000,
            TcpKeepaliveEnabled = pluginConfig.TcpKeepaliveEnabled,
        };

        return networkConfig;
    }
}
