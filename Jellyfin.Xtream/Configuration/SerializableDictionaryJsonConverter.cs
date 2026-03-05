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
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Xtream.Configuration;

/// <summary>
/// JSON converter for SerializableDictionary that handles integer keys properly.
/// </summary>
/// <typeparam name="TKey">The dictionary key type.</typeparam>
/// <typeparam name="TValue">The dictionary value type.</typeparam>
public class SerializableDictionaryJsonConverter<TKey, TValue> : JsonConverter<SerializableDictionary<TKey, TValue>>
    where TKey : notnull
{
    /// <inheritdoc/>
    public override SerializableDictionary<TKey, TValue>? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Expected StartObject, got {reader.TokenType}");
        }

        var dictionary = new SerializableDictionary<TKey, TValue>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return dictionary;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException($"Expected PropertyName, got {reader.TokenType}");
            }

            var propertyName = reader.GetString()!;
            var key = ConvertKey(propertyName);

            _ = reader.Read();
            var value = JsonSerializer.Deserialize<TValue>(ref reader, options)!;

            dictionary[key] = value;
        }

        throw new JsonException("Unexpected end of JSON");
    }

    /// <inheritdoc/>
    public override void Write(
        Utf8JsonWriter writer,
        SerializableDictionary<TKey, TValue> value,
        JsonSerializerOptions options
    )
    {
        writer.WriteStartObject();

        foreach (var kvp in value)
        {
            var keyString = kvp.Key?.ToString() ?? string.Empty;
            writer.WritePropertyName(keyString);
            JsonSerializer.Serialize(writer, kvp.Value, options);
        }

        writer.WriteEndObject();
    }

    private static TKey ConvertKey(string keyString)
    {
        var keyType = typeof(TKey);

        if (keyType == typeof(int))
        {
            return (TKey)(object)int.Parse(keyString, System.Globalization.CultureInfo.InvariantCulture);
        }

        if (keyType == typeof(long))
        {
            return (TKey)(object)long.Parse(keyString, System.Globalization.CultureInfo.InvariantCulture);
        }

        // Cast chain (TKey)(object) is required: C# generics don't allow direct cast from
        // concrete types (string, Guid) to unconstrained TKey. The intermediate cast to object
        // is necessary to satisfy the compiler's type system.
        return keyType == typeof(string) ? (TKey)(object)keyString
            : keyType == typeof(Guid) ? (TKey)(object)Guid.Parse(keyString)
            : throw new JsonException($"Unsupported key type: {keyType}");
    }
}
