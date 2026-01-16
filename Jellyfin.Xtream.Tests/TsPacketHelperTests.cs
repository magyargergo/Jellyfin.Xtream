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
using Jellyfin.Xtream.Service.MpegTs.Core;
using Jellyfin.Xtream.Service.MpegTs.Models;
using Jellyfin.Xtream.Service.MpegTs.Parsing;
using Xunit;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Unit tests for TsPacketHelper shared parsing utilities.
/// </summary>
public sealed class TsPacketHelperTests
{
    #region GetPayloadStart Tests

    [Fact]
    public void GetPayloadStart_PayloadOnly_ReturnsOffset4()
    {
        // Arrange: adaptation_field_control = 01 (payload only)
        var packet = new byte[188];
        packet[0] = 0x47; // Sync
        packet[1] = 0x00;
        packet[2] = 0x00;
        packet[3] = 0x10; // AFC=01 (payload only)

        // Act
        var result = TsPacketHelper.GetPayloadStart(packet);

        // Assert
        Assert.Equal(4, result);
    }

    [Fact]
    public void GetPayloadStart_AdaptationAndPayload_ReturnsCorrectOffset()
    {
        // Arrange: adaptation_field_control = 11 (both), adaptation_length = 7
        var packet = new byte[188];
        packet[0] = 0x47;
        packet[1] = 0x00;
        packet[2] = 0x00;
        packet[3] = 0x30; // AFC=11 (both)
        packet[4] = 0x07; // Adaptation length = 7

        // Act
        var result = TsPacketHelper.GetPayloadStart(packet);

        // Assert: 5 + 7 = 12
        Assert.Equal(12, result);
    }

    [Fact]
    public void GetPayloadStart_AdaptationOnly_ReturnsMinusOne()
    {
        // Arrange: adaptation_field_control = 10 (adaptation only)
        var packet = new byte[188];
        packet[0] = 0x47;
        packet[1] = 0x00;
        packet[2] = 0x00;
        packet[3] = 0x20; // AFC=10

        // Act
        var result = TsPacketHelper.GetPayloadStart(packet);

        // Assert
        Assert.Equal(-1, result);
    }

    [Fact]
    public void GetPayloadStart_Reserved_ReturnsMinusOne()
    {
        // Arrange: adaptation_field_control = 00 (reserved)
        var packet = new byte[188];
        packet[0] = 0x47;
        packet[1] = 0x00;
        packet[2] = 0x00;
        packet[3] = 0x00; // AFC=00

        // Act
        var result = TsPacketHelper.GetPayloadStart(packet);

        // Assert
        Assert.Equal(-1, result);
    }

    [Fact]
    public void GetPayloadStart_TooShort_ReturnsMinusOne()
    {
        // Arrange
        var packet = new byte[4]; // Too short

        // Act
        var result = TsPacketHelper.GetPayloadStart(packet);

        // Assert
        Assert.Equal(-1, result);
    }

    #endregion

    #region GetPesHeaderSize Tests

    [Fact]
    public void GetPesHeaderSize_ValidVideoPes_ReturnsHeaderSize()
    {
        // Arrange: PES packet with stream_id 0xE0, header_data_length = 5
        var payload = new byte[20];
        payload[0] = 0x00;
        payload[1] = 0x00;
        payload[2] = 0x01;
        payload[3] = 0xE0; // Video stream
        payload[8] = 0x05; // Header data length

        // Act
        var result = TsPacketHelper.GetPesHeaderSize(payload);

        // Assert: 9 + 5 = 14
        Assert.Equal(14, result);
    }

    [Fact]
    public void GetPesHeaderSize_AudioStream_ReturnsZero()
    {
        // Arrange: Audio PES (stream_id 0xC0)
        var payload = new byte[20];
        payload[0] = 0x00;
        payload[1] = 0x00;
        payload[2] = 0x01;
        payload[3] = 0xC0; // Audio stream (not detected by this method)
        payload[8] = 0x05;

        // Act
        var result = TsPacketHelper.GetPesHeaderSize(payload);

        // Assert: Audio streams return 0 (method only detects video)
        Assert.Equal(0, result);
    }

    [Fact]
    public void GetPesHeaderSize_InvalidStartCode_ReturnsZero()
    {
        // Arrange: Invalid start code
        var payload = new byte[20];
        payload[0] = 0x00;
        payload[1] = 0x00;
        payload[2] = 0x02; // Wrong start code

        // Act
        var result = TsPacketHelper.GetPesHeaderSize(payload);

        // Assert
        Assert.Equal(0, result);
    }

    [Fact]
    public void GetPesHeaderSize_TooShort_ReturnsZero()
    {
        // Arrange
        var payload = new byte[5]; // Too short

        // Act
        var result = TsPacketHelper.GetPesHeaderSize(payload);

        // Assert
        Assert.Equal(0, result);
    }

    #endregion

    #region FindNalStartCode Tests

    [Fact]
    public void FindNalStartCode_ThreeByteCode_FindsOffset()
    {
        // Arrange: 00 00 01 at offset 5
        byte[] data = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x01, 0x67];

        // Act
        var result = TsPacketHelper.FindNalStartCode(data);

        // Assert
        Assert.Equal(5, result);
    }

    [Fact]
    public void FindNalStartCode_FourByteCode_FindsOffset()
    {
        // Arrange: 00 00 00 01 at offset 3
        byte[] data = [0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x01, 0x67];

        // Act
        var result = TsPacketHelper.FindNalStartCode(data);

        // Assert
        Assert.Equal(3, result);
    }

    [Fact]
    public void FindNalStartCode_WithStartOffset_SkipsEarlier()
    {
        // Arrange: 3-byte start codes at offset 0 and 10
        // Use non-zero padding between them to avoid false 4-byte detection
        var data = new byte[20];
        data[0] = 0x00;
        data[1] = 0x00;
        data[2] = 0x01; // First at offset 0
        data[3] = 0x67; // NAL header
        data[4] = 0xFF; // Padding to break any spurious patterns
        data[5] = 0xFF;
        data[6] = 0xFF;
        data[7] = 0xFF;
        data[8] = 0xFF;
        data[9] = 0xFF;
        data[10] = 0x00;
        data[11] = 0x00;
        data[12] = 0x01; // Second at offset 10
        data[13] = 0x68; // NAL header

        // Act: Start searching at offset 5
        var result = TsPacketHelper.FindNalStartCode(data, startOffset: 5);

        // Assert: Should find the one at offset 10
        Assert.Equal(10, result);
    }

    [Fact]
    public void FindNalStartCode_NoStartCode_ReturnsMinusOne()
    {
        // Arrange
        byte[] data = [0xFF, 0xFF, 0xFF, 0xFF];

        // Act
        var result = TsPacketHelper.FindNalStartCode(data);

        // Assert
        Assert.Equal(-1, result);
    }

    #endregion

    #region IsLongStartCode Tests

    [Fact]
    public void IsLongStartCode_FourByteCode_ReturnsTrue()
    {
        // Arrange
        byte[] data = [0x00, 0x00, 0x00, 0x01, 0x67];

        // Act
        var result = TsPacketHelper.IsLongStartCode(data, 0);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsLongStartCode_ThreeByteCode_ReturnsFalse()
    {
        // Arrange
        byte[] data = [0x00, 0x00, 0x01, 0x67];

        // Act
        var result = TsPacketHelper.IsLongStartCode(data, 0);

        // Assert
        Assert.False(result);
    }

    #endregion

    #region ClassifyNalUnit Tests

    [Fact]
    public void ClassifyNalUnit_H264Idr_ReturnsH264Idr()
    {
        // Arrange: NAL type 5 (IDR) with nal_ref_idc=3 -> 0x65
        byte[] payload = [0x65, 0x88];

        // Act
        var result = TsPacketHelper.ClassifyNalUnit(0x65, payload, 0);

        // Assert
        Assert.Equal(NalUnitType.H264Idr, result);
    }

    [Fact]
    public void ClassifyNalUnit_H264Sps_ReturnsH264Sps()
    {
        // Arrange: NAL type 7 (SPS) with nal_ref_idc=3 -> 0x67
        byte[] payload = [0x67, 0x42];

        // Act
        var result = TsPacketHelper.ClassifyNalUnit(0x67, payload, 0);

        // Assert
        Assert.Equal(NalUnitType.H264Sps, result);
    }

    [Fact]
    public void ClassifyNalUnit_H264Pps_ReturnsH264Pps()
    {
        // Arrange: NAL type 8 (PPS) with nal_ref_idc=3 -> 0x68
        byte[] payload = [0x68, 0xCE];

        // Act
        var result = TsPacketHelper.ClassifyNalUnit(0x68, payload, 0);

        // Assert
        Assert.Equal(NalUnitType.H264Pps, result);
    }

    [Fact]
    public void ClassifyNalUnit_H265Sps_ReturnsH265Sps()
    {
        // Arrange: H.265 SPS (NAL type 33) -> header = (33 << 1) | 0 = 0x42
        // Second byte with valid temporal_id_plus1 (1) = 0x01
        byte[] payload = [0x42, 0x01];

        // Act
        var result = TsPacketHelper.ClassifyNalUnit(0x42, payload, 0);

        // Assert
        Assert.Equal(NalUnitType.H265Sps, result);
    }

    [Fact]
    public void ClassifyNalUnit_ForbiddenBitSet_ReturnsUnknown()
    {
        // Arrange: Forbidden bit is set (bit 7 = 1)
        byte[] payload = [0x80, 0x00];

        // Act
        var result = TsPacketHelper.ClassifyNalUnit(0x80, payload, 0);

        // Assert
        Assert.Equal(NalUnitType.Unknown, result);
    }

    #endregion

    #region IsParameterSetNal Tests

    [Theory]
    [InlineData(NalUnitType.H264Sps, true)]
    [InlineData(NalUnitType.H264Pps, true)]
    [InlineData(NalUnitType.H265Sps, true)]
    [InlineData(NalUnitType.H265Pps, true)]
    [InlineData(NalUnitType.H265Vps, true)]
    [InlineData(NalUnitType.H264Idr, false)]
    [InlineData(NalUnitType.H265Idr, false)]
    [InlineData(NalUnitType.Unknown, false)]
    public void IsParameterSetNal_VariousTypes_ReturnsExpected(NalUnitType type, bool expected) =>
        Assert.Equal(expected, TsPacketHelper.IsParameterSetNal(type));

    #endregion

    #region IsIdrNal Tests

    [Theory]
    [InlineData(NalUnitType.H264Idr, true)]
    [InlineData(NalUnitType.H265Idr, true)]
    [InlineData(NalUnitType.H265Cra, true)]
    [InlineData(NalUnitType.H264Sps, false)]
    [InlineData(NalUnitType.Unknown, false)]
    public void IsIdrNal_VariousTypes_ReturnsExpected(NalUnitType type, bool expected) =>
        Assert.Equal(expected, TsPacketHelper.IsIdrNal(type));

    #endregion

    #region GetPid Tests

    [Fact]
    public void GetPid_PatPacket_ReturnsZero()
    {
        // Arrange: PAT at PID 0
        byte[] packet = [0x47, 0x40, 0x00, 0x10];

        // Act
        var result = TsPacketHelper.GetPid(packet);

        // Assert
        Assert.Equal(0, result);
    }

    [Fact]
    public void GetPid_VideoPid256_Returns256()
    {
        // Arrange: PID 256 = 0x0100
        byte[] packet = [0x47, 0x01, 0x00, 0x10];

        // Act
        var result = TsPacketHelper.GetPid(packet);

        // Assert
        Assert.Equal(256, result);
    }

    [Fact]
    public void GetPid_NullPacket_Returns8191()
    {
        // Arrange: Null PID = 0x1FFF
        byte[] packet = [0x47, 0x1F, 0xFF, 0x10];

        // Act
        var result = TsPacketHelper.GetPid(packet);

        // Assert
        Assert.Equal(8191, result);
    }

    #endregion

    #region HasPusi Tests

    [Fact]
    public void HasPusi_BitSet_ReturnsTrue()
    {
        // Arrange: PUSI bit (0x40) is set
        byte[] packet = [0x47, 0x40, 0x00, 0x10];

        // Act
        var result = TsPacketHelper.HasPusi(packet);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void HasPusi_BitNotSet_ReturnsFalse()
    {
        // Arrange: PUSI bit not set
        byte[] packet = [0x47, 0x00, 0x00, 0x10];

        // Act
        var result = TsPacketHelper.HasPusi(packet);

        // Assert
        Assert.False(result);
    }

    #endregion

    #region HasTransportError Tests

    [Fact]
    public void HasTransportError_BitSet_ReturnsTrue()
    {
        // Arrange: TEI bit (0x80) is set
        byte[] packet = [0x47, 0x80, 0x00, 0x10];

        // Act
        var result = TsPacketHelper.HasTransportError(packet);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void HasTransportError_BitNotSet_ReturnsFalse()
    {
        // Arrange: TEI bit not set
        byte[] packet = [0x47, 0x00, 0x00, 0x10];

        // Act
        var result = TsPacketHelper.HasTransportError(packet);

        // Assert
        Assert.False(result);
    }

    #endregion

    #region HasRandomAccessIndicator Tests

    [Fact]
    public void HasRandomAccessIndicator_FlagSet_ReturnsTrue()
    {
        // Arrange: Adaptation field with RAI flag
        var packet = new byte[188];
        packet[0] = 0x47;
        packet[1] = 0x00;
        packet[2] = 0x00;
        packet[3] = 0x20; // AFC=10 (adaptation only)
        packet[4] = 0x07; // Adaptation length
        packet[5] = 0x40; // RAI flag set

        // Act
        var result = TsPacketHelper.HasRandomAccessIndicator(packet);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void HasRandomAccessIndicator_NoAdaptation_ReturnsFalse()
    {
        // Arrange: Payload only (no adaptation field)
        var packet = new byte[188];
        packet[0] = 0x47;
        packet[1] = 0x00;
        packet[2] = 0x00;
        packet[3] = 0x10; // AFC=01 (payload only)

        // Act
        var result = TsPacketHelper.HasRandomAccessIndicator(packet);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void HasRandomAccessIndicator_AllFlagsSet_ReturnsTrue()
    {
        // Arrange: All adaptation flags set (0xFF includes RAI at bit 6)
        var packet = new byte[188];
        packet[0] = 0x47;
        packet[1] = 0x01;
        packet[2] = 0x01;
        packet[3] = 0x30; // AFC=11 (adaptation + payload)
        packet[4] = 7; // Adaptation length = 7 (enough for all flags)
        packet[5] = 0xFF; // All flags including RAI

        // Act
        var result = TsPacketHelper.HasRandomAccessIndicator(packet);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void HasRandomAccessIndicator_DiscontinuityOnly_ReturnsFalse()
    {
        // Arrange: Only discontinuity flag (0x80), not RAI
        var packet = new byte[188];
        packet[0] = 0x47;
        packet[1] = 0x01;
        packet[2] = 0x01;
        packet[3] = 0x30;
        packet[4] = 1;
        packet[5] = 0x80; // Discontinuity flag only

        // Act
        var result = TsPacketHelper.HasRandomAccessIndicator(packet);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void HasRandomAccessIndicator_PcrFlagOnly_ReturnsFalse()
    {
        // Arrange: PCR flag (0x10) without RAI
        var packet = new byte[188];
        packet[0] = 0x47;
        packet[1] = 0x01;
        packet[2] = 0x01;
        packet[3] = 0x30;
        packet[4] = 7; // Needs space for PCR data
        packet[5] = 0x10; // PCR flag only

        // Act
        var result = TsPacketHelper.HasRandomAccessIndicator(packet);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void HasRandomAccessIndicator_RaiWithPcr_ReturnsTrue()
    {
        // Arrange: Both RAI (0x40) and PCR (0x10) flags
        var packet = new byte[188];
        packet[0] = 0x47;
        packet[1] = 0x01;
        packet[2] = 0x01;
        packet[3] = 0x30;
        packet[4] = 7;
        packet[5] = 0x50; // RAI + PCR

        // Act
        var result = TsPacketHelper.HasRandomAccessIndicator(packet);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void HasRandomAccessIndicator_ZeroAdaptationLength_ReturnsFalse()
    {
        // Arrange: adaptation_length = 0 (no flags byte present)
        var packet = new byte[188];
        packet[0] = 0x47;
        packet[1] = 0x01;
        packet[2] = 0x01;
        packet[3] = 0x30;
        packet[4] = 0; // Zero adaptation length

        // Act
        var result = TsPacketHelper.HasRandomAccessIndicator(packet);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void HasRandomAccessIndicator_InvalidAdaptationLength_ReturnsFalse()
    {
        // Arrange: Invalid adaptation length > 183
        var packet = new byte[188];
        packet[0] = 0x47;
        packet[1] = 0x01;
        packet[2] = 0x01;
        packet[3] = 0x30;
        packet[4] = 184; // Invalid: > 183
        packet[5] = 0x40; // Would have RAI if valid

        // Act
        var result = TsPacketHelper.HasRandomAccessIndicator(packet);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void HasRandomAccessIndicator_MaxAdaptationLength_ReturnsTrue()
    {
        // Arrange: Maximum valid adaptation length (183)
        var packet = new byte[188];
        packet[0] = 0x47;
        packet[1] = 0x01;
        packet[2] = 0x01;
        packet[3] = 0x30;
        packet[4] = 183; // Maximum valid
        packet[5] = 0x40;

        // Act
        var result = TsPacketHelper.HasRandomAccessIndicator(packet);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void HasRandomAccessIndicator_MinAdaptationLength_ReturnsTrue()
    {
        // Arrange: Minimum adaptation length (1) to have flags byte
        var packet = new byte[188];
        packet[0] = 0x47;
        packet[1] = 0x01;
        packet[2] = 0x01;
        packet[3] = 0x30;
        packet[4] = 1;
        packet[5] = 0x40;

        // Act
        var result = TsPacketHelper.HasRandomAccessIndicator(packet);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void HasRandomAccessIndicator_ReservedControlValue_ReturnsFalse()
    {
        // Arrange: adaptation_field_control = 0 (reserved)
        var packet = new byte[188];
        packet[0] = 0x47;
        packet[1] = 0x01;
        packet[2] = 0x01;
        packet[3] = 0x00; // AFC=00 (reserved)
        packet[4] = 1;
        packet[5] = 0x40;

        // Act
        var result = TsPacketHelper.HasRandomAccessIndicator(packet);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void HasRandomAccessIndicator_NullPacketWithRai_ReturnsTrue()
    {
        // Arrange: Null packet (PID 0x1FFF) with RAI flag
        var packet = new byte[188];
        packet[0] = 0x47;
        packet[1] = 0x1F;
        packet[2] = 0xFF;
        packet[3] = 0x30;
        packet[4] = 1;
        packet[5] = 0x40;

        // Act
        var result = TsPacketHelper.HasRandomAccessIndicator(packet);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void HasRandomAccessIndicator_TruncatedPacket_ReturnsFalse()
    {
        // Arrange: Packet too short (5 bytes - no flags byte)
        byte[] packet = [0x47, 0x01, 0x01, 0x30, 1];

        // Act
        var result = TsPacketHelper.HasRandomAccessIndicator(packet);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void HasRandomAccessIndicator_AdaptationOnlyMode_ReturnsTrue()
    {
        // Arrange: AFC=10 (adaptation only, no payload)
        var packet = new byte[188];
        packet[0] = 0x47;
        packet[1] = 0x01;
        packet[2] = 0x01;
        packet[3] = 0x20; // AFC=10
        packet[4] = 183; // Fill entire packet with adaptation
        packet[5] = 0x40;

        // Act
        var result = TsPacketHelper.HasRandomAccessIndicator(packet);

        // Assert
        Assert.True(result);
    }

    #endregion

    #region HasDiscontinuityIndicator Tests

    [Fact]
    public void HasDiscontinuityIndicator_FlagSet_ReturnsTrue()
    {
        // Arrange: Discontinuity flag (0x80) set
        var packet = new byte[188];
        packet[0] = 0x47;
        packet[1] = 0x01;
        packet[2] = 0x01;
        packet[3] = 0x30;
        packet[4] = 1;
        packet[5] = 0x80;

        // Act
        var result = TsPacketHelper.HasDiscontinuityIndicator(packet);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void HasDiscontinuityIndicator_FlagNotSet_ReturnsFalse()
    {
        // Arrange: RAI flag only, no discontinuity
        var packet = new byte[188];
        packet[0] = 0x47;
        packet[1] = 0x01;
        packet[2] = 0x01;
        packet[3] = 0x30;
        packet[4] = 1;
        packet[5] = 0x40; // RAI only

        // Act
        var result = TsPacketHelper.HasDiscontinuityIndicator(packet);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void HasDiscontinuityIndicator_NoAdaptation_ReturnsFalse()
    {
        // Arrange: No adaptation field
        var packet = new byte[188];
        packet[0] = 0x47;
        packet[1] = 0x01;
        packet[2] = 0x01;
        packet[3] = 0x10; // Payload only

        // Act
        var result = TsPacketHelper.HasDiscontinuityIndicator(packet);

        // Assert
        Assert.False(result);
    }

    #endregion

    #region HasAdaptationField Tests

    [Theory]
    [InlineData(0x00, false)] // Reserved
    [InlineData(0x10, false)] // Payload only
    [InlineData(0x20, true)] // Adaptation only
    [InlineData(0x30, true)] // Both
    public void HasAdaptationField_VariousControlValues_ReturnsExpected(byte byte3, bool expected)
    {
        // Arrange
        var packet = new byte[188];
        packet[0] = 0x47;
        packet[1] = 0x01;
        packet[2] = 0x01;
        packet[3] = byte3;

        // Act
        var result = TsPacketHelper.HasAdaptationField(packet);

        // Assert
        Assert.Equal(expected, result);
    }

    #endregion

    #region GetContinuityCounter Tests

    [Theory]
    [InlineData(0x00, 0)]
    [InlineData(0x01, 1)]
    [InlineData(0x0F, 15)]
    [InlineData(0x30, 0)] // With AFC bits
    [InlineData(0x3F, 15)] // With AFC bits
    public void GetContinuityCounter_VariousValues_ReturnsExpected(byte byte3, int expected)
    {
        // Arrange
        byte[] packet = [0x47, 0x01, 0x01, byte3];

        // Act
        var result = TsPacketHelper.GetContinuityCounter(packet);

        // Assert
        Assert.Equal(expected, result);
    }

    #endregion

    #region GetScramblingControl Tests

    [Theory]
    [InlineData(0x00, 0)] // Not scrambled
    [InlineData(0x40, 1)] // Reserved
    [InlineData(0x80, 2)] // Even key
    [InlineData(0xC0, 3)] // Odd key
    public void GetScramblingControl_VariousValues_ReturnsExpected(byte byte3, int expected)
    {
        // Arrange
        byte[] packet = [0x47, 0x01, 0x01, byte3];

        // Act
        var result = TsPacketHelper.GetScramblingControl(packet);

        // Assert
        Assert.Equal(expected, result);
    }

    #endregion
}
