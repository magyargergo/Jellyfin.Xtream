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
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Service.MpegTs.UseCases;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.MpegTs.Infrastructure.Pooling;

/// <summary>
/// Pool configuration for <see cref="FFmpegProcessorPool"/>.
/// </summary>
public sealed class FFmpegProcessorPoolOptions : PoolOptions
{
    /// <summary>
    /// Gets or sets the processor kind (demuxer or remuxer).
    /// </summary>
    public FFmpegProcessorKind Kind { get; set; } = FFmpegProcessorKind.Demuxer;

    /// <summary>
    /// Gets or sets the buffer size for demuxer mode.
    /// </summary>
    public int BufferSize { get; set; } = 512 * 1024;
}

/// <summary>
/// Interface for FFmpeg processor pool.
/// </summary>
public interface IFFmpegProcessorPool : IDisposable
{
    /// <summary>
    /// Gets the processor kind this pool manages.
    /// </summary>
    FFmpegProcessorKind Kind { get; }

    /// <summary>
    /// Warms the pool with pre-created instances.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the warmup operation.</returns>
    Task WarmPoolAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Rents a processor from the pool.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A lease wrapping the processor.</returns>
    ValueTask<PoolLease<IPooledFFmpegProcessor>> RentAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a prepared processor from the pool.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A prepared processor, or null if unavailable.</returns>
    ValueTask<IPooledFFmpegProcessor?> GetPreparedProcessorAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a processor to the pool.
    /// </summary>
    /// <param name="processor">The processor to return.</param>
    void ReturnProcessor(IPooledFFmpegProcessor processor);

    /// <summary>
    /// Gets pool statistics.
    /// </summary>
    /// <returns>Current pool statistics.</returns>
    PoolStatistics GetStatistics();
}

/// <summary>
/// Unified pool for FFmpeg processors (demuxers and remuxers).
/// </summary>
/// <remarks>
/// <para>
/// This pool replaces both <c>FFmpegDemuxerPool</c> and <c>FFmpegRemuxerPool</c>
/// with a single implementation using <see cref="PooledFFmpegProcessor"/>.
/// </para>
/// <para>
/// Usage:
/// <code>
/// // For demuxers
/// var demuxerPool = new FFmpegProcessorPool(new FFmpegProcessorPoolOptions { Kind = FFmpegProcessorKind.Demuxer });
///
/// // For remuxers
/// var remuxerPool = new FFmpegProcessorPool(new FFmpegProcessorPoolOptions { Kind = FFmpegProcessorKind.Remuxer });
/// </code>
/// </para>
/// </remarks>
public sealed class FFmpegProcessorPool : IFFmpegProcessorPool
{
    private readonly ObjectPool<IPooledFFmpegProcessor> _pool;
    private readonly FFmpegProcessorKind _kind;

    /// <summary>
    /// Initializes a new instance of the <see cref="FFmpegProcessorPool"/> class.
    /// </summary>
    /// <param name="options">Pool configuration options.</param>
    /// <param name="ffmpegContext">FFmpeg context for processor creation.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="factory">Optional custom factory for processor creation.</param>
    public FFmpegProcessorPool(
        FFmpegProcessorPoolOptions? options = null,
        IFFmpegContext? ffmpegContext = null,
        ILogger<FFmpegProcessorPool>? logger = null,
        Func<IPooledFFmpegProcessor>? factory = null
    )
    {
        var poolOptions = options ?? new FFmpegProcessorPoolOptions();
        poolOptions.Validate();

        _kind = poolOptions.Kind;
        var context = ffmpegContext ?? FFmpegContextAdapter.Instance;
        var bufferSize = poolOptions.BufferSize;

        var baseOptions = new PoolOptions
        {
            MinPoolSize = poolOptions.MinPoolSize,
            MaxPoolSize = poolOptions.MaxPoolSize,
            MaxIdleTime = poolOptions.MaxIdleTime,
            AcquisitionTimeout = poolOptions.AcquisitionTimeout,
            MaintenanceInterval = poolOptions.MaintenanceInterval,
            WarmOnFirstUse = poolOptions.WarmOnFirstUse,
        };

        Func<IPooledFFmpegProcessor> processorFactory =
            factory ?? (() => new PooledFFmpegProcessor(_kind, bufferSize, logger, context));

        var poolName = _kind == FFmpegProcessorKind.Demuxer ? "FFmpegDemuxer" : "FFmpegRemuxer";

        _pool = new ObjectPool<IPooledFFmpegProcessor>(processorFactory, baseOptions, logger, poolName);
    }

    /// <inheritdoc />
    public FFmpegProcessorKind Kind => _kind;

    /// <inheritdoc />
    public Task WarmPoolAsync(CancellationToken cancellationToken = default) => _pool.WarmPoolAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask<PoolLease<IPooledFFmpegProcessor>> RentAsync(CancellationToken cancellationToken = default) =>
        _pool.RentAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask<IPooledFFmpegProcessor?> GetPreparedProcessorAsync(
        CancellationToken cancellationToken = default
    ) => _pool.GetPreparedInstanceAsync(cancellationToken);

    /// <inheritdoc />
    public void ReturnProcessor(IPooledFFmpegProcessor processor) => _pool.Return(processor);

    /// <inheritdoc />
    public PoolStatistics GetStatistics() => _pool.GetStatistics();

    /// <inheritdoc />
    public void Dispose() => _pool.Dispose();
}

/// <summary>
/// Extension methods for creating specialized processor pools.
/// </summary>
public static class FFmpegProcessorPoolFactory
{
    /// <summary>
    /// Creates a demuxer pool.
    /// </summary>
    /// <param name="ffmpegContext">FFmpeg context.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="minPoolSize">Minimum pool size.</param>
    /// <param name="maxPoolSize">Maximum pool size.</param>
    /// <param name="bufferSize">Demuxer buffer size.</param>
    /// <returns>A configured demuxer pool.</returns>
    public static FFmpegProcessorPool CreateDemuxerPool(
        IFFmpegContext? ffmpegContext = null,
        ILogger<FFmpegProcessorPool>? logger = null,
        int minPoolSize = 2,
        int maxPoolSize = 8,
        int bufferSize = 512 * 1024
    )
    {
        return new FFmpegProcessorPool(
            new FFmpegProcessorPoolOptions
            {
                Kind = FFmpegProcessorKind.Demuxer,
                MinPoolSize = minPoolSize,
                MaxPoolSize = maxPoolSize,
                BufferSize = bufferSize,
            },
            ffmpegContext,
            logger
        );
    }

    /// <summary>
    /// Creates a remuxer pool.
    /// </summary>
    /// <param name="ffmpegContext">FFmpeg context.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="minPoolSize">Minimum pool size.</param>
    /// <param name="maxPoolSize">Maximum pool size.</param>
    /// <returns>A configured remuxer pool.</returns>
    public static FFmpegProcessorPool CreateRemuxerPool(
        IFFmpegContext? ffmpegContext = null,
        ILogger<FFmpegProcessorPool>? logger = null,
        int minPoolSize = 2,
        int maxPoolSize = 8
    )
    {
        return new FFmpegProcessorPool(
            new FFmpegProcessorPoolOptions
            {
                Kind = FFmpegProcessorKind.Remuxer,
                MinPoolSize = minPoolSize,
                MaxPoolSize = maxPoolSize,
            },
            ffmpegContext,
            logger
        );
    }
}
