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
using System.Linq;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// A struct which holds information of parsed stream names.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="ParsedName"/> struct.
/// </remarks>
/// <param name="title">The parsed title.</param>
/// <param name="tags">The parsed tags.</param>
public readonly struct ParsedName(string title, IReadOnlyList<string> tags) : IEquatable<ParsedName>
{
    /// <summary>
    /// Gets the parsed title.
    /// </summary>
    public string Title { get; init; } = title;

    /// <summary>
    /// Gets the parsed tags.
    /// </summary>
    public IReadOnlyList<string> Tags { get; init; } = tags;

    /// <summary>
    /// Determines whether two <see cref="ParsedName"/> instances are equal.
    /// </summary>
    /// <param name="left">The first instance.</param>
    /// <param name="right">The second instance.</param>
    /// <returns>True if the instances are equal; otherwise, false.</returns>
    public static bool operator ==(ParsedName left, ParsedName right) => left.Equals(right);

    /// <summary>
    /// Determines whether two <see cref="ParsedName"/> instances are not equal.
    /// </summary>
    /// <param name="left">The first instance.</param>
    /// <param name="right">The second instance.</param>
    /// <returns>True if the instances are not equal; otherwise, false.</returns>
    public static bool operator !=(ParsedName left, ParsedName right) => !(left == right);

    /// <inheritdoc />
    public bool Equals(ParsedName other)
    {
        return string.Equals(Title, other.Title, StringComparison.Ordinal)
            && Tags.SequenceEqual(other.Tags, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ParsedName other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = default;
        hash.Add(Title);
        foreach (var tag in Tags)
        {
            hash.Add(tag);
        }

        return hash.ToHashCode();
    }
}
