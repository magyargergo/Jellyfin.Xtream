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

using Jellyfin.Xtream.Service.Discovery;
using Xunit;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Unit tests for the DiscoveredCredential class.
/// </summary>
public sealed class DiscoveredCredentialTests
{
    /// <summary>
    /// Tests default values are set correctly.
    /// </summary>
    [Fact]
    public void DefaultValues_AreSetCorrectly()
    {
        var credential = new DiscoveredCredential();

        Assert.Equal(string.Empty, credential.Server);
        Assert.Equal(8080, credential.Port);
        Assert.Equal(string.Empty, credential.Username);
        Assert.Equal(string.Empty, credential.Password);
    }

    /// <summary>
    /// Tests BaseUrl is constructed correctly.
    /// </summary>
    [Fact]
    public void BaseUrl_ConstructedCorrectly()
    {
        var credential = new DiscoveredCredential { Server = "example.com", Port = 25461 };

        Assert.Equal("http://example.com:25461", credential.BaseUrl);
    }

    /// <summary>
    /// Tests BaseUrl with default port.
    /// </summary>
    [Fact]
    public void BaseUrl_WithDefaultPort_ConstructedCorrectly()
    {
        var credential = new DiscoveredCredential { Server = "example.com" };

        Assert.Equal("http://example.com:8080", credential.BaseUrl);
    }

    /// <summary>
    /// Tests UniqueKey is constructed correctly.
    /// </summary>
    [Fact]
    public void UniqueKey_ConstructedCorrectly()
    {
        var credential = new DiscoveredCredential
        {
            Server = "example.com",
            Port = 8080,
            Username = "testuser",
        };

        Assert.Equal("example.com:8080:testuser", credential.UniqueKey);
    }

    /// <summary>
    /// Tests UniqueKey does not include password.
    /// </summary>
    [Fact]
    public void UniqueKey_DoesNotIncludePassword()
    {
        var credential = new DiscoveredCredential
        {
            Server = "example.com",
            Port = 8080,
            Username = "testuser",
            Password = "secretpass",
        };

        Assert.DoesNotContain("secretpass", credential.UniqueKey);
    }

    /// <summary>
    /// Tests Equals returns true for identical credentials.
    /// </summary>
    [Fact]
    public void Equals_IdenticalCredentials_ReturnsTrue()
    {
        var cred1 = new DiscoveredCredential
        {
            Server = "example.com",
            Port = 8080,
            Username = "user",
            Password = "pass",
        };

        var cred2 = new DiscoveredCredential
        {
            Server = "example.com",
            Port = 8080,
            Username = "user",
            Password = "pass",
        };

        Assert.True(cred1.Equals(cred2));
        Assert.Equal(cred1, cred2);
    }

    /// <summary>
    /// Tests Equals returns false for different servers.
    /// </summary>
    [Fact]
    public void Equals_DifferentServer_ReturnsFalse()
    {
        var cred1 = new DiscoveredCredential
        {
            Server = "server1.com",
            Port = 8080,
            Username = "user",
            Password = "pass",
        };

        var cred2 = new DiscoveredCredential
        {
            Server = "server2.com",
            Port = 8080,
            Username = "user",
            Password = "pass",
        };

        Assert.False(cred1.Equals(cred2));
        Assert.NotEqual(cred1, cred2);
    }

    /// <summary>
    /// Tests Equals returns false for different ports.
    /// </summary>
    [Fact]
    public void Equals_DifferentPort_ReturnsFalse()
    {
        var cred1 = new DiscoveredCredential
        {
            Server = "example.com",
            Port = 8080,
            Username = "user",
            Password = "pass",
        };

        var cred2 = new DiscoveredCredential
        {
            Server = "example.com",
            Port = 8081,
            Username = "user",
            Password = "pass",
        };

        Assert.False(cred1.Equals(cred2));
    }

    /// <summary>
    /// Tests Equals returns false for different username.
    /// </summary>
    [Fact]
    public void Equals_DifferentUsername_ReturnsFalse()
    {
        var cred1 = new DiscoveredCredential
        {
            Server = "example.com",
            Port = 8080,
            Username = "user1",
            Password = "pass",
        };

        var cred2 = new DiscoveredCredential
        {
            Server = "example.com",
            Port = 8080,
            Username = "user2",
            Password = "pass",
        };

        Assert.False(cred1.Equals(cred2));
    }

    /// <summary>
    /// Tests Equals returns false for different password.
    /// </summary>
    [Fact]
    public void Equals_DifferentPassword_ReturnsFalse()
    {
        var cred1 = new DiscoveredCredential
        {
            Server = "example.com",
            Port = 8080,
            Username = "user",
            Password = "pass1",
        };

        var cred2 = new DiscoveredCredential
        {
            Server = "example.com",
            Port = 8080,
            Username = "user",
            Password = "pass2",
        };

        Assert.False(cred1.Equals(cred2));
    }

    /// <summary>
    /// Tests Equals returns false for null.
    /// </summary>
    [Fact]
    public void Equals_Null_ReturnsFalse()
    {
        var credential = new DiscoveredCredential
        {
            Server = "example.com",
            Port = 8080,
            Username = "user",
            Password = "pass",
        };

        Assert.False(credential.Equals((object?)null));
    }

    /// <summary>
    /// Tests Equals returns false for different type.
    /// </summary>
    [Fact]
    public void Equals_DifferentType_ReturnsFalse()
    {
        var credential = new DiscoveredCredential
        {
            Server = "example.com",
            Port = 8080,
            Username = "user",
            Password = "pass",
        };

        Assert.False(credential.Equals((object)"not a credential"));
    }

    /// <summary>
    /// Tests GetHashCode returns same value for identical credentials.
    /// </summary>
    [Fact]
    public void GetHashCode_IdenticalCredentials_ReturnsSameValue()
    {
        var cred1 = new DiscoveredCredential
        {
            Server = "example.com",
            Port = 8080,
            Username = "user",
            Password = "pass",
        };

        var cred2 = new DiscoveredCredential
        {
            Server = "example.com",
            Port = 8080,
            Username = "user",
            Password = "pass",
        };

        Assert.Equal(cred1.GetHashCode(), cred2.GetHashCode());
    }

    /// <summary>
    /// Tests GetHashCode returns different values for different credentials.
    /// </summary>
    [Fact]
    public void GetHashCode_DifferentCredentials_ReturnsDifferentValues()
    {
        var cred1 = new DiscoveredCredential
        {
            Server = "example.com",
            Port = 8080,
            Username = "user1",
            Password = "pass",
        };

        var cred2 = new DiscoveredCredential
        {
            Server = "example.com",
            Port = 8080,
            Username = "user2",
            Password = "pass",
        };

        Assert.NotEqual(cred1.GetHashCode(), cred2.GetHashCode());
    }

    /// <summary>
    /// Tests credentials work correctly in HashSet.
    /// </summary>
    [Fact]
    public void HashSet_DeduplicatesCorrectly()
    {
        var cred1 = new DiscoveredCredential
        {
            Server = "example.com",
            Port = 8080,
            Username = "user",
            Password = "pass",
        };

        var cred2 = new DiscoveredCredential
        {
            Server = "example.com",
            Port = 8080,
            Username = "user",
            Password = "pass",
        };

        var set = new HashSet<DiscoveredCredential> { cred1, cred2 };

        _ = Assert.Single(set);
    }

    /// <summary>
    /// Tests credentials with special characters in server.
    /// </summary>
    [Fact]
    public void BaseUrl_WithSpecialServerName_HandlesCorrectly()
    {
        var credential = new DiscoveredCredential { Server = "sub.domain.example.com", Port = 443 };

        Assert.Equal("http://sub.domain.example.com:443", credential.BaseUrl);
    }

    /// <summary>
    /// Tests UniqueKey with special characters in username.
    /// </summary>
    [Fact]
    public void UniqueKey_WithSpecialUsername_HandlesCorrectly()
    {
        var credential = new DiscoveredCredential
        {
            Server = "example.com",
            Port = 8080,
            Username = "user.name_123",
        };

        Assert.Equal("example.com:8080:user.name_123", credential.UniqueKey);
    }
}
