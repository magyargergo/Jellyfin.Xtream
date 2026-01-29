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

using Jellyfin.Xtream.Service.Resilience;
using Jellyfin.Xtream.Service.Streaming.Native;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Tests for StreamingOutcomeRecorder to verify correct recording of streaming outcomes.
/// </summary>
public sealed class StreamingOutcomeRecorderTests
{
    private readonly ProviderHealthScorer _healthScorer;
    private readonly StreamingOutcomeRecorder _recorder;

    public StreamingOutcomeRecorderTests()
    {
        _healthScorer = new ProviderHealthScorer();
        _recorder = new StreamingOutcomeRecorder(_healthScorer, NullLogger<StreamingOutcomeRecorder>.Instance);
    }

    [Fact]
    public void RecordSuccess_UpdatesHealthScore()
    {
        // Act
        _recorder.RecordSuccess("provider1", qualityScore: 90, connectionTimeMs: 500);

        // Assert
        var score = _healthScorer.GetScore("provider1");
        Assert.True(score > 50, "Score should be above neutral after success");
    }

    [Fact]
    public void RecordFailure_DecreasesHealthScore()
    {
        // Arrange - first record a success to establish baseline
        _recorder.RecordSuccess("provider1", qualityScore: 80, connectionTimeMs: 500);
        var initialScore = _healthScorer.GetScore("provider1");

        // Act
        _recorder.RecordFailure("provider1", FailureType.ConnectionTimeout);

        // Assert
        var newScore = _healthScorer.GetScore("provider1");
        Assert.True(newScore < initialScore, "Score should decrease after failure");
    }

    [Fact]
    public void RecordFromEvent_DataReceived_RecordsSuccess()
    {
        // Act
        _recorder.RecordFromEvent(
            "provider1",
            StreamerEvent.DataReceived,
            detail: 0,
            connectionTimeMs: 300,
            qualityScore: 85
        );

        // Assert
        var status = _healthScorer.GetStatus("provider1");
        Assert.Equal(1, status.SampleCount);
        Assert.True(status.Score > 50);
    }

    [Fact]
    public void RecordFromEvent_QualityDegraded_RecordsFailure()
    {
        // Arrange - establish baseline
        _recorder.RecordSuccess("provider1", qualityScore: 80, connectionTimeMs: 500);

        // Act
        _recorder.RecordFromEvent("provider1", StreamerEvent.QualityDegraded, detail: 0);

        // Assert
        var status = _healthScorer.GetStatus("provider1");
        Assert.Equal(1, status.ConsecutiveFailures);
    }

    [Fact]
    public void RecordFromEvent_Stalled_RecordsDataStallFailure()
    {
        // Act
        _recorder.RecordFromEvent("provider1", StreamerEvent.Stalled, detail: 0);

        // Assert
        var status = _healthScorer.GetStatus("provider1");
        Assert.Equal(1, status.ConsecutiveFailures);
    }

    [Fact]
    public void RecordFromEvent_Error_MapsHttpErrorCorrectly()
    {
        // Act - CURLE_HTTP_RETURNED_ERROR = 22
        _recorder.RecordFromEvent("provider1", StreamerEvent.Error, detail: 22);

        // Assert
        var status = _healthScorer.GetStatus("provider1");
        Assert.Equal(1, status.ConsecutiveFailures);
    }

    [Fact]
    public void RecordFromEvent_Error_MapsTimeoutCorrectly()
    {
        // Act - CURLE_OPERATION_TIMEDOUT = 28
        _recorder.RecordFromEvent("provider1", StreamerEvent.Error, detail: 28);

        // Assert
        var status = _healthScorer.GetStatus("provider1");
        Assert.Equal(1, status.ConsecutiveFailures);
    }

    [Fact]
    public void RecordFromEvent_Connected_DoesNotRecordYet()
    {
        // Act - Connected event should not record until data is received
        _recorder.RecordFromEvent("provider1", StreamerEvent.Connected, detail: 0);

        // Assert
        var status = _healthScorer.GetStatus("provider1");
        Assert.Equal(0, status.SampleCount);
    }

    [Fact]
    public void MapHttpStatusToFailureType_401_ReturnsAuthenticationFailed()
    {
        var result = StreamingOutcomeRecorder.MapHttpStatusToFailureType(401);
        Assert.Equal(FailureType.AuthenticationFailed, result);
    }

    [Fact]
    public void MapHttpStatusToFailureType_429_ReturnsCapacityExceeded()
    {
        var result = StreamingOutcomeRecorder.MapHttpStatusToFailureType(429);
        Assert.Equal(FailureType.CapacityExceeded, result);
    }

    [Fact]
    public void MapHttpStatusToFailureType_500_ReturnsHttpError()
    {
        var result = StreamingOutcomeRecorder.MapHttpStatusToFailureType(500);
        Assert.Equal(FailureType.HttpError, result);
    }

    [Fact]
    public void MultipleProviders_IndependentScores()
    {
        // Arrange & Act
        _recorder.RecordSuccess("provider1", qualityScore: 95, connectionTimeMs: 200);
        _recorder.RecordSuccess("provider2", qualityScore: 60, connectionTimeMs: 2000);
        _recorder.RecordFailure("provider3", FailureType.ConnectionTimeout);

        // Assert
        var score1 = _healthScorer.GetScore("provider1");
        var score2 = _healthScorer.GetScore("provider2");
        var score3 = _healthScorer.GetScore("provider3");

        Assert.True(score1 > score2, "Provider 1 should have higher score than provider 2");
        Assert.True(score2 > score3, "Provider 2 should have higher score than provider 3");
    }
}
