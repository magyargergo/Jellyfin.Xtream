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

using Jellyfin.Xtream.Service.MpegTs.Infrastructure;
using Jellyfin.Xtream.Service.MpegTs.Models;
using Xunit;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Tests for FFmpegParameterSetExtractor - AVCC/HVCC/Annex B extradata parsing.
/// </summary>
/// <remarks>
/// These tests verify the fallback manual parsing path since FFmpeg is not loaded in tests.
/// </remarks>
public sealed class ParameterSetExtractorTests
{
    /// <summary>
    /// NAL unit start code for Annex B format.
    /// </summary>
    private static readonly byte[] StartCode = [0x00, 0x00, 0x00, 0x01];

    // FFmpeg codec IDs
    private const int AvCodecIdH264 = 27;

    [Fact]
    public void Extract_H264Avcc_ValidExtradata_ExtractsSpsAndPps()
    {
        // Arrange - Create a minimal valid AVCC extradata
        var spsData = new byte[] { 0x67, 0x42, 0x00, 0x1E, 0x96, 0x52, 0x02, 0x83, 0xF6, 0x00, 0x88, 0x00 };
        var ppsData = new byte[] { 0x68, 0xCE, 0x06, 0xE2 };

        var extradata = new byte[6 + 2 + spsData.Length + 1 + 2 + ppsData.Length];
        var offset = 0;

        // Configuration header (6 bytes)
        extradata[offset++] = 1; // configurationVersion
        extradata[offset++] = 0x42; // AVCProfileIndication (Baseline)
        extradata[offset++] = 0x00; // profile_compatibility
        extradata[offset++] = 0x1E; // AVCLevelIndication (3.0)
        extradata[offset++] = 0xFF; // lengthSizeMinusOne (3 => 4 bytes) with reserved bits
        extradata[offset++] = 0xE1; // numOfSPS = 1 with reserved bits

        // SPS (2-byte length + data)
        extradata[offset++] = (byte)(spsData.Length >> 8);
        extradata[offset++] = (byte)(spsData.Length & 0xFF);
        spsData.CopyTo(extradata, offset);
        offset += spsData.Length;

        // PPS (1-byte count + 2-byte length + data)
        extradata[offset++] = 1; // numOfPPS
        extradata[offset++] = (byte)(ppsData.Length >> 8);
        extradata[offset++] = (byte)(ppsData.Length & 0xFF);
        ppsData.CopyTo(extradata, offset);

        var cache = new CachedParameterSets();

        // Act
        var result = FFmpegParameterSetExtractor.Extract(extradata, AvCodecIdH264, cache);

        // Assert
        Assert.True(result);
        Assert.True(cache.HasSps);
        Assert.True(cache.HasPps);
        Assert.True(cache.HasH264ParameterSets);

        // Verify the extracted data includes start codes
        Assert.NotNull(cache.Sps);
        Assert.NotNull(cache.Pps);
        Assert.Equal(StartCode.Length + spsData.Length, cache.Sps.Length);
        Assert.Equal(StartCode.Length + ppsData.Length, cache.Pps.Length);
    }

    [Fact]
    public void Extract_H264Avcc_TooShortExtradata_ReturnsFalse()
    {
        var extradata = new byte[] { 1, 2, 3 }; // Too short to be valid
        var cache = new CachedParameterSets();

        var result = FFmpegParameterSetExtractor.Extract(extradata, AvCodecIdH264, cache);

        Assert.False(result);
        Assert.False(cache.HasH264ParameterSets);
    }

    [Fact]
    public void Extract_H264Avcc_InvalidVersion_ReturnsFalse()
    {
        // Create extradata with invalid configurationVersion
        var extradata = new byte[] { 2, 0x42, 0x00, 0x1E, 0xFF, 0xE1, 0x00, 0x04, 0x67, 0x42, 0x00, 0x1E };

        var cache = new CachedParameterSets();

        var result = FFmpegParameterSetExtractor.Extract(extradata, AvCodecIdH264, cache);

        Assert.False(result);
    }

    [Fact]
    public void Extract_H264Avcc_WithAutoDetect_ExtractsSpsAndPps()
    {
        // Arrange - AVCC format
        var spsData = new byte[] { 0x67, 0x42, 0x00, 0x1E };
        var ppsData = new byte[] { 0x68, 0xCE };

        var extradata = new byte[6 + 2 + spsData.Length + 1 + 2 + ppsData.Length];
        extradata[0] = 1;
        extradata[1] = 0x42;
        extradata[2] = 0x00;
        extradata[3] = 0x1E;
        extradata[4] = 0xFF;
        extradata[5] = 0xE1;
        extradata[6] = 0;
        extradata[7] = (byte)spsData.Length;
        spsData.CopyTo(extradata, 8);
        var ppsOffset = 8 + spsData.Length;
        extradata[ppsOffset] = 1;
        extradata[ppsOffset + 1] = 0;
        extradata[ppsOffset + 2] = (byte)ppsData.Length;
        ppsData.CopyTo(extradata, ppsOffset + 3);

        var cache = new CachedParameterSets();

        // AV_CODEC_ID_H264 = 27
        var result = FFmpegParameterSetExtractor.Extract(extradata, AvCodecIdH264, cache);

        Assert.True(result);
        Assert.True(cache.HasH264ParameterSets);
    }

    [Fact]
    public void Extract_UnknownCodecId_ReturnsFalse()
    {
        var extradata = new byte[] { 1, 0x42, 0x00, 0x1E, 0xFF, 0xE1, 0x00, 0x04, 0x67, 0x42, 0x00, 0x1E };

        var cache = new CachedParameterSets();

        // Unknown codec ID
        var result = FFmpegParameterSetExtractor.Extract(extradata, 999, cache);

        Assert.False(result);
    }

    [Fact]
    public void CachedParameterSets_HasH264ParameterSets_RequiresBoth()
    {
        var cache = new CachedParameterSets();

        Assert.False(cache.HasH264ParameterSets);

        cache.UpdateSps([0x00, 0x00, 0x00, 0x01, 0x67]);
        Assert.False(cache.HasH264ParameterSets); // Only SPS

        cache.UpdatePps([0x00, 0x00, 0x00, 0x01, 0x68]);
        Assert.True(cache.HasH264ParameterSets); // Both present
    }

    [Fact]
    public void Extract_AnnexB_ValidExtradata_ExtractsSpsAndPps()
    {
        // Arrange - Create Annex B format extradata with SPS and PPS
        var spsData = new byte[] { 0x67, 0x42, 0x00, 0x1E, 0x96, 0x52 }; // NAL type 7 = SPS
        var ppsData = new byte[] { 0x68, 0xCE, 0x06, 0xE2 }; // NAL type 8 = PPS

        // Build Annex B extradata: 00 00 00 01 <SPS> 00 00 00 01 <PPS>
        var extradata = new byte[4 + spsData.Length + 4 + ppsData.Length];
        var offset = 0;

        // SPS with 4-byte start code
        extradata[offset++] = 0x00;
        extradata[offset++] = 0x00;
        extradata[offset++] = 0x00;
        extradata[offset++] = 0x01;
        spsData.CopyTo(extradata, offset);
        offset += spsData.Length;

        // PPS with 4-byte start code
        extradata[offset++] = 0x00;
        extradata[offset++] = 0x00;
        extradata[offset++] = 0x00;
        extradata[offset++] = 0x01;
        ppsData.CopyTo(extradata, offset);

        var cache = new CachedParameterSets();

        // Act
        var result = FFmpegParameterSetExtractor.Extract(extradata, AvCodecIdH264, cache);

        // Assert
        Assert.True(result);
        Assert.True(cache.HasSps);
        Assert.True(cache.HasPps);
        Assert.True(cache.HasH264ParameterSets);

        // Verify SPS includes start code
        Assert.NotNull(cache.Sps);
        Assert.Equal(4 + spsData.Length, cache.Sps.Length);
        Assert.Equal(StartCode, cache.Sps[..4]);

        // Verify PPS includes start code
        Assert.NotNull(cache.Pps);
        Assert.Equal(4 + ppsData.Length, cache.Pps.Length);
        Assert.Equal(StartCode, cache.Pps[..4]);
    }

    [Fact]
    public void Extract_AnnexB_ThreeByteStartCode_ExtractsSpsAndPps()
    {
        // Arrange - Create Annex B format with 3-byte start codes
        var spsData = new byte[] { 0x67, 0x42, 0x00, 0x1E }; // NAL type 7 = SPS
        var ppsData = new byte[] { 0x68, 0xCE }; // NAL type 8 = PPS

        // Build Annex B extradata: 00 00 01 <SPS> 00 00 01 <PPS>
        var extradata = new byte[3 + spsData.Length + 3 + ppsData.Length];
        var offset = 0;

        // SPS with 3-byte start code
        extradata[offset++] = 0x00;
        extradata[offset++] = 0x00;
        extradata[offset++] = 0x01;
        spsData.CopyTo(extradata, offset);
        offset += spsData.Length;

        // PPS with 3-byte start code
        extradata[offset++] = 0x00;
        extradata[offset++] = 0x00;
        extradata[offset++] = 0x01;
        ppsData.CopyTo(extradata, offset);

        var cache = new CachedParameterSets();

        // Act
        var result = FFmpegParameterSetExtractor.Extract(extradata, AvCodecIdH264, cache);

        // Assert
        Assert.True(result);
        Assert.True(cache.HasH264ParameterSets);
    }

    [Fact]
    public void Extract_AnnexB_AutoDetectsFormat()
    {
        // Arrange - Annex B format extradata (starts with start code)
        var spsData = new byte[] { 0x67, 0x42, 0x00, 0x1E };
        var ppsData = new byte[] { 0x68, 0xCE };

        var extradata = new byte[4 + spsData.Length + 4 + ppsData.Length];
        extradata[0] = 0x00;
        extradata[1] = 0x00;
        extradata[2] = 0x00;
        extradata[3] = 0x01;
        spsData.CopyTo(extradata, 4);
        extradata[4 + spsData.Length] = 0x00;
        extradata[5 + spsData.Length] = 0x00;
        extradata[6 + spsData.Length] = 0x00;
        extradata[7 + spsData.Length] = 0x01;
        ppsData.CopyTo(extradata, 8 + spsData.Length);

        var cache = new CachedParameterSets();

        // Act - Use Extract() which auto-detects format
        var result = FFmpegParameterSetExtractor.Extract(extradata, AvCodecIdH264, cache);

        // Assert
        Assert.True(result);
        Assert.True(cache.HasH264ParameterSets);
    }

    [Fact]
    public void Extract_AnnexB_OnlySps_ReturnsFalse()
    {
        // Arrange - Only SPS, no PPS
        var extradata = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x67, 0x42, 0x00, 0x1E };
        var cache = new CachedParameterSets();

        // Act
        var result = FFmpegParameterSetExtractor.Extract(extradata, AvCodecIdH264, cache);

        // Assert
        Assert.False(result); // Needs both SPS and PPS
        Assert.True(cache.HasSps);
        Assert.False(cache.HasPps);
    }

    [Fact]
    public void Extract_AnnexB_IgnoresNonParameterSetNalUnits()
    {
        // Arrange - SPS + IDR slice + PPS (IDR should be ignored)
        var spsData = new byte[] { 0x67, 0x42, 0x00, 0x1E }; // NAL type 7 = SPS
        var idrData = new byte[] { 0x65, 0x88, 0x84 }; // NAL type 5 = IDR slice
        var ppsData = new byte[] { 0x68, 0xCE }; // NAL type 8 = PPS

        var extradata = new byte[4 + spsData.Length + 4 + idrData.Length + 4 + ppsData.Length];
        var offset = 0;

        // SPS
        extradata[offset++] = 0x00;
        extradata[offset++] = 0x00;
        extradata[offset++] = 0x00;
        extradata[offset++] = 0x01;
        spsData.CopyTo(extradata, offset);
        offset += spsData.Length;

        // IDR (should be ignored)
        extradata[offset++] = 0x00;
        extradata[offset++] = 0x00;
        extradata[offset++] = 0x00;
        extradata[offset++] = 0x01;
        idrData.CopyTo(extradata, offset);
        offset += idrData.Length;

        // PPS
        extradata[offset++] = 0x00;
        extradata[offset++] = 0x00;
        extradata[offset++] = 0x00;
        extradata[offset++] = 0x01;
        ppsData.CopyTo(extradata, offset);

        var cache = new CachedParameterSets();

        // Act
        var result = FFmpegParameterSetExtractor.Extract(extradata, AvCodecIdH264, cache);

        // Assert
        Assert.True(result);
        Assert.True(cache.HasH264ParameterSets);
    }
}
