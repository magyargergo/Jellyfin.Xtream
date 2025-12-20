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
using Jellyfin.Xtream.Service.MpegTs;
using Xunit;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Unit tests for ProgramInfo PMT interval monitoring and continuity counter validation.
/// </summary>
public class ProgramInfoTests
{
    /// <summary>
    /// Tests that PMT reception within 500ms does not count as a violation.
    /// </summary>
    [Fact]
    public void RecordPmtReceptionWithinIntervalNoViolation()
    {
        var program = new ProgramInfo(1, 256);

        // First reception
        long baseTicks = DateTime.UtcNow.Ticks;
        program.RecordPmtReception(baseTicks);

        // Second reception at 400ms - within limit
        long secondTicks = baseTicks + (400 * TimeSpan.TicksPerMillisecond);
        bool violation = program.RecordPmtReception(secondTicks);

        Assert.False(violation);
        Assert.Equal(0, program.PmtIntervalViolations);
    }

    /// <summary>
    /// Tests that PMT reception exceeding 500ms counts as a violation.
    /// </summary>
    [Fact]
    public void RecordPmtReceptionExceedsIntervalViolation()
    {
        var program = new ProgramInfo(1, 256);

        // First reception
        long baseTicks = DateTime.UtcNow.Ticks;
        program.RecordPmtReception(baseTicks);

        // Second reception at 600ms - exceeds 500ms limit
        long secondTicks = baseTicks + (600 * TimeSpan.TicksPerMillisecond);
        bool violation = program.RecordPmtReception(secondTicks);

        Assert.True(violation);
        Assert.Equal(1, program.PmtIntervalViolations);
    }

    /// <summary>
    /// Tests that multiple violations are counted correctly.
    /// </summary>
    [Fact]
    public void RecordPmtReceptionMultipleViolationsCountsCorrectly()
    {
        var program = new ProgramInfo(1, 256);

        long baseTicks = DateTime.UtcNow.Ticks;
        program.RecordPmtReception(baseTicks);

        // Simulate 3 violations
        for (int i = 1; i <= 3; i++)
        {
            long violationTicks = baseTicks + (i * 600 * TimeSpan.TicksPerMillisecond);
            program.RecordPmtReception(violationTicks);
        }

        Assert.Equal(3, program.PmtIntervalViolations);
    }

    /// <summary>
    /// Tests that first reception does not trigger a violation.
    /// </summary>
    [Fact]
    public void RecordPmtReceptionFirstReceptionNoViolation()
    {
        var program = new ProgramInfo(1, 256);

        long baseTicks = DateTime.UtcNow.Ticks;
        bool violation = program.RecordPmtReception(baseTicks);

        Assert.False(violation);
        Assert.Equal(0, program.PmtIntervalViolations);
    }

    /// <summary>
    /// Tests that exactly 500ms does not count as a violation.
    /// </summary>
    [Fact]
    public void RecordPmtReceptionExactlyAtLimitNoViolation()
    {
        var program = new ProgramInfo(1, 256);

        long baseTicks = DateTime.UtcNow.Ticks;
        program.RecordPmtReception(baseTicks);

        // Exactly at 500ms boundary
        long secondTicks = baseTicks + (500 * TimeSpan.TicksPerMillisecond);
        bool violation = program.RecordPmtReception(secondTicks);

        Assert.False(violation);
        Assert.Equal(0, program.PmtIntervalViolations);
    }

    /// <summary>
    /// Tests continuity counter validation for first packet on a PID.
    /// </summary>
    [Fact]
    public void ValidateContinuityCounterFirstPacketAlwaysValid()
    {
        var program = new ProgramInfo(1, 256);

        // First packet on PID 100 with any CC value is valid
        bool valid = program.ValidateContinuityCounter(100, 5, hasPayload: true);

        Assert.True(valid);
    }

    /// <summary>
    /// Tests continuity counter validation for sequential packets.
    /// </summary>
    [Fact]
    public void ValidateContinuityCounterSequentialPacketsValid()
    {
        var program = new ProgramInfo(1, 256);

        // Initialize with CC=0
        program.ValidateContinuityCounter(100, 0, hasPayload: true);

        // CC=1 should be valid
        bool valid = program.ValidateContinuityCounter(100, 1, hasPayload: true);

        Assert.True(valid);
    }

    /// <summary>
    /// Tests continuity counter wrap-around from 15 to 0.
    /// </summary>
    [Fact]
    public void ValidateContinuityCounterWrapAroundValid()
    {
        var program = new ProgramInfo(1, 256);

        // Set CC to 15
        program.ValidateContinuityCounter(100, 15, hasPayload: true);

        // CC=0 should be valid (wrap around)
        bool valid = program.ValidateContinuityCounter(100, 0, hasPayload: true);

        Assert.True(valid);
    }

    /// <summary>
    /// Tests continuity counter discontinuity detection.
    /// </summary>
    [Fact]
    public void ValidateContinuityCounterDiscontinuityInvalid()
    {
        var program = new ProgramInfo(1, 256);

        // Initialize with CC=0
        program.ValidateContinuityCounter(100, 0, hasPayload: true);

        // CC=5 should be invalid (expected 1)
        bool valid = program.ValidateContinuityCounter(100, 5, hasPayload: true);

        Assert.False(valid);
        Assert.Equal(1, program.GetPacketLossCount(100));
    }

    /// <summary>
    /// Tests that duplicate CC is valid for packets without payload.
    /// </summary>
    [Fact]
    public void ValidateContinuityCounterDuplicateCcNoPayloadValid()
    {
        var program = new ProgramInfo(1, 256);

        // Initialize with CC=5
        program.ValidateContinuityCounter(100, 5, hasPayload: true);

        // Same CC without payload (adaptation-only) should be valid
        bool valid = program.ValidateContinuityCounter(100, 5, hasPayload: false);

        Assert.True(valid);
    }

    /// <summary>
    /// Tests packet loss count aggregation across PIDs.
    /// </summary>
    [Fact]
    public void GetTotalPacketLossMultiplePidsAggregates()
    {
        var program = new ProgramInfo(1, 256);

        // Create discontinuities on two different PIDs
        program.ValidateContinuityCounter(100, 0, hasPayload: true);
        program.ValidateContinuityCounter(100, 5, hasPayload: true); // Loss on PID 100

        program.ValidateContinuityCounter(200, 0, hasPayload: true);
        program.ValidateContinuityCounter(200, 10, hasPayload: true); // Loss on PID 200

        Assert.Equal(2, program.GetTotalPacketLoss());
    }

    /// <summary>
    /// Tests that Reset clears continuity counters.
    /// </summary>
    [Fact]
    public void ResetClearsContinuityCounters()
    {
        var program = new ProgramInfo(1, 256);

        // Create some state
        program.ValidateContinuityCounter(100, 0, hasPayload: true);
        program.ValidateContinuityCounter(100, 5, hasPayload: true);

        Assert.Equal(1, program.GetTotalPacketLoss());

        // Reset
        program.Reset();

        // State should be cleared
        Assert.Equal(0, program.GetTotalPacketLoss());
    }

    /// <summary>
    /// Tests keyframe count tracking.
    /// </summary>
    [Fact]
    public void KeyframeCountIncrementDecrementWorks()
    {
        var program = new ProgramInfo(1, 256);

        Assert.Equal(0, program.GetKeyframeCount());

        program.IncrementKeyframeCount();
        Assert.Equal(1, program.GetKeyframeCount());

        program.IncrementKeyframeCount();
        Assert.Equal(2, program.GetKeyframeCount());

        program.DecrementKeyframeCount();
        Assert.Equal(1, program.GetKeyframeCount());
    }
}
