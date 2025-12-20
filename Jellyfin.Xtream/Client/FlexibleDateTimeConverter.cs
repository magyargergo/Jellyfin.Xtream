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
/// JSON converter that handles Unix timestamps coming as strings or numbers.
/// Xtream API returns timestamps as Unix epoch seconds, but sometimes as strings.
/// Also handles null/empty values gracefully.
/// </summary>
public class FlexibleDateTimeConverter : JsonConverter<DateTime?>
{
    private static readonly DateTime _unixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    public override DateTime? ReadJson(
        JsonReader reader,
        Type objectType,
        DateTime? existingValue,
        bool hasExistingValue,
        JsonSerializer serializer
    )
    {
        if (reader.TokenType == JsonToken.Null)
        {
            return null;
        }

        // Handle integer Unix timestamp
        if (reader.TokenType == JsonToken.Integer)
        {
            long unixSeconds = Convert.ToInt64(reader.Value, CultureInfo.InvariantCulture);
            if (unixSeconds <= 0)
            {
                return null;
            }

            return _unixEpoch.AddSeconds(unixSeconds);
        }

        // Handle string value (could be Unix timestamp as string, or date string)
        if (reader.TokenType == JsonToken.String)
        {
            string? stringValue = reader.Value?.ToString();
            if (string.IsNullOrWhiteSpace(stringValue))
            {
                return null;
            }

            // Try parsing as Unix timestamp first
            if (long.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out long unixSeconds))
            {
                if (unixSeconds <= 0)
                {
                    return null;
                }

                return _unixEpoch.AddSeconds(unixSeconds);
            }

            // Try parsing as date string (format: "Y-m-d H:i:s" or various other formats)
            if (
                DateTime.TryParse(
                    stringValue,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out DateTime dateResult
                )
            )
            {
                return dateResult.ToUniversalTime();
            }

            // Try specific format used by Xtream: "2024-01-15 20:00:00"
            if (
                DateTime.TryParseExact(
                    stringValue,
                    "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out dateResult
                )
            )
            {
                return dateResult.ToUniversalTime();
            }

            return null;
        }

        // Handle float (some providers return as decimal)
        if (reader.TokenType == JsonToken.Float)
        {
            double unixSeconds = Convert.ToDouble(reader.Value, CultureInfo.InvariantCulture);
            if (unixSeconds <= 0)
            {
                return null;
            }

            return _unixEpoch.AddSeconds(unixSeconds);
        }

        return null;
    }

    /// <inheritdoc />
    public override void WriteJson(JsonWriter writer, DateTime? value, JsonSerializer serializer)
    {
        if (value.HasValue)
        {
            long unixSeconds = (long)(value.Value.ToUniversalTime() - _unixEpoch).TotalSeconds;
            writer.WriteValue(unixSeconds);
        }
        else
        {
            writer.WriteNull();
        }
    }
}
