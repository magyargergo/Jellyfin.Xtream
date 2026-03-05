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
using System.Runtime.InteropServices;
using Jellyfin.Xtream.Service.Streaming.Native;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.Streaming.SharedMemory;

/// <summary>
/// Managed wrapper for native shared memory producer.
/// Used for E2E testing to simulate a native TS data producer.
/// </summary>
/// <remarks>
/// <para>
/// This class creates a shared memory region that can be consumed by
/// <see cref="SharedMemoryConsumer"/> or the native SharedMemoryConsumer.
/// </para>
/// <para>
/// Thread safety: Write, Signal, and Set* methods should be called from a single thread.
/// IsConsumerAttached is safe to call from any thread.
/// </para>
/// </remarks>
public sealed class SharedMemoryProducer : IDisposable
{
    /// <summary>
    /// Default number of slots in the ring buffer (must be power of 2).
    /// </summary>
    public const uint DefaultSlotCount = 1024;

    /// <summary>
    /// Default packets per slot (7 packets fits well in cache).
    /// </summary>
    public const uint DefaultPacketsPerSlot = 7;

    /// <summary>
    /// TS packet size in bytes.
    /// </summary>
    public const uint TsPacketSize = 188;

    /// <summary>
    /// Default slot size in bytes (7 packets * 188 bytes).
    /// </summary>
    public const uint DefaultSlotSize = DefaultPacketsPerSlot * TsPacketSize;

    private readonly SharedMemoryProducerSafeHandle _producer;
    private readonly ILogger? _logger;
    private readonly string _name;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SharedMemoryProducer"/> class.
    /// </summary>
    /// <param name="name">Unique name for the shared memory region.</param>
    /// <param name="slotCount">Number of slots in ring buffer (must be power of 2).</param>
    /// <param name="slotSize">Size of each slot in bytes (must be multiple of 188).</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="name"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="slotCount"/> is not a power of 2
    /// or <paramref name="slotSize"/> is not a multiple of 188.</exception>
    /// <exception cref="TsDuckNativeException">Thrown if native producer creation fails.</exception>
    public SharedMemoryProducer(
        string name,
        uint slotCount = DefaultSlotCount,
        uint slotSize = DefaultSlotSize,
        ILogger? logger = null
    )
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!IsPowerOf2(slotCount))
        {
            throw new ArgumentException("Slot count must be a power of 2", nameof(slotCount));
        }

        if (slotSize < TsPacketSize || slotSize % TsPacketSize != 0)
        {
            throw new ArgumentException("Slot size must be a positive multiple of 188", nameof(slotSize));
        }

        _name = name;
        _logger = logger;

        // Initialize native logging (idempotent - safe to call multiple times)
        NativeLogging.Initialize(logger);

        _producer = SharedMemoryProducerSafeHandle.Create(name, slotCount, slotSize);

        if (_producer.IsInvalid)
        {
            throw new TsDuckNativeException($"Failed to create shared memory producer '{name}'");
        }

        _logger?.LogDebugIfEnabled(
            "SharedMemoryProducer '{Name}' created ({SlotCount} slots x {SlotSize} bytes)",
            name,
            slotCount,
            slotSize
        );
    }

    /// <summary>
    /// Gets the name of the shared memory region.
    /// </summary>
    public string Name => _name;

    /// <summary>
    /// Gets a value indicating whether a consumer is currently attached to the shared memory region.
    /// </summary>
    public bool IsConsumerAttached
    {
        get
        {
            if (_disposed)
            {
                return false;
            }

            return TsDuckNativeMethods.ShmProducerIsConsumerAttached(_producer.DangerousGetHandle()) != 0;
        }
    }

    /// <summary>
    /// Creates a new <see cref="SharedMemoryProducer"/> instance, returning null if the native library
    /// is unavailable or creation fails. Use this for graceful degradation.
    /// </summary>
    /// <param name="name">Unique name for the shared memory region.</param>
    /// <param name="slotCount">Number of slots in ring buffer.</param>
    /// <param name="slotSize">Size of each slot in bytes.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <returns>A new producer instance, or null if creation failed.</returns>
    public static SharedMemoryProducer? TryCreate(
        string name,
        uint slotCount = DefaultSlotCount,
        uint slotSize = DefaultSlotSize,
        ILogger? logger = null
    )
    {
        try
        {
            return new SharedMemoryProducer(name, slotCount, slotSize, logger);
        }
        catch (TsDuckNativeException ex)
        {
            logger?.PluginLogWarning(ex, "Shared memory producer unavailable: {Message}", ex.Message);
            return null;
        }
        catch (DllNotFoundException ex)
        {
            logger?.PluginLogWarning(ex, "Native TsDuck library not found: {Message}", ex.Message);
            return null;
        }
        catch (EntryPointNotFoundException ex)
        {
            logger?.PluginLogWarning(ex, "Native TsDuck entry point missing: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Writes TS packet data to the shared memory ring buffer.
    /// </summary>
    /// <param name="data">TS packet data (must be multiple of 188 bytes).</param>
    /// <returns>Number of bytes actually written.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if the producer has been disposed.</exception>
    /// <exception cref="ArgumentException">Thrown if data length is not a multiple of 188.</exception>
    public int Write(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (data.IsEmpty)
        {
            return 0;
        }

        if (data.Length % TsPacketSize != 0)
        {
            throw new ArgumentException("Data must be a multiple of 188 bytes", nameof(data));
        }

        unsafe
        {
            fixed (byte* ptr = data)
            {
                var result = TsDuckNativeMethods.ShmProducerWrite(
                    _producer.DangerousGetHandle(),
                    (nint)ptr,
                    (uint)data.Length,
                    out var bytesWritten
                );

                if (result < 0)
                {
                    throw new InvalidOperationException("Failed to write to shared memory");
                }

                if (result == 1)
                {
                    _logger?.PluginLogWarning("Shared memory buffer overflow - data may have been lost");
                }

                return (int)bytesWritten;
            }
        }
    }

    /// <summary>
    /// Writes TS packet data to the shared memory ring buffer.
    /// </summary>
    /// <param name="data">TS packet data (must be multiple of 188 bytes).</param>
    /// <param name="overflow">Set to true if buffer overflow occurred (data may have been lost).</param>
    /// <returns>Number of bytes actually written.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if the producer has been disposed.</exception>
    /// <exception cref="ArgumentException">Thrown if data length is not a multiple of 188.</exception>
    public int Write(ReadOnlySpan<byte> data, out bool overflow)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        overflow = false;

        if (data.IsEmpty)
        {
            return 0;
        }

        if (data.Length % TsPacketSize != 0)
        {
            throw new ArgumentException("Data must be a multiple of 188 bytes", nameof(data));
        }

        unsafe
        {
            fixed (byte* ptr = data)
            {
                var result = TsDuckNativeMethods.ShmProducerWrite(
                    _producer.DangerousGetHandle(),
                    (nint)ptr,
                    (uint)data.Length,
                    out var bytesWritten
                );

                if (result < 0)
                {
                    throw new InvalidOperationException("Failed to write to shared memory");
                }

                overflow = result == 1;
                return (int)bytesWritten;
            }
        }
    }

    /// <summary>
    /// Signals the consumer that data is available.
    /// Call after writing a batch of data for efficient wakeup.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if the producer has been disposed.</exception>
    public void Signal()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        TsDuckNativeMethods.ShmProducerSignal(_producer.DangerousGetHandle());
    }

    /// <summary>
    /// Sets the end-of-stream flag.
    /// Consumer will complete after reading remaining data.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if the producer has been disposed.</exception>
    public void SetEndOfStream()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        TsDuckNativeMethods.ShmProducerSetEndOfStream(_producer.DangerousGetHandle());
        _logger?.LogDebugIfEnabled("SharedMemoryProducer '{Name}' set end-of-stream", _name);
    }

    /// <summary>
    /// Sets an error condition.
    /// </summary>
    /// <param name="code">Error code.</param>
    /// <param name="message">Human-readable error message.</param>
    /// <exception cref="ObjectDisposedException">Thrown if the producer has been disposed.</exception>
    public void SetError(uint code, string message)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        TsDuckNativeMethods.ShmProducerSetError(_producer.DangerousGetHandle(), code, message ?? string.Empty);
        _logger?.LogDebugIfEnabled(
            "SharedMemoryProducer '{Name}' set error: code={Code}, message={Message}",
            _name,
            code,
            message
        );
    }

    /// <summary>
    /// Sets the discontinuity flag.
    /// Consumer should handle stream discontinuity (e.g., after URL switch).
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if the producer has been disposed.</exception>
    public void SetDiscontinuity()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        TsDuckNativeMethods.ShmProducerSetDiscontinuity(_producer.DangerousGetHandle());
        _logger?.LogDebugIfEnabled("SharedMemoryProducer '{Name}' set discontinuity", _name);
    }

    /// <summary>
    /// Clears the discontinuity flag.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if the producer has been disposed.</exception>
    public void ClearDiscontinuity()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        TsDuckNativeMethods.ShmProducerClearDiscontinuity(_producer.DangerousGetHandle());
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _producer.Dispose();
        _logger?.LogDebugIfEnabled("SharedMemoryProducer '{Name}' disposed", _name);
    }

    private static bool IsPowerOf2(uint value) => value != 0 && (value & (value - 1)) == 0;
}
