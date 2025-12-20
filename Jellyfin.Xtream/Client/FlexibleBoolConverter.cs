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
/// JSON converter that handles booleans coming as various formats.
/// Xtream API returns boolean fields as 0/1 integers or "true"/"false" strings.
/// </summary>
public class FlexibleBoolConverter : JsonConverter<bool>
{
    /// <inheritdoc />
    public override bool ReadJson(
        JsonReader reader,
        Type objectType,
        bool existingValue,
        bool hasExistingValue,
        JsonSerializer serializer
    )
    {
        if (reader.TokenType == JsonToken.Null)
        {
            return false;
        }

        if (reader.TokenType == JsonToken.Boolean)
        {
            return (bool)reader.Value!;
        }

        if (reader.TokenType == JsonToken.Integer)
        {
            return Convert.ToInt64(reader.Value, CultureInfo.InvariantCulture) != 0;
        }

        if (reader.TokenType == JsonToken.String)
        {
            string? stringValue = reader.Value?.ToString();
            if (string.IsNullOrWhiteSpace(stringValue))
            {
                return false;
            }

            // Handle "true"/"false" strings
            if (bool.TryParse(stringValue, out bool boolResult))
            {
                return boolResult;
            }

            // Handle "1"/"0" strings
            if (int.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intResult))
            {
                return intResult != 0;
            }

            return false;
        }

        return false;
    }

    /// <inheritdoc />
    public override void WriteJson(JsonWriter writer, bool value, JsonSerializer serializer)
    {
        writer.WriteValue(value);
    }
}
