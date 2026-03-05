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

// ============================================================================
// Channel List Entry (for enumeration)
// ============================================================================

/// <summary>
/// Native channel list entry for paginated enumeration.
/// Layout must match RegistryChannelListEntryNative in tsduck_interop.h exactly.
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal unsafe struct RegistryChannelListEntryNative
{
    public long GuidHigh;
    public long GuidLow;
    public fixed byte DisplayName[128];
    public fixed byte IconUrl[512];
    public int ProviderCount;
    public int BestQualityScore;

    /// <summary>
    /// Converts to managed ChannelListEntry.
    /// </summary>
    public readonly ChannelListEntry ToManaged()
    {
        fixed (byte* namePtr = DisplayName)
        fixed (byte* iconPtr = IconUrl)
        {
            var name = MarshalFixedString(namePtr, 128);
            var icon = MarshalFixedString(iconPtr, 512);
            return new ChannelListEntry(GuidFromParts(GuidHigh, GuidLow), name, icon, ProviderCount, BestQualityScore);
        }
    }

    private static string MarshalFixedString(byte* ptr, int maxLen)
    {
        int len = 0;
        while (len < maxLen && ptr[len] != 0)
        {
            len++;
        }

        return System.Text.Encoding.UTF8.GetString(ptr, len);
    }

    private static Guid GuidFromParts(long high, long low)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, high);
        BitConverter.TryWriteBytes(bytes[8..], low);
        return new Guid(bytes);
    }
}

/// <summary>
/// Managed channel list entry.
/// </summary>
/// <param name="Guid">Channel GUID.</param>
/// <param name="DisplayName">Channel display name.</param>
/// <param name="IconUrl">Channel icon URL.</param>
/// <param name="ProviderCount">Number of providers offering this channel.</param>
/// <param name="BestQualityScore">Highest quality score among providers.</param>
[SuppressMessage(
    "Interoperability",
    "MA0008:Add StructLayoutAttribute",
    Justification = "Managed type, not for P/Invoke"
)]
public readonly record struct ChannelListEntry(
    Guid Guid,
    string DisplayName,
    string IconUrl,
    int ProviderCount,
    int BestQualityScore
);

// ============================================================================
// Provider Status (for diagnostics)
// ============================================================================

/// <summary>
/// Native provider status for diagnostics dashboard.
/// Layout must match RegistryProviderStatusNative in tsduck_interop.h exactly.
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal unsafe struct RegistryProviderStatusNative
{
    public fixed byte Id[16];
    public fixed byte Name[64];
    public double HealthScore;
    public int State;
    public int CircuitBreakerState;
    public long QuarantineUntil;
    public int ChannelCount;
    public int ConsecutiveFailures;
    public double LatencyEwmaMs;
    public double SuccessRate;
    public int Reserved;
    public int Reserved2;

    /// <summary>
    /// Converts to managed ProviderStatus.
    /// </summary>
    public readonly ProviderStatus ToManaged()
    {
        fixed (byte* idPtr = Id)
        fixed (byte* namePtr = Name)
        {
            var id = MarshalFixedString(idPtr, 16);
            var name = MarshalFixedString(namePtr, 64);
            return new ProviderStatus(
                id,
                name,
                HealthScore,
                (ProviderState)State,
                (CircuitBreakerState)CircuitBreakerState,
                QuarantineUntil > 0 ? new DateTime(QuarantineUntil, DateTimeKind.Utc) : null,
                ChannelCount,
                ConsecutiveFailures,
                LatencyEwmaMs,
                SuccessRate
            );
        }
    }

    private static string MarshalFixedString(byte* ptr, int maxLen)
    {
        int len = 0;
        while (len < maxLen && ptr[len] != 0)
        {
            len++;
        }

        return System.Text.Encoding.UTF8.GetString(ptr, len);
    }
}

/// <summary>
/// Circuit breaker state.
/// </summary>
public enum CircuitBreakerState
{
    /// <summary>Circuit closed, requests flowing normally.</summary>
    Closed = 0,

    /// <summary>Circuit open, requests blocked.</summary>
    Open = 1,

    /// <summary>Circuit half-open, testing recovery.</summary>
    HalfOpen = 2,
}

/// <summary>
/// API error type for reporting failures to native health system.
/// </summary>
public enum ApiErrorType
{
    /// <summary>DNS resolution failed.</summary>
    DnsFailure = 0,

    /// <summary>Request timed out.</summary>
    Timeout = 1,

    /// <summary>HTTP error (4xx/5xx).</summary>
    HttpError = 2,

    /// <summary>Authentication failed (401/403).</summary>
    AuthFailure = 3,
}

/// <summary>
/// Managed provider status for diagnostics.
/// </summary>
/// <param name="Id">Provider ID.</param>
/// <param name="Name">Provider display name.</param>
/// <param name="HealthScore">Health score (0-100).</param>
/// <param name="State">Current provider state.</param>
/// <param name="CircuitBreaker">Circuit breaker state.</param>
/// <param name="QuarantineUntil">When quarantine ends (null if not quarantined).</param>
/// <param name="ChannelCount">Channels this provider serves.</param>
/// <param name="ConsecutiveFailures">Times circuit has opened.</param>
/// <param name="LatencyEwmaMs">EWMA latency in milliseconds.</param>
/// <param name="SuccessRate">Success rate (0.0-1.0).</param>
[SuppressMessage(
    "Interoperability",
    "MA0008:Add StructLayoutAttribute",
    Justification = "Managed type, not for P/Invoke"
)]
public readonly record struct ProviderStatus(
    string Id,
    string Name,
    double HealthScore,
    ProviderState State,
    CircuitBreakerState CircuitBreaker,
    DateTime? QuarantineUntil,
    int ChannelCount,
    int ConsecutiveFailures,
    double LatencyEwmaMs,
    double SuccessRate
)
{
    /// <summary>Gets whether the provider is currently ejected.</summary>
    public bool IsEjected => State == ProviderState.Ejected;

    /// <summary>Gets whether the provider is in probation.</summary>
    public bool IsInProbation => State == ProviderState.Probation;

    /// <summary>Gets whether the provider is healthy.</summary>
    public bool IsHealthy => State == ProviderState.Active && SuccessRate >= 0.9;
}
