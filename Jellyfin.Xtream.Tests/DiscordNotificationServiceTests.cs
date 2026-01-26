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
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Service;
using Xunit;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Unit tests for Discord notification data structures and EpgRefreshResult.
/// </summary>
public sealed class DiscordNotificationServiceTests
{
    #region EpgRefreshResult Tests

    [Fact]
    public void EpgRefreshResult_IsSuccess_TrueWhenAllSucceed()
    {
        var result = new EpgRefreshResult { SuccessCount = 100, TotalCount = 100 };

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void EpgRefreshResult_IsSuccess_FalseWhenHasErrors()
    {
        var result = new EpgRefreshResult
        {
            SuccessCount = 90,
            TotalCount = 100,
            ErrorMessage = "Some channels failed",
        };

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void EpgRefreshResult_IsPartialSuccess_TrueWhenSomeFailed()
    {
        var result = new EpgRefreshResult { SuccessCount = 80, TotalCount = 100 };

        Assert.True(result.IsPartialSuccess);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void EpgRefreshResult_IsFailure_TrueWhenAllFailed()
    {
        var result = new EpgRefreshResult { SuccessCount = 0, TotalCount = 100 };

        Assert.True(result.IsFailure);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void EpgRefreshResult_IsFailure_TrueWhenHasErrorMessage()
    {
        var result = new EpgRefreshResult
        {
            SuccessCount = 50,
            TotalCount = 100,
            ErrorMessage = "Critical error occurred",
        };

        Assert.True(result.IsFailure);
    }

    [Fact]
    public void EpgRefreshResult_Duration_CanBeSet()
    {
        var result = new EpgRefreshResult { Duration = TimeSpan.FromMinutes(5) };

        Assert.Equal(TimeSpan.FromMinutes(5), result.Duration);
    }

    [Fact]
    public void EpgRefreshResult_FailedChannels_CanContainMultiple()
    {
        var result = new EpgRefreshResult { FailedChannels = ["BBC One", "ITV", "Channel 4"] };

        Assert.Equal(3, result.FailedChannels.Count);
        Assert.Contains("BBC One", result.FailedChannels);
        Assert.Contains("ITV", result.FailedChannels);
        Assert.Contains("Channel 4", result.FailedChannels);
    }

    [Fact]
    public void EpgRefreshResult_RetriedSuccessCount_TracksRetries()
    {
        var result = new EpgRefreshResult
        {
            SuccessCount = 95,
            TotalCount = 100,
            RetriedSuccessCount = 5,
        };

        Assert.Equal(5, result.RetriedSuccessCount);
    }

    [Fact]
    public void EpgRefreshResult_ErrorCounts_TrackFailures()
    {
        var result = new EpgRefreshResult
        {
            SuccessCount = 80,
            TotalCount = 100,
            HttpErrorCount = 10,
            NoDataCount = 10,
        };

        Assert.Equal(10, result.HttpErrorCount);
        Assert.Equal(10, result.NoDataCount);
    }

    #endregion

    #region Interface Contract Validation Tests

    [Fact]
    public void IDiscordNotificationService_HasAllRequiredMethods()
    {
        // Verify the interface has all expected methods
        var interfaceType = typeof(IDiscordNotificationService);

        // Buffer notifications
        Assert.NotNull(interfaceType.GetMethod("NotifyBufferOverflowAsync"));
        Assert.NotNull(interfaceType.GetMethod("NotifyBufferHealthIssueAsync"));
        Assert.NotNull(interfaceType.GetMethod("SendBufferDiagnosticsAsync"));

        // Stream notifications
        Assert.NotNull(interfaceType.GetMethod("NotifyStreamStartAsync"));
        Assert.NotNull(interfaceType.GetMethod("NotifyStreamErrorAsync"));
        Assert.NotNull(interfaceType.GetMethod("NotifyStreamKilledAsync"));

        // EPG notifications
        Assert.NotNull(interfaceType.GetMethod("NotifyEpgRefreshStartedAsync"));
        Assert.NotNull(interfaceType.GetMethod("NotifyEpgRefreshAsync"));

        // Quality notifications
        Assert.NotNull(interfaceType.GetMethod("NotifyAVDriftAsync"));
        Assert.NotNull(interfaceType.GetMethod("NotifyStreamQualityViolationAsync"));

        // Connection limit notifications
        Assert.NotNull(interfaceType.GetMethod("NotifyConnectionLimitChangeAsync"));

        // Audio sync correction notification
        Assert.NotNull(interfaceType.GetMethod("NotifyAudioSyncCorrectionAsync"));

        // Webhook test
        Assert.NotNull(interfaceType.GetMethod("TestWebhookAsync"));
    }

    [Fact]
    public void NotifyAudioSyncCorrectionAsync_HasCorrectSignature()
    {
        var method = typeof(IDiscordNotificationService).GetMethod("NotifyAudioSyncCorrectionAsync");
        Assert.NotNull(method);

        var parameters = method.GetParameters();
        Assert.Equal(7, parameters.Length);

        Assert.Equal("streamId", parameters[0].Name);
        Assert.Equal(typeof(string), parameters[0].ParameterType);

        Assert.Equal("channelName", parameters[1].Name);
        Assert.Equal(typeof(string), parameters[1].ParameterType);

        Assert.Equal("originalDriftMs", parameters[2].Name);
        Assert.Equal(typeof(double), parameters[2].ParameterType);

        Assert.Equal("correctionMs", parameters[3].Name);
        Assert.Equal(typeof(double), parameters[3].ParameterType);

        Assert.Equal("correctionType", parameters[4].Name);
        Assert.Equal(typeof(string), parameters[4].ParameterType);

        Assert.Equal("streamOffset", parameters[5].Name);
        Assert.Equal(typeof(long), parameters[5].ParameterType);

        Assert.Equal("cancellationToken", parameters[6].Name);
        Assert.Equal(typeof(CancellationToken), parameters[6].ParameterType);

        Assert.Equal(typeof(Task), method.ReturnType);
    }

    [Fact]
    public void NotifyConnectionLimitChangeAsync_HasCorrectSignature()
    {
        var method = typeof(IDiscordNotificationService).GetMethod("NotifyConnectionLimitChangeAsync");
        Assert.NotNull(method);

        var parameters = method.GetParameters();
        Assert.Equal(6, parameters.Length);

        Assert.Equal("providerName", parameters[0].Name);
        Assert.Equal(typeof(string), parameters[0].ParameterType);

        Assert.Equal("activeConnections", parameters[1].Name);
        Assert.Equal(typeof(int), parameters[1].ParameterType);

        Assert.Equal("maxConnections", parameters[2].Name);
        Assert.Equal(typeof(int), parameters[2].ParameterType);

        Assert.Equal("isAtLimit", parameters[3].Name);
        Assert.Equal(typeof(bool), parameters[3].ParameterType);

        Assert.Equal("externalConnections", parameters[4].Name);
        Assert.Equal(typeof(int), parameters[4].ParameterType);
        Assert.True(parameters[4].HasDefaultValue);
        Assert.Equal(0, parameters[4].DefaultValue);

        Assert.Equal("cancellationToken", parameters[5].Name);
    }

    #endregion

    #region Null Discord Service Implementation Tests

    /// <summary>
    /// A null implementation of IDiscordNotificationService for testing.
    /// All methods are no-ops that return completed tasks.
    /// </summary>
    private sealed class NullDiscordNotificationService : IDiscordNotificationService
    {
        public int NotifyAudioSyncCorrectionCallCount { get; private set; }
        public (string StreamId, double DriftMs, string CorrectionType)? LastAudioSyncCorrection { get; private set; }

        public Task NotifyBufferOverflowAsync(
            string streamId,
            string channelName,
            int overflowCount,
            double lostMB,
            double totalLostMB,
            CancellationToken cancellationToken = default
        ) => Task.CompletedTask;

        public Task NotifyStreamStartAsync(
            string streamId,
            string channelName,
            CancellationToken cancellationToken = default
        ) => Task.CompletedTask;

        public Task NotifyStreamErrorAsync(
            string streamId,
            string channelName,
            string errorMessage,
            CancellationToken cancellationToken = default
        ) => Task.CompletedTask;

        public Task NotifyStreamKilledAsync(
            string streamId,
            string channelName,
            string reason,
            TimeSpan duration,
            long bytesTransferred,
            CancellationToken cancellationToken = default
        ) => Task.CompletedTask;

        public Task SendBufferDiagnosticsAsync(
            string streamId,
            string channelName,
            string diagnostics,
            CancellationToken cancellationToken = default
        ) => Task.CompletedTask;

        public Task NotifyBufferHealthIssueAsync(
            string streamId,
            string channelName,
            int underrunCount,
            double fillPercentage,
            double currentBitrate,
            CancellationToken cancellationToken = default
        ) => Task.CompletedTask;

        public Task<bool> TestWebhookAsync(string webhookUrl, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task NotifyStreamQualityViolationAsync(
            string streamId,
            string channelName,
            string violationType,
            string details,
            CancellationToken cancellationToken = default
        ) => Task.CompletedTask;

        public Task NotifyEpgRefreshStartedAsync(int channelCount, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task NotifyEpgRefreshAsync(EpgRefreshResult result, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task NotifyAVDriftAsync(
            string streamId,
            string channelName,
            double driftMs,
            string status,
            double peakDriftMs,
            long violationCount,
            CancellationToken cancellationToken = default
        ) => Task.CompletedTask;

        public Task NotifyConnectionLimitChangeAsync(
            string providerName,
            int activeConnections,
            int maxConnections,
            bool isAtLimit,
            int externalConnections = 0,
            CancellationToken cancellationToken = default
        ) => Task.CompletedTask;

        public Task NotifyAudioSyncCorrectionAsync(
            string streamId,
            string channelName,
            double originalDriftMs,
            double correctionMs,
            string correctionType,
            long streamOffset,
            CancellationToken cancellationToken = default
        )
        {
            NotifyAudioSyncCorrectionCallCount++;
            LastAudioSyncCorrection = (streamId, originalDriftMs, correctionType);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task NullService_NotifyAudioSyncCorrectionAsync_TracksCall()
    {
        var service = new NullDiscordNotificationService();

        await service.NotifyAudioSyncCorrectionAsync(
            "stream-123",
            "Test Channel",
            45.5,
            -45.5,
            "Gradual",
            1024 * 1024,
            CancellationToken.None
        );

        Assert.Equal(1, service.NotifyAudioSyncCorrectionCallCount);
        _ = Assert.NotNull(service.LastAudioSyncCorrection);
        Assert.Equal("stream-123", service.LastAudioSyncCorrection.Value.StreamId);
        Assert.Equal(45.5, service.LastAudioSyncCorrection.Value.DriftMs);
        Assert.Equal("Gradual", service.LastAudioSyncCorrection.Value.CorrectionType);
    }

    [Theory]
    [InlineData("Gradual", 20.0)]
    [InlineData("Immediate", 50.0)]
    [InlineData("Predictive", 30.0)]
    [InlineData("Reset", 150.0)]
    public async Task NullService_HandlesAllCorrectionTypes(string correctionType, double driftMs)
    {
        var service = new NullDiscordNotificationService();

        await service.NotifyAudioSyncCorrectionAsync(
            "stream-456",
            "HD Sports",
            driftMs,
            -driftMs,
            correctionType,
            2048 * 1024,
            CancellationToken.None
        );

        Assert.Equal(correctionType, service.LastAudioSyncCorrection?.CorrectionType);
        Assert.Equal(driftMs, service.LastAudioSyncCorrection?.DriftMs);
    }

    [Fact]
    public async Task NullService_HandlesNegativeDrift()
    {
        var service = new NullDiscordNotificationService();

        // Audio behind (negative drift)
        await service.NotifyAudioSyncCorrectionAsync(
            "stream-789",
            "News Channel",
            -35.5,
            35.5,
            "Immediate",
            512 * 1024,
            CancellationToken.None
        );

        Assert.Equal(-35.5, service.LastAudioSyncCorrection?.DriftMs);
    }

    [Fact]
    public async Task NullService_TestWebhookAsync_ReturnsTrue()
    {
        var service = new NullDiscordNotificationService();

        var result = await service.TestWebhookAsync("https://discord.com/api/webhooks/123/abc", CancellationToken.None);

        Assert.True(result);
    }

    #endregion
}
