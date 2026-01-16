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
/// Base interface for objects that can be pooled and reused.
/// </summary>
/// <remarks>
/// <para>
/// Follows Interface Segregation Principle - minimal contract for pooling.
/// Specific poolable types (demuxers, remuxers) extend this with their domain-specific interfaces.
/// </para>
/// <para>
/// Design principles:
/// - InstanceId enables tracking and diagnostics
/// - IsHealthy allows pool to discard corrupted instances
/// - PrepareForReuse enables efficient reset without disposal
/// </para>
/// </remarks>
public interface IPoolable : IDisposable
{
    /// <summary>
    /// Gets a unique identifier for this instance (for tracking and diagnostics).
    /// </summary>
    Guid InstanceId { get; }

    /// <summary>
    /// Gets a value indicating whether the instance is in a healthy state for reuse.
    /// Returns false if the instance encountered errors or is in an inconsistent state.
    /// </summary>
    bool IsHealthy { get; }

    /// <summary>
    /// Prepares the instance for reuse by another consumer.
    /// This should clear internal state without disposing underlying resources.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unlike Dispose(), this method keeps resources alive but resets state.
    /// The pool calls this before returning an instance to the available queue.
    /// </para>
    /// <para>
    /// IMPORTANT: Implementations should be thread-safe and idempotent.
    /// </para>
    /// </remarks>
    /// <returns>True if preparation succeeded; false if the instance should be disposed.</returns>
    bool PrepareForReuse();
}
