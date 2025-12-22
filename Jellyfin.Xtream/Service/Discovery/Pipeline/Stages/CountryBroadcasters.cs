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
using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace Jellyfin.Xtream.Service.Discovery.Pipeline.Stages;

/// <summary>
/// Registry of country-specific broadcasters and detection patterns.
/// Add new countries by creating a new <see cref="CountryProfile"/> and registering it.
/// </summary>
public static class CountryBroadcasters
{
    /// <summary>
    /// Polish broadcasters and detection patterns.
    /// </summary>
    public static CountryProfile Poland { get; } =
        new(
            CountryCode: "PL",
            CountryNames: ["poland", "polish", "polska", "polskie"],
            Broadcasters:
            [
                // TVP - Public broadcaster
                "tvp1",
                "tvp2",
                "tvp3",
                "tvp info",
                "tvp sport",
                "tvp historia",
                "tvp kultura",
                "tvp abc",
                "tvp seriale",
                "tvp polonia",
                "tvp hd",
                "tvp rozrywka",
                "tvp world",
                "tvp dokument",
                "alfa tvp",
                // Polsat group
                "polsat",
                "polsat news",
                "polsat sport",
                "polsat film",
                "polsat play",
                "polsat cafe",
                "polsat games",
                "polsat doku",
                "super polsat",
                "polsat comedy",
                "polsat reality",
                "polsat music",
                "polsat box go",
                "polsat sport extra",
                "polsat sport news",
                "polsat sport fight",
                // TVN group
                "tvn",
                "tvn24",
                "tvn24 bis",
                "tvn7",
                "tvn style",
                "tvn turbo",
                "tvn fabula",
                "tvn international",
                "metro tv",
                // Canal+ Poland
                "canal+ polska",
                "canal+ sport",
                "canal+ film",
                "canal+ seriale",
                "canal+ family",
                "canal+ discovery",
                "canal+ dokument",
                "canal+ domo",
                "canal+ premium",
                "canal+ now",
                "ale kino",
                // Other Polish channels
                "tv puls",
                "puls 2",
                "tv4",
                "tv6",
                "tele5",
                "fokus tv",
                "nowa tv",
                "zoom tv",
                "wp tv",
                "onet tv",
            ],
            BroadcasterPattern: @"\b(tvp|polsat|tvn)\d*\b"
        );

    /// <summary>
    /// UK broadcasters and detection patterns.
    /// </summary>
    public static CountryProfile UnitedKingdom { get; } =
        new(
            CountryCode: "UK",
            CountryNames: ["uk", "united kingdom", "britain", "british", "england", "english"],
            Broadcasters:
            [
                // BBC
                "bbc one",
                "bbc two",
                "bbc three",
                "bbc four",
                "bbc news",
                "bbc parliament",
                "bbc alba",
                "cbbc",
                "cbeebies",
                "bbc iplayer",
                // ITV
                "itv",
                "itv2",
                "itv3",
                "itv4",
                "itvbe",
                "itv news",
                // Channel 4
                "channel 4",
                "e4",
                "more4",
                "film4",
                "4music",
                // Channel 5
                "channel 5",
                "5usa",
                "5star",
                "5select",
                "5action",
                // Sky
                "sky news",
                "sky sports",
                "sky atlantic",
                "sky one",
                "sky cinema",
                "sky arts",
                "sky comedy",
                "sky crime",
                "sky documentaries",
                // Other UK channels
                "dave",
                "gold",
                "alibi",
                "eden",
                "yesterday",
                "drama",
                "quest",
                "dmax",
                "really",
                "together tv",
            ],
            BroadcasterPattern: @"\b(bbc|itv|sky|channel\s*[45])\b"
        );

    /// <summary>
    /// German broadcasters and detection patterns.
    /// </summary>
    public static CountryProfile Germany { get; } =
        new(
            CountryCode: "DE",
            CountryNames: ["germany", "german", "deutschland", "deutsch"],
            Broadcasters:
            [
                // Public broadcasters (ARD/ZDF)
                "das erste",
                "ard",
                "zdf",
                "3sat",
                "arte",
                "phoenix",
                "tagesschau24",
                "one",
                "zdfneo",
                "zdfinfo",
                "kika",
                // Regional (ARD)
                "wdr",
                "ndr",
                "br",
                "swr",
                "hr",
                "mdr",
                "rbb",
                "sr",
                // RTL Group
                "rtl",
                "rtl2",
                "vox",
                "nitro",
                "rtlplus",
                "toggo plus",
                "super rtl",
                "rtl crime",
                "rtl passion",
                "rtl living",
                // ProSiebenSat.1
                "sat1",
                "sat.1",
                "prosieben",
                "pro7",
                "kabel eins",
                "kabel1",
                "sixx",
                "sat1 gold",
                "prosieben maxx",
                "pro7 maxx",
                // Other German channels
                "sport1",
                "dmax",
                "tele 5",
                "servus tv",
            ],
            BroadcasterPattern: @"\b(ard|zdf|rtl|sat\.?1|pro\s*7|prosieben)\b"
        );

    /// <summary>
    /// French broadcasters and detection patterns.
    /// </summary>
    public static CountryProfile France { get; } =
        new(
            CountryCode: "FR",
            CountryNames: ["france", "french", "français", "francais"],
            Broadcasters:
            [
                // Public broadcasters
                "france 2",
                "france 3",
                "france 4",
                "france 5",
                "france info",
                "france 24",
                "arte",
                // TF1 Group
                "tf1",
                "tmc",
                "tfx",
                "tf1 series films",
                "lci",
                // M6 Group
                "m6",
                "w9",
                "6ter",
                "gulli",
                "paris premiere",
                // Canal+ Group
                "canal+",
                "canal+ cinema",
                "canal+ sport",
                "canal+ series",
                "cstar",
                "cnews",
                // Other French channels
                "bfm tv",
                "rmc sport",
                "rmc decouverte",
                "nrj12",
                "cherie 25",
            ],
            BroadcasterPattern: @"\b(tf1|france\s*\d|canal\+?|m6)\b"
        );

    /// <summary>
    /// Gets a country profile by its ISO country code.
    /// </summary>
    /// <param name="countryCode">The 2-letter ISO country code (e.g., "PL", "UK", "DE").</param>
    /// <returns>The country profile, or null if not found.</returns>
    public static CountryProfile? GetByCode(string countryCode)
    {
        return countryCode.ToUpperInvariant() switch
        {
            "PL" => Poland,
            "UK" or "GB" => UnitedKingdom,
            "DE" => Germany,
            "FR" => France,
            _ => null,
        };
    }

    /// <summary>
    /// Gets all registered country profiles.
    /// </summary>
    public static IReadOnlyList<CountryProfile> All { get; } = [Poland, UnitedKingdom, Germany, France];
}

/// <summary>
/// Profile for a country containing broadcasters and detection patterns.
/// </summary>
/// <param name="CountryCode">The 2-letter ISO country code.</param>
/// <param name="CountryNames">Alternative names for the country (used in category detection).</param>
/// <param name="Broadcasters">List of known broadcaster names for this country.</param>
/// <param name="BroadcasterPattern">Optional regex pattern to match broadcaster names.</param>
public sealed record CountryProfile(
    string CountryCode,
    IReadOnlyList<string> CountryNames,
    IReadOnlyList<string> Broadcasters,
    string? BroadcasterPattern = null
)
{
    private HashSet<string>? _broadcasterSet;
    private HashSet<string>? _countryNameSet;
    private Regex? _broadcasterRegex;

    /// <summary>
    /// Gets the broadcaster names as a HashSet for O(1) lookup.
    /// </summary>
    public HashSet<string> BroadcasterSet =>
        _broadcasterSet ??= new HashSet<string>(Broadcasters, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the country names as a HashSet for O(1) lookup.
    /// </summary>
    public HashSet<string> CountryNameSet =>
        _countryNameSet ??= new HashSet<string>(CountryNames, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the compiled broadcaster regex pattern, or null if not specified.
    /// </summary>
    public Regex? BroadcasterRegex =>
        BroadcasterPattern != null
            ? (_broadcasterRegex ??= new Regex(BroadcasterPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled))
            : null;
}
