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
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Jellyfin.Xtream.Service.Discovery.Pipeline;

/// <summary>
/// Interface for a pipeline stage that transforms items.
/// </summary>
public interface IPipelineStage : IAsyncDisposable
{
    /// <summary>
    /// Gets the stage identifier.
    /// </summary>
    PipelineStage StageId { get; }

    /// <summary>
    /// Gets the current statistics for this stage.
    /// </summary>
    StageStats Stats { get; }

    /// <summary>
    /// Gets the input channel for this stage.
    /// </summary>
    ChannelWriter<PipelineItem> Input { get; }

    /// <summary>
    /// Gets the output channel for items that pass.
    /// </summary>
    ChannelReader<PipelineItem> Output { get; }

    /// <summary>
    /// Gets the channel for failed items.
    /// </summary>
    ChannelReader<PipelineItem> Failed { get; }

    /// <summary>
    /// Gets the event channel for stage notifications.
    /// </summary>
    ChannelReader<StageEvent> Events { get; }

    /// <summary>
    /// Runs the stage processing loop.
    /// </summary>
    /// <param name="concurrency">Max parallel processing.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task that completes when stage finishes.</returns>
    Task RunAsync(int concurrency, CancellationToken cancellationToken);

    /// <summary>
    /// Signals that no more input will be provided.
    /// </summary>
    void Complete();
}

/// <summary>
/// Configuration for a pipeline stage.
/// </summary>
public sealed class StageConfiguration
{
    /// <summary>
    /// Gets or sets the maximum degree of parallelism.
    /// </summary>
    public int Concurrency { get; set; } = 10;

    /// <summary>
    /// Gets or sets the timeout per item in milliseconds.
    /// </summary>
    public int TimeoutMs { get; set; } = 5000;

    /// <summary>
    /// Gets or sets the output channel capacity (0 = unbounded).
    /// </summary>
    public int OutputCapacity { get; set; } = 0;

    /// <summary>
    /// Gets or sets whether to continue on individual item errors.
    /// </summary>
    public bool ContinueOnError { get; set; } = true;
}
