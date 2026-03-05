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

namespace Jellyfin.Xtream.Client;

/// <summary>
/// Represents the lifecycle state of a streaming connection.
/// Follows industry-standard connection state machine patterns.
/// </summary>
public enum ConnectionState
{
    /// <summary>
    /// Initial state before connection attempt.
    /// </summary>
    Disconnected,

    /// <summary>
    /// DNS resolution and TCP handshake in progress.
    /// </summary>
    Connecting,

    /// <summary>
    /// HTTP request sent, awaiting response headers.
    /// </summary>
    Negotiating,

    /// <summary>
    /// Successfully connected and streaming data.
    /// </summary>
    Streaming,

    /// <summary>
    /// Connection temporarily interrupted, will retry.
    /// </summary>
    Reconnecting,

    /// <summary>
    /// Graceful shutdown initiated by client.
    /// </summary>
    Closing,

    /// <summary>
    /// Connection failed permanently (no more retries).
    /// </summary>
    Failed,

    /// <summary>
    /// Connection terminated by server (EOF received).
    /// </summary>
    ServerClosed,
}
