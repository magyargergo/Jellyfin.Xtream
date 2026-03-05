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

namespace Jellyfin.Xtream.Service.Epg;

/// <summary>
/// Parses XMLTV datetime strings using span-based parsing for zero allocations.
/// </summary>
public static class XmltvDateTimeParser
{
    private const int MinDateLength = 14;
    private const int TimezoneStartIndex = 15;

    /// <summary>
    /// Parses XMLTV datetime format (YYYYMMDDHHmmss +HHMM).
    /// </summary>
    /// <param name="dateStr">The date string in XMLTV format.</param>
    /// <returns>The parsed DateTime in UTC, or DateTime.MinValue if parsing fails.</returns>
    public static DateTime Parse(ReadOnlySpan<char> dateStr)
    {
        if (dateStr.IsEmpty || dateStr.Length < MinDateLength)
        {
            return DateTime.MinValue;
        }

        if (!TryParseDateTimeParts(dateStr, out var dt))
        {
            return DateTime.MinValue;
        }

        if (dateStr.Length <= TimezoneStartIndex)
        {
            return dt.ToUniversalTime();
        }

        var tzPart = dateStr[TimezoneStartIndex..].Trim();
        return tzPart.IsEmpty ? dt.ToUniversalTime()
            : !TryParseTimezoneOffset(tzPart, out var offset) ? dt.ToUniversalTime()
            : dt.Add(-offset);
    }

    private static bool TryParseDateTimeParts(ReadOnlySpan<char> dateStr, out DateTime result)
    {
        result = DateTime.MinValue;

        if (
            !int.TryParse(dateStr[..4], out var year)
            || !int.TryParse(dateStr.Slice(4, 2), out var month)
            || !int.TryParse(dateStr.Slice(6, 2), out var day)
            || !int.TryParse(dateStr.Slice(8, 2), out var hour)
            || !int.TryParse(dateStr.Slice(10, 2), out var minute)
            || !int.TryParse(dateStr.Slice(12, 2), out var second)
        )
        {
            return false;
        }

        try
        {
            result = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryParseTimezoneOffset(ReadOnlySpan<char> tzPart, out TimeSpan offset)
    {
        offset = TimeSpan.Zero;

        if (tzPart.Length < 4)
        {
            return false;
        }

        var sign = tzPart[0] == '-' ? -1 : 1;
        var offsetStr = tzPart.TrimStart(['+', '-']);

        var colonIdx = offsetStr.IndexOf(':');

        int offsetHours;
        int offsetMinutes;

        if (colonIdx > 0)
        {
            if (
                !int.TryParse(offsetStr[..colonIdx], out offsetHours)
                || !int.TryParse(offsetStr.Slice(colonIdx + 1, 2), out offsetMinutes)
            )
            {
                return false;
            }
        }
        else if (offsetStr.Length >= 4)
        {
            if (
                !int.TryParse(offsetStr[..2], out offsetHours)
                || !int.TryParse(offsetStr.Slice(2, 2), out offsetMinutes)
            )
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        offset = new TimeSpan(sign * offsetHours, sign * offsetMinutes, 0);
        return true;
    }
}
