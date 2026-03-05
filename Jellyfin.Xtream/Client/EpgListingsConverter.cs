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
using Jellyfin.Xtream.Client.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Xtream.Client;

/// <summary>
/// JSON converter that handles EPG listings in different formats.
/// Some providers return {"epg_listings": [...]} while others return just [...].
/// </summary>
public class EpgListingsConverter : JsonConverter<EpgListings>
{
    /// <inheritdoc />
    public override EpgListings ReadJson(
        JsonReader reader,
        Type objectType,
        EpgListings? existingValue,
        bool hasExistingValue,
        JsonSerializer serializer
    )
    {
        var result = new EpgListings();

        if (reader.TokenType == JsonToken.Null)
        {
            return result;
        }

        // Load into JToken to inspect structure
        var token = JToken.Load(reader);

        if (token.Type == JTokenType.Array)
        {
            // Direct array format: [...]
            var listings = token.ToObject<List<EpgInfo>>(serializer) ?? [];
            result.Listings = listings;
        }
        else if (token.Type == JTokenType.Object)
        {
            var obj = (JObject)token;

            // Standard format: {"epg_listings": [...]}
            if (obj.TryGetValue("epg_listings", out var epgToken))
            {
                var listings = epgToken.ToObject<List<EpgInfo>>(serializer) ?? [];
                result.Listings = listings;
            }
        }

        return result;
    }

    /// <inheritdoc />
    public override void WriteJson(JsonWriter writer, EpgListings? value, JsonSerializer serializer)
    {
        if (value == null)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteStartObject();
        writer.WritePropertyName("epg_listings");
        serializer.Serialize(writer, value.Listings);
        writer.WriteEndObject();
    }
}
