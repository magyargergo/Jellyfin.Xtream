using System.Buffers;
using System.Buffers.Binary;

namespace Jellyfin.Xtream.E2ETests.Infrastructure;

/// <summary>
/// H.264 NAL unit type constants.
/// </summary>
internal static class H264NalType
{
    public const byte NonIdrSlice = 1;
    public const byte IdrSlice = 5;
    public const byte Sps = 7;
    public const byte Pps = 8;
    public const byte Aud = 9;
}

/// <summary>
/// Generates valid MPEG-TS data for E2E testing.
/// Produces compliant TS packets with PAT, PMT, PCR, and PES headers.
/// </summary>
internal sealed class TestStreamGenerator
{
    private const int TsPacketSize = 188;
    private const byte SyncByte = 0x47;

    // PID assignments
    private const int PatPid = 0x0000;
    private const int PmtPid = 0x0100;
    private const int VideoPid = 0x0101;
    private const int AudioPid = 0x0102;
    private const int NullPid = 0x1FFF;

    // H.264 NAL generation
    private static readonly byte[] NalStartCode4 = [0x00, 0x00, 0x00, 0x01];
    private static readonly byte[] NalStartCode3 = [0x00, 0x00, 0x01];
    private bool _enableH264Nals;
    private int _frameCounter;
    private int _gopSize = 30; // IDR every 30 frames

    // PCR clock: 27MHz base, 300 extension divisor → 90kHz PCR base
    private const long PcrTicksPerSecond = 27_000_000L;
    private const long Pcr90KhzPerSecond = 90_000L;
    private const long PcrIntervalMs = 40; // PCR every 40ms per spec

    // Frame timing: PTS must advance at frame rate, not packet rate
    // Video: 30fps → 3003 ticks per frame (90000/29.97)
    // Audio: AAC at 48kHz with 1024 samples → 46.875fps → 1920 ticks per frame
    private const long VideoFrameTicks90Khz = 3003;
    private const long AudioFrameTicks90Khz = 1920;

    // Continuity counters (0-15)
    private readonly int[] _continuityCounters = new int[8192];
    private long _currentPcr90Khz;
    private long _currentVideoPts90Khz;
    private long _currentAudioPts90Khz;
    private long _nextVideoFramePcr90Khz; // PCR threshold for next video frame
    private long _nextAudioFramePcr90Khz; // PCR threshold for next audio frame
    private long _packetIndex;
    private int _pcrPacketCounter;
    private readonly int _pcrIntervalPackets;
    private readonly long _initialPcrOffset;

    /// <summary>
    /// Initializes a new instance of the <see cref="TestStreamGenerator"/> class.
    /// </summary>
    /// <param name="bitrateKbps">Target bitrate in kbps (determines null packet insertion rate).</param>
    /// <param name="initialPcrOffset">Initial PCR offset in 90kHz ticks (for restamp testing).</param>
    /// <param name="enableH264Nals">When true, generates valid H.264 NAL units (SPS/PPS/IDR) instead of pseudo data.</param>
    /// <param name="gopSize">Number of frames between IDR frames (default 30).</param>
    public TestStreamGenerator(
        int bitrateKbps = 5000,
        long initialPcrOffset = 0,
        bool enableH264Nals = false,
        int gopSize = 30
    )
    {
        _initialPcrOffset = initialPcrOffset;
        _currentPcr90Khz = initialPcrOffset;
        _currentVideoPts90Khz = initialPcrOffset;
        _currentAudioPts90Khz = initialPcrOffset;
        _nextVideoFramePcr90Khz = initialPcrOffset;
        _nextAudioFramePcr90Khz = initialPcrOffset;
        _enableH264Nals = enableH264Nals;
        _gopSize = gopSize;

        // Calculate how many packets between PCR insertions
        // At bitrate B, packets/sec = B*1000/8/188
        double packetsPerSecond = (bitrateKbps * 1000.0) / 8.0 / TsPacketSize;
        _pcrIntervalPackets = Math.Max(1, (int)(packetsPerSecond * PcrIntervalMs / 1000.0));
    }

    /// <summary>
    /// Gets the current PCR value in 90kHz ticks.
    /// </summary>
    public long CurrentPcr90Khz => _currentPcr90Khz;

    /// <summary>
    /// Gets the total packets generated.
    /// </summary>
    public long TotalPackets => _packetIndex;

    /// <summary>
    /// Generates a chunk of MPEG-TS data containing the specified number of packets.
    /// Includes PAT/PMT at the start and periodic PCR insertion.
    /// </summary>
    /// <param name="packetCount">Number of TS packets to generate.</param>
    /// <returns>Byte array containing valid MPEG-TS data.</returns>
    public byte[] GenerateChunk(int packetCount)
    {
        var buffer = new byte[packetCount * TsPacketSize];
        var offset = 0;

        for (int i = 0; i < packetCount; i++)
        {
            var span = buffer.AsSpan(offset, TsPacketSize);

            if (_packetIndex == 0)
            {
                // First packet is always PAT
                WritePatPacket(span);
            }
            else if (_packetIndex == 1)
            {
                // Second packet is always PMT
                WritePmtPacket(span);
            }
            else if (_pcrPacketCounter >= _pcrIntervalPackets)
            {
                // Insert video packet with PCR
                WriteVideoPacketWithPcr(span);
                _pcrPacketCounter = 0;
            }
            else if (_packetIndex % 7 == 0)
            {
                // Periodic PAT/PMT refresh (every 7 content packets)
                if (_packetIndex % 14 == 0)
                {
                    WritePatPacket(span);
                }
                else
                {
                    WritePmtPacket(span);
                }
            }
            else if (_packetIndex % 5 == 0)
            {
                // Audio PES packet
                WriteAudioPesPacket(span);
            }
            else if (_packetIndex % 3 == 0)
            {
                // Video PES packet (no PCR)
                WriteVideoPesPacket(span);
            }
            else
            {
                // Null/stuffing packet
                WriteNullPacket(span);
            }

            offset += TsPacketSize;
            _packetIndex++;
            _pcrPacketCounter++;
        }

        return buffer;
    }

    /// <summary>
    /// Generates a finite stream of TS data as a MemoryStream.
    /// </summary>
    /// <param name="totalPackets">Total packets to generate.</param>
    /// <returns>MemoryStream containing the TS data.</returns>
    public MemoryStream GenerateStream(int totalPackets)
    {
        var data = GenerateChunk(totalPackets);
        return new MemoryStream(data, writable: false);
    }

    /// <summary>
    /// Resets the generator state for a new stream.
    /// </summary>
    public void Reset()
    {
        Array.Clear(_continuityCounters);
        _currentPcr90Khz = _initialPcrOffset;
        _currentVideoPts90Khz = _initialPcrOffset;
        _currentAudioPts90Khz = _initialPcrOffset;
        _nextVideoFramePcr90Khz = _initialPcrOffset;
        _nextAudioFramePcr90Khz = _initialPcrOffset;
        _packetIndex = 0;
        _pcrPacketCounter = 0;
        _frameCounter = 0;
    }

    private void WritePatPacket(Span<byte> packet)
    {
        packet.Clear();
        var cc = NextContinuityCounter(PatPid);

        // TS header
        packet[0] = SyncByte;
        packet[1] = 0x40; // PUSI=1, PID high=0
        packet[2] = 0x00; // PID low=0 (PAT)
        packet[3] = (byte)(0x10 | cc); // no adaptation, payload only

        // Pointer field (required when PUSI=1)
        packet[4] = 0x00;

        // PAT section
        int offset = 5;
        packet[offset++] = 0x00; // table_id = 0 (PAT)
        packet[offset++] = 0xB0; // section_syntax_indicator=1, reserved, section_length high
        packet[offset++] = 0x0D; // section_length = 13 bytes
        packet[offset++] = 0x00; // transport_stream_id high
        packet[offset++] = 0x01; // transport_stream_id low
        packet[offset++] = 0xC1; // reserved, version=0, current_next=1
        packet[offset++] = 0x00; // section_number
        packet[offset++] = 0x00; // last_section_number

        // Program 1 -> PMT PID 0x100
        packet[offset++] = 0x00; // program_number high
        packet[offset++] = 0x01; // program_number low
        packet[offset++] = 0xE1; // reserved + PMT PID high (0x100)
        packet[offset++] = 0x00; // PMT PID low

        // CRC32 (simplified - use 0xFFFFFFFF placeholder, native will recalculate)
        var crc = CalculateCrc32(packet.Slice(5, offset - 5));
        BinaryPrimitives.WriteUInt32BigEndian(packet.Slice(offset), crc);
        offset += 4;

        // Fill rest with 0xFF (stuffing)
        packet.Slice(offset).Fill(0xFF);
    }

    private void WritePmtPacket(Span<byte> packet)
    {
        packet.Clear();
        var cc = NextContinuityCounter(PmtPid);

        // TS header
        packet[0] = SyncByte;
        packet[1] = (byte)(0x40 | ((PmtPid >> 8) & 0x1F)); // PUSI=1
        packet[2] = (byte)(PmtPid & 0xFF);
        packet[3] = (byte)(0x10 | cc); // payload only

        // Pointer field
        packet[4] = 0x00;

        // PMT section
        int offset = 5;
        packet[offset++] = 0x02; // table_id = 2 (PMT)
        packet[offset++] = 0xB0; // section_syntax_indicator=1
        packet[offset++] = 0x17; // section_length = 23 bytes
        packet[offset++] = 0x00; // program_number high
        packet[offset++] = 0x01; // program_number low
        packet[offset++] = 0xC1; // reserved, version=0, current_next=1
        packet[offset++] = 0x00; // section_number
        packet[offset++] = 0x00; // last_section_number
        packet[offset++] = (byte)(0xE0 | ((VideoPid >> 8) & 0x1F)); // PCR PID high (video carries PCR)
        packet[offset++] = (byte)(VideoPid & 0xFF); // PCR PID low
        packet[offset++] = 0xF0; // reserved + program_info_length high
        packet[offset++] = 0x00; // program_info_length = 0

        // Video stream (H.264 = stream_type 0x1B)
        packet[offset++] = 0x1B; // stream_type = H.264
        packet[offset++] = (byte)(0xE0 | ((VideoPid >> 8) & 0x1F));
        packet[offset++] = (byte)(VideoPid & 0xFF);
        packet[offset++] = 0xF0; // ES_info_length high
        packet[offset++] = 0x00; // ES_info_length = 0

        // Audio stream (AAC = stream_type 0x0F)
        packet[offset++] = 0x0F; // stream_type = AAC
        packet[offset++] = (byte)(0xE0 | ((AudioPid >> 8) & 0x1F));
        packet[offset++] = (byte)(AudioPid & 0xFF);
        packet[offset++] = 0xF0; // ES_info_length high
        packet[offset++] = 0x00; // ES_info_length = 0

        // CRC32
        var crc = CalculateCrc32(packet.Slice(5, offset - 5));
        BinaryPrimitives.WriteUInt32BigEndian(packet.Slice(offset), crc);
        offset += 4;

        // Fill rest with 0xFF
        packet.Slice(offset).Fill(0xFF);
    }

    private void WriteVideoPacketWithPcr(Span<byte> packet)
    {
        packet.Clear();
        var cc = NextContinuityCounter(VideoPid);

        // Advance PCR by interval
        _currentPcr90Khz += (PcrIntervalMs * Pcr90KhzPerSecond) / 1000;

        // Advance video PTS at frame rate (may need multiple frames per PCR step)
        AdvanceVideoFrames();

        // Determine if this is a keyframe
        bool isKeyframe = _frameCounter % _gopSize == 0;

        // TS header with adaptation field + payload
        packet[0] = SyncByte;
        packet[1] = (byte)(0x40 | ((VideoPid >> 8) & 0x1F)); // PUSI=1
        packet[2] = (byte)(VideoPid & 0xFF);
        packet[3] = (byte)(0x30 | cc); // adaptation + payload

        // Adaptation field
        packet[4] = 7; // adaptation_field_length (1 flags + 6 PCR)
        packet[5] = (byte)(0x50 | (isKeyframe ? 0x40 : 0x00)); // PCR flag=1, random_access=1 if keyframe

        // PCR (42-bit base + 6 reserved + 9-bit extension)
        long pcrBase = _currentPcr90Khz;
        int pcrExt = 0;
        packet[6] = (byte)((pcrBase >> 25) & 0xFF);
        packet[7] = (byte)((pcrBase >> 17) & 0xFF);
        packet[8] = (byte)((pcrBase >> 9) & 0xFF);
        packet[9] = (byte)((pcrBase >> 1) & 0xFF);
        packet[10] = (byte)((((int)(pcrBase & 1)) << 7) | 0x7E | ((pcrExt >> 8) & 0x01));
        packet[11] = (byte)(pcrExt & 0xFF);

        // PES header starts after adaptation field (offset 12)
        int pesOffset = 12;
        WriteVideoPesHeaderWithPts(packet.Slice(pesOffset));

        // Fill with video data
        int dataStart = pesOffset + 14; // PES header is 14 bytes with PTS
        if (_enableH264Nals)
        {
            // Generate real H.264 NAL units
            var nals = GenerateH264FrameNals(isKeyframe, _frameCounter % _gopSize);
            int copyLen = Math.Min(nals.Length, TsPacketSize - dataStart);
            nals.AsSpan(0, copyLen).CopyTo(packet.Slice(dataStart));
            // Fill remainder with padding
            packet[(dataStart + copyLen)..].Fill(0xFF);
        }
        else
        {
            // Fill with pseudo video data
            for (int i = dataStart; i < TsPacketSize; i++)
            {
                packet[i] = (byte)(i & 0xFF);
            }
        }
    }

    private void WriteVideoPesPacket(Span<byte> packet)
    {
        packet.Clear();
        var cc = NextContinuityCounter(VideoPid);

        // Determine if this is a keyframe
        bool isKeyframe = _frameCounter % _gopSize == 0;

        packet[0] = SyncByte;
        packet[1] = (byte)(0x40 | ((VideoPid >> 8) & 0x1F)); // PUSI=1
        packet[2] = (byte)(VideoPid & 0xFF);

        // If keyframe, add adaptation field with RAI flag
        if (isKeyframe && _enableH264Nals)
        {
            packet[3] = (byte)(0x30 | cc); // adaptation + payload
            packet[4] = 1; // adaptation_field_length = 1
            packet[5] = 0x40; // random_access_indicator = 1

            WriteVideoPesHeaderWithPts(packet.Slice(6));

            // Fill with video data
            int dataStart = 6 + 14; // adaptation(2) + PES header(14)
            var nals = GenerateH264FrameNals(isKeyframe, _frameCounter % _gopSize);
            int copyLen = Math.Min(nals.Length, TsPacketSize - dataStart);
            nals.AsSpan(0, copyLen).CopyTo(packet.Slice(dataStart));
            packet[(dataStart + copyLen)..].Fill(0xFF);
        }
        else
        {
            packet[3] = (byte)(0x10 | cc); // payload only

            WriteVideoPesHeaderWithPts(packet[4..]);

            // Fill with video data
            int dataStart = 18; // TS header(4) + PES header(14)
            if (_enableH264Nals)
            {
                var nals = GenerateH264FrameNals(isKeyframe, _frameCounter % _gopSize);
                int copyLen = Math.Min(nals.Length, TsPacketSize - dataStart);
                nals.AsSpan(0, copyLen).CopyTo(packet[dataStart..]);
                packet[(dataStart + copyLen)..].Fill(0xFF);
            }
            else
            {
                // Fill with pseudo data
                for (int i = dataStart; i < TsPacketSize; i++)
                {
                    packet[i] = (byte)(i & 0xFF);
                }
            }
        }
    }

    private void WriteAudioPesPacket(Span<byte> packet)
    {
        packet.Clear();
        var cc = NextContinuityCounter(AudioPid);

        // Advance audio PTS at AAC frame rate (may need multiple frames per PCR step)
        AdvanceAudioFrames();

        packet[0] = SyncByte;
        packet[1] = (byte)(0x40 | ((AudioPid >> 8) & 0x1F)); // PUSI=1
        packet[2] = (byte)(AudioPid & 0xFF);
        packet[3] = (byte)(0x10 | cc); // payload only

        WriteAudioPesHeaderWithPts(packet.Slice(4));

        // Fill with pseudo audio data
        for (int i = 18; i < TsPacketSize; i++)
        {
            packet[i] = (byte)((i + 0xAA) & 0xFF);
        }
    }

    private void WriteNullPacket(Span<byte> packet)
    {
        packet.Clear();

        packet[0] = SyncByte;
        packet[1] = (byte)((NullPid >> 8) & 0x1F);
        packet[2] = (byte)(NullPid & 0xFF);
        packet[3] = 0x10; // payload only, cc=0

        // Null packets are all 0xFF payload
        packet.Slice(4).Fill(0xFF);
    }

    private void WriteVideoPesHeaderWithPts(Span<byte> dest)
    {
        WritePesHeaderCore(dest, 0xE0, _currentVideoPts90Khz);
    }

    private void WriteAudioPesHeaderWithPts(Span<byte> dest)
    {
        WritePesHeaderCore(dest, 0xC0, _currentAudioPts90Khz);
    }

    private static void WritePesHeaderCore(Span<byte> dest, byte streamId, long pts)
    {
        // PES start code: 00 00 01
        dest[0] = 0x00;
        dest[1] = 0x00;
        dest[2] = 0x01;
        dest[3] = streamId;

        // PES packet length = 0 (unbounded for video)
        dest[4] = 0x00;
        dest[5] = 0x00;

        // PES header flags
        dest[6] = 0x80; // '10' marker bits
        dest[7] = 0x80; // PTS_DTS_flags = '10' (PTS only)
        dest[8] = 0x05; // PES_header_data_length = 5 bytes (PTS)

        // PTS (5 bytes, '0010' marker pattern)
        dest[9] = (byte)(0x21 | ((pts >> 29) & 0x0E)); // '0010' + PTS[32..30] + marker
        dest[10] = (byte)((pts >> 22) & 0xFF); // PTS[29..22]
        dest[11] = (byte)(0x01 | ((pts >> 14) & 0xFE)); // PTS[21..15] + marker
        dest[12] = (byte)((pts >> 7) & 0xFF); // PTS[14..7]
        dest[13] = (byte)(0x01 | ((pts << 1) & 0xFE)); // PTS[6..0] + marker
    }

    /// <summary>
    /// Advances video PTS to catch up with current PCR, creating frames at 30fps rate.
    /// </summary>
    private void AdvanceVideoFrames()
    {
        while (_currentPcr90Khz >= _nextVideoFramePcr90Khz)
        {
            _currentVideoPts90Khz = _nextVideoFramePcr90Khz;
            _nextVideoFramePcr90Khz += VideoFrameTicks90Khz;
            _frameCounter++;
        }
    }

    /// <summary>
    /// Advances audio PTS to catch up with current PCR, creating frames at AAC rate.
    /// </summary>
    private void AdvanceAudioFrames()
    {
        while (_currentPcr90Khz >= _nextAudioFramePcr90Khz)
        {
            _currentAudioPts90Khz = _nextAudioFramePcr90Khz;
            _nextAudioFramePcr90Khz += AudioFrameTicks90Khz;
        }
    }

    private int NextContinuityCounter(int pid)
    {
        int cc = _continuityCounters[pid];
        _continuityCounters[pid] = (cc + 1) & 0x0F;
        return cc;
    }

    // ========================================================================
    // H.264 NAL Unit Generation
    // ========================================================================

    /// <summary>
    /// Creates a minimal H.264 SPS NAL unit.
    /// Profile: High (100), Level: 4.0, Resolution: 1920x1080.
    /// </summary>
    /// <returns>SPS NAL unit bytes including NAL header (without start code).</returns>
    public static byte[] CreateH264Sps()
    {
        // NAL header: nal_ref_idc=3, nal_unit_type=7 (SPS) -> 0x67
        // Profile: High (100), Level: 4.0 (40), Resolution: 1920x1080
        // This is a simplified but parseable SPS that TsDuck can decode
        return new byte[]
        {
            0x67, // NAL header (SPS)
            0x64, // profile_idc = 100 (High)
            0x00, // constraint_set flags
            0x28, // level_idc = 40 (4.0)
            0xAC, // seq_parameter_set_id=0 + log2_max_frame_num=4 + pic_order_cnt_type=0
            0xD9, // log2_max_pic_order_cnt_lsb=4 + max_num_ref_frames=4
            0x40, // gaps_in_frame_num_allowed=0 + pic_width_in_mbs_minus1=119 (1920/16-1)
            0x77, // pic_width cont'd
            0x20, // pic_height_in_map_units_minus1=67 (1080/16-1)
            0x10, // pic_height cont'd + frame_mbs_only=1
            0xB8, // direct_8x8_inference=1 + frame_cropping=1
            0x00, // crop_left=0, crop_right=0
            0x00, // crop_top=0
            0x04, // crop_bottom=4 (1088-1080=8 pixels, /2=4)
            0x80, // rbsp_trailing_bits
        };
    }

    /// <summary>
    /// Creates a minimal H.264 PPS NAL unit.
    /// </summary>
    /// <returns>PPS NAL unit bytes including NAL header (without start code).</returns>
    public static byte[] CreateH264Pps()
    {
        // NAL header: nal_ref_idc=3, nal_unit_type=8 (PPS) -> 0x68
        return new byte[]
        {
            0x68, // NAL header (PPS)
            0xE8, // pic_parameter_set_id=0 + seq_parameter_set_id=0
            0x43, // entropy_coding_mode=1 (CABAC) + other flags
            0xC8, // deblocking, constrained_intra, redundant_pic_cnt
            0x80, // rbsp_trailing_bits
        };
    }

    /// <summary>
    /// Creates an H.264 IDR slice NAL unit header.
    /// </summary>
    /// <returns>IDR NAL unit header bytes (without start code).</returns>
    public static byte[] CreateH264Idr()
    {
        // NAL header: nal_ref_idc=3, nal_unit_type=5 (IDR) -> 0x65
        return new byte[]
        {
            0x65, // NAL header (IDR slice)
            0x88, // first_mb_in_slice=0 + slice_type=7 (I)
            0x84, // pic_parameter_set_id=0 + frame_num=0
            0x00, // padding
        };
    }

    /// <summary>
    /// Creates an H.264 non-IDR P-slice NAL unit header.
    /// </summary>
    /// <param name="frameNum">Frame number (0-15).</param>
    /// <returns>P-slice NAL unit header bytes (without start code).</returns>
    public static byte[] CreateH264PSlice(int frameNum)
    {
        // NAL header: nal_ref_idc=2, nal_unit_type=1 (non-IDR) -> 0x41
        return new byte[]
        {
            0x41, // NAL header (non-IDR slice)
            0x9A, // first_mb_in_slice=0 + slice_type=5 (P)
            (byte)(0x80 | ((frameNum & 0x0F) << 3)), // pic_parameter_set_id + frame_num
            0x00, // padding
        };
    }

    /// <summary>
    /// Creates an H.264 AUD (Access Unit Delimiter) NAL unit.
    /// </summary>
    /// <param name="primaryPicType">Primary picture type (0=I, 1=P/I, 2=B/P/I, 7=any).</param>
    /// <returns>AUD NAL unit bytes (without start code).</returns>
    public static byte[] CreateH264Aud(int primaryPicType = 7)
    {
        // NAL header: nal_ref_idc=0, nal_unit_type=9 (AUD) -> 0x09
        return new byte[]
        {
            0x09, // NAL header (AUD)
            (byte)((primaryPicType << 5) | 0x10), // primary_pic_type + rbsp_trailing_bits
        };
    }

    /// <summary>
    /// Generates H.264 NAL data for a video frame.
    /// Returns SPS+PPS+IDR for keyframes, AUD+P-slice for inter frames.
    /// </summary>
    /// <param name="isKeyframe">True if this should be an IDR frame.</param>
    /// <param name="frameNum">Frame number within GOP.</param>
    /// <returns>NAL units with start codes.</returns>
    private byte[] GenerateH264FrameNals(bool isKeyframe, int frameNum)
    {
        using var ms = new MemoryStream(64);

        if (isKeyframe)
        {
            // AUD (I-frame)
            ms.Write(NalStartCode4);
            ms.Write(CreateH264Aud(0));

            // SPS
            ms.Write(NalStartCode4);
            ms.Write(CreateH264Sps());

            // PPS
            ms.Write(NalStartCode4);
            ms.Write(CreateH264Pps());

            // IDR slice
            ms.Write(NalStartCode3);
            ms.Write(CreateH264Idr());
        }
        else
        {
            // AUD (P-frame)
            ms.Write(NalStartCode4);
            ms.Write(CreateH264Aud(1));

            // P-slice
            ms.Write(NalStartCode3);
            ms.Write(CreateH264PSlice(frameNum));
        }

        return ms.ToArray();
    }

    /// <summary>
    /// MPEG-2 CRC-32 calculation (ISO/IEC 13818-1).
    /// Polynomial: 0x04C11DB7 (normal form).
    /// </summary>
    private static uint CalculateCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= (uint)b << 24;
            for (int i = 0; i < 8; i++)
            {
                if ((crc & 0x80000000) != 0)
                {
                    crc = (crc << 1) ^ 0x04C11DB7;
                }
                else
                {
                    crc <<= 1;
                }
            }
        }

        return crc;
    }
}
