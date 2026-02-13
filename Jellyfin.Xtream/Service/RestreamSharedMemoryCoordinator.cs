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
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Service.Streaming.SharedMemory;
using Jellyfin.Xtream.Utility;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Coordinates the shared memory connection between the native C++ streamer and the
/// managed circular buffer. Handles initial connection with exponential backoff,
/// reads data in a loop, and delegates periodic health checks to <see cref="RestreamHealthMonitor"/>.
/// </summary>
internal sealed class RestreamSharedMemoryCoordinator : IDisposable
{
    private const int HealthCheckIntervalSeconds = 30;
    private const int NoDataTimeoutSeconds = 30;

    private readonly ILogger _logger;
    private readonly string _sharedMemoryName;
    private readonly string _streamId;
    private readonly string _streamName;
    private readonly CircularBufferWriteStream _buffer;
    private readonly RestreamHealthMonitor _healthMonitor;
    private readonly Func<int> _getConsumerCount;

    private SharedMemoryConsumer? _shmConsumer;

    /// <summary>
    /// Initializes a new instance of the <see cref="RestreamSharedMemoryCoordinator"/> class.
    /// </summary>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="sharedMemoryName">Name of the shared memory region created by the native producer.</param>
    /// <param name="streamId">Stream identifier for log correlation.</param>
    /// <param name="streamName">Human-readable stream/channel name.</param>
    /// <param name="buffer">The circular buffer that receives data from shared memory.</param>
    /// <param name="healthMonitor">Health monitor for periodic buffer and streamer checks.</param>
    /// <param name="getConsumerCount">Delegate that returns the current consumer count.</param>
    public RestreamSharedMemoryCoordinator(
        ILogger logger,
        string sharedMemoryName,
        string streamId,
        string streamName,
        CircularBufferWriteStream buffer,
        RestreamHealthMonitor healthMonitor,
        Func<int> getConsumerCount
    )
    {
        _logger = logger;
        _sharedMemoryName = sharedMemoryName;
        _streamId = streamId;
        _streamName = streamName;
        _buffer = buffer;
        _healthMonitor = healthMonitor;
        _getConsumerCount = getConsumerCount;
    }

    /// <summary>
    /// Connects to shared memory and reads data in a loop, writing to the circular buffer.
    /// Handles discontinuity, overflow, end-of-stream, and error flags from the producer.
    /// Uses exponential backoff for initial shared memory connection.
    /// </summary>
    /// <param name="nativeStreamer">The native streamer, used for health checks.</param>
    /// <param name="receivingData">Reference to volatile flag indicating data reception is active.</param>
    /// <param name="setKillReason">Callback to set the kill reason when the stream must stop.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>A task representing the asynchronous read loop.</returns>
    public async Task ConnectAndReadAsync(
        Streaming.Native.NativeStreamer? nativeStreamer,
        Func<bool> receivingData,
        Action<string> setKillReason,
        CancellationToken cancellationToken
    )
    {
        if (!await TryConnectAsync(setKillReason, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        _logger.PluginLogInformation("Connected to shared memory: {Name}", _sharedMemoryName);

        var consumer = _shmConsumer;
        if (consumer == null)
        {
            setKillReason("Shared memory consumer unexpectedly null");
            return;
        }

        var lastHealthCheckTime = DateTime.UtcNow;
        var lastDataTime = DateTime.UtcNow;

        while (!cancellationToken.IsCancellationRequested && receivingData())
        {
            // Wait for data with timeout
            if (!consumer.WaitForData(TimeSpan.FromMilliseconds(100), cancellationToken))
            {
                // Check for terminal conditions
                if (consumer.IsEndOfStream && consumer.AvailableBytes == 0)
                {
                    _logger.PluginLogInformation("Shared memory end of stream for {ChannelId}", _streamId);
                    setKillReason("Stream ended");
                    break;
                }

                if (consumer.HasError)
                {
                    _logger.PluginLogWarning(
                        "Shared memory error {Code}: {Message}",
                        consumer.ErrorCode,
                        consumer.ErrorMessage
                    );
                    setKillReason($"Shared memory error: {consumer.ErrorCode}");
                    break;
                }

                // No-data timeout: if no data arrives for 30s, the stream is dead
                if ((DateTime.UtcNow - lastDataTime).TotalSeconds >= NoDataTimeoutSeconds)
                {
                    _logger.PluginLogWarning(
                        "No data from shared memory for {Timeout}s for channel {ChannelId}",
                        NoDataTimeoutSeconds,
                        _streamId
                    );
                    setKillReason("No data timeout");
                    break;
                }

                continue;
            }

            // Data received - reset no-data timer
            lastDataTime = DateTime.UtcNow;

            // Handle discontinuity (URL switch in native streamer)
            if (consumer.ConsumeDiscontinuity())
            {
                _logger.LogDebugIfEnabled("Discontinuity detected for {ChannelId}", _streamId);
                _buffer.MarkDiscontinuityAligned();
            }

            // Handle overflow (slow consumer - data was dropped)
            if (consumer.ConsumeOverflow())
            {
                _logger.PluginLogWarning("Shared memory overflow for {ChannelId}", _streamId);
            }

            // Read directly from shared memory to circular buffer (zero-copy)
            consumer.ReadTo(_buffer);

            // Periodic health check
            var now = DateTime.UtcNow;
            if ((now - lastHealthCheckTime).TotalSeconds >= HealthCheckIntervalSeconds)
            {
                lastHealthCheckTime = now;
                _healthMonitor.PerformHealthCheck(
                    _streamId,
                    _streamName,
                    nativeStreamer,
                    _buffer,
                    _getConsumerCount(),
                    setKillReason
                );
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _shmConsumer?.Dispose();
        _shmConsumer = null;
    }

    /// <summary>
    /// Attempts to connect to shared memory with exponential backoff.
    /// The C++ worker creates the /dev/shm file on startup, but thread scheduling
    /// can delay this. Retry avoids the race condition where C# opens before C++ creates.
    /// </summary>
    private async Task<bool> TryConnectAsync(Action<string> setKillReason, CancellationToken cancellationToken)
    {
        const int maxRetries = 6;
        var retryDelayMs = 100;

        for (int attempt = 0; attempt < maxRetries && !cancellationToken.IsCancellationRequested; attempt++)
        {
            await Task.Delay(retryDelayMs, cancellationToken).ConfigureAwait(false);

            try
            {
                _shmConsumer?.Dispose();
                _shmConsumer = new SharedMemoryConsumer(_sharedMemoryName);
                return true;
            }
            catch (FileNotFoundException) when (attempt < maxRetries - 1)
            {
                _logger.PluginLogInformation(
                    "Shared memory not ready (attempt {Attempt}/{Max}), retrying in {Delay}ms",
                    attempt + 1,
                    maxRetries,
                    retryDelayMs
                );
                retryDelayMs = Math.Min(retryDelayMs * 2, 1600);
            }
            catch (Exception ex)
            {
                _logger.PluginLogError(ex, "Failed to connect to shared memory: {Name}", _sharedMemoryName);
                setKillReason("Shared memory connection failed");
                return false;
            }
        }

        _logger.PluginLogWarning(
            "Shared memory not available after {Max} retries for channel {ChannelId}: {Name}",
            maxRetries,
            _streamId,
            _sharedMemoryName
        );
        setKillReason("Shared memory connection failed after retries");
        return false;
    }
}
