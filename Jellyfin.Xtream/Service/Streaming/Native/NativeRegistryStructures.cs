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
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Jellyfin.Xtream.Service.Streaming.Native;

/// <summary>
/// Native provider information structure for registration.
/// Layout must match RegistryProviderInfoNative in tsduck_interop.h exactly.
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal unsafe struct RegistryProviderInfoNative
{
    public fixed byte Id[16];
    public fixed byte Name[64];
    public fixed byte BaseUrl[512];
    public fixed byte Username[128];
    public fixed byte Password[128];
    public int Priority;
    public int IdHash;
    public double InitialHealth;

    /// <summary>
    /// Creates native provider info from managed strings.
    /// </summary>
    public static RegistryProviderInfoNative Create(
        string id,
        string name,
        string baseUrl,
        string username,
        string password,
        int priority,
        int idHash,
        double initialHealth
    )
    {
        var info = new RegistryProviderInfoNative
        {
            Priority = priority,
            IdHash = idHash,
            InitialHealth = initialHealth,
        };

        CopyString(info.Id, 16, id);
        CopyString(info.Name, 64, name);
        CopyString(info.BaseUrl, 512, baseUrl);
        CopyString(info.Username, 128, username);
        CopyString(info.Password, 128, password);

        return info;
    }

    private static void CopyString(byte* dest, int maxLen, string? source)
    {
        if (string.IsNullOrEmpty(source))
        {
            dest[0] = 0;
            return;
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(source);
        var copyLen = Math.Min(bytes.Length, maxLen - 1);

        for (int i = 0; i < copyLen; i++)
        {
            dest[i] = bytes[i];
        }

        dest[copyLen] = 0;
    }
}

/// <summary>
/// Native registry statistics structure.
/// Layout must match RegistryStatsNative in tsduck_interop.h exactly.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RegistryStatsNative
{
    public int ProviderCount;
    public int ChannelCount;
    public int StreamCount;
    public int GuidCount;
    public int SkippedCount;

    /// <summary>
    /// Converts to managed RegistryStats.
    /// </summary>
    public readonly RegistryStats ToManaged() => new(ProviderCount, ChannelCount, StreamCount, GuidCount, SkippedCount);
}

/// <summary>
/// Managed registry statistics.
/// </summary>
/// <param name="ProviderCount">Number of providers registered.</param>
/// <param name="ChannelCount">Unique channels after deduplication.</param>
/// <param name="StreamCount">Total streams before deduplication.</param>
/// <param name="GuidCount">Total GUIDs in lookup.</param>
/// <param name="SkippedCount">Streams with empty normalized names.</param>
[SuppressMessage(
    "Interoperability",
    "MA0008:Add StructLayoutAttribute",
    Justification = "Managed type, not for P/Invoke"
)]
public readonly record struct RegistryStats(
    int ProviderCount,
    int ChannelCount,
    int StreamCount,
    int GuidCount,
    int SkippedCount
)
{
    /// <summary>
    /// Gets the deduplication ratio (channels / streams).
    /// </summary>
    public double DeduplicationRatio => StreamCount > 0 ? (double)ChannelCount / StreamCount : 0;
}
