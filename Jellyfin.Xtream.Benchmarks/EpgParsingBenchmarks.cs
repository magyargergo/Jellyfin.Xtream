// Copyright (C) 2025  Gergo Magyar
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Jellyfin.Xtream.Service.Epg;

namespace Jellyfin.Xtream.Benchmarks;

/// <summary>
/// Benchmarks for EPG (Electronic Program Guide) parsing operations.
/// Compares XmlReader (async) vs TurboXml (sync, zero-allocation) implementations.
/// </summary>
[SimpleJob(RuntimeMoniker.Net80)]
[MemoryDiagnoser]
public class EpgParsingBenchmarks
{
    private static readonly string[] DateTimeFormats =
    [
        "yyyyMMddHHmmss zzz",
        "yyyyMMddHHmmss +HHmm",
        "yyyyMMddHHmmss -HHmm",
    ];

    private byte[] _smallXmltv = null!;
    private byte[] _mediumXmltv = null!;
    private byte[] _largeXmltv = null!;
    private string _smallXmltvString = null!;
    private string _mediumXmltvString = null!;
    private string _largeXmltvString = null!;
    private string _dateWithTimezone = null!;
    private string _dateWithoutTimezone = null!;

    /// <summary>
    /// Gets or sets the number of channels for parameterized tests.
    /// </summary>
    [Params(10, 100)]
    public int ChannelCount { get; set; }

    /// <summary>
    /// Gets or sets the number of programs per channel.
    /// </summary>
    [Params(24, 168)]
    public int ProgramsPerChannel { get; set; }

    /// <summary>
    /// Setup test data.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        // Small XMLTV: 10 channels, 24 programs each (1 day)
        _smallXmltv = GenerateXmltvData(10, 24);
        _smallXmltvString = Encoding.UTF8.GetString(_smallXmltv);

        // Medium XMLTV: 50 channels, 168 programs each (1 week)
        _mediumXmltv = GenerateXmltvData(50, 168);
        _mediumXmltvString = Encoding.UTF8.GetString(_mediumXmltv);

        // Large XMLTV: 200 channels, 336 programs each (2 weeks)
        _largeXmltv = GenerateXmltvData(200, 336);
        _largeXmltvString = Encoding.UTF8.GetString(_largeXmltv);

        // DateTime test strings
        _dateWithTimezone = "20251220180000 +0100";
        _dateWithoutTimezone = "20251220180000";
    }

    // ===== XmlReader (Async) Benchmarks =====

    /// <summary>
    /// Benchmark: Parse small XMLTV file with XmlReader (async).
    /// </summary>
    [Benchmark(Description = "XmlReader Small (240 prg)")]
    [BenchmarkCategory("XmlReader", "Small")]
    public async Task<int> XmlReader_Small()
    {
        using var stream = new MemoryStream(_smallXmltv);
        return await ParseXmltvWithXmlReaderAsync(stream, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Benchmark: Parse medium XMLTV file with XmlReader (async).
    /// </summary>
    [Benchmark(Description = "XmlReader Medium (8,400 prg)")]
    [BenchmarkCategory("XmlReader", "Medium")]
    public async Task<int> XmlReader_Medium()
    {
        using var stream = new MemoryStream(_mediumXmltv);
        return await ParseXmltvWithXmlReaderAsync(stream, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Benchmark: Parse large XMLTV file with XmlReader (async).
    /// </summary>
    [Benchmark(Description = "XmlReader Large (67,200 prg)")]
    [BenchmarkCategory("XmlReader", "Large")]
    public async Task<int> XmlReader_Large()
    {
        using var stream = new MemoryStream(_largeXmltv);
        return await ParseXmltvWithXmlReaderAsync(stream, CancellationToken.None).ConfigureAwait(false);
    }

    // ===== TurboXml (Sync, Zero-Allocation) Benchmarks =====

    /// <summary>
    /// Benchmark: Parse small XMLTV file with TurboXml (sync).
    /// </summary>
    [Benchmark(Description = "TurboXml Small (240 prg)")]
    [BenchmarkCategory("TurboXml", "Small")]
    public int TurboXml_Small()
    {
        var result = TurboXmltvParser.Parse(_smallXmltvString);
        return result.Values.Sum(l => l.Count);
    }

    /// <summary>
    /// Benchmark: Parse medium XMLTV file with TurboXml (sync).
    /// </summary>
    [Benchmark(Description = "TurboXml Medium (8,400 prg)")]
    [BenchmarkCategory("TurboXml", "Medium")]
    public int TurboXml_Medium()
    {
        var result = TurboXmltvParser.Parse(_mediumXmltvString);
        return result.Values.Sum(l => l.Count);
    }

    /// <summary>
    /// Benchmark: Parse large XMLTV file with TurboXml (sync).
    /// </summary>
    [Benchmark(Description = "TurboXml Large (67,200 prg)")]
    [BenchmarkCategory("TurboXml", "Large")]
    public int TurboXml_Large()
    {
        var result = TurboXmltvParser.Parse(_largeXmltvString);
        return result.Values.Sum(l => l.Count);
    }

    /// <summary>
    /// Benchmark: Parse with TurboXml from Stream (includes UTF-8 decoding).
    /// </summary>
    [Benchmark(Description = "TurboXml Stream Large (67,200 prg)")]
    [BenchmarkCategory("TurboXml", "Large", "Stream")]
    public int TurboXml_Stream_Large()
    {
        using var stream = new MemoryStream(_largeXmltv);
        var result = TurboXmltvParser.Parse(stream);
        return result.Values.Sum(l => l.Count);
    }

    // ===== DateTime Parsing Benchmarks =====

    /// <summary>
    /// Benchmark: Parse XMLTV datetime with timezone (original method).
    /// </summary>
    [Benchmark(Description = "DateTime Original (with tz)")]
    [BenchmarkCategory("DateTime", "Original")]
    public DateTime ParseDateTime_Original_WithTimezone()
    {
        return ParseXmltvDateTimeOriginal(_dateWithTimezone);
    }

    /// <summary>
    /// Benchmark: Parse XMLTV datetime with TurboXml span-based parser.
    /// </summary>
    [Benchmark(Description = "DateTime TurboXml (with tz)")]
    [BenchmarkCategory("DateTime", "TurboXml")]
    public DateTime ParseDateTime_TurboXml_WithTimezone()
    {
        return XmltvDateTimeParser.Parse(_dateWithTimezone);
    }

    /// <summary>
    /// Benchmark: Parse XMLTV datetime without timezone (original method).
    /// </summary>
    [Benchmark(Description = "DateTime Original (no tz)")]
    [BenchmarkCategory("DateTime", "Original")]
    public DateTime ParseDateTime_Original_WithoutTimezone()
    {
        return ParseXmltvDateTimeOriginal(_dateWithoutTimezone);
    }

    /// <summary>
    /// Benchmark: Parse XMLTV datetime without timezone with TurboXml.
    /// </summary>
    [Benchmark(Description = "DateTime TurboXml (no tz)")]
    [BenchmarkCategory("DateTime", "TurboXml")]
    public DateTime ParseDateTime_TurboXml_WithoutTimezone()
    {
        return XmltvDateTimeParser.Parse(_dateWithoutTimezone);
    }

    /// <summary>
    /// Benchmark: Parse 1000 datetime strings (batch) - original.
    /// </summary>
    [Benchmark(Description = "DateTime Batch Original (1000x)")]
    [BenchmarkCategory("DateTime", "Batch", "Original")]
    public int ParseDateTime_Batch_Original()
    {
        int successCount = 0;
        for (int i = 0; i < 1000; i++)
        {
            var date = ParseXmltvDateTimeOriginal(_dateWithTimezone);
            if (date != DateTime.MinValue)
            {
                successCount++;
            }
        }

        return successCount;
    }

    /// <summary>
    /// Benchmark: Parse 1000 datetime strings (batch) - TurboXml.
    /// </summary>
    [Benchmark(Description = "DateTime Batch TurboXml (1000x)")]
    [BenchmarkCategory("DateTime", "Batch", "TurboXml")]
    public int ParseDateTime_Batch_TurboXml()
    {
        int successCount = 0;
        for (int i = 0; i < 1000; i++)
        {
            var date = XmltvDateTimeParser.Parse(_dateWithTimezone);
            if (date != DateTime.MinValue)
            {
                successCount++;
            }
        }

        return successCount;
    }

    // ===== Helper Methods =====

    private static byte[] GenerateXmltvData(int channels, int programsPerChannel)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<tv generator-info-name=\"benchmark\">");

        // Generate channels
        for (int c = 0; c < channels; c++)
        {
            sb.AppendLine($"  <channel id=\"channel_{c}\">");
            sb.AppendLine($"    <display-name>Channel {c}</display-name>");
            sb.AppendLine("  </channel>");
        }

        // Generate programs
        var baseTime = new DateTime(2025, 12, 20, 0, 0, 0, DateTimeKind.Utc);
        for (int c = 0; c < channels; c++)
        {
            for (int p = 0; p < programsPerChannel; p++)
            {
                var start = baseTime.AddHours(p);
                var stop = start.AddHours(1);
                string startStr = start.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + " +0000";
                string stopStr = stop.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + " +0000";

                sb.AppendLine($"  <programme start=\"{startStr}\" stop=\"{stopStr}\" channel=\"channel_{c}\">");
                sb.AppendLine($"    <title>Program {p} on Channel {c}</title>");
                sb.AppendLine(
                    $"    <desc>Description for program {p} on channel {c}. This is some sample text.</desc>"
                );
                sb.AppendLine("    <category>Entertainment</category>");
                sb.AppendLine("    <category>Series</category>");
                sb.AppendLine("  </programme>");
            }
        }

        sb.AppendLine("</tv>");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static async Task<int> ParseXmltvWithXmlReaderAsync(Stream stream, CancellationToken cancellationToken)
    {
        int estimatedChannels = 100;
        int estimatedProgramsPerChannel = 168;

        if (stream.CanSeek && stream.Length > 0)
        {
            int estimatedPrograms = (int)(stream.Length / 500);
            estimatedChannels = Math.Max(10, Math.Min(500, estimatedPrograms / 100));
            estimatedProgramsPerChannel = Math.Max(24, estimatedPrograms / estimatedChannels);
        }

        var programsByChannel = new System.Collections.Generic.Dictionary<
            string,
            System.Collections.Generic.List<(DateTime Start, DateTime End, string Title)>
        >(estimatedChannels, StringComparer.OrdinalIgnoreCase);

        var settings = new XmlReaderSettings
        {
            Async = true,
            IgnoreWhitespace = true,
            IgnoreComments = true,
            DtdProcessing = DtdProcessing.Ignore,
        };

        using var reader = XmlReader.Create(stream, settings);

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.Element && reader.Name == "programme")
            {
                string? channelId = reader.GetAttribute("channel");
                if (!string.IsNullOrEmpty(channelId))
                {
                    string? startStr = reader.GetAttribute("start");
                    string? stopStr = reader.GetAttribute("stop");

                    if (!string.IsNullOrEmpty(startStr))
                    {
                        var startUtc = ParseXmltvDateTimeOriginal(startStr);
                        var endUtc = ParseXmltvDateTimeOriginal(stopStr);

                        string? title = null;
                        if (!reader.IsEmptyElement)
                        {
                            var subtree = reader.ReadSubtree();
                            while (subtree.Read())
                            {
                                if (subtree.NodeType == XmlNodeType.Element)
                                {
                                    switch (subtree.Name)
                                    {
                                        case "title":
                                            title = subtree.ReadElementContentAsString();
                                            break;
                                        case "desc":
                                            _ = subtree.ReadElementContentAsString();
                                            break;
                                    }
                                }
                            }
                        }

                        if (startUtc != DateTime.MinValue && title != null)
                        {
                            if (!programsByChannel.TryGetValue(channelId, out var list))
                            {
                                list = new System.Collections.Generic.List<(DateTime, DateTime, string)>(
                                    estimatedProgramsPerChannel
                                );
                                programsByChannel[channelId] = list;
                            }

                            list.Add((startUtc, endUtc, title));
                        }
                    }
                }
            }
        }

        int totalCount = 0;
        foreach (var kvp in programsByChannel)
        {
            totalCount += kvp.Value.Count;
        }

        return totalCount;
    }

    private static DateTime ParseXmltvDateTimeOriginal(string? dateStr)
    {
        if (string.IsNullOrEmpty(dateStr))
        {
            return DateTime.MinValue;
        }

        try
        {
            if (
                dateStr.Length >= 20
                && (dateStr.Contains('+', StringComparison.Ordinal) || dateStr.Contains('-', StringComparison.Ordinal))
            )
            {
                if (
                    DateTimeOffset.TryParseExact(
                        dateStr,
                        DateTimeFormats,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal,
                        out var dto
                    )
                )
                {
                    return dto.UtcDateTime;
                }

                if (dateStr.Length > 15)
                {
                    string datePart = dateStr[..14];
                    string tzPart = dateStr[15..].Trim();

                    if (
                        DateTime.TryParseExact(
                            datePart,
                            "yyyyMMddHHmmss",
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None,
                            out var dt
                        )
                    )
                    {
                        if (tzPart.Length >= 4)
                        {
                            int sign = tzPart[0] == '-' ? -1 : 1;
                            var offsetStr = tzPart.AsSpan().TrimStart(['+', '-']);
                            if (
                                offsetStr.Length >= 4
                                && int.TryParse(offsetStr[..2], out int hours)
                                && int.TryParse(offsetStr.Slice(2, 2), out int minutes)
                            )
                            {
                                var offset = new TimeSpan(sign * hours, sign * minutes, 0);
                                return dt.Add(-offset).ToUniversalTime();
                            }
                        }

                        return dt.ToUniversalTime();
                    }
                }
            }

            if (
                DateTime.TryParseExact(
                    dateStr.Trim(),
                    "yyyyMMddHHmmss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var result
                )
            )
            {
                return result.ToUniversalTime();
            }
        }
        catch
        {
            // Fall through
        }

        return DateTime.MinValue;
    }
}
