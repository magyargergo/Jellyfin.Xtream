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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace Jellyfin.Xtream.Benchmarks;

/// <summary>
/// Benchmarks comparing different memory copy strategies for C#/native interop.
/// Measures performance of Marshal.Copy vs Unsafe.CopyBlockUnaligned for FFmpeg data paths.
/// </summary>
[SimpleJob(RuntimeMoniker.Net80)]
[MemoryDiagnoser]
public class NativeInteropBenchmarks
{
    private byte[] _sourceBuffer = null!;
    private byte[] _destBuffer = null!;
    private IntPtr _nativeBuffer;
    private const int BufferSize = 32 * 1024; // 32KB - typical AVIO buffer size
    private const int LargeBufferSize = 256 * 1024; // 256KB for large buffer tests

    /// <summary>
    /// Setup test data for benchmarks.
    /// </summary>
    [GlobalSetup]
    public unsafe void Setup()
    {
        // Allocate buffers large enough for all tests
        _sourceBuffer = new byte[LargeBufferSize];
        _destBuffer = new byte[LargeBufferSize];
        Random.Shared.NextBytes(_sourceBuffer);

        // Allocate native buffer large enough for all tests
        _nativeBuffer = Marshal.AllocHGlobal(LargeBufferSize);

        // Pre-fill native buffer for native-to-managed tests
        Marshal.Copy(_sourceBuffer, 0, _nativeBuffer, LargeBufferSize);
    }

    /// <summary>
    /// Cleanup native memory.
    /// </summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        Marshal.FreeHGlobal(_nativeBuffer);
    }

    /// <summary>
    /// Baseline: Marshal.Copy from managed to native buffer.
    /// This was the previous implementation in FFmpegStreamDemuxer.ReadFromBuffer.
    /// </summary>
    [Benchmark(Baseline = true)]
    public void MarshalCopy_ManagedToNative()
    {
        Marshal.Copy(_sourceBuffer, 0, _nativeBuffer, BufferSize);
    }

    /// <summary>
    /// Optimized: Unsafe.CopyBlockUnaligned with fixed statement.
    /// Current implementation in FFmpegStreamDemuxer.ReadFromBuffer.
    /// </summary>
    [Benchmark]
    public unsafe void UnsafeCopyBlock_ManagedToNative()
    {
        fixed (byte* srcPtr = &_sourceBuffer[0])
        {
            Unsafe.CopyBlockUnaligned((byte*)_nativeBuffer, srcPtr, (uint)BufferSize);
        }
    }

    /// <summary>
    /// Alternative: Span-based copy using MemoryMarshal.
    /// For comparison with Span APIs.
    /// </summary>
    [Benchmark]
    public unsafe void SpanCopy_ManagedToNative()
    {
        var destSpan = new Span<byte>((byte*)_nativeBuffer, BufferSize);
        _sourceBuffer.AsSpan(0, BufferSize).CopyTo(destSpan);
    }

    /// <summary>
    /// Simulates wrap-around copy (two separate copies) with Marshal.Copy.
    /// Common pattern in circular buffers.
    /// </summary>
    [Benchmark]
    public void MarshalCopy_WrapAround()
    {
        int firstCopy = BufferSize / 2;
        int secondCopy = BufferSize - firstCopy;

        Marshal.Copy(_sourceBuffer, 0, _nativeBuffer, firstCopy);
        Marshal.Copy(_sourceBuffer, firstCopy, _nativeBuffer + firstCopy, secondCopy);
    }

    /// <summary>
    /// Simulates wrap-around copy with Unsafe.CopyBlockUnaligned.
    /// Current optimized implementation for circular buffer reads.
    /// </summary>
    [Benchmark]
    public unsafe void UnsafeCopyBlock_WrapAround()
    {
        int firstCopy = BufferSize / 2;
        int secondCopy = BufferSize - firstCopy;

        byte* destPtr = (byte*)_nativeBuffer;

        fixed (byte* srcPtr = &_sourceBuffer[0])
        {
            Unsafe.CopyBlockUnaligned(destPtr, srcPtr, (uint)firstCopy);
        }

        fixed (byte* srcPtr = &_sourceBuffer[firstCopy])
        {
            Unsafe.CopyBlockUnaligned(destPtr + firstCopy, srcPtr, (uint)secondCopy);
        }
    }

    /// <summary>
    /// Small buffer copy (188 bytes - TS packet size) with Marshal.Copy.
    /// </summary>
    [Benchmark]
    public void MarshalCopy_SmallPacket()
    {
        Marshal.Copy(_sourceBuffer, 0, _nativeBuffer, 188);
    }

    /// <summary>
    /// Small buffer copy (188 bytes - TS packet size) with Unsafe.CopyBlockUnaligned.
    /// </summary>
    [Benchmark]
    public unsafe void UnsafeCopyBlock_SmallPacket()
    {
        fixed (byte* srcPtr = &_sourceBuffer[0])
        {
            Unsafe.CopyBlockUnaligned((byte*)_nativeBuffer, srcPtr, 188);
        }
    }

    /// <summary>
    /// Very small buffer copy (64 bytes) with Marshal.Copy.
    /// Tests overhead for tiny copies.
    /// </summary>
    [Benchmark]
    public void MarshalCopy_TinyBuffer()
    {
        Marshal.Copy(_sourceBuffer, 0, _nativeBuffer, 64);
    }

    /// <summary>
    /// Very small buffer copy (64 bytes) with Unsafe.CopyBlockUnaligned.
    /// Tests overhead for tiny copies.
    /// </summary>
    [Benchmark]
    public unsafe void UnsafeCopyBlock_TinyBuffer()
    {
        fixed (byte* srcPtr = &_sourceBuffer[0])
        {
            Unsafe.CopyBlockUnaligned((byte*)_nativeBuffer, srcPtr, 64);
        }
    }

    // ========== LARGER BUFFER SIZES ==========

    /// <summary>
    /// Large buffer copy (64KB) with Marshal.Copy.
    /// Tests scaling with larger buffers.
    /// </summary>
    [Benchmark]
    public void MarshalCopy_64KB()
    {
        Marshal.Copy(_sourceBuffer, 0, _nativeBuffer, 64 * 1024);
    }

    /// <summary>
    /// Large buffer copy (64KB) with Unsafe.CopyBlockUnaligned.
    /// </summary>
    [Benchmark]
    public unsafe void UnsafeCopyBlock_64KB()
    {
        fixed (byte* srcPtr = &_sourceBuffer[0])
        {
            Unsafe.CopyBlockUnaligned((byte*)_nativeBuffer, srcPtr, 64 * 1024);
        }
    }

    /// <summary>
    /// Large buffer copy (128KB) with Marshal.Copy.
    /// </summary>
    [Benchmark]
    public void MarshalCopy_128KB()
    {
        Marshal.Copy(_sourceBuffer, 0, _nativeBuffer, 128 * 1024);
    }

    /// <summary>
    /// Large buffer copy (128KB) with Unsafe.CopyBlockUnaligned.
    /// </summary>
    [Benchmark]
    public unsafe void UnsafeCopyBlock_128KB()
    {
        fixed (byte* srcPtr = &_sourceBuffer[0])
        {
            Unsafe.CopyBlockUnaligned((byte*)_nativeBuffer, srcPtr, 128 * 1024);
        }
    }

    /// <summary>
    /// Very large buffer copy (256KB) with Marshal.Copy.
    /// Tests performance at larger scales.
    /// </summary>
    [Benchmark]
    public void MarshalCopy_256KB()
    {
        Marshal.Copy(_sourceBuffer, 0, _nativeBuffer, 256 * 1024);
    }

    /// <summary>
    /// Very large buffer copy (256KB) with Unsafe.CopyBlockUnaligned.
    /// </summary>
    [Benchmark]
    public unsafe void UnsafeCopyBlock_256KB()
    {
        fixed (byte* srcPtr = &_sourceBuffer[0])
        {
            Unsafe.CopyBlockUnaligned((byte*)_nativeBuffer, srcPtr, 256 * 1024);
        }
    }

    // ========== NATIVE TO MANAGED (REVERSE DIRECTION) ==========

    /// <summary>
    /// Copy from native to managed buffer with Marshal.Copy.
    /// This is the read-from-FFmpeg direction.
    /// </summary>
    [Benchmark]
    public void MarshalCopy_NativeToManaged()
    {
        Marshal.Copy(_nativeBuffer, _destBuffer, 0, BufferSize);
    }

    /// <summary>
    /// Copy from native to managed buffer with Unsafe.CopyBlockUnaligned.
    /// </summary>
    [Benchmark]
    public unsafe void UnsafeCopyBlock_NativeToManaged()
    {
        fixed (byte* destPtr = &_destBuffer[0])
        {
            Unsafe.CopyBlockUnaligned(destPtr, (byte*)_nativeBuffer, (uint)BufferSize);
        }
    }

    /// <summary>
    /// Copy from native to managed buffer with Span.
    /// </summary>
    [Benchmark]
    public unsafe void SpanCopy_NativeToManaged()
    {
        var srcSpan = new ReadOnlySpan<byte>((byte*)_nativeBuffer, BufferSize);
        srcSpan.CopyTo(_destBuffer);
    }

    // ========== REPEATED SMALL COPIES (STREAMING SIMULATION) ==========

    /// <summary>
    /// Repeated TS packet copies (100x 188 bytes) with Marshal.Copy.
    /// Simulates processing multiple TS packets in a tight loop.
    /// </summary>
    [Benchmark]
    public void MarshalCopy_100TsPackets()
    {
        const int packetSize = 188;
        for (int i = 0; i < 100; i++)
        {
            int offset = (i * packetSize) % (BufferSize - packetSize);
            Marshal.Copy(_sourceBuffer, offset, _nativeBuffer, packetSize);
        }
    }

    /// <summary>
    /// Repeated TS packet copies (100x 188 bytes) with Unsafe.CopyBlockUnaligned.
    /// </summary>
    [Benchmark]
    public unsafe void UnsafeCopyBlock_100TsPackets()
    {
        const int packetSize = 188;
        byte* destPtr = (byte*)_nativeBuffer;
        for (int i = 0; i < 100; i++)
        {
            int offset = (i * packetSize) % (BufferSize - packetSize);
            fixed (byte* srcPtr = &_sourceBuffer[offset])
            {
                Unsafe.CopyBlockUnaligned(destPtr, srcPtr, packetSize);
            }
        }
    }

    /// <summary>
    /// Repeated small buffer copies (100x 4KB) with Marshal.Copy.
    /// Simulates typical read chunk sizes.
    /// </summary>
    [Benchmark]
    public void MarshalCopy_100x4KB()
    {
        const int chunkSize = 4 * 1024;
        for (int i = 0; i < 100; i++)
        {
            int offset = (i * chunkSize) % (BufferSize - chunkSize);
            Marshal.Copy(_sourceBuffer, offset, _nativeBuffer, chunkSize);
        }
    }

    /// <summary>
    /// Repeated small buffer copies (100x 4KB) with Unsafe.CopyBlockUnaligned.
    /// </summary>
    [Benchmark]
    public unsafe void UnsafeCopyBlock_100x4KB()
    {
        const int chunkSize = 4 * 1024;
        byte* destPtr = (byte*)_nativeBuffer;
        for (int i = 0; i < 100; i++)
        {
            int offset = (i * chunkSize) % (BufferSize - chunkSize);
            fixed (byte* srcPtr = &_sourceBuffer[offset])
            {
                Unsafe.CopyBlockUnaligned(destPtr, srcPtr, chunkSize);
            }
        }
    }

    // ========== ASYMMETRIC WRAP-AROUND (REALISTIC CIRCULAR BUFFER) ==========

    /// <summary>
    /// Wrap-around with 75%/25% split using Marshal.Copy.
    /// Common scenario when buffer is mostly full.
    /// </summary>
    [Benchmark]
    public void MarshalCopy_WrapAround_75_25()
    {
        int firstCopy = (BufferSize * 3) / 4;
        int secondCopy = BufferSize - firstCopy;

        Marshal.Copy(_sourceBuffer, 0, _nativeBuffer, firstCopy);
        Marshal.Copy(_sourceBuffer, firstCopy, _nativeBuffer + firstCopy, secondCopy);
    }

    /// <summary>
    /// Wrap-around with 75%/25% split using Unsafe.CopyBlockUnaligned.
    /// </summary>
    [Benchmark]
    public unsafe void UnsafeCopyBlock_WrapAround_75_25()
    {
        int firstCopy = (BufferSize * 3) / 4;
        int secondCopy = BufferSize - firstCopy;

        byte* destPtr = (byte*)_nativeBuffer;

        fixed (byte* srcPtr = &_sourceBuffer[0])
        {
            Unsafe.CopyBlockUnaligned(destPtr, srcPtr, (uint)firstCopy);
        }

        fixed (byte* srcPtr = &_sourceBuffer[firstCopy])
        {
            Unsafe.CopyBlockUnaligned(destPtr + firstCopy, srcPtr, (uint)secondCopy);
        }
    }

    /// <summary>
    /// Small wrap-around (2x 94 bytes = 1 TS packet split) with Marshal.Copy.
    /// Edge case when TS packet crosses buffer boundary.
    /// </summary>
    [Benchmark]
    public void MarshalCopy_TsPacketSplit()
    {
        const int halfPacket = 94;
        Marshal.Copy(_sourceBuffer, 0, _nativeBuffer, halfPacket);
        Marshal.Copy(_sourceBuffer, halfPacket, _nativeBuffer + halfPacket, halfPacket);
    }

    /// <summary>
    /// Small wrap-around (2x 94 bytes = 1 TS packet split) with Unsafe.CopyBlockUnaligned.
    /// </summary>
    [Benchmark]
    public unsafe void UnsafeCopyBlock_TsPacketSplit()
    {
        const int halfPacket = 94;
        byte* destPtr = (byte*)_nativeBuffer;

        fixed (byte* srcPtr = &_sourceBuffer[0])
        {
            Unsafe.CopyBlockUnaligned(destPtr, srcPtr, halfPacket);
        }

        fixed (byte* srcPtr = &_sourceBuffer[halfPacket])
        {
            Unsafe.CopyBlockUnaligned(destPtr + halfPacket, srcPtr, halfPacket);
        }
    }

    // ========== THROUGHPUT BENCHMARKS ==========

    /// <summary>
    /// Throughput test: copy 1MB total in 32KB chunks with Marshal.Copy.
    /// Measures sustained copy performance.
    /// </summary>
    [Benchmark]
    public void MarshalCopy_Throughput_1MB()
    {
        const int totalBytes = 1024 * 1024;
        const int chunkSize = 32 * 1024;
        const int iterations = totalBytes / chunkSize;

        for (int i = 0; i < iterations; i++)
        {
            Marshal.Copy(_sourceBuffer, 0, _nativeBuffer, chunkSize);
        }
    }

    /// <summary>
    /// Throughput test: copy 1MB total in 32KB chunks with Unsafe.CopyBlockUnaligned.
    /// </summary>
    [Benchmark]
    public unsafe void UnsafeCopyBlock_Throughput_1MB()
    {
        const int totalBytes = 1024 * 1024;
        const int chunkSize = 32 * 1024;
        const int iterations = totalBytes / chunkSize;

        byte* destPtr = (byte*)_nativeBuffer;
        for (int i = 0; i < iterations; i++)
        {
            fixed (byte* srcPtr = &_sourceBuffer[0])
            {
                Unsafe.CopyBlockUnaligned(destPtr, srcPtr, chunkSize);
            }
        }
    }

    /// <summary>
    /// Throughput test: copy 1MB total in 32KB chunks with Span.
    /// </summary>
    [Benchmark]
    public unsafe void SpanCopy_Throughput_1MB()
    {
        const int totalBytes = 1024 * 1024;
        const int chunkSize = 32 * 1024;
        const int iterations = totalBytes / chunkSize;

        var destSpan = new Span<byte>((byte*)_nativeBuffer, chunkSize);
        var srcSpan = _sourceBuffer.AsSpan(0, chunkSize);

        for (int i = 0; i < iterations; i++)
        {
            srcSpan.CopyTo(destSpan);
        }
    }
}
