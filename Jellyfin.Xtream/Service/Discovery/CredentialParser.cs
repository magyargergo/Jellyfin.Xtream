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
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Xtream.Service.Discovery;

/// <summary>
/// Parses Xtream credentials from text and HTML content.
/// </summary>
public sealed partial class CredentialParser : ICredentialParser
{
    private static readonly HashSet<string> CodePatterns =
    [
        "function",
        "return ",
        "var ",
        "const ",
        "let ",
        "===",
        "!==",
        "==",
        "!=",
        ".split(",
        ".join(",
        ".replace(",
        "console.",
        "document.",
        "window.",
        "typeof ",
        "null!=",
        "null==",
        "&&",
        "||",
        "++",
        "--",
        "[];",
        "{}",
        "classList",
        "querySelector",
        "getAttribute",
        "setAttribute",
        "<div",
        "<span",
        "style=",
        "class=",
    ];

    /// <inheritdoc />
    public IReadOnlyList<DiscoveredCredential> ParseText(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var credentials = new List<DiscoveredCredential>();
        var lines = content.Split('\n');

        string? currentServer = null;
        int currentPort = 8080;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            // Format B: Emoji-based portal line
            if (line.Contains("🌐", StringComparison.Ordinal))
            {
                var (server, port) = ParseEmojiPortal(line);
                if (!string.IsNullOrEmpty(server))
                {
                    currentServer = server;
                    currentPort = port;
                }

                continue;
            }

            // Format B: Emoji-based credentials
            if (line.Contains("👤", StringComparison.Ordinal) && line.Contains("🔐", StringComparison.Ordinal))
            {
                var cred = ParseEmojiCredentials(line, currentServer, currentPort);
                if (cred != null)
                {
                    credentials.Add(cred);
                }

                continue;
            }

            // Format C: Direct URL with credentials
            if (
                line.Contains("username=", StringComparison.OrdinalIgnoreCase)
                && line.Contains("password=", StringComparison.OrdinalIgnoreCase)
                && line.Contains("http", StringComparison.OrdinalIgnoreCase)
            )
            {
                var cred = ParseDirectUrl(line);
                if (cred != null)
                {
                    credentials.Add(cred);
                }

                continue;
            }

            // Format A: Portal/server line
            if (IsPortalLine(line))
            {
                var (server, port) = ParsePortalLine(line);
                if (!string.IsNullOrEmpty(server))
                {
                    currentServer = server;
                    currentPort = port;
                }

                continue;
            }

            // Format A: Username/password line
            if (
                (
                    line.Contains('|', StringComparison.Ordinal)
                    || (
                        line.Contains(':', StringComparison.Ordinal)
                        && !line.Contains("http", StringComparison.OrdinalIgnoreCase)
                    )
                ) && !IsCodeLine(line)
            )
            {
                var cred = ParseCredentialLine(line, currentServer, currentPort);
                if (cred != null)
                {
                    credentials.Add(cred);
                }
            }
        }

        return DeduplicateCredentials(credentials);
    }

    /// <inheritdoc />
    public IReadOnlyList<DiscoveredCredential> ParseHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return [];
        }

        var credentials = new List<DiscoveredCredential>();

        // Decode Cloudflare protected content
        html = DecodeCloudflareEmails(html);

        // Extract content blocks
        var contentBlocks = ExtractContentBlocks(html);

        // Parse each content block
        foreach (var block in contentBlocks)
        {
            var cleanText = StripHtmlTags(block);
            var blockCredentials = ParseText(cleanText);
            credentials.AddRange(blockCredentials);
        }

        // Also parse direct URL patterns
        var directUrlCredentials = ParseDirectUrlsFromHtml(html);
        credentials.AddRange(directUrlCredentials);

        // Parse emoji format from HTML
        var emojiCredentials = ParseEmojiFormatFromHtml(html);
        credentials.AddRange(emojiCredentials);

        return DeduplicateCredentials(credentials);
    }

    private static (string? Server, int Port) ParseEmojiPortal(string line)
    {
        var urlPart = line.Split("🌐").LastOrDefault()?.Trim() ?? string.Empty;
        urlPart = urlPart
            .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase);

        if (urlPart.Contains(':', StringComparison.Ordinal))
        {
            var parts = urlPart.Split(':');
            var server = parts[0];
            var portPart = parts[1].Split('/')[0];
            if (int.TryParse(portPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port))
            {
                return (server, port);
            }
        }
        else if (!string.IsNullOrEmpty(urlPart))
        {
            var server = urlPart.Split('/')[0];
            return (server, 80);
        }

        return (null, 0);
    }

    private static DiscoveredCredential? ParseEmojiCredentials(string line, string? currentServer, int currentPort)
    {
        if (string.IsNullOrEmpty(currentServer))
        {
            return null;
        }

        try
        {
            var userPart = line.Split("👤")[1].Split("🔐")[0].Trim();
            var passPart = line.Split("🔐")[1].Trim();

            // Remove URL parameters
            if (passPart.Contains('&', StringComparison.Ordinal))
            {
                passPart = passPart.Split('&')[0];
            }

            if (!string.IsNullOrEmpty(userPart) && !string.IsNullOrEmpty(passPart))
            {
                return new DiscoveredCredential
                {
                    Server = currentServer,
                    Port = currentPort,
                    Username = userPart,
                    Password = passPart,
                };
            }
        }
        catch
        {
            // Parsing failed, return null
        }

        return null;
    }

    private static DiscoveredCredential? ParseDirectUrl(string line)
    {
        var match = DirectUrlRegex().Match(line);
        if (!match.Success)
        {
            return null;
        }

        var server = match.Groups[1].Value;
        var port = string.IsNullOrEmpty(match.Groups[2].Value)
            ? 80
            : int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var username = match.Groups[3].Value;
        var password = match.Groups[4].Value;

        if (username.Length >= 50 || password.Length >= 50 || username.Contains('(', StringComparison.Ordinal))
        {
            return null;
        }

        return new DiscoveredCredential
        {
            Server = server,
            Port = port,
            Username = username,
            Password = password,
        };
    }

    private static bool IsPortalLine(string line)
    {
        return line.Contains("portal", StringComparison.OrdinalIgnoreCase)
            || line.Contains("server", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("http", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCodeLine(string line)
    {
        return CodePatterns.Any(pattern => line.Contains(pattern, StringComparison.Ordinal));
    }

    private static (string? Server, int Port) ParsePortalLine(string line)
    {
        if (IsCodeLine(line))
        {
            return (null, 0);
        }

        var match = UrlPatternRegex().Match(line);
        if (!match.Success)
        {
            return (null, 0);
        }

        var server = match.Groups[1].Value;
        var port = string.IsNullOrEmpty(match.Groups[2].Value)
            ? 80
            : int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);

        // Validate server looks like a real hostname
        if (!server.Contains('.', StringComparison.Ordinal) || server.Length <= 4 || server.StartsWith('.'))
        {
            return (null, 0);
        }

        return (server, port);
    }

    private static DiscoveredCredential? ParseCredentialLine(string line, string? currentServer, int currentPort)
    {
        if (string.IsNullOrEmpty(currentServer))
        {
            return null;
        }

        var separator = line.Contains('|', StringComparison.Ordinal) ? '|' : ':';
        var parts = line.Split(separator);
        if (parts.Length < 2)
        {
            return null;
        }

        var username = parts[0].Trim();
        var password = parts[1].Trim();

        // Remove common label prefixes only if followed by colon/space (e.g., "username: foo" or "username foo")
        if (username.StartsWith("username:", StringComparison.OrdinalIgnoreCase))
        {
            username = username[9..].Trim();
        }
        else if (username.StartsWith("username ", StringComparison.OrdinalIgnoreCase))
        {
            username = username[9..].Trim();
        }

        if (password.StartsWith("password:", StringComparison.OrdinalIgnoreCase))
        {
            password = password[9..].Trim();
        }
        else if (password.StartsWith("password ", StringComparison.OrdinalIgnoreCase))
        {
            password = password[9..].Trim();
        }

        // Validate credentials
        if (
            string.IsNullOrEmpty(username)
            || string.IsNullOrEmpty(password)
            || username.Length >= 40
            || password.Length >= 40
            || username.Any(c => "(){}=;<>".Contains(c, StringComparison.Ordinal))
            || password.Any(c => "(){}<>;".Contains(c, StringComparison.Ordinal))
        )
        {
            return null;
        }

        return new DiscoveredCredential
        {
            Server = currentServer,
            Port = currentPort,
            Username = username,
            Password = password,
        };
    }

    private static string DecodeCloudflareEmails(string html)
    {
        return CloudflareEmailRegex()
            .Replace(
                html,
                match =>
                {
                    var encoded = match.Groups[1].Value;
                    var decoded = DecodeCloudflareString(encoded);

                    // Extract domain from email format
                    if (decoded.Contains('@', StringComparison.Ordinal))
                    {
                        return decoded.Split('@').Last();
                    }

                    return decoded;
                }
            );
    }

    private static string DecodeCloudflareString(string encoded)
    {
        try
        {
            var decoded = new List<char>();
            var key = Convert.ToInt32(encoded[..2], 16);

            for (int i = 2; i < encoded.Length; i += 2)
            {
                var charCode = Convert.ToInt32(encoded.Substring(i, 2), 16) ^ key;
                decoded.Add((char)charCode);
            }

            return new string(decoded.ToArray());
        }
        catch
        {
            return string.Empty;
        }
    }

    private static List<string> ExtractContentBlocks(string html)
    {
        var blocks = new List<string>();

        // Extract <pre> blocks
        foreach (Match match in PreBlockRegex().Matches(html))
        {
            blocks.Add(match.Groups[1].Value);
        }

        // Extract <code> blocks
        foreach (Match match in CodeBlockRegex().Matches(html))
        {
            blocks.Add(match.Groups[1].Value);
        }

        // Extract <p> tags with credential-like content
        foreach (Match match in ParagraphRegex().Matches(html))
        {
            var content = match.Groups[1].Value;
            if (
                content.Contains("portal", StringComparison.OrdinalIgnoreCase)
                || content.Contains("username", StringComparison.OrdinalIgnoreCase)
                || content.Contains("password", StringComparison.OrdinalIgnoreCase)
                || content.Contains("http://", StringComparison.OrdinalIgnoreCase)
                || content.Contains("🌐", StringComparison.Ordinal)
                || content.Contains("👤", StringComparison.Ordinal)
            )
            {
                blocks.Add(content);
            }
        }

        return blocks;
    }

    private static string StripHtmlTags(string html)
    {
        // Remove script and style blocks
        var result = ScriptBlockRegex().Replace(html, string.Empty);
        result = StyleBlockRegex().Replace(result, string.Empty);

        // Remove HTML tags
        result = HtmlTagRegex().Replace(result, "\n");

        // Decode HTML entities
        result = result
            .Replace("&amp;", "&", StringComparison.Ordinal)
            .Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal)
            .Replace("&nbsp;", " ", StringComparison.Ordinal)
            .Replace("&#8211;", "-", StringComparison.Ordinal);

        return result;
    }

    private static IEnumerable<DiscoveredCredential> ParseDirectUrlsFromHtml(string html)
    {
        foreach (Match match in DirectUrlHtmlRegex().Matches(html))
        {
            var server = match.Groups[1].Value;
            var port = string.IsNullOrEmpty(match.Groups[2].Value)
                ? 80
                : int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var username = match.Groups[3].Value;
            var password = match.Groups[4].Value;

            if (username.Length < 50 && password.Length < 50 && !username.Contains('(', StringComparison.Ordinal))
            {
                yield return new DiscoveredCredential
                {
                    Server = server,
                    Port = port,
                    Username = username,
                    Password = password,
                };
            }
        }
    }

    private static IEnumerable<DiscoveredCredential> ParseEmojiFormatFromHtml(string html)
    {
        string? currentServer = null;
        int currentPort = 8080;

        foreach (var rawLine in html.Split('\n'))
        {
            var line = rawLine.Trim();
            if (IsCodeLine(line))
            {
                continue;
            }

            var portalMatch = EmojiPortalRegex().Match(line);
            if (portalMatch.Success)
            {
                currentServer = portalMatch.Groups[1].Value;
                currentPort = int.Parse(portalMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                continue;
            }

            if (currentServer != null)
            {
                var credMatch = EmojiCredRegex().Match(line);
                if (credMatch.Success)
                {
                    var username = credMatch.Groups[1].Value.Trim();
                    var password = credMatch.Groups[2].Value.Trim();
                    if (!string.IsNullOrEmpty(username) && username.Length < 50)
                    {
                        yield return new DiscoveredCredential
                        {
                            Server = currentServer,
                            Port = currentPort,
                            Username = username,
                            Password = password,
                        };
                    }
                }
            }
        }
    }

    private static List<DiscoveredCredential> DeduplicateCredentials(IEnumerable<DiscoveredCredential> credentials)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<DiscoveredCredential>();

        foreach (var cred in credentials)
        {
            if (seen.Add(cred.UniqueKey))
            {
                result.Add(cred);
            }
        }

        return result;
    }

    [GeneratedRegex(
        @"https?://([^:/\s<>""]+):?(\d+)?/(?:get\.php|player_api\.php)\?username=([^&\s<>""]+)&password=([^&\s<>""]+)"
    )]
    private static partial Regex DirectUrlRegex();

    [GeneratedRegex(@"https?://([a-zA-Z0-9][a-zA-Z0-9.\-]+[a-zA-Z0-9]):?(\d+)?")]
    private static partial Regex UrlPatternRegex();

    [GeneratedRegex(
        @"<a[^>]*class=""__cf_email__""[^>]*data-cfemail=""([a-f0-9]+)""[^>]*>\[email[^<]*\]</a>",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex CloudflareEmailRegex();

    [GeneratedRegex(@"<pre[^>]*>(.*?)</pre>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex PreBlockRegex();

    [GeneratedRegex(@"<code[^>]*>(.*?)</code>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex CodeBlockRegex();

    [GeneratedRegex(@"<p[^>]*>(.*?)</p>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ParagraphRegex();

    [GeneratedRegex(@"<script[^>]*>.*?</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptBlockRegex();

    [GeneratedRegex(@"<style[^>]*>.*?</style>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex StyleBlockRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(
        @"https?://([^:/\s<>""]+):?(\d+)?/(?:get\.php|player_api\.php)\?username=([^&\s<>""]+)&password=([^&\s<>""]+)"
    )]
    private static partial Regex DirectUrlHtmlRegex();

    [GeneratedRegex(@"🌐\s*(?:https?://)?([^:/\s<>""]+):(\d+)")]
    private static partial Regex EmojiPortalRegex();

    [GeneratedRegex(@"👤\s*([^\s🔐<>""]{2,30})\s*🔐\s*([^\s&<>""]{2,30})")]
    private static partial Regex EmojiCredRegex();
}
