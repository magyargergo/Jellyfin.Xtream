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
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Extension methods for fire-and-forget Discord notifications.
/// Provides a clean pattern for sending notifications without blocking the caller.
/// </summary>
public static class DiscordNotificationExtensions
{
    /// <summary>
    /// Sends a Discord notification in a fire-and-forget manner.
    /// Handles null service checks and exceptions internally.
    /// </summary>
    /// <param name="service">The Discord notification service (can be null).</param>
    /// <param name="notificationAction">The async action that sends the notification.</param>
    public static void SendFireAndForget(
        this IDiscordNotificationService? service,
        Func<IDiscordNotificationService, Task> notificationAction
    )
    {
        if (service == null)
        {
            return;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await notificationAction(service).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore notification failures - notifications should never affect stream processing
                }
            },
            CancellationToken.None
        );
    }
}
