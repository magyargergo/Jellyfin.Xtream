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
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Jellyfin.Xtream.Service.MpegTs;

namespace Jellyfin.Xtream.Benchmarks;

/// <summary>
/// Benchmarks for MPEG-TS parsing components: PesParser, RingBuffer, TimestampTracker.
/// Measures performance of A/V sync detection infrastructure.
/// </summary>
[SimpleJob(RuntimeMoniker.Net80)]
[MemoryDiagnoser]
public class MpegTsParserBenchmarks
{
    private byte[] _validPesPacket = null!;
    private byte[] _videoPacketWithPts = null!;
    private byte[] _audioPacketWithPts = null!;
    private byte[] _invalidPacket = null!;
    private RingBuffer<StreamTimestamp> _ringBuffer = null!;
    private TimestampTracker _timestampTracker = null!;
    private AudioStreamInfo _audioStreamInfo = null!;

    /// <summary>
    /// Setup test data for benchmarks.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        // Valid PES packet with PTS (video stream ID 0xE0)
        _videoPacketWithPts = CreatePesPacketWithPts(0xE0, 90000 * 10); // 10 seconds

        // Valid PES packet with PTS (audio stream ID 0xC0)
        _audioPacketWithPts = CreatePesPacketWithPts(0xC0, 90000 * 10);

        // Valid PES packet structure
        _validPesPacket = _videoPacketWithPts;

        // Invalid packet (no PES start code)
        _invalidPacket = new byte[188];
        Random.Shared.NextBytes(_invalidPacket);

        // Pre-populated ring buffer
        _ringBuffer = new RingBuffer<StreamTimestamp>(50);
        for (int i = 0; i < 50; i++)
        {
            _ringBuffer.Add(new StreamTimestamp(90000 * i, i * 188));
        }

        // Pre-populated timestamp tracker
        _timestampTracker = new TimestampTracker();
        for (int i = 0; i < 100; i++)
        {
            _timestampTracker.RecordVideoPts(90000 * i, i * 188);
            _timestampTracker.RecordAudioPts((90000 * i) + 1000, (i * 188) + 94);
        }

        // Pre-populated audio stream info
        _audioStreamInfo = new AudioStreamInfo { Pid = 257, StreamType = 0x0F };
        for (int i = 0; i < 100; i++)
        {
            _audioStreamInfo.RecordFrame(i * 188, 90000 * i);
        }
    }

    /// <summary>
    /// Benchmark: Extract PTS from valid video PES packet.
    /// </summary>
    /// <returns></returns>
    [Benchmark(Description = "PesParser.TryExtractPts (valid video)")]
    public bool PesParser_TryExtractPts_ValidVideo()
    {
        return PesParser.TryExtractPts(_videoPacketWithPts, out _);
    }

    /// <summary>
    /// Benchmark: Extract PTS from valid audio PES packet.
    /// </summary>
    /// <returns></returns>
    [Benchmark(Description = "PesParser.TryExtractPts (valid audio)")]
    public bool PesParser_TryExtractPts_ValidAudio()
    {
        return PesParser.TryExtractPts(_audioPacketWithPts, out _);
    }

    /// <summary>
    /// Benchmark: Reject invalid packet (fast path).
    /// </summary>
    /// <returns></returns>
    [Benchmark(Description = "PesParser.TryExtractPts (invalid - fast reject)")]
    public bool PesParser_TryExtractPts_Invalid()
    {
        return PesParser.TryExtractPts(_invalidPacket, out _);
    }

    /// <summary>
    /// Benchmark: Extract both PTS and DTS.
    /// </summary>
    /// <returns></returns>
    [Benchmark(Description = "PesParser.TryExtractTimestamps")]
    public bool PesParser_TryExtractTimestamps()
    {
        return PesParser.TryExtractTimestamps(_videoPacketWithPts, out _, out _);
    }

    /// <summary>
    /// Benchmark: Stream type classification.
    /// </summary>
    /// <returns></returns>
    [Benchmark(Description = "PesParser.IsVideoStream")]
    public bool PesParser_IsVideoStream()
    {
        return PesParser.IsVideoStream(0xE0);
    }

    /// <summary>
    /// Benchmark: Get PES header length.
    /// </summary>
    /// <returns></returns>
    [Benchmark(Description = "PesParser.GetPesHeaderLength")]
    public int PesParser_GetPesHeaderLength()
    {
        return PesParser.GetPesHeaderLength(_videoPacketWithPts);
    }

    /// <summary>
    /// Benchmark: Add item to ring buffer (overwrites when full).
    /// </summary>
    [Benchmark(Description = "RingBuffer.Add")]
    public void RingBuffer_Add()
    {
        _ringBuffer.Add(new StreamTimestamp(12345678, 9999));
    }

    /// <summary>
    /// Benchmark: Iterate over all items in ring buffer.
    /// </summary>
    /// <returns></returns>
    [Benchmark(Description = "RingBuffer.Enumerate (50 items)")]
    public int RingBuffer_Enumerate()
    {
        int count = 0;
        foreach (var item in _ringBuffer)
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Benchmark: Get latest item from ring buffer.
    /// </summary>
    /// <returns></returns>
    [Benchmark(Description = "RingBuffer.TryGetLatest")]
    public bool RingBuffer_TryGetLatest()
    {
        return _ringBuffer.TryGetLatest(out _);
    }

    /// <summary>
    /// Benchmark: Add + enumerate cycle (realistic usage).
    /// </summary>
    /// <returns></returns>
    [Benchmark(Description = "RingBuffer Add+Enumerate cycle")]
    public int RingBuffer_AddAndEnumerate()
    {
        var buffer = new RingBuffer<StreamTimestamp>(50);
        for (int i = 0; i < 100; i++)
        {
            buffer.Add(new StreamTimestamp(i * 90000, i * 188));
        }

        int count = 0;
        foreach (var item in buffer)
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Benchmark: Record video PTS (includes sync status update every 8 samples).
    /// </summary>
    [Benchmark(Description = "TimestampTracker.RecordVideoPts")]
    public void TimestampTracker_RecordVideoPts()
    {
        _timestampTracker.RecordVideoPts(90000 * 500, 500 * 188);
    }

    /// <summary>
    /// Benchmark: Record audio PTS.
    /// </summary>
    [Benchmark(Description = "TimestampTracker.RecordAudioPts")]
    public void TimestampTracker_RecordAudioPts()
    {
        _timestampTracker.RecordAudioPts((90000 * 500) + 1000, (500 * 188) + 94);
    }

    /// <summary>
    /// Benchmark: Find best sync point in offset range.
    /// </summary>
    /// <returns></returns>
    [Benchmark(Description = "TimestampTracker.FindBestSyncPoint")]
    public SyncPoint? TimestampTracker_FindBestSyncPoint()
    {
        return _timestampTracker.FindBestSyncPoint(0, 10000);
    }

    /// <summary>
    /// Benchmark: Get average drift calculation.
    /// </summary>
    /// <returns></returns>
    [Benchmark(Description = "TimestampTracker.GetAverageDriftMs")]
    public double TimestampTracker_GetAverageDriftMs()
    {
        return _timestampTracker.GetAverageDriftMs();
    }

    /// <summary>
    /// Benchmark: Current drift property access.
    /// </summary>
    /// <returns></returns>
    [Benchmark(Description = "TimestampTracker.CurrentDriftMs")]
    public double TimestampTracker_CurrentDriftMs()
    {
        return _timestampTracker.CurrentDriftMs;
    }

    /// <summary>
    /// Benchmark: Full recording cycle (100 video + 100 audio samples).
    /// </summary>
    [Benchmark(Description = "TimestampTracker full cycle (200 samples)")]
    public void TimestampTracker_FullCycle()
    {
        var tracker = new TimestampTracker();
        for (int i = 0; i < 100; i++)
        {
            tracker.RecordVideoPts(90000 * i, i * 188);
            tracker.RecordAudioPts((90000 * i) + 500, (i * 188) + 94);
        }
    }

    /// <summary>
    /// Benchmark: Record audio frame.
    /// </summary>
    [Benchmark(Description = "AudioStreamInfo.RecordFrame")]
    public void AudioStreamInfo_RecordFrame()
    {
        _audioStreamInfo.RecordFrame(999999, 90000 * 999);
    }

    /// <summary>
    /// Benchmark: Find nearest audio frame to offset.
    /// </summary>
    /// <returns></returns>
    [Benchmark(Description = "AudioStreamInfo.FindNearestFrame")]
    public AudioFrameInfo? AudioStreamInfo_FindNearestFrame()
    {
        return _audioStreamInfo.FindNearestFrame(5000);
    }

    /// <summary>
    /// Benchmark: Classify stream type (O(1) lookup table).
    /// </summary>
    /// <returns></returns>
    [Benchmark(Description = "StreamTypeClassifier.GetCategory")]
    public StreamCategory StreamTypeClassifier_GetCategory()
    {
        return StreamTypeClassifier.GetCategory(0x1B); // H.264
    }

    /// <summary>
    /// Benchmark: Get audio codec (O(1) lookup table).
    /// </summary>
    /// <returns></returns>
    [Benchmark(Description = "StreamTypeClassifier.GetAudioCodec")]
    public AudioCodec StreamTypeClassifier_GetAudioCodec()
    {
        return StreamTypeClassifier.GetAudioCodec(0x0F); // AAC
    }

    /// <summary>
    /// Benchmark: Calculate timestamp difference (with wrap-around handling).
    /// </summary>
    /// <returns></returns>
    [Benchmark(Description = "StreamTimestamp.DifferenceInMsFrom")]
    public double StreamTimestamp_DifferenceInMs()
    {
        var ts1 = new StreamTimestamp(90000 * 100, 0);
        var ts2 = new StreamTimestamp(90000 * 105, 188);
        return ts1.DifferenceInMsFrom(ts2);
    }

    /// <summary>
    /// Benchmark: Timestamp wrap-around detection.
    /// </summary>
    /// <returns></returns>
    [Benchmark(Description = "StreamTimestamp wrap-around difference")]
    public double StreamTimestamp_WrapAround()
    {
        // Near 33-bit wrap (about 26.5 hours)
        var ts1 = new StreamTimestamp((1L << 33) - 90000, 0);
        var ts2 = new StreamTimestamp(90000, 188);
        return ts1.DifferenceInMsFrom(ts2);
    }

    private static byte[] CreatePesPacketWithPts(byte streamId, long pts)
    {
        var packet = new byte[20];

        // PES start code prefix (00 00 01)
        packet[0] = 0x00;
        packet[1] = 0x00;
        packet[2] = 0x01;

        // Stream ID
        packet[3] = streamId;

        // PES packet length (high byte, low byte) - 0 means unbounded for video
        packet[4] = 0x00;
        packet[5] = 0x00;

        // Optional PES header
        packet[6] = 0x80; // marker bits
        packet[7] = 0x80; // PTS flag set, no DTS
        packet[8] = 0x05; // PES header data length (5 bytes for PTS)

        // PTS (5 bytes, 33-bit value)
        // Format: 0010 [32..30] 1 [29..15] 1 [14..0] 1
        packet[9] = (byte)(0x21 | ((pts >> 29) & 0x0E));
        packet[10] = (byte)((pts >> 22) & 0xFF);
        packet[11] = (byte)(0x01 | ((pts >> 14) & 0xFE));
        packet[12] = (byte)((pts >> 7) & 0xFF);
        packet[13] = (byte)(0x01 | ((pts << 1) & 0xFE));

        // Payload follows...
        return packet;
    }
}
