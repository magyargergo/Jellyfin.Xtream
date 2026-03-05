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
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using TurboXml;

namespace Jellyfin.Xtream.Service.Epg;

/// <summary>
/// Parses XMLTV data using TurboXml for high-performance, low-allocation parsing.
/// </summary>
public static class TurboXmltvParser
{
    private const int BytesPerProgram = 500;
    private const int DefaultChannelCount = 100;
    private const int DefaultProgramsPerChannel = 168;
    private const int MinChannelCount = 10;
    private const int MaxChannelCount = 500;
    private const int MinProgramsPerChannel = 24;

    /// <summary>
    /// Parses XMLTV data from a string.
    /// </summary>
    /// <param name="xmlContent">The XML content.</param>
    /// <returns>Read-only dictionary mapping channel IDs to their program lists.</returns>
    public static IReadOnlyDictionary<string, IReadOnlyList<EpgProgram>> Parse(string xmlContent) =>
        Parse(xmlContent, DefaultChannelCount, DefaultProgramsPerChannel);

    /// <summary>
    /// Parses XMLTV data from a string with capacity hints.
    /// </summary>
    /// <param name="xmlContent">The XML content.</param>
    /// <param name="estimatedChannels">Estimated number of channels.</param>
    /// <param name="estimatedProgramsPerChannel">Estimated programs per channel.</param>
    /// <returns>Read-only dictionary mapping channel IDs to their program lists.</returns>
    public static IReadOnlyDictionary<string, IReadOnlyList<EpgProgram>> Parse(
        string xmlContent,
        int estimatedChannels,
        int estimatedProgramsPerChannel
    )
    {
        var result = new Dictionary<string, List<EpgProgram>>(estimatedChannels, StringComparer.OrdinalIgnoreCase);
        var handler = new XmltvParseHandler(result, estimatedProgramsPerChannel);

        XmlParser.Parse(xmlContent, ref handler);

        return new ReadOnlyDictionary<string, IReadOnlyList<EpgProgram>>(
            result.ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlyList<EpgProgram>)kvp.Value,
                StringComparer.OrdinalIgnoreCase
            )
        );
    }

    /// <summary>
    /// Parses XMLTV data from a stream.
    /// </summary>
    /// <param name="stream">The stream containing XMLTV data.</param>
    /// <returns>Read-only dictionary mapping channel IDs to their program lists.</returns>
    public static IReadOnlyDictionary<string, IReadOnlyList<EpgProgram>> Parse(Stream stream)
    {
        var (channels, programsPerChannel) = EstimateCapacity(stream);
        var content = ReadStreamContent(stream);

        return Parse(content, channels, programsPerChannel);
    }

    private static (int Channels, int ProgramsPerChannel) EstimateCapacity(Stream stream)
    {
        if (!stream.CanSeek || stream.Length <= 0)
        {
            return (DefaultChannelCount, DefaultProgramsPerChannel);
        }

        var estimatedPrograms = (int)(stream.Length / BytesPerProgram);
        var channels = Math.Clamp(estimatedPrograms / 100, MinChannelCount, MaxChannelCount);
        var programsPerChannel = Math.Max(MinProgramsPerChannel, estimatedPrograms / channels);

        return (channels, programsPerChannel);
    }

    private static string ReadStreamContent(Stream stream)
    {
        if (stream is MemoryStream ms && ms.TryGetBuffer(out var segment))
        {
            return Encoding.UTF8.GetString(segment.Array!, segment.Offset, segment.Count);
        }

        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 65536,
            leaveOpen: true
        );
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Handler for TurboXml parsing events.
    /// </summary>
    private struct XmltvParseHandler(
        Dictionary<string, List<EpgProgram>> programsByChannel,
        int estimatedProgramsPerChannel
    ) : IXmlReadHandler
    {
        private readonly Dictionary<string, List<EpgProgram>> _programsByChannel = programsByChannel;
        private readonly int _estimatedProgramsPerChannel = estimatedProgramsPerChannel;

        private bool _inProgramme = false;
        private string _currentChannelId = string.Empty;
        private string _currentElement = string.Empty;
        private string? _pendingStart = null;
        private string? _pendingStop = null;
        private string _pendingTitle = string.Empty;
        private string? _pendingDescription = null;
        private string? _pendingImageUrl = null;
        private List<string>? _pendingCategories = null;

        public void OnBeginTag(ReadOnlySpan<char> name, int line, int column)
        {
            _currentElement = name.ToString();

            if (name is "programme")
            {
                _inProgramme = true;
                _currentChannelId = string.Empty;
                _pendingStart = null;
                _pendingStop = null;
                _pendingTitle = string.Empty;
                _pendingDescription = null;
                _pendingImageUrl = null;
                _pendingCategories = null;
            }
        }

        public void OnEndTagEmpty() => _currentElement = string.Empty;

        public void OnEndTag(ReadOnlySpan<char> name, int line, int column)
        {
            if (name is "programme")
            {
                FinalizeCurrentProgram();
                _inProgramme = false;
            }

            _currentElement = string.Empty;
        }

        public void OnAttribute(
            ReadOnlySpan<char> name,
            ReadOnlySpan<char> value,
            int nameLine,
            int nameColumn,
            int valueLine,
            int valueColumn
        )
        {
            if (!_inProgramme)
            {
                return;
            }

            if (name is "channel")
            {
                _currentChannelId = value.ToString();
            }
            else if (name is "start")
            {
                _pendingStart = value.ToString();
            }
            else if (name is "stop")
            {
                _pendingStop = value.ToString();
            }
            else if (_currentElement == "icon" && name is "src")
            {
                _pendingImageUrl = value.ToString();
            }
        }

        public void OnText(ReadOnlySpan<char> text, int line, int column)
        {
            if (!_inProgramme || text.IsWhiteSpace())
            {
                return;
            }

            switch (_currentElement)
            {
                case "title":
                    _pendingTitle = text.ToString();
                    break;
                case "desc":
                    _pendingDescription = text.ToString();
                    break;
                case "category":
                    _pendingCategories ??= new List<string>(4);
                    _pendingCategories.Add(CategoryInterner.Intern(text));
                    break;
            }
        }

        public readonly void OnXmlDeclaration(
            ReadOnlySpan<char> version,
            ReadOnlySpan<char> encoding,
            ReadOnlySpan<char> standalone,
            int line,
            int column
        ) { }

        public readonly void OnComment(ReadOnlySpan<char> comment, int line, int column) { }

        public readonly void OnCData(ReadOnlySpan<char> cdata, int line, int column) { }

        public static void OnProcessingInstruction(
            ReadOnlySpan<char> name,
            ReadOnlySpan<char> content,
            int line,
            int column
        ) { }

        private readonly void FinalizeCurrentProgram()
        {
            var startUtc = XmltvDateTimeParser.Parse(_pendingStart);
            var endUtc = XmltvDateTimeParser.Parse(_pendingStop);

            if (string.IsNullOrEmpty(_currentChannelId) || startUtc <= DateTime.MinValue || endUtc <= DateTime.MinValue)
            {
                return;
            }

            var program = new EpgProgram
            {
                Id = HashCode.Combine(_pendingStart, _pendingStop),
                Title = _pendingTitle,
                Description = _pendingDescription,
                StartUtc = startUtc,
                EndUtc = endUtc,
                ImageUrl = _pendingImageUrl,
                Categories = _pendingCategories ?? [],
            };

            AddProgramToChannel(program);
        }

        private readonly void AddProgramToChannel(EpgProgram program)
        {
            if (!_programsByChannel.TryGetValue(_currentChannelId, out var list))
            {
                list = new List<EpgProgram>(_estimatedProgramsPerChannel);
                _programsByChannel[_currentChannelId] = list;
            }

            list.Add(program);
        }
    }
}
