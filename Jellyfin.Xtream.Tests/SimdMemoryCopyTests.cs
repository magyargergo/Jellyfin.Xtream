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
using System.Runtime.InteropServices;
using Jellyfin.Xtream.Service;
using Xunit;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Tests for <see cref="SimdMemoryCopy"/>.
/// Validates correctness across all SIMD paths and remainder handling.
/// </summary>
public sealed class SimdMemoryCopyTests
{
    #region Threshold Configuration

    /// <summary>
    /// Verifies SIMD threshold is a positive value.
    /// </summary>
    [Fact]
    public void SimdThreshold_IsPositive()
    {
        Assert.True(SimdMemoryCopy.SimdThreshold > 0);
    }

    /// <summary>
    /// Verifies prefetch distance is a positive value.
    /// </summary>
    [Fact]
    public void PrefetchDistance_IsPositive()
    {
        Assert.True(SimdMemoryCopy.PrefetchDistance > 0);
    }

    #endregion

    #region Copy Correctness

    /// <summary>
    /// Verifies copy correctness for small buffers (below SIMD threshold).
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(63)]
    public void Copy_SmallBuffers_CopiesCorrectly(int length)
    {
        AssertCopyCorrectness(length, useNonTemporal: false);
    }

    /// <summary>
    /// Verifies copy correctness for medium buffers (around SIMD threshold).
    /// </summary>
    [Theory]
    [InlineData(128)]
    [InlineData(256)]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(2048)]
    public void Copy_MediumBuffers_CopiesCorrectly(int length)
    {
        AssertCopyCorrectness(length, useNonTemporal: false);
    }

    /// <summary>
    /// Verifies copy correctness for large buffers (well above SIMD threshold).
    /// </summary>
    [Theory]
    [InlineData(4096)]
    [InlineData(8192)]
    [InlineData(65536)]
    public void Copy_LargeBuffers_CopiesCorrectly(int length)
    {
        AssertCopyCorrectness(length, useNonTemporal: false);
    }

    /// <summary>
    /// Verifies copy with non-temporal stores produces correct results.
    /// </summary>
    [Theory]
    [InlineData(512)]
    [InlineData(4096)]
    [InlineData(65536)]
    public void Copy_NonTemporal_CopiesCorrectly(int length)
    {
        AssertCopyCorrectness(length, useNonTemporal: true);
    }

    #endregion

    #region Remainder Handling

    /// <summary>
    /// Verifies remainder bytes are handled correctly for sizes that are
    /// not multiples of any SIMD vector width.
    /// </summary>
    [Theory]
    [InlineData(33)] // 32 + 1
    [InlineData(65)] // 64 + 1
    [InlineData(129)] // 128 + 1
    [InlineData(1000)] // Not a power of 2
    [InlineData(4097)] // 4096 + 1
    public void Copy_NonAlignedSizes_HandlesRemainderCorrectly(int length)
    {
        AssertCopyCorrectness(length, useNonTemporal: false);
    }

    #endregion

    #region Zero Length

    /// <summary>
    /// Verifies copy with zero length does not throw.
    /// </summary>
    [Fact]
    public void Copy_ZeroLength_DoesNotThrow()
    {
        var src = new byte[16];
        var dst = new byte[16];

        unsafe
        {
            fixed (
                byte* pSrc = src,
                    pDst = dst
            )
            {
                SimdMemoryCopy.Copy(pSrc, pDst, 0);
            }
        }

        // Destination should remain all zeros
        Assert.All(dst, b => Assert.Equal(0, b));
    }

    #endregion

    #region Data Pattern Verification

    /// <summary>
    /// Verifies all byte values (0x00-0xFF) are preserved through copy.
    /// </summary>
    [Fact]
    public void Copy_AllByteValues_PreservedCorrectly()
    {
        var length = 256;
        var src = new byte[length];
        var dst = new byte[length];

        for (var i = 0; i < length; i++)
        {
            src[i] = (byte)i;
        }

        unsafe
        {
            fixed (
                byte* pSrc = src,
                    pDst = dst
            )
            {
                SimdMemoryCopy.Copy(pSrc, pDst, length);
            }
        }

        Assert.Equal(src, dst);
    }

    /// <summary>
    /// Verifies copy does not write beyond the specified length.
    /// </summary>
    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    public void Copy_DoesNotWriteBeyondLength(int copyLength)
    {
        var totalSize = copyLength + 64;
        var src = new byte[totalSize];
        var dst = new byte[totalSize];
        byte sentinel = 0xAA;

        // Fill source with pattern
        for (var i = 0; i < totalSize; i++)
        {
            src[i] = (byte)(i & 0xFF);
        }

        // Fill destination sentinel area
        for (var i = copyLength; i < totalSize; i++)
        {
            dst[i] = sentinel;
        }

        unsafe
        {
            fixed (
                byte* pSrc = src,
                    pDst = dst
            )
            {
                SimdMemoryCopy.Copy(pSrc, pDst, copyLength);
            }
        }

        // Verify copied region
        for (var i = 0; i < copyLength; i++)
        {
            Assert.Equal(src[i], dst[i]);
        }

        // Verify sentinel region is untouched
        for (var i = copyLength; i < totalSize; i++)
        {
            Assert.Equal(sentinel, dst[i]);
        }
    }

    #endregion

    #region CopyRemainder While-Loop Fix

    /// <summary>
    /// Tests CopyRemainder while-loop fix for sizes that leave 16-31 byte remainders
    /// after SIMD vector processing. The bug was using <c>if (remaining >= 8)</c> instead
    /// of <c>while (remaining >= 8)</c>, causing remainders between 16-31 bytes to only
    /// process the first 8 bytes.
    /// </summary>
    [Theory]
    [InlineData(17)] // 1 byte after 16-byte SSE vector - exercises 8-byte while loop
    [InlineData(23)] // 7 bytes after 16-byte SSE vector
    [InlineData(24)] // 8 bytes after 16-byte SSE vector - exactly one 8-byte iteration
    [InlineData(47)] // 15 bytes after 32-byte AVX2 vector
    [InlineData(48)] // 16 bytes after 32-byte AVX2 vector - two 8-byte iterations
    [InlineData(55)] // 23 bytes after 32-byte AVX2 vector
    [InlineData(127)] // Tests remainder after multiple vectors
    [InlineData(188)] // MPEG-TS packet size - real-world remainder scenario
    [InlineData(255)] // Max single-byte value - stress remainder path
    [InlineData(376)] // 2x MPEG-TS packets - real-world streaming size
    [InlineData(1316)] // 7x MPEG-TS packets - typical streaming chunk
    public void Copy_RemainderSizes_AllBytesCorrectAfterWhileLoopFix(int length)
    {
        AssertCopyCorrectness(length, useNonTemporal: false);
    }

    /// <summary>
    /// Tests non-temporal store path with a large 512KB buffer to exercise
    /// the non-temporal code path and its remainder handling.
    /// </summary>
    [Fact]
    public void Copy_LargeNonTemporalTransfer_AllBytesCorrect()
    {
        const int size = 512 * 1024; // 512KB - forces non-temporal path
        AssertCopyCorrectness(size, useNonTemporal: true);
    }

    #endregion

    #region Helpers

    private static void AssertCopyCorrectness(int length, bool useNonTemporal)
    {
        // Use aligned allocation to ensure SIMD alignment requirements are met
        var src = GC.AllocateArray<byte>(length, pinned: true);
        var dst = GC.AllocateArray<byte>(length, pinned: true);

        // Fill source with recognizable pattern
        var rng = new Random(42);
        rng.NextBytes(src);

        unsafe
        {
            fixed (
                byte* pSrc = src,
                    pDst = dst
            )
            {
                SimdMemoryCopy.Copy(pSrc, pDst, length, useNonTemporal);
            }
        }

        Assert.Equal(src, dst);
    }

    #endregion
}
