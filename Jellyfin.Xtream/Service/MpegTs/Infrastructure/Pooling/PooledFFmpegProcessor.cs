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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Service.MpegTs.UseCases;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.MpegTs.Infrastructure.Pooling;

/// <summary>
/// Unified pooled FFmpeg processor supporting both demuxing and remuxing.
/// </summary>
/// <remarks>
/// <para>
/// This class consolidates <c>PooledFFmpegStreamDemuxer</c> and <c>PooledFFmpegRemuxer</c>
/// into a single implementation, differentiated by <see cref="Kind"/>.
/// </para>
/// <para>
/// Key design principles:
/// <list type="bullet">
///   <item>
///     <description>
///       Uses "replace don't reset" pattern - creates fresh inner processor on reuse
///       to avoid SIGSEGV from FFmpeg cleanup while blocked in native calls.
///     </description>
///   </item>
///   <item>
///     <description>
///       Remuxer mode uses background worker with producer-consumer queues to avoid
///       blocking HTTP read loop during <c>avformat_find_stream_info()</c> (~500ms).
///     </description>
///   </item>
///   <item>
///     <description>
///       Health tracking allows pool to discard corrupted instances.
///     </description>
///   </item>
/// </list>
/// </para>
/// </remarks>
public sealed class PooledFFmpegProcessor : IPooledFFmpegProcessor
{
    // Increased from 64 to 256 to handle high-bitrate streams (~20Mbps) during
    // FFmpeg initialization which can take 500ms. At 4KB/chunk and 20Mbps:
    // - Old: 64 chunks = 256KB = ~100ms buffer (fills before FFmpeg ready)
    // - New: 256 chunks = 1MB = ~400ms buffer (closer to FFmpeg init time)
    private const int MaxQueueChunks = 256;

    private readonly FFmpegProcessorKind _kind;
    private readonly int _bufferSize;
    private readonly ILogger? _logger;
    private readonly IFFmpegContext _ffmpegContext;
    private readonly object _lock = new();

    // Demuxer state
    private FFmpegStreamDemuxer? _demuxer;

    // Remuxer state
    private FFmpegStreamRemuxer? _remuxer;
    private readonly ConcurrentQueue<byte[]> _inputQueue = new();
    private readonly ConcurrentQueue<byte[]> _outputQueue = new();
    private long _inputQueueBytes;
    private long _outputQueueBytes;
    private int _inputChunkCount;
    private int _outputChunkCount;
    private Task? _workerTask;
    private CancellationTokenSource? _workerCts;
    private SemaphoreSlim? _dataAvailableSignal;

    // Common state
    private string _streamId = string.Empty;
    private volatile bool _isHealthy = true;
    private volatile bool _disposed;

    /// <inheritdoc />
    public Guid InstanceId { get; } = Guid.NewGuid();

    /// <inheritdoc />
    public FFmpegProcessorKind Kind => _kind;

    /// <inheritdoc />
    public bool IsHealthy =>
        _isHealthy && !_disposed && (_kind == FFmpegProcessorKind.Demuxer ? _demuxer != null : _remuxer != null);

    /// <inheritdoc />
    public bool IsInitialized =>
        _kind == FFmpegProcessorKind.Demuxer ? _demuxer?.IsInitialized ?? false : _remuxer != null;

    /// <inheritdoc />
    public bool IsRunning =>
        _kind == FFmpegProcessorKind.Demuxer ? _demuxer?.IsInitialized ?? false : _remuxer?.IsRunning ?? false;

    /// <inheritdoc />
    public int ProgramCount => _demuxer?.ProgramCount ?? 0;

    /// <inheritdoc />
    public bool HasOutput => !_outputQueue.IsEmpty;

    /// <inheritdoc />
    public long QueuedBytes => Interlocked.Read(ref _inputQueueBytes);

    /// <inheritdoc />
    public InProcessRemuxerStatistics Statistics => _remuxer?.Statistics ?? default;

    /// <summary>
    /// Initializes a new instance of the <see cref="PooledFFmpegProcessor"/> class.
    /// </summary>
    /// <param name="kind">The processor kind (demuxer or remuxer).</param>
    /// <param name="bufferSize">Size of the input buffer (demuxer mode).</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="ffmpegContext">FFmpeg context.</param>
    public PooledFFmpegProcessor(
        FFmpegProcessorKind kind,
        int bufferSize = 512 * 1024,
        ILogger? logger = null,
        IFFmpegContext? ffmpegContext = null
    )
    {
        _kind = kind;
        _bufferSize = bufferSize;
        _logger = logger;
        _ffmpegContext = ffmpegContext ?? FFmpegContextAdapter.Instance;

        InitializeInner();

        _logger?.LogDebugIfEnabled("PooledFFmpegProcessor {Id} created as {Kind}", InstanceId, _kind);
    }

    #region Events (Demuxer mode)

    /// <inheritdoc />
    public event EventHandler<DemuxerProgramEventArgs>? ProgramDetected
    {
        add
        {
            if (_kind == FFmpegProcessorKind.Demuxer && _demuxer != null)
            {
                _demuxer.ProgramDetected += value;
            }
        }
        remove
        {
            if (_kind == FFmpegProcessorKind.Demuxer && _demuxer != null)
            {
                _demuxer.ProgramDetected -= value;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<DemuxedPacketEventArgs>? PacketDemuxed
    {
        add
        {
            if (_kind == FFmpegProcessorKind.Demuxer && _demuxer != null)
            {
                _demuxer.PacketDemuxed += value;
            }
        }
        remove
        {
            if (_kind == FFmpegProcessorKind.Demuxer && _demuxer != null)
            {
                _demuxer.PacketDemuxed -= value;
            }
        }
    }

    #endregion

    #region Input Methods

    /// <inheritdoc />
    public void FeedData(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            if (_kind == FFmpegProcessorKind.Demuxer)
            {
                EnsureDemuxer().FeedData(data);
            }
            else
            {
                // For remuxer, delegate to queue-based input
                TryQueueData(data);
            }
        }
        catch (Exception ex)
        {
            _isHealthy = false;
            _logger?.PluginLogWarning(ex, "Processor {Id} marked unhealthy after FeedData error", InstanceId);
            throw;
        }
    }

    /// <inheritdoc />
    public bool TryQueueData(ReadOnlySpan<byte> data)
    {
        if (_disposed || data.IsEmpty)
        {
            return false;
        }

        if (_kind == FFmpegProcessorKind.Demuxer)
        {
            // In demuxer mode, just feed directly
            FeedData(data);
            return true;
        }

        // Remuxer mode: queue for background processing
        if (Volatile.Read(ref _inputChunkCount) >= MaxQueueChunks)
        {
            _logger?.PluginLogWarning(
                "Processor {Id} input queue full ({Count} chunks, {Bytes}KB)",
                InstanceId,
                _inputChunkCount,
                _inputQueueBytes / 1024
            );
            return false;
        }

        var chunk = data.ToArray();
        _inputQueue.Enqueue(chunk);
        Interlocked.Add(ref _inputQueueBytes, chunk.Length);
        Interlocked.Increment(ref _inputChunkCount);

        // Signal worker
        try
        {
            _dataAvailableSignal?.Release();
        }
        catch (SemaphoreFullException)
        {
            // Worker will process
        }

        return true;
    }

    #endregion

    #region Processing

    /// <inheritdoc />
    public bool Process()
    {
        if (_disposed)
        {
            return false;
        }

        if (_kind == FFmpegProcessorKind.Demuxer)
        {
            try
            {
                return EnsureDemuxer().Process();
            }
            catch (Exception ex)
            {
                _isHealthy = false;
                _logger?.PluginLogWarning(ex, "Processor {Id} marked unhealthy after Process error", InstanceId);
                throw;
            }
        }

        // Remuxer mode: background worker handles processing
        return false;
    }

    /// <inheritdoc />
    public void Reset()
    {
        _logger?.LogDebugIfEnabled("Processor {Id} Reset called (use PrepareForReuse for pool)", InstanceId);

        if (_kind == FFmpegProcessorKind.Remuxer)
        {
            _remuxer?.Reset();
        }
    }

    /// <inheritdoc />
    public void SetStreamId(string streamId)
    {
        _streamId = streamId;

        if (_kind == FFmpegProcessorKind.Remuxer)
        {
            _remuxer?.Initialize(streamId);
        }
    }

    #endregion

    #region Demuxer Output

    /// <inheritdoc />
    public IEnumerable<int> GetProgramNumbers() => _demuxer?.GetProgramNumbers() ?? Array.Empty<int>();

    /// <inheritdoc />
    public int GetVideoPid(int programNumber) => _demuxer?.GetVideoPid(programNumber) ?? -1;

    /// <inheritdoc />
    public int[] GetAudioPids(int programNumber) => _demuxer?.GetAudioPids(programNumber) ?? Array.Empty<int>();

    /// <inheritdoc />
    public int GetPcrPid(int programNumber) => _demuxer?.GetPcrPid(programNumber) ?? -1;

    /// <inheritdoc />
    public int GetPmtPid(int programNumber) => _demuxer?.GetPmtPid(programNumber) ?? -1;

    #endregion

    #region Remuxer Output

    /// <inheritdoc />
    public bool TryReadOutput(out byte[]? output)
    {
        if (_outputQueue.TryDequeue(out output))
        {
            Interlocked.Add(ref _outputQueueBytes, -output!.Length);
            Interlocked.Decrement(ref _outputChunkCount);
            return true;
        }

        output = null;
        return false;
    }

    /// <inheritdoc />
    public void NotifyProviderSwitch(string? fromProvider, string? toProvider)
    {
        if (_kind == FFmpegProcessorKind.Remuxer)
        {
            _remuxer?.NotifyProviderSwitch(fromProvider, toProvider);
        }
    }

    #endregion

    #region Lifecycle

    /// <inheritdoc />
    public bool PrepareForReuse()
    {
        if (_disposed)
        {
            return false;
        }

        lock (_lock)
        {
            if (_disposed)
            {
                return false;
            }

            _logger?.LogDebugIfEnabled("Processor {Id} ({Kind}) preparing for reuse", InstanceId, _kind);

            // Cleanup old resources
            DisposeInnerAsync();

            // Clear queues (remuxer mode)
            ClearQueues();

            // Create fresh inner processor
            try
            {
                InitializeInner();
                _isHealthy = true;
                _streamId = string.Empty;

                _logger?.LogDebugIfEnabled("Processor {Id} ready for reuse", InstanceId);
                return true;
            }
            catch (Exception ex)
            {
                _logger?.PluginLogError(ex, "Failed to recreate processor during PrepareForReuse");
                _isHealthy = false;
                return false;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            _logger?.LogDebugIfEnabled("Processor {Id} ({Kind}) disposing", InstanceId, _kind);

            // Stop worker (remuxer mode)
            StopWorker();

            // Dispose inner processor
            DisposeInnerAsync();

            // Cleanup
            _dataAvailableSignal?.Dispose();
            _workerCts?.Dispose();
        }
    }

    #endregion

    #region Private Methods

    private void InitializeInner()
    {
        if (_kind == FFmpegProcessorKind.Demuxer)
        {
            // Dispose previous if exists (shouldn't happen but satisfies analyzer)
            _demuxer?.Dispose();
            _demuxer = new FFmpegStreamDemuxer(_bufferSize, _logger, _ffmpegContext);
        }
        else
        {
            // Dispose previous if exists (shouldn't happen but satisfies analyzer)
            _remuxer?.Dispose();
            _dataAvailableSignal?.Dispose();
            _workerCts?.Dispose();

            _remuxer = new FFmpegStreamRemuxer(_logger as ILogger<FFmpegStreamRemuxer>, _ffmpegContext);
            _dataAvailableSignal = new SemaphoreSlim(0, int.MaxValue);
            _workerCts = new CancellationTokenSource();
            _workerTask = Task.Run(WorkerLoopAsync);
        }
    }

    private void DisposeInnerAsync()
    {
        if (_kind == FFmpegProcessorKind.Demuxer)
        {
            var oldDemuxer = _demuxer;
            _demuxer = null;

            if (oldDemuxer != null)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        oldDemuxer.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebugIfEnabled(ex, "Error disposing old demuxer");
                    }
                });
            }
        }
        else
        {
            StopWorker();

            var oldRemuxer = _remuxer;
            _remuxer = null;

            if (oldRemuxer != null)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        oldRemuxer.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebugIfEnabled(ex, "Error disposing old remuxer");
                    }
                });
            }

            // Recreate synchronization primitives
            _dataAvailableSignal?.Dispose();
            _workerCts?.Dispose();
        }
    }

    private void StopWorker()
    {
        if (_workerCts != null && !_workerCts.IsCancellationRequested)
        {
            _workerCts.Cancel();

            try
            {
                _dataAvailableSignal?.Release();
            }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }

            try
            {
                _workerTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException) { }
            catch (ObjectDisposedException) { }
        }
    }

    private void ClearQueues()
    {
        while (_inputQueue.TryDequeue(out _)) { }
        while (_outputQueue.TryDequeue(out _)) { }

        Interlocked.Exchange(ref _inputQueueBytes, 0);
        Interlocked.Exchange(ref _outputQueueBytes, 0);
        Interlocked.Exchange(ref _inputChunkCount, 0);
        Interlocked.Exchange(ref _outputChunkCount, 0);
    }

    private FFmpegStreamDemuxer EnsureDemuxer()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var demuxer = _demuxer;
        if (demuxer == null)
        {
            throw new InvalidOperationException("Demuxer is null. Call PrepareForReuse() before using.");
        }

        return demuxer;
    }

    private async Task WorkerLoopAsync()
    {
        var token = _workerCts!.Token;

        _logger?.LogDebugIfEnabled("Processor {Id} worker started", InstanceId);

        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await _dataAvailableSignal!.WaitAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                while (!token.IsCancellationRequested && _inputQueue.TryDequeue(out var chunk))
                {
                    Interlocked.Add(ref _inputQueueBytes, -chunk.Length);
                    Interlocked.Decrement(ref _inputChunkCount);

                    try
                    {
                        var result = _remuxer!.ProcessData(chunk);

                        if (!result.Success)
                        {
                            _logger?.PluginLogWarning(
                                "Processor {Id} ProcessData failed: {Error}",
                                InstanceId,
                                result.ErrorMessage
                            );
                            _isHealthy = false;
                            continue;
                        }

                        if (result.Output is { Length: > 0 })
                        {
                            QueueOutput(result.Output);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.PluginLogError(ex, "Processor {Id} error processing data", InstanceId);
                        _isHealthy = false;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.PluginLogError(ex, "Processor {Id} worker crashed", InstanceId);
            _isHealthy = false;
        }

        _logger?.LogDebugIfEnabled("Processor {Id} worker stopped", InstanceId);
    }

    private void QueueOutput(byte[] output)
    {
        while (Volatile.Read(ref _outputChunkCount) >= MaxQueueChunks && _outputQueue.TryDequeue(out var old))
        {
            Interlocked.Add(ref _outputQueueBytes, -old.Length);
            Interlocked.Decrement(ref _outputChunkCount);
            _logger?.LogDebugIfEnabled("Processor {Id} output overflow, discarding {Size}B", InstanceId, old.Length);
        }

        _outputQueue.Enqueue(output);
        Interlocked.Add(ref _outputQueueBytes, output.Length);
        Interlocked.Increment(ref _outputChunkCount);
    }

    #endregion
}
