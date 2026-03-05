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

using System.Collections.ObjectModel;

namespace Jellyfin.Xtream.Api.Models;

/// <summary>
/// A single field change in a configuration preview.
/// </summary>
public sealed class ConfigFieldChange
{
    /// <summary>
    /// Gets or sets the field name.
    /// </summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the current value.
    /// </summary>
    public string OldValue { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the proposed new value.
    /// </summary>
    public string NewValue { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this field actually changed.
    /// </summary>
    public bool Changed { get; set; }
}

/// <summary>
/// Response from a configuration dry-run/preview request.
/// </summary>
public sealed class ConfigPreviewResponse
{
    /// <summary>
    /// Gets or sets a value indicating whether the proposed changes are valid.
    /// </summary>
    public bool IsValid { get; set; }

    /// <summary>
    /// Gets or sets the configuration section name.
    /// </summary>
    public string Section { get; set; } = string.Empty;

    /// <summary>
    /// Gets validation error messages.
    /// </summary>
    public Collection<string> Errors { get; } = [];

    /// <summary>
    /// Gets validation warning messages.
    /// </summary>
    public Collection<string> Warnings { get; } = [];

    /// <summary>
    /// Gets the list of field changes (including unchanged fields for comparison).
    /// </summary>
    public Collection<ConfigFieldChange> Changes { get; } = [];

    /// <summary>
    /// Gets or sets the number of active streams that may be impacted.
    /// </summary>
    public int ImpactedStreams { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether changes require a stream restart to take effect.
    /// </summary>
    public bool RequiresRestart { get; set; }
}
