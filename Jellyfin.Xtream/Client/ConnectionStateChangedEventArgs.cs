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

namespace Jellyfin.Xtream.Client;

/// <summary>
/// Event arguments for connection state changes.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="ConnectionStateChangedEventArgs"/> class.
/// </remarks>
/// <param name="previousState">The previous connection state.</param>
/// <param name="currentState">The current connection state.</param>
public sealed class ConnectionStateChangedEventArgs(ConnectionState previousState, ConnectionState currentState)
    : EventArgs
{
    /// <summary>
    /// Gets the previous connection state.
    /// </summary>
    public ConnectionState PreviousState { get; } = previousState;

    /// <summary>
    /// Gets the current connection state.
    /// </summary>
    public ConnectionState CurrentState { get; } = currentState;

    /// <summary>
    /// Gets the timestamp of the state change.
    /// </summary>
    public DateTime Timestamp { get; } = DateTime.UtcNow;
}
