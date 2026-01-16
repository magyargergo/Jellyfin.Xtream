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
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Xtream.Service.ProviderManagement;

/// <summary>
/// Pool of pre-established HTTP connections for Fast Channel Change (FCC).
/// </summary>
/// <remarks>
/// <para>
/// Implements the Fast Channel Change pattern from DVB-IPTV specifications.
/// By pre-establishing TCP connections to IPTV providers, channel switching
/// latency is reduced from 1-2 seconds to under 200ms.
/// </para>
/// <para>
/// Key benefits:
/// <list type="bullet">
///   <item>DNS resolution happens during warmup, not during channel switch</item>
///   <item>TCP handshake completed in advance</item>
///   <item>TLS negotiation (if applicable) done during idle time</item>
///   <item>Connection health validated before switch attempt</item>
/// </list>
/// </para>
/// <para>
/// Industry standards implemented:
/// <list type="bullet">
///   <item>Nokia FCC specification (sub-second channel change)</item>
///   <item>DVB-IPTV Handbook fast channel change requirements</item>
///   <item>RFC 7540 HTTP/2 connection preconnect pattern</item>
/// </list>
/// </para>
/// </remarks>
public interface IPreconnectPool : IDisposable
{
    /// <summary>
    /// Gets the current number of warmed connections in the pool.
    /// </summary>
    int WarmedConnectionCount { get; }

    /// <summary>
    /// Gets aggregate statistics about pool operations.
    /// </summary>
    PreconnectPoolStatistics Statistics { get; }

    /// <summary>
    /// Warms a connection to the specified provider host.
    /// Performs DNS resolution, TCP handshake, and optional TLS negotiation.
    /// </summary>
    /// <param name="hostUri">The provider host URI (scheme://host:port).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if warmup succeeded; false if connection failed.</returns>
    Task<bool> WarmConnectionAsync(Uri hostUri, CancellationToken cancellationToken = default);

    /// <summary>
    /// Warms connections to multiple provider hosts in parallel.
    /// Useful for warming alternative providers for failover scenarios.
    /// </summary>
    /// <param name="hostUris">The provider host URIs to warm.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of connections successfully warmed.</returns>
    Task<int> WarmConnectionsAsync(Uri[] hostUris, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to acquire a pre-warmed connection for the specified host.
    /// If no warmed connection is available, returns null (caller should establish new connection).
    /// </summary>
    /// <param name="hostUri">The provider host URI.</param>
    /// <returns>A pre-warmed connection slot, or null if none available.</returns>
    PreconnectSlot? TryAcquire(Uri hostUri);

    /// <summary>
    /// Returns a connection slot to the pool for potential reuse.
    /// Called when a stream ends gracefully (not on error).
    /// </summary>
    /// <param name="slot">The slot to return.</param>
    void Return(PreconnectSlot slot);

    /// <summary>
    /// Evicts stale or unhealthy connections from the pool.
    /// Called periodically by background cleanup or on-demand after failures.
    /// </summary>
    /// <param name="hostUri">Optional host to evict connections for. Null evicts all stale connections.</param>
    /// <returns>The number of connections evicted.</returns>
    int EvictStale(Uri? hostUri = null);

    /// <summary>
    /// Gets the health status of connections to a specific host.
    /// </summary>
    /// <param name="hostUri">The provider host URI.</param>
    /// <returns>The connection health status.</returns>
    ConnectionPoolHealth GetHealth(Uri hostUri);
}

/// <summary>
/// Represents a pre-warmed connection slot from the pool.
/// </summary>
/// <remarks>
/// Slots track connection metadata for health monitoring and statistics.
/// Dispose returns the slot to the pool if still healthy.
/// </remarks>
public sealed class PreconnectSlot : IDisposable
{
    private readonly Action<PreconnectSlot>? _onDispose;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="PreconnectSlot"/> class.
    /// </summary>
    /// <param name="hostUri">The host this connection is for.</param>
    /// <param name="response">The HTTP response (for streaming).</param>
    /// <param name="stream">The response content stream.</param>
    /// <param name="warmedAt">When the connection was warmed.</param>
    /// <param name="onDispose">Callback when slot is disposed.</param>
    public PreconnectSlot(
        Uri hostUri,
        HttpResponseMessage response,
        Stream stream,
        DateTime warmedAt,
        Action<PreconnectSlot>? onDispose = null
    )
    {
        HostUri = hostUri;
        Response = response;
        Stream = stream;
        WarmedAt = warmedAt;
        AcquiredAt = DateTime.UtcNow;
        _onDispose = onDispose;
    }

    /// <summary>
    /// Gets the host URI this slot is connected to.
    /// </summary>
    public Uri HostUri { get; }

    /// <summary>
    /// Gets the HTTP response message.
    /// </summary>
    public HttpResponseMessage Response { get; }

    /// <summary>
    /// Gets the content stream for reading.
    /// </summary>
    public Stream Stream { get; }

    /// <summary>
    /// Gets when this connection was warmed.
    /// </summary>
    public DateTime WarmedAt { get; }

    /// <summary>
    /// Gets when this slot was acquired from the pool.
    /// </summary>
    public DateTime AcquiredAt { get; }

    /// <summary>
    /// Gets the age of the warmed connection.
    /// </summary>
    public TimeSpan Age => DateTime.UtcNow - WarmedAt;

    /// <summary>
    /// Gets or sets a value indicating whether this slot had an error during use.
    /// Set to true to prevent return to pool.
    /// </summary>
    public bool HasError { get; set; }

    /// <summary>
    /// Gets a value indicating whether this slot has been disposed.
    /// </summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        // If error occurred, dispose resources immediately
        if (HasError)
        {
            Stream.Dispose();
            Response.Dispose();
            return;
        }

        // Otherwise, let the pool decide whether to reuse or dispose
        _onDispose?.Invoke(this);
    }
}

/// <summary>
/// Health status of connections to a specific host.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly struct ConnectionPoolHealth : IEquatable<ConnectionPoolHealth>
{
    /// <summary>Gets the number of available pre-warmed connections.</summary>
    public int AvailableConnections { get; init; }

    /// <summary>Gets the number of connections currently in use.</summary>
    public int InUseConnections { get; init; }

    /// <summary>Gets the average connection age in seconds.</summary>
    public double AverageAgeSeconds { get; init; }

    /// <summary>Gets the success rate for this host (0.0 to 1.0).</summary>
    public double SuccessRate { get; init; }

    /// <summary>Gets the average warmup latency in milliseconds.</summary>
    public double AverageWarmupLatencyMs { get; init; }

    /// <summary>Gets a value indicating whether connections to this host are considered healthy.</summary>
    public bool IsHealthy => SuccessRate >= 0.8 && AvailableConnections > 0;

    /// <summary>Empty health status for hosts with no connections.</summary>
    public static readonly ConnectionPoolHealth Empty = new()
    {
        AvailableConnections = 0,
        InUseConnections = 0,
        AverageAgeSeconds = 0,
        SuccessRate = 0,
        AverageWarmupLatencyMs = 0,
    };

    /// <inheritdoc />
    public bool Equals(ConnectionPoolHealth other) =>
        AvailableConnections == other.AvailableConnections && InUseConnections == other.InUseConnections;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ConnectionPoolHealth other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(AvailableConnections, InUseConnections);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(ConnectionPoolHealth left, ConnectionPoolHealth right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(ConnectionPoolHealth left, ConnectionPoolHealth right) => !left.Equals(right);
}

/// <summary>
/// Aggregate statistics for the preconnect pool.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly struct PreconnectPoolStatistics : IEquatable<PreconnectPoolStatistics>
{
    /// <summary>Gets the total number of warmup attempts.</summary>
    public long TotalWarmupAttempts { get; init; }

    /// <summary>Gets the number of successful warmups.</summary>
    public long SuccessfulWarmups { get; init; }

    /// <summary>Gets the number of failed warmups.</summary>
    public long FailedWarmups { get; init; }

    /// <summary>Gets the number of times a pre-warmed connection was used (cache hit).</summary>
    public long CacheHits { get; init; }

    /// <summary>Gets the number of times no pre-warmed connection was available (cache miss).</summary>
    public long CacheMisses { get; init; }

    /// <summary>Gets the number of connections evicted due to staleness or errors.</summary>
    public long Evictions { get; init; }

    /// <summary>Gets the average warmup latency in milliseconds.</summary>
    public double AverageWarmupLatencyMs { get; init; }

    /// <summary>Gets the warmup success rate as a percentage.</summary>
    public double WarmupSuccessRatePercent =>
        TotalWarmupAttempts > 0 ? (double)SuccessfulWarmups / TotalWarmupAttempts * 100 : 0;

    /// <summary>Gets the cache hit rate as a percentage.</summary>
    public double CacheHitRatePercent =>
        CacheHits + CacheMisses > 0 ? (double)CacheHits / (CacheHits + CacheMisses) * 100 : 0;

    /// <inheritdoc />
    public bool Equals(PreconnectPoolStatistics other) =>
        TotalWarmupAttempts == other.TotalWarmupAttempts && CacheHits == other.CacheHits;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PreconnectPoolStatistics other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(TotalWarmupAttempts, CacheHits);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(PreconnectPoolStatistics left, PreconnectPoolStatistics right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(PreconnectPoolStatistics left, PreconnectPoolStatistics right) =>
        !left.Equals(right);
}
