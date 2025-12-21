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
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Xtream.Service.Discovery;

/// <summary>
/// Interface for testing discovered provider credentials.
/// </summary>
public interface IProviderTester
{
    /// <summary>
    /// Tests a single credential.
    /// </summary>
    /// <param name="credential">The credential to test.</param>
    /// <param name="testStream">Whether to test stream playback.</param>
    /// <param name="testEpg">Whether to test EPG availability.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The test result.</returns>
    Task<ProviderTestResult> TestCredentialAsync(
        DiscoveredCredential credential,
        bool testStream,
        bool testEpg,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Tests multiple credentials in parallel.
    /// </summary>
    /// <param name="credentials">The credentials to test.</param>
    /// <param name="maxWorkers">Maximum parallel workers.</param>
    /// <param name="testStream">Whether to test stream playback.</param>
    /// <param name="testEpg">Whether to test EPG availability.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of test results.</returns>
    Task<IReadOnlyList<ProviderTestResult>> TestCredentialsAsync(
        IReadOnlyList<DiscoveredCredential> credentials,
        int maxWorkers,
        bool testStream,
        bool testEpg,
        IProgress<DiscoveryProgress>? progress,
        CancellationToken cancellationToken
    );
}
