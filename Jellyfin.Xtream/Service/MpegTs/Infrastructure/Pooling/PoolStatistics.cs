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

namespace Jellyfin.Xtream.Service.MpegTs.Infrastructure.Pooling;

/// <summary>
/// Statistics for an <see cref="ObjectPool{T}"/>.
/// </summary>
public sealed record PoolStatistics
{
    /// <summary>
    /// Gets the number of instances currently available in the pool.
    /// </summary>
    public int Available { get; init; }

    /// <summary>
    /// Gets the number of instances currently rented out.
    /// </summary>
    public int InUse { get; init; }

    /// <summary>
    /// Gets the total number of instances created by the pool.
    /// </summary>
    public long TotalCreated { get; init; }

    /// <summary>
    /// Gets the total number of instances disposed by the pool.
    /// </summary>
    public long TotalDisposed { get; init; }

    /// <summary>
    /// Gets the total number of rent operations.
    /// </summary>
    public long TotalRented { get; init; }

    /// <summary>
    /// Gets the total number of return operations.
    /// </summary>
    public long TotalReturned { get; init; }

    /// <summary>
    /// Gets the total number of instances expired due to idle timeout.
    /// </summary>
    public long TotalExpired { get; init; }

    /// <summary>
    /// Gets the cache hit rate (0.0 to 1.0).
    /// </summary>
    public double HitRate { get; init; }

    /// <inheritdoc/>
    public override string ToString() =>
        $"Available={Available}, InUse={InUse}, Created={TotalCreated}, Disposed={TotalDisposed}, Expired={TotalExpired}, HitRate={HitRate:P1}";
}
