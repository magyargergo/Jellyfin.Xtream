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
using System.Threading.RateLimiting;

namespace Jellyfin.Xtream.Client;

/// <summary>
/// Factory for creating rate limiters with various strategies.
/// </summary>
public static class RateLimiterFactory
{
    /// <summary>
    /// Creates a token bucket rate limiter.
    /// </summary>
    /// <param name="requestsPerSecond">Maximum requests per second.</param>
    /// <param name="burstSize">Maximum burst size (tokens available at once).</param>
    /// <returns>A configured rate limiter.</returns>
    public static RateLimiter CreateTokenBucket(int requestsPerSecond, int burstSize)
    {
        return new TokenBucketRateLimiter(
            new TokenBucketRateLimiterOptions
            {
                TokenLimit = burstSize,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 100,
                ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                TokensPerPeriod = requestsPerSecond,
                AutoReplenishment = true,
            }
        );
    }

    /// <summary>
    /// Creates a sliding window rate limiter.
    /// </summary>
    /// <param name="permitLimit">Maximum permits in the window.</param>
    /// <param name="windowSeconds">Window size in seconds.</param>
    /// <returns>A configured rate limiter.</returns>
    public static RateLimiter CreateSlidingWindow(int permitLimit, int windowSeconds)
    {
        return new SlidingWindowRateLimiter(
            new SlidingWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 100,
                Window = TimeSpan.FromSeconds(windowSeconds),
                SegmentsPerWindow = 8,
                AutoReplenishment = true,
            }
        );
    }

    /// <summary>
    /// Creates a fixed window rate limiter.
    /// </summary>
    /// <param name="permitLimit">Maximum permits in the window.</param>
    /// <param name="windowSeconds">Window size in seconds.</param>
    /// <returns>A configured rate limiter.</returns>
    public static RateLimiter CreateFixedWindow(int permitLimit, int windowSeconds)
    {
        return new FixedWindowRateLimiter(
            new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 100,
                Window = TimeSpan.FromSeconds(windowSeconds),
                AutoReplenishment = true,
            }
        );
    }
}
