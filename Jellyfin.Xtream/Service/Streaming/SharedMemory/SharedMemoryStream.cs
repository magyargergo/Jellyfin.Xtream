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
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Xtream.Service.Streaming.SharedMemory;

/// <summary>
/// A Stream wrapper around SharedMemoryConsumer for integration with
/// existing streaming infrastructure that expects Stream-based APIs.
/// </summary>
/// <remarks>
/// <para>
/// This stream is read-only and provides blocking reads that wait for
/// data from the native producer. It integrates with CancellationToken
/// for graceful shutdown.
/// </para>
/// <para>
/// The stream automatically handles:
/// <list type="bullet">
/// <item>End of stream detection</item>
/// <item>Error propagation from native producer</item>
/// <item>Discontinuity notification via events</item>
/// <item>Overflow notification via events</item>
/// </list>
/// </para>
/// </remarks>
public sealed class SharedMemoryStream : Stream
{
    private readonly SharedMemoryConsumer? _ownedConsumer;
    private readonly SharedMemoryConsumer _consumer;
    private readonly CancellationToken _cancellationToken;
    private readonly TimeSpan _readTimeout;
    private bool _disposed;
    private long _position;

    /// <summary>
    /// Event raised when a stream discontinuity is detected.
    /// </summary>
    public event EventHandler? DiscontinuityDetected;

    /// <summary>
    /// Event raised when buffer overflow occurs (data may be lost).
    /// </summary>
    public event EventHandler? OverflowDetected;

    /// <summary>
    /// Event raised when an error is received from the producer.
    /// </summary>
    public event EventHandler<SharedMemoryErrorEventArgs>? ErrorReceived;

    /// <summary>
    /// Initializes a new instance of the <see cref="SharedMemoryStream"/> class.
    /// </summary>
    /// <param name="sharedMemoryName">Name of the shared memory region.</param>
    /// <param name="readTimeout">Maximum time to wait for data on each read.</param>
    /// <param name="cancellationToken">Cancellation token for stopping reads.</param>
    public SharedMemoryStream(
        string sharedMemoryName,
        TimeSpan? readTimeout = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(sharedMemoryName);

        _ownedConsumer = new SharedMemoryConsumer(sharedMemoryName);
        _consumer = _ownedConsumer;
        _cancellationToken = cancellationToken;
        _readTimeout = readTimeout ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SharedMemoryStream"/> class
    /// using an existing consumer.
    /// </summary>
    /// <param name="consumer">Shared memory consumer instance.</param>
    /// <param name="readTimeout">Maximum time to wait for data on each read.</param>
    /// <param name="cancellationToken">Cancellation token for stopping reads.</param>
    [SuppressMessage(
        "IDisposableAnalyzers.Correctness",
        "IDISP008:Don't assign member with injected and created disposables",
        Justification = "Ownership is tracked via separate _ownedConsumer field; injected consumers are not disposed."
    )]
    public SharedMemoryStream(
        SharedMemoryConsumer consumer,
        TimeSpan? readTimeout = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(consumer);

        _ownedConsumer = null;
        _consumer = consumer;
        _cancellationToken = cancellationToken;
        _readTimeout = readTimeout ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Gets the shared memory consumer.
    /// </summary>
    public SharedMemoryConsumer Consumer => _consumer;

    /// <summary>
    /// Gets the producer state.
    /// </summary>
    public ProducerState ProducerState => _consumer.ProducerState;

    /// <summary>
    /// Gets statistics about the shared memory channel.
    /// </summary>
    public SharedMemoryStatistics Statistics => _consumer.GetStatistics();

    /// <inheritdoc/>
    public override bool CanRead => !_disposed;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (buffer.IsEmpty)
        {
            return 0;
        }

        // Check for events before reading
        CheckForEvents();

        // Check for error condition
        if (_consumer.HasError)
        {
            var errorCode = _consumer.ErrorCode;
            var errorMessage = _consumer.ErrorMessage;
            ErrorReceived?.Invoke(this, new SharedMemoryErrorEventArgs(errorCode, errorMessage));
            throw new IOException($"Shared memory producer error: {errorCode} - {errorMessage}");
        }

        // Wait for data with timeout
        while (!_cancellationToken.IsCancellationRequested)
        {
            // Try to read available data
            int bytesRead = _consumer.Read(buffer);
            if (bytesRead > 0)
            {
                _position += bytesRead;
                CheckForEvents();
                return bytesRead;
            }

            // Check for end of stream
            if (_consumer.IsEndOfStream)
            {
                return 0;
            }

            // Check for error
            if (_consumer.HasError)
            {
                var errorCode = _consumer.ErrorCode;
                var errorMessage = _consumer.ErrorMessage;
                ErrorReceived?.Invoke(this, new SharedMemoryErrorEventArgs(errorCode, errorMessage));
                throw new IOException($"Shared memory producer error: {errorCode} - {errorMessage}");
            }

            // Wait for data
            if (!_consumer.WaitForData(_readTimeout, _cancellationToken))
            {
                // Timeout - check if producer is still alive
                var state = _consumer.ProducerState;
                if (state is ProducerState.Stopped or ProducerState.Failed)
                {
                    return 0;
                }

                // Continue waiting
            }
        }

        // Cancelled
        _cancellationToken.ThrowIfCancellationRequested();
        return 0;
    }

    /// <inheritdoc/>
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (buffer.IsEmpty)
        {
            return 0;
        }

        // Use combined cancellation token
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken, cancellationToken);

        // Run the blocking read on a thread pool thread
        return await Task.Run(() => Read(buffer.Span), cts.Token).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override void Flush()
    {
        // No-op for read-only stream
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (disposing)
        {
            _ownedConsumer?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void CheckForEvents()
    {
        if (_consumer.ConsumeDiscontinuity())
        {
            DiscontinuityDetected?.Invoke(this, EventArgs.Empty);
        }

        if (_consumer.ConsumeOverflow())
        {
            OverflowDetected?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>
/// Event arguments for shared memory errors.
/// </summary>
public sealed class SharedMemoryErrorEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SharedMemoryErrorEventArgs"/> class.
    /// </summary>
    /// <param name="errorCode">The error code.</param>
    /// <param name="message">The error message.</param>
    public SharedMemoryErrorEventArgs(SharedMemoryErrorCode errorCode, string message)
    {
        ErrorCode = errorCode;
        Message = message;
    }

    /// <summary>
    /// Gets the error code.
    /// </summary>
    public SharedMemoryErrorCode ErrorCode { get; }

    /// <summary>
    /// Gets the error message.
    /// </summary>
    public string Message { get; }
}
