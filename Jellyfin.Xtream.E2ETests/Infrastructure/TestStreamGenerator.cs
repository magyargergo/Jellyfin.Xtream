using System.Buffers;
using System.Buffers.Binary;

namespace Jellyfin.Xtream.E2ETests.Infrastructure;

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

    // PCR clock: 27MHz base, 300 extension divisor → 90kHz PCR base
    private const long PcrTicksPerSecond = 27_000_000L;
    private const long Pcr90KhzPerSecond = 90_000L;
    private const long PcrIntervalMs = 40; // PCR every 40ms per spec

    // Continuity counters (0-15)
    private readonly int[] _continuityCounters = new int[8192];
    private long _currentPcr90Khz;
    private long _currentPts90Khz;
    private long _packetIndex;
    private int _pcrPacketCounter;
    private readonly int _pcrIntervalPackets;
    private readonly long _initialPcrOffset;

    /// <summary>
    /// Initializes a new instance of the <see cref="TestStreamGenerator"/> class.
    /// </summary>
    /// <param name="bitrateKbps">Target bitrate in kbps (determines null packet insertion rate).</param>
    /// <param name="initialPcrOffset">Initial PCR offset in 90kHz ticks (for restamp testing).</param>
    public TestStreamGenerator(int bitrateKbps = 5000, long initialPcrOffset = 0)
    {
        _initialPcrOffset = initialPcrOffset;
        _currentPcr90Khz = initialPcrOffset;
        _currentPts90Khz = initialPcrOffset;

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
        _currentPts90Khz = _initialPcrOffset;
        _packetIndex = 0;
        _pcrPacketCounter = 0;
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

        // TS header with adaptation field + payload
        packet[0] = SyncByte;
        packet[1] = (byte)(0x40 | ((VideoPid >> 8) & 0x1F)); // PUSI=1
        packet[2] = (byte)(VideoPid & 0xFF);
        packet[3] = (byte)(0x30 | cc); // adaptation + payload

        // Adaptation field
        packet[4] = 7; // adaptation_field_length (1 flags + 6 PCR)
        packet[5] = 0x50; // PCR flag=1, random_access=1

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
        WritePesHeader(packet.Slice(pesOffset), 0xE0, true); // video stream_id, with PTS

        // Fill remaining with pseudo video data
        int dataStart = pesOffset + 14; // PES header is 14 bytes with PTS
        for (int i = dataStart; i < TsPacketSize; i++)
        {
            packet[i] = (byte)(i & 0xFF);
        }
    }

    private void WriteVideoPesPacket(Span<byte> packet)
    {
        packet.Clear();
        var cc = NextContinuityCounter(VideoPid);

        packet[0] = SyncByte;
        packet[1] = (byte)(0x40 | ((VideoPid >> 8) & 0x1F)); // PUSI=1
        packet[2] = (byte)(VideoPid & 0xFF);
        packet[3] = (byte)(0x10 | cc); // payload only

        // Advance PTS (assuming 30fps -> ~3003 ticks per frame at 90kHz)
        _currentPts90Khz += 3003;

        WritePesHeader(packet.Slice(4), 0xE0, true);

        // Fill with pseudo data
        for (int i = 18; i < TsPacketSize; i++)
        {
            packet[i] = (byte)(i & 0xFF);
        }
    }

    private void WriteAudioPesPacket(Span<byte> packet)
    {
        packet.Clear();
        var cc = NextContinuityCounter(AudioPid);

        packet[0] = SyncByte;
        packet[1] = (byte)(0x40 | ((AudioPid >> 8) & 0x1F)); // PUSI=1
        packet[2] = (byte)(AudioPid & 0xFF);
        packet[3] = (byte)(0x10 | cc); // payload only

        WritePesHeader(packet.Slice(4), 0xC0, true); // audio stream_id

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

    private void WritePesHeader(Span<byte> dest, byte streamId, bool includePts)
    {
        // PES start code: 00 00 01
        dest[0] = 0x00;
        dest[1] = 0x00;
        dest[2] = 0x01;
        dest[3] = streamId;

        if (includePts)
        {
            // PES packet length = 0 (unbounded for video)
            dest[4] = 0x00;
            dest[5] = 0x00;

            // PES header flags
            dest[6] = 0x80; // '10' marker bits
            dest[7] = 0x80; // PTS_DTS_flags = '10' (PTS only)
            dest[8] = 0x05; // PES_header_data_length = 5 bytes (PTS)

            // PTS (5 bytes, '0010' marker pattern)
            long pts = _currentPts90Khz;
            dest[9] = (byte)(0x21 | ((pts >> 29) & 0x0E)); // '0010' + PTS[32..30] + marker
            dest[10] = (byte)((pts >> 22) & 0xFF); // PTS[29..22]
            dest[11] = (byte)(0x01 | ((pts >> 14) & 0xFE)); // PTS[21..15] + marker
            dest[12] = (byte)((pts >> 7) & 0xFF); // PTS[14..7]
            dest[13] = (byte)(0x01 | ((pts << 1) & 0xFE)); // PTS[6..0] + marker
        }
        else
        {
            dest[4] = 0x00;
            dest[5] = 0x00;
            dest[6] = 0x80;
            dest[7] = 0x00; // no PTS/DTS
            dest[8] = 0x00; // header data length = 0
        }
    }

    private int NextContinuityCounter(int pid)
    {
        int cc = _continuityCounters[pid];
        _continuityCounters[pid] = (cc + 1) & 0x0F;
        return cc;
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
