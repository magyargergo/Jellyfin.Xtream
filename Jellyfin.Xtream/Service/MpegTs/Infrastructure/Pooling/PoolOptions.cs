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

namespace Jellyfin.Xtream.Service.MpegTs.Infrastructure.Pooling;

/// <summary>
/// Configuration options for <see cref="ObjectPool{T}"/>.
/// </summary>
public class PoolOptions
{
    /// <summary>
    /// Gets or sets the minimum number of instances to keep in the pool.
    /// Pool will replenish to this level during maintenance.
    /// </summary>
    public int MinPoolSize { get; set; } = 2;

    /// <summary>
    /// Gets or sets the maximum number of instances the pool can hold.
    /// Requests beyond this limit will wait or fail.
    /// </summary>
    public int MaxPoolSize { get; set; } = 8;

    /// <summary>
    /// Gets or sets maximum time an instance can sit idle before disposal.
    /// Prevents memory pressure from long-lived unused instances.
    /// </summary>
    public TimeSpan MaxIdleTime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets the timeout for acquiring an instance when pool is exhausted.
    /// </summary>
    public TimeSpan AcquisitionTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets how often the pool runs maintenance (cleanup expired, replenish min).
    /// </summary>
    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets a value indicating whether to warm pool on first use.
    /// When true, first RentAsync will pre-create MinPoolSize instances.
    /// </summary>
    public bool WarmOnFirstUse { get; set; } = true;

    /// <summary>
    /// Validates the options and throws if invalid.
    /// </summary>
    public void Validate()
    {
        if (MinPoolSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MinPoolSize), "Must be non-negative");
        }

        if (MaxPoolSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxPoolSize), "Must be at least 1");
        }

        if (MinPoolSize > MaxPoolSize)
        {
            throw new ArgumentException($"{nameof(MinPoolSize)} cannot exceed {nameof(MaxPoolSize)}");
        }

        if (MaxIdleTime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxIdleTime), "Must be positive");
        }

        if (AcquisitionTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(AcquisitionTimeout), "Must be positive");
        }

        if (MaintenanceInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MaintenanceInterval), "Must be positive");
        }
    }
}
