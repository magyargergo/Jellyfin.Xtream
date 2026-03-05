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
using System.Globalization;
using Newtonsoft.Json;

namespace Jellyfin.Xtream.Client;

/// <summary>
/// JSON converter that handles integers coming as strings or numbers.
/// Xtream API returns some integer fields (like id, epg_id) as strings.
/// </summary>
public class FlexibleIntConverter : JsonConverter<int>
{
    /// <inheritdoc />
    public override int ReadJson(
        JsonReader reader,
        Type objectType,
        int existingValue,
        bool hasExistingValue,
        JsonSerializer serializer
    )
    {
        if (reader.TokenType == JsonToken.Null)
        {
            return 0;
        }

        if (reader.TokenType == JsonToken.Integer)
        {
            return Convert.ToInt32(reader.Value, CultureInfo.InvariantCulture);
        }

        if (reader.TokenType == JsonToken.String)
        {
            var stringValue = reader.Value?.ToString();
            return string.IsNullOrWhiteSpace(stringValue) ? 0
                : int.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result
                : 0;
        }

        return 0;
    }

    /// <inheritdoc />
    public override void WriteJson(JsonWriter writer, int value, JsonSerializer serializer) => writer.WriteValue(value);
}
