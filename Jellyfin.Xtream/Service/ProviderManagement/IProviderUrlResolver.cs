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

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Xtream.Service.ProviderManagement;

/// <summary>
/// Resolves alternative provider URLs for hot-swap operations.
/// </summary>
public interface IProviderUrlResolver
{
    /// <summary>
    /// Gets an alternative provider URL for the given stream.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="currentUrl">The current provider URL to exclude.</param>
    /// <param name="reason">The reason for needing an alternative.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The alternative URL if available, null otherwise.</returns>
    Task<string?> GetAlternativeUrlAsync(
        string streamId,
        string currentUrl,
        SwitchReason reason,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Gets all alternative provider URLs for the given stream, ordered by health score.
    /// </summary>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="currentUrl">The current provider URL to exclude.</param>
    /// <param name="reason">The reason for needing alternatives.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of alternative URLs ordered by health score (best first), empty if none available.</returns>
    Task<IReadOnlyList<string>> GetAllAlternativeUrlsAsync(
        string streamId,
        string currentUrl,
        SwitchReason reason,
        CancellationToken cancellationToken
    );
}
