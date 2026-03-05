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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Jellyfin.Xtream.Service.Events;

/// <summary>
/// In-memory event bus for broadcasting plugin events to SSE subscribers.
/// Thread-safe singleton - publish from any thread, subscribe from API endpoints.
/// </summary>
public sealed class PluginEventBus
{
    /// <summary>
    /// Maximum events retained in the replay buffer.
    /// </summary>
    private const int MaxReplayBuffer = 200;

    private static readonly Lazy<PluginEventBus> LazyInstance = new(() => new PluginEventBus());

    private readonly ConcurrentDictionary<Guid, Channel<PluginEvent>> _subscribers = new();
    private readonly LinkedList<PluginEvent> _replayBuffer = new();
    private readonly object _bufferLock = new();
    private long _nextId;

    private PluginEventBus() { }

    /// <summary>
    /// Gets the singleton instance.
    /// </summary>
    public static PluginEventBus Instance => LazyInstance.Value;

    /// <summary>
    /// Publish an event to all subscribers and the replay buffer.
    /// </summary>
    /// <param name="type">Event type string (e.g. "stream.started").</param>
    /// <param name="streamId">Optional stream ID.</param>
    /// <param name="data">Optional event data.</param>
    public void Publish(string type, string? streamId = null, Dictionary<string, object>? data = null)
    {
        var evt = new PluginEvent
        {
            Id = Interlocked.Increment(ref _nextId),
            Type = type,
            Timestamp = DateTime.UtcNow,
            StreamId = streamId,
            Data = data,
        };

        lock (_bufferLock)
        {
            _replayBuffer.AddLast(evt);
            while (_replayBuffer.Count > MaxReplayBuffer)
            {
                _replayBuffer.RemoveFirst();
            }
        }

        foreach (var kvp in _subscribers)
        {
            kvp.Value.Writer.TryWrite(evt);
        }
    }

    /// <summary>
    /// Subscribe to the event stream. Returns events as they arrive.
    /// Optionally replays events after a given lastEventId.
    /// </summary>
    /// <param name="lastEventId">Resume from this event ID (0 for no replay).</param>
    /// <param name="cancellationToken">Cancellation token to end subscription.</param>
    /// <returns>Async enumerable of plugin events.</returns>
    public async IAsyncEnumerable<PluginEvent> SubscribeAsync(
        long lastEventId = 0,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<PluginEvent>(
            new BoundedChannelOptions(512)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            }
        );
        _subscribers.TryAdd(id, channel);

        try
        {
            // Replay missed events
            if (lastEventId > 0)
            {
                lock (_bufferLock)
                {
                    foreach (var evt in _replayBuffer)
                    {
                        if (evt.Id > lastEventId)
                        {
                            yield return evt;
                        }
                    }
                }
            }

            // Stream live events
            await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return evt;
            }
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Gets the current number of active subscribers.
    /// </summary>
    /// <returns>Active subscriber count.</returns>
    public int GetSubscriberCount() => _subscribers.Count;

    /// <summary>
    /// Gets the most recent events from the replay buffer.
    /// </summary>
    /// <param name="count">Maximum number of events to return.</param>
    /// <returns>Recent events, newest last.</returns>
    public IReadOnlyList<PluginEvent> GetRecentEvents(int count = 50)
    {
        lock (_bufferLock)
        {
            var result = new List<PluginEvent>(Math.Min(count, _replayBuffer.Count));
            var node = _replayBuffer.Last;
            while (node != null && result.Count < count)
            {
                result.Add(node.Value);
                node = node.Previous;
            }

            result.Reverse();
            return result;
        }
    }
}
