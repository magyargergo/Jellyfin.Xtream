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
using System.Net.Http;
using System.Net.Sockets;
using Jellyfin.Xtream.Client;
using Jellyfin.Xtream.Service.ProviderManagement;
using Xunit;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Unit tests for BackendHealthValidator and BackendHealthResult.
/// </summary>
public class BackendHealthValidatorTests
{
    #region BackendHealthResult Factory Methods

    [Fact]
    public void Healthy_CreatesCorrectResult()
    {
        var result = BackendHealthResult.Healthy(200, 150.5);

        Assert.True(result.IsHealthy);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(150.5, result.ResponseTimeMs);
        Assert.False(result.IsZombieBackend);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Zombie_CreatesCorrectResult()
    {
        var result = BackendHealthResult.Zombie("No response within 5000ms", 4500.0);

        Assert.False(result.IsHealthy);
        Assert.True(result.IsZombieBackend);
        Assert.Equal("No response within 5000ms", result.Error);
        Assert.Equal(4500.0, result.ResponseTimeMs);
        Assert.Null(result.StatusCode);
    }

    [Fact]
    public void Failed_CreatesCorrectResult()
    {
        var result = BackendHealthResult.Failed("Connection refused");

        Assert.False(result.IsHealthy);
        Assert.False(result.IsZombieBackend);
        Assert.Equal("Connection refused", result.Error);
        Assert.Null(result.StatusCode);
        Assert.Null(result.ResponseTimeMs);
    }

    #endregion

    #region ToFailureReason Tests

    [Fact]
    public void ToFailureReason_HealthyResult_ReturnsNull()
    {
        var result = BackendHealthResult.Healthy(200, 100);

        var failureReason = result.ToFailureReason();

        Assert.Null(failureReason);
    }

    [Fact]
    public void ToFailureReason_ZombieBackend_ReturnsZombieBackend()
    {
        var result = BackendHealthResult.Zombie("Timeout");

        var failureReason = result.ToFailureReason();

        Assert.Equal(ProviderFailureReason.ZombieBackend, failureReason);
    }

    [Fact]
    public void ToFailureReason_ConnectionRefused_ReturnsNetworkError()
    {
        var result = BackendHealthResult.Failed("Connection refused");

        var failureReason = result.ToFailureReason();

        Assert.Equal(ProviderFailureReason.NetworkError, failureReason);
    }

    [Fact]
    public void ToFailureReason_HostNotFound_ReturnsNetworkError()
    {
        var result = BackendHealthResult.Failed("Host not found");

        var failureReason = result.ToFailureReason();

        Assert.Equal(ProviderFailureReason.NetworkError, failureReason);
    }

    [Fact]
    public void ToFailureReason_NoSuchHost_ReturnsNetworkError()
    {
        var result = BackendHealthResult.Failed("No such host is known");

        var failureReason = result.ToFailureReason();

        Assert.Equal(ProviderFailureReason.NetworkError, failureReason);
    }

    [Fact]
    public void ToFailureReason_UnknownError_ReturnsUnknown()
    {
        var result = BackendHealthResult.Failed("Some unknown error occurred");

        var failureReason = result.ToFailureReason();

        Assert.Equal(ProviderFailureReason.Unknown, failureReason);
    }

    [Fact]
    public void ToFailureReason_NullError_ReturnsUnknown()
    {
        var result = new BackendHealthResult { IsHealthy = false, IsZombieBackend = false };

        var failureReason = result.ToFailureReason();

        Assert.Equal(ProviderFailureReason.Unknown, failureReason);
    }

    [Theory]
    [InlineData("407 Proxy Authentication Required")]
    [InlineData("Status code 407")]
    public void ToFailureReason_ProxyError407_ReturnsProxyAuthenticationError(string errorMessage)
    {
        var result = BackendHealthResult.Failed(errorMessage);

        var failureReason = result.ToFailureReason();

        Assert.Equal(ProviderFailureReason.ProxyAuthenticationError, failureReason);
    }

    [Theory]
    [InlineData("429 Too Many Requests")]
    [InlineData("Rate limit exceeded")]
    public void ToFailureReason_RateLimited_ReturnsRateLimited(string errorMessage)
    {
        var result = BackendHealthResult.Failed(errorMessage);

        var failureReason = result.ToFailureReason();

        Assert.Equal(ProviderFailureReason.RateLimited, failureReason);
    }

    [Theory]
    [InlineData("408 Request Timeout")]
    [InlineData("504 Gateway Timeout")]
    [InlineData("Gateway timeout occurred")]
    public void ToFailureReason_Timeout_ReturnsTimeout(string errorMessage)
    {
        var result = BackendHealthResult.Failed(errorMessage);

        var failureReason = result.ToFailureReason();

        Assert.Equal(ProviderFailureReason.Timeout, failureReason);
    }

    [Theory]
    [InlineData("503 Service Unavailable")]
    [InlineData("Service unavailable")]
    public void ToFailureReason_ServiceUnavailable_ReturnsServerError(string errorMessage)
    {
        var result = BackendHealthResult.Failed(errorMessage);

        var failureReason = result.ToFailureReason();

        Assert.Equal(ProviderFailureReason.ServerError, failureReason);
    }

    #endregion

    #region RetryAttempts Tests

    [Fact]
    public void Healthy_WithRetryAttempts_TracksRetries()
    {
        var result = BackendHealthResult.Healthy(200, 150.5, retryAttempts: 2);

        Assert.Equal(2, result.RetryAttempts);
        Assert.True(result.IsHealthy);
    }

    [Fact]
    public void Zombie_WithRetryAttempts_TracksRetries()
    {
        var result = BackendHealthResult.Zombie("Timeout", 5000.0, retryAttempts: 3);

        Assert.Equal(3, result.RetryAttempts);
        Assert.True(result.IsZombieBackend);
    }

    [Fact]
    public void Failed_WithRetryAttempts_TracksRetries()
    {
        var result = BackendHealthResult.Failed("Error", retryAttempts: 1);

        Assert.Equal(1, result.RetryAttempts);
        Assert.False(result.IsHealthy);
    }

    #endregion

    #region Exception Analysis Tests

    [Fact]
    public void Failed_WithSocketException_HostNotFound_SetsDnsFailure()
    {
        var socketException = new SocketException((int)SocketError.HostNotFound);
        var httpException = new HttpRequestException("DNS lookup failed", socketException);

        var result = BackendHealthResult.Failed("DNS lookup failed", httpException);

        Assert.True(result.IsDnsFailure);
        Assert.False(result.IsConnectionRefused);
        Assert.Equal("SocketException", result.InnerExceptionType);
    }

    [Fact]
    public void Failed_WithSocketException_ConnectionRefused_SetsConnectionRefused()
    {
        var socketException = new SocketException((int)SocketError.ConnectionRefused);
        var httpException = new HttpRequestException("Connection refused", socketException);

        var result = BackendHealthResult.Failed("Connection refused", httpException);

        Assert.False(result.IsDnsFailure);
        Assert.True(result.IsConnectionRefused);
        Assert.Equal("SocketException", result.InnerExceptionType);
    }

    [Fact]
    public void Failed_WithSocketException_HostUnreachable_SetsDnsFailure()
    {
        var socketException = new SocketException((int)SocketError.HostUnreachable);
        var httpException = new HttpRequestException("Host unreachable", socketException);

        var result = BackendHealthResult.Failed("Host unreachable", httpException);

        Assert.True(result.IsDnsFailure);
        Assert.False(result.IsConnectionRefused);
    }

    [Fact]
    public void Failed_WithSocketException_ConnectionReset_SetsConnectionRefused()
    {
        var socketException = new SocketException((int)SocketError.ConnectionReset);
        var httpException = new HttpRequestException("Connection reset", socketException);

        var result = BackendHealthResult.Failed("Connection reset", httpException);

        Assert.False(result.IsDnsFailure);
        Assert.True(result.IsConnectionRefused);
    }

    [Fact]
    public void Failed_WithNoException_DoesNotSetFlags()
    {
        var result = BackendHealthResult.Failed("Generic error");

        Assert.False(result.IsDnsFailure);
        Assert.False(result.IsConnectionRefused);
        Assert.Null(result.InnerExceptionType);
    }

    [Fact]
    public void Failed_WithDnsMessagePattern_SetsDnsFailure()
    {
        var result = BackendHealthResult.Failed(
            "No such host is known",
            new HttpRequestException("No such host is known")
        );

        // Falls back to message pattern since there's no SocketException
        Assert.True(result.IsDnsFailure);
    }

    [Fact]
    public void Failed_WithConnectionRefusedMessagePattern_SetsConnectionRefused()
    {
        var result = BackendHealthResult.Failed(
            "Connection actively refused",
            new HttpRequestException("Connection actively refused")
        );

        Assert.True(result.IsConnectionRefused);
    }

    [Fact]
    public void ToFailureReason_IsDnsFailure_ReturnsNetworkError()
    {
        var socketException = new SocketException((int)SocketError.HostNotFound);
        var httpException = new HttpRequestException("DNS lookup failed", socketException);
        var result = BackendHealthResult.Failed("DNS lookup failed", httpException);

        var failureReason = result.ToFailureReason();

        Assert.Equal(ProviderFailureReason.NetworkError, failureReason);
    }

    [Fact]
    public void ToFailureReason_IsConnectionRefused_ReturnsNetworkError()
    {
        var socketException = new SocketException((int)SocketError.ConnectionRefused);
        var httpException = new HttpRequestException("Connection refused", socketException);
        var result = BackendHealthResult.Failed("Connection refused", httpException);

        var failureReason = result.ToFailureReason();

        Assert.Equal(ProviderFailureReason.NetworkError, failureReason);
    }

    #endregion
}
