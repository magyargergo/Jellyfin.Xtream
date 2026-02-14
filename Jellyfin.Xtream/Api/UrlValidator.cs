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
using System.Net;

namespace Jellyfin.Xtream.Api;

/// <summary>
/// URL validation utilities for SSRF prevention.
/// </summary>
internal static class UrlValidator
{
    private static readonly string[] DiscordWebhookPrefixes =
    [
        "https://discord.com/api/webhooks/",
        "https://discordapp.com/api/webhooks/",
    ];

    /// <summary>
    /// Validates that a webhook URL is a legitimate Discord webhook URL.
    /// </summary>
    /// <param name="url">The webhook URL to validate.</param>
    /// <param name="error">Error message if validation fails.</param>
    /// <returns>True if the URL is a valid Discord webhook URL.</returns>
    internal static bool IsValidDiscordWebhookUrl(string? url, out string error)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            error = "Webhook URL is required.";
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            error = "Invalid URL format.";
            return false;
        }

        if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            error = "Webhook URL must use HTTPS.";
            return false;
        }

        foreach (var prefix in DiscordWebhookPrefixes)
        {
            if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                error = string.Empty;
                return true;
            }
        }

        error = "Webhook URL must be a Discord webhook (https://discord.com/api/webhooks/...).";
        return false;
    }

    /// <summary>
    /// Validates that a provider base URL does not point to private/loopback addresses.
    /// Xtream providers use HTTP by protocol design, so HTTP is allowed.
    /// </summary>
    /// <param name="host">The hostname or IP address.</param>
    /// <param name="error">Error message if validation fails.</param>
    /// <returns>True if the host is not a private or loopback address.</returns>
    internal static bool IsValidProviderHost(string? host, out string error)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            error = "Server hostname is required.";
            return false;
        }

        // Block localhost aliases
        if (
            string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "ip6-localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "ip6-loopback", StringComparison.OrdinalIgnoreCase)
        )
        {
            error = "Provider server cannot be localhost.";
            return false;
        }

        // Check if it's an IP address
        if (IPAddress.TryParse(host, out var ip))
        {
            if (IPAddress.IsLoopback(ip))
            {
                error = "Provider server cannot be a loopback address.";
                return false;
            }

            if (IsPrivateNetwork(ip))
            {
                error = "Provider server cannot be a private network address.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool IsPrivateNetwork(IPAddress ip)
    {
        byte[] bytes = ip.GetAddressBytes();

        return ip.AddressFamily switch
        {
            System.Net.Sockets.AddressFamily.InterNetwork => bytes[0] switch
            {
                10 => true, // 10.0.0.0/8
                172 => bytes[1] >= 16 && bytes[1] <= 31, // 172.16.0.0/12
                192 => bytes[1] == 168, // 192.168.0.0/16
                169 => bytes[1] == 254, // 169.254.0.0/16 (link-local)
                _ => false,
            },
            System.Net.Sockets.AddressFamily.InterNetworkV6 =>
            // fe80::/10 (link-local), fc00::/7 (ULA), ::1 (loopback already covered above)
            (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)
                || (bytes[0] & 0xFE) == 0xFC,
            _ => false,
        };
    }
}
