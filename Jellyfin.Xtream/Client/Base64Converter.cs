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
using System.Text;
using Newtonsoft.Json;

namespace Jellyfin.Xtream.Client;

/// <summary>
/// Class Base64Converter converts strings from and to base64.
/// </summary>
public class Base64Converter : JsonConverter
{
    /// <inheritdoc />
    public override bool CanConvert(Type objectType) => objectType == typeof(string);

    /// <inheritdoc />
    public override object ReadJson(
        JsonReader reader,
        Type objectType,
        object? existingValue,
        JsonSerializer serializer
    )
    {
        if (reader.Value == null)
        {
            return string.Empty;
        }

        var value = (string)reader.Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        try
        {
            var bytes = Convert.FromBase64String(value);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException)
        {
            // If the value isn't valid base64, return it as-is
            return value;
        }
    }

    /// <inheritdoc />
    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value == null)
        {
            throw new ArgumentException("Value cannot be null.");
        }

        var bytes = Encoding.UTF8.GetBytes((string)value);
        writer.WriteValue(Convert.ToBase64String(bytes));
    }
}
