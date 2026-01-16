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
using FFmpeg.AutoGen.Abstractions;
using Jellyfin.Xtream.Service.MpegTs.Models;

namespace Jellyfin.Xtream.Service.MpegTs.Infrastructure;

/// <summary>
/// Extracts H.264/H.265 parameter sets from FFmpeg extradata using bitstream filters.
/// Uses the h264_mp4toannexb and hevc_mp4toannexb filters to properly convert
/// AVCC/HVCC format to Annex B format with start codes.
/// </summary>
/// <remarks>
/// <para>
/// This extractor uses FFmpeg's bitstream filters which are the authoritative
/// implementation for AVCC/HVCC parsing. This ensures correct handling of all
/// edge cases and format variations.
/// </para>
/// <para>
/// When FFmpeg is not available (e.g., in unit tests), falls back to manual parsing.
/// </para>
/// </remarks>
public static unsafe class FFmpegParameterSetExtractor
{
    // FFmpeg codec IDs
    private const int AvCodecIdH264 = 27;
    private const int AvCodecIdHevc = 173;

    // NAL unit types
    private const int H264NalSps = 7;
    private const int H264NalPps = 8;
    private const int H265NalVps = 32;
    private const int H265NalSps = 33;
    private const int H265NalPps = 34;

    // NAL unit start code (Annex B format)
    private static readonly byte[] StartCode = [0x00, 0x00, 0x00, 0x01];

    /// <summary>
    /// Extracts parameter sets from FFmpeg extradata and populates the cache.
    /// Uses FFmpeg's bitstream filters for proper AVCC/HVCC to Annex B conversion.
    /// Falls back to manual parsing if FFmpeg is not available.
    /// </summary>
    /// <param name="extradata">FFmpeg's codecpar->extradata.</param>
    /// <param name="codecId">FFmpeg codec ID (AV_CODEC_ID_H264=27, AV_CODEC_ID_HEVC=173).</param>
    /// <param name="cache">The cache to populate with extracted parameter sets.</param>
    /// <returns>True if parameter sets were successfully extracted.</returns>
    public static bool Extract(ReadOnlySpan<byte> extradata, int codecId, CachedParameterSets cache)
    {
        if (extradata.Length < 4)
        {
            return false;
        }

        // Check if this is already Annex B format (starts with start code)
        // Start codes: 00 00 00 01 (4-byte) or 00 00 01 (3-byte)
        var isAnnexB =
            (extradata[0] == 0x00 && extradata[1] == 0x00 && extradata[2] == 0x00 && extradata[3] == 0x01)
            || (extradata[0] == 0x00 && extradata[1] == 0x00 && extradata[2] == 0x01);

        if (isAnnexB)
        {
            // Already Annex B - parse directly
            return ParseAnnexB(extradata, codecId, cache);
        }

        // Check if FFmpeg is available
        if (!FFmpegContext.IsAvailable)
        {
            // Fall back to manual parsing if FFmpeg is not available
            return ParseAvccHvccManually(extradata, codecId, cache);
        }

        // Use FFmpeg bitstream filter to convert AVCC/HVCC to Annex B
        return ExtractWithBitstreamFilter(extradata, codecId, cache);
    }

    /// <summary>
    /// Extracts parameter sets using FFmpeg's h264_mp4toannexb or hevc_mp4toannexb filter.
    /// </summary>
    private static bool ExtractWithBitstreamFilter(ReadOnlySpan<byte> extradata, int codecId, CachedParameterSets cache)
    {
        var filterName = codecId switch
        {
            AvCodecIdH264 => "h264_mp4toannexb",
            AvCodecIdHevc => "hevc_mp4toannexb",
            _ => null,
        };

        if (filterName == null)
        {
            return false;
        }

        AVBSFContext* bsfContext = null;
        AVPacket* packet = null;

        try
        {
            // Get the bitstream filter
            var bsf = ffmpeg.av_bsf_get_by_name(filterName);
            if (bsf == null)
            {
                // Filter not available, fall back to manual parsing
                return ParseAvccHvccManually(extradata, codecId, cache);
            }

            // Allocate BSF context
            var ret = ffmpeg.av_bsf_alloc(bsf, &bsfContext);
            if (ret < 0 || bsfContext == null)
            {
                return ParseAvccHvccManually(extradata, codecId, cache);
            }

            // Set up input codec parameters
            bsfContext->par_in->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
            bsfContext->par_in->codec_id = (AVCodecID)codecId;

            // Copy extradata to BSF context
            bsfContext->par_in->extradata_size = extradata.Length;
            bsfContext->par_in->extradata = (byte*)ffmpeg.av_malloc((ulong)extradata.Length);
            if (bsfContext->par_in->extradata == null)
            {
                return ParseAvccHvccManually(extradata, codecId, cache);
            }

            fixed (byte* src = extradata)
            {
                Buffer.MemoryCopy(src, bsfContext->par_in->extradata, extradata.Length, extradata.Length);
            }

            // Initialize the BSF
            ret = ffmpeg.av_bsf_init(bsfContext);
            if (ret < 0)
            {
                return ParseAvccHvccManually(extradata, codecId, cache);
            }

            // After initialization, par_out->extradata contains the converted Annex B extradata
            if (bsfContext->par_out->extradata != null && bsfContext->par_out->extradata_size > 0)
            {
                var annexBData = new ReadOnlySpan<byte>(
                    bsfContext->par_out->extradata,
                    bsfContext->par_out->extradata_size
                );

                return ParseAnnexB(annexBData, codecId, cache);
            }

            return false;
        }
        catch
        {
            // On any exception, fall back to manual parsing
            return ParseAvccHvccManually(extradata, codecId, cache);
        }
        finally
        {
            if (packet != null)
            {
                ffmpeg.av_packet_free(&packet);
            }

            if (bsfContext != null)
            {
                ffmpeg.av_bsf_free(&bsfContext);
            }
        }
    }

    /// <summary>
    /// Parses Annex B format data to extract individual parameter set NAL units.
    /// </summary>
    private static bool ParseAnnexB(ReadOnlySpan<byte> data, int codecId, CachedParameterSets cache)
    {
        var offset = 0;

        while (offset < data.Length)
        {
            // Find start code
            var startCodeLength = FindStartCode(data, offset);
            if (startCodeLength == 0)
            {
                break;
            }

            var nalStart = offset;
            offset += startCodeLength;

            if (offset >= data.Length)
            {
                break;
            }

            // Get NAL unit type based on codec
            int nalType;
            if (codecId == AvCodecIdH264)
            {
                nalType = data[offset] & 0x1F;
            }
            else if (codecId == AvCodecIdHevc)
            {
                nalType = (data[offset] >> 1) & 0x3F;
            }
            else
            {
                break;
            }

            // Find the end of this NAL unit
            var nalEnd = FindNextStartCode(data, offset);
            var nalLength = nalEnd - nalStart;

            if (nalLength > 0)
            {
                var nalUnit = data.Slice(nalStart, nalLength).ToArray();

                if (codecId == AvCodecIdH264)
                {
                    switch (nalType)
                    {
                        case H264NalSps:
                            cache.UpdateSps(nalUnit);
                            break;
                        case H264NalPps:
                            cache.UpdatePps(nalUnit);
                            break;
                    }
                }
                else if (codecId == AvCodecIdHevc)
                {
                    switch (nalType)
                    {
                        case H265NalVps:
                            cache.UpdateVps(nalUnit);
                            break;
                        case H265NalSps:
                            cache.UpdateSps(nalUnit);
                            break;
                        case H265NalPps:
                            cache.UpdatePps(nalUnit);
                            break;
                    }
                }
            }

            offset = nalEnd;
        }

        return codecId == AvCodecIdH264 ? cache.HasH264ParameterSets : cache.HasH265ParameterSets;
    }

    /// <summary>
    /// Manual fallback parser for AVCC/HVCC format when FFmpeg is not available.
    /// </summary>
    private static bool ParseAvccHvccManually(ReadOnlySpan<byte> extradata, int codecId, CachedParameterSets cache)
    {
        return codecId switch
        {
            AvCodecIdH264 => ParseH264AvccManually(extradata, cache),
            AvCodecIdHevc => ParseH265HvccManually(extradata, cache),
            _ => false,
        };
    }

    /// <summary>
    /// Parses H.264 AVCC extradata manually (fallback when FFmpeg unavailable).
    /// </summary>
    private static bool ParseH264AvccManually(ReadOnlySpan<byte> extradata, CachedParameterSets cache)
    {
        if (extradata.Length < 7)
        {
            return false;
        }

        // Verify AVCC format (configurationVersion must be 1)
        if (extradata[0] != 1)
        {
            return false;
        }

        var offset = 5; // Skip header to numOfSPS

        // Number of SPS units (lower 5 bits)
        var numSps = extradata[offset] & 0x1F;
        offset++;

        // Parse SPS units
        for (var i = 0; i < numSps; i++)
        {
            if (offset + 2 > extradata.Length)
            {
                return false;
            }

            var spsLength = (extradata[offset] << 8) | extradata[offset + 1];
            offset += 2;

            if (offset + spsLength > extradata.Length)
            {
                return false;
            }

            // Convert to Annex B format (add start code)
            var spsWithStartCode = new byte[StartCode.Length + spsLength];
            StartCode.CopyTo(spsWithStartCode, 0);
            extradata.Slice(offset, spsLength).CopyTo(spsWithStartCode.AsSpan(StartCode.Length));

            cache.UpdateSps(spsWithStartCode);
            offset += spsLength;
        }

        // Number of PPS units
        if (offset >= extradata.Length)
        {
            return false;
        }

        var numPps = extradata[offset];
        offset++;

        // Parse PPS units
        for (var i = 0; i < numPps; i++)
        {
            if (offset + 2 > extradata.Length)
            {
                return false;
            }

            var ppsLength = (extradata[offset] << 8) | extradata[offset + 1];
            offset += 2;

            if (offset + ppsLength > extradata.Length)
            {
                return false;
            }

            // Convert to Annex B format (add start code)
            var ppsWithStartCode = new byte[StartCode.Length + ppsLength];
            StartCode.CopyTo(ppsWithStartCode, 0);
            extradata.Slice(offset, ppsLength).CopyTo(ppsWithStartCode.AsSpan(StartCode.Length));

            cache.UpdatePps(ppsWithStartCode);
            offset += ppsLength;
        }

        return cache.HasH264ParameterSets;
    }

    /// <summary>
    /// Parses H.265 HVCC extradata manually (fallback when FFmpeg unavailable).
    /// </summary>
    private static bool ParseH265HvccManually(ReadOnlySpan<byte> extradata, CachedParameterSets cache)
    {
        // HVCC format (ISO/IEC 14496-15 section 8.3.3.1):
        // - 22 bytes header
        // - 1 byte: numOfArrays
        // - For each array: 1 byte type, 2 bytes numNalus, then NAL units

        if (extradata.Length < 23)
        {
            return false;
        }

        // Verify HVCC format (configurationVersion must be 1)
        if (extradata[0] != 1)
        {
            return false;
        }

        var offset = 22; // Skip header
        var numArrays = extradata[offset];
        offset++;

        for (var i = 0; i < numArrays; i++)
        {
            if (offset >= extradata.Length)
            {
                return false;
            }

            // NAL unit type (lower 6 bits)
            var nalType = extradata[offset] & 0x3F;
            offset++;

            if (offset + 2 > extradata.Length)
            {
                return false;
            }

            var numNalus = (extradata[offset] << 8) | extradata[offset + 1];
            offset += 2;

            for (var j = 0; j < numNalus; j++)
            {
                if (offset + 2 > extradata.Length)
                {
                    return false;
                }

                var nalLength = (extradata[offset] << 8) | extradata[offset + 1];
                offset += 2;

                if (offset + nalLength > extradata.Length)
                {
                    return false;
                }

                // Convert to Annex B format
                var nalWithStartCode = new byte[StartCode.Length + nalLength];
                StartCode.CopyTo(nalWithStartCode, 0);
                extradata.Slice(offset, nalLength).CopyTo(nalWithStartCode.AsSpan(StartCode.Length));

                // H.265 NAL unit types: VPS=32, SPS=33, PPS=34
                switch (nalType)
                {
                    case H265NalVps:
                        cache.UpdateVps(nalWithStartCode);
                        break;
                    case H265NalSps:
                        cache.UpdateSps(nalWithStartCode);
                        break;
                    case H265NalPps:
                        cache.UpdatePps(nalWithStartCode);
                        break;
                }

                offset += nalLength;
            }
        }

        return cache.HasH265ParameterSets;
    }

    /// <summary>
    /// Finds a start code at the given position.
    /// </summary>
    /// <returns>Length of start code (3 or 4) if found, 0 otherwise.</returns>
    private static int FindStartCode(ReadOnlySpan<byte> data, int offset)
    {
        if (
            offset + 4 <= data.Length
            && data[offset] == 0x00
            && data[offset + 1] == 0x00
            && data[offset + 2] == 0x00
            && data[offset + 3] == 0x01
        )
        {
            return 4;
        }

        if (offset + 3 <= data.Length && data[offset] == 0x00 && data[offset + 1] == 0x00 && data[offset + 2] == 0x01)
        {
            return 3;
        }

        return 0;
    }

    /// <summary>
    /// Finds the position of the next start code after the given offset.
    /// </summary>
    /// <returns>Position of next start code, or end of data if not found.</returns>
    private static int FindNextStartCode(ReadOnlySpan<byte> data, int offset)
    {
        for (var i = offset; i < data.Length - 2; i++)
        {
            if (data[i] == 0x00 && data[i + 1] == 0x00)
            {
                // Check for 3-byte start code (00 00 01)
                if (data[i + 2] == 0x01)
                {
                    return i;
                }

                // Check for 4-byte start code (00 00 00 01)
                if (i + 3 < data.Length && data[i + 2] == 0x00 && data[i + 3] == 0x01)
                {
                    return i;
                }
            }
        }

        return data.Length;
    }
}
