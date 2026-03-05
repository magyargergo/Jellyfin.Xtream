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
using System.Collections.Immutable;

namespace Jellyfin.Xtream.Service.Discovery.Pipeline;

/// <summary>
/// Identifies stages in the discovery pipeline.
/// Ordered by execution sequence.
/// </summary>
public enum PipelineStage
{
    /// <summary>Initial state before processing.</summary>
    Pending = 0,

    /// <summary>TCP connectivity check.</summary>
    Connectivity = 1,

    /// <summary>API authentication validation.</summary>
    Authentication = 2,

    /// <summary>Stream playback test.</summary>
    StreamTest = 3,

    /// <summary>EPG data availability check.</summary>
    EpgCheck = 4,

    /// <summary>Country-specific channel detection.</summary>
    CountryFilter = 5,

    /// <summary>Quality and trust scoring.</summary>
    QualityScoring = 6,

    /// <summary>Successfully completed all stages.</summary>
    Completed = 7,

    /// <summary>Failed at some stage.</summary>
    Failed = -1,
}

/// <summary>
/// Immutable item flowing through the discovery pipeline.
/// Each stage can attach properties for downstream consumption.
/// </summary>
public sealed record PipelineItem
{
    /// <summary>
    /// Gets the unique identifier for this pipeline item.
    /// </summary>
    public int Id { get; init; }

    /// <summary>
    /// Gets the credential being tested.
    /// </summary>
    public required DiscoveredCredential Credential { get; init; }

    /// <summary>
    /// Gets the current pipeline stage.
    /// </summary>
    public PipelineStage Stage { get; init; } = PipelineStage.Pending;

    /// <summary>
    /// Gets the timestamp when this item entered the pipeline.
    /// </summary>
    public long EnteredAtTicks { get; init; } = DateTime.UtcNow.Ticks;

    /// <summary>
    /// Gets the properties attached by stages.
    /// Uses immutable dictionary for thread-safety without locks.
    /// </summary>
    public ImmutableDictionary<string, object> Properties { get; init; } = ImmutableDictionary<string, object>.Empty;

    /// <summary>
    /// Creates a new item with an additional property.
    /// </summary>
    /// <param name="key">Property key.</param>
    /// <param name="value">Property value.</param>
    /// <returns>New item with property added.</returns>
    public PipelineItem WithProperty(string key, object value) =>
        this with
        {
            Properties = Properties.SetItem(key, value),
        };

    /// <summary>
    /// Creates a new item with multiple properties.
    /// </summary>
    /// <param name="properties">Properties to add.</param>
    /// <returns>New item with properties added.</returns>
    public PipelineItem WithProperties(params (string Key, object Value)[] properties)
    {
        var builder = Properties.ToBuilder();
        foreach (var (key, value) in properties)
        {
            builder[key] = value;
        }

        return this with
        {
            Properties = builder.ToImmutable(),
        };
    }

    /// <summary>
    /// Gets a typed property value.
    /// </summary>
    /// <typeparam name="T">Property type.</typeparam>
    /// <param name="key">Property key.</param>
    /// <returns>Property value or default.</returns>
    public T? GetProperty<T>(string key) =>
        Properties.TryGetValue(key, out var value) && value is T typed ? typed : default;

    /// <summary>
    /// Gets a typed property value or throws.
    /// </summary>
    /// <typeparam name="T">Property type.</typeparam>
    /// <param name="key">Property key.</param>
    /// <returns>Property value.</returns>
    /// <exception cref="InvalidOperationException">Property not found or wrong type.</exception>
    public T GetRequiredProperty<T>(string key) =>
        Properties.TryGetValue(key, out var value) && value is T typed
            ? typed
            : throw new InvalidOperationException($"Required property '{key}' not found or wrong type");

    /// <summary>
    /// Checks if a property exists.
    /// </summary>
    /// <param name="key">Property key.</param>
    /// <returns>True if property exists.</returns>
    public bool HasProperty(string key) => Properties.ContainsKey(key);

    /// <summary>
    /// Creates a new item advanced to the next stage.
    /// </summary>
    /// <param name="nextStage">The next stage.</param>
    /// <returns>New item at next stage.</returns>
    public PipelineItem AdvanceTo(PipelineStage nextStage) => this with { Stage = nextStage };

    /// <summary>
    /// Creates a new item marked as failed.
    /// </summary>
    /// <param name="reason">Failure reason.</param>
    /// <returns>New failed item.</returns>
    public PipelineItem MarkFailed(string reason) =>
        this with
        {
            Stage = PipelineStage.Failed,
            Properties = Properties.SetItem("FailureReason", reason).SetItem("FailedAt", DateTime.UtcNow.Ticks),
        };
}

/// <summary>
/// Well-known property keys for pipeline items.
/// </summary>
public static class PipelinePropertyKeys
{
    /// <summary>User and server info from authentication.</summary>
    public const string AuthInfo = "AuthInfo";

    /// <summary>List of available streams.</summary>
    public const string Streams = "Streams";

    /// <summary>Total stream count.</summary>
    public const string StreamCount = "StreamCount";

    /// <summary>Stream test result.</summary>
    public const string StreamResult = "StreamResult";

    /// <summary>Stream quality snapshot.</summary>
    public const string StreamQuality = "StreamQuality";

    /// <summary>EPG program count.</summary>
    public const string EpgCount = "EpgCount";

    /// <summary>Filtered channel list (matching country filter).</summary>
    public const string FilteredChannels = "FilteredChannels";

    /// <summary>Filtered channel count.</summary>
    public const string FilteredChannelCount = "FilteredChannelCount";

    /// <summary>Detected category list.</summary>
    public const string FilteredCategories = "FilteredCategories";

    /// <summary>Country code used for filtering.</summary>
    public const string FilterCountryCode = "FilterCountryCode";

    /// <summary>Expiration date.</summary>
    public const string ExpirationDate = "ExpirationDate";

    /// <summary>Max connections allowed.</summary>
    public const string MaxConnections = "MaxConnections";

    /// <summary>Active connections in use.</summary>
    public const string ActiveConnections = "ActiveConnections";

    /// <summary>Failure reason if failed.</summary>
    public const string FailureReason = "FailureReason";

    /// <summary>Timestamp when failed.</summary>
    public const string FailedAt = "FailedAt";
}
