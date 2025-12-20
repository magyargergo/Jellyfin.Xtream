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

namespace Jellyfin.Xtream.Service.Epg;

/// <summary>
/// Interns category strings to reduce memory usage across many EPG programs.
/// Maps common category variations to canonical names.
/// </summary>
public static class CategoryInterner
{
    private static readonly Dictionary<string, string> _canonicalCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Movie"] = "Movie",
        ["Movies"] = "Movie",
        ["Film"] = "Movie",
        ["Series"] = "Series",
        ["News"] = "News",
        ["Sports"] = "Sports",
        ["Sport"] = "Sports",
        ["Entertainment"] = "Entertainment",
        ["Documentary"] = "Documentary",
        ["Kids"] = "Kids",
        ["Children"] = "Kids",
        ["Music"] = "Music",
        ["Drama"] = "Drama",
        ["Comedy"] = "Comedy",
        ["Action"] = "Action",
        ["Thriller"] = "Thriller",
        ["Horror"] = "Horror",
        ["Sci-Fi"] = "Sci-Fi",
        ["Science Fiction"] = "Sci-Fi",
        ["Romance"] = "Romance",
        ["Animation"] = "Animation",
        ["Reality"] = "Reality",
        ["Talk Show"] = "Talk Show",
        ["Game Show"] = "Game Show",
        ["Cooking"] = "Cooking",
        ["Travel"] = "Travel",
        ["Nature"] = "Nature",
        ["History"] = "History",
        ["Educational"] = "Educational",
        ["Religious"] = "Religious",
        ["Shopping"] = "Shopping",
        ["Adult"] = "Adult",
    };

    /// <summary>
    /// Returns an interned version of a category string.
    /// </summary>
    /// <param name="category">The category string to intern.</param>
    /// <returns>An interned or canonical category string.</returns>
    public static string Intern(ReadOnlySpan<char> category)
    {
        if (category.IsEmpty)
        {
            return string.Empty;
        }

        var categoryStr = category.ToString();
        return Intern(categoryStr);
    }

    /// <summary>
    /// Returns an interned version of a category string.
    /// </summary>
    /// <param name="category">The category string to intern.</param>
    /// <returns>An interned or canonical category string.</returns>
    public static string Intern(string category)
    {
        if (string.IsNullOrEmpty(category))
        {
            return string.Empty;
        }

        if (_canonicalCategories.TryGetValue(category, out var canonical))
        {
            return canonical;
        }

        return string.Intern(category);
    }
}
