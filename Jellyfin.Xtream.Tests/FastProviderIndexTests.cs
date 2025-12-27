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
using System.Threading.Tasks;
using Jellyfin.Xtream.Service;
using Jellyfin.Xtream.Service.ProviderManagement;
using Xunit;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Unit tests for FastProviderIndex.
/// Tests O(1) provider lookup, score management, and thread safety.
/// </summary>
public sealed class FastProviderIndexTests
{
    [Fact]
    public void NewIndex_HasZeroCount()
    {
        var index = new FastProviderIndex();

        Assert.Equal(0, index.Count);
    }

    [Fact]
    public void GetOrRegisterIndex_FirstProvider_ReturnsZero()
    {
        var index = new FastProviderIndex();

        int result = index.GetOrRegisterIndex("provider-1");

        Assert.Equal(0, result);
        Assert.Equal(1, index.Count);
    }

    [Fact]
    public void GetOrRegisterIndex_SecondProvider_ReturnsOne()
    {
        var index = new FastProviderIndex();
        index.GetOrRegisterIndex("provider-1");

        int result = index.GetOrRegisterIndex("provider-2");

        Assert.Equal(1, result);
        Assert.Equal(2, index.Count);
    }

    [Fact]
    public void GetOrRegisterIndex_SameProvider_ReturnsSameIndex()
    {
        var index = new FastProviderIndex();
        int first = index.GetOrRegisterIndex("provider-1");

        int second = index.GetOrRegisterIndex("provider-1");

        Assert.Equal(first, second);
        Assert.Equal(1, index.Count);
    }

    [Fact]
    public void TryGetIndex_ExistingProvider_ReturnsTrue()
    {
        var index = new FastProviderIndex();
        index.GetOrRegisterIndex("provider-1");

        bool result = index.TryGetIndex("provider-1", out int foundIndex);

        Assert.True(result);
        Assert.Equal(0, foundIndex);
    }

    [Fact]
    public void TryGetIndex_NonExistingProvider_ReturnsFalse()
    {
        var index = new FastProviderIndex();

        bool result = index.TryGetIndex("unknown", out _);

        Assert.False(result);
    }

    [Fact]
    public void GetProviderId_ValidIndex_ReturnsId()
    {
        var index = new FastProviderIndex();
        index.GetOrRegisterIndex("provider-1");

        string? result = index.GetProviderId(0);

        Assert.Equal("provider-1", result);
    }

    [Fact]
    public void GetProviderId_InvalidIndex_ReturnsNull()
    {
        var index = new FastProviderIndex();

        string? result = index.GetProviderId(999);

        Assert.Null(result);
    }

    [Fact]
    public void SetScore_ValidIndex_UpdatesScore()
    {
        var index = new FastProviderIndex();
        index.GetOrRegisterIndex("provider-1");

        index.SetScore(0, 75);
        int result = index.GetScore(0);

        Assert.Equal(75, result);
    }

    [Fact]
    public void SetScore_ByProviderId_UpdatesScore()
    {
        var index = new FastProviderIndex();
        index.GetOrRegisterIndex("provider-1");

        bool updated = index.SetScore("provider-1", 80);
        int result = index.GetScore("provider-1");

        Assert.True(updated);
        Assert.Equal(80, result);
    }

    [Fact]
    public void SetScore_NonExistingProvider_ReturnsFalse()
    {
        var index = new FastProviderIndex();

        bool result = index.SetScore("unknown", 50);

        Assert.False(result);
    }

    [Fact]
    public void GetScore_NewProvider_ReturnsZero()
    {
        var index = new FastProviderIndex();
        index.GetOrRegisterIndex("provider-1");

        int result = index.GetScore(0);

        Assert.Equal(0, result);
    }

    [Fact]
    public void GetScore_InvalidIndex_ReturnsZero()
    {
        var index = new FastProviderIndex();

        int result = index.GetScore(999);

        Assert.Equal(0, result);
    }

    [Fact]
    public void GetTopProviderIndices_EmptyIndex_ReturnsEmpty()
    {
        var index = new FastProviderIndex();

        int[] result = index.GetTopProviderIndices(5);

        Assert.Empty(result);
    }

    [Fact]
    public void GetTopProviderIndices_SingleProvider_ReturnsSingle()
    {
        var index = new FastProviderIndex();
        index.GetOrRegisterIndex("provider-1");
        index.SetScore(0, 50);

        int[] result = index.GetTopProviderIndices(5);

        Assert.Single(result);
        Assert.Equal(0, result[0]);
    }

    [Fact]
    public void GetTopProviderIndices_SortsByScoreDescending()
    {
        var index = new FastProviderIndex();
        index.GetOrRegisterIndex("low");
        index.GetOrRegisterIndex("high");
        index.GetOrRegisterIndex("medium");
        index.SetScore(0, 25);
        index.SetScore(1, 100);
        index.SetScore(2, 50);

        int[] result = index.GetTopProviderIndices(3);

        Assert.Equal(3, result.Length);
        Assert.Equal(1, result[0]); // high (100)
        Assert.Equal(2, result[1]); // medium (50)
        Assert.Equal(0, result[2]); // low (25)
    }

    [Fact]
    public void GetTopProviderIndices_LimitsResults()
    {
        var index = new FastProviderIndex();
        for (int i = 0; i < 10; i++)
        {
            index.GetOrRegisterIndex($"provider-{i}");
            index.SetScore(i, i * 10);
        }

        int[] result = index.GetTopProviderIndices(3);

        Assert.Equal(3, result.Length);
    }

    [Fact]
    public void GetMillisSinceUpdate_NeverUpdated_ReturnsMaxValue()
    {
        var index = new FastProviderIndex();
        index.GetOrRegisterIndex("provider-1");

        long result = index.GetMillisSinceUpdate(0);

        Assert.Equal(long.MaxValue, result);
    }

    [Fact]
    public void GetMillisSinceUpdate_AfterSetScore_ReturnsSmallValue()
    {
        var index = new FastProviderIndex();
        index.GetOrRegisterIndex("provider-1");
        index.SetScore(0, 50);

        long result = index.GetMillisSinceUpdate(0);

        Assert.True(result < 1000); // Should be less than 1 second
    }

    [Fact]
    public void IsScoreStale_RecentUpdate_ReturnsFalse()
    {
        var index = new FastProviderIndex();
        index.GetOrRegisterIndex("provider-1");
        index.SetScore(0, 50);

        bool result = index.IsScoreStale(0, 30000);

        Assert.False(result);
    }

    [Fact]
    public void IsScoreStale_NeverUpdated_ReturnsTrue()
    {
        var index = new FastProviderIndex();
        index.GetOrRegisterIndex("provider-1");

        bool result = index.IsScoreStale(0, 30000);

        Assert.True(result);
    }

    [Fact]
    public void ClearScores_ResetsAllScores()
    {
        var index = new FastProviderIndex();
        index.GetOrRegisterIndex("provider-1");
        index.GetOrRegisterIndex("provider-2");
        index.SetScore(0, 75);
        index.SetScore(1, 50);

        index.ClearScores();

        Assert.Equal(0, index.GetScore(0));
        Assert.Equal(0, index.GetScore(1));
    }

    [Fact]
    public void GetAllProviderIds_ReturnsAllRegistered()
    {
        var index = new FastProviderIndex();
        index.GetOrRegisterIndex("provider-1");
        index.GetOrRegisterIndex("provider-2");
        index.GetOrRegisterIndex("provider-3");

        ReadOnlySpan<string> result = index.GetAllProviderIds();

        Assert.Equal(3, result.Length);
    }

    [Fact]
    public void GetAllScores_ReturnsAllScores()
    {
        var index = new FastProviderIndex();
        index.GetOrRegisterIndex("provider-1");
        index.GetOrRegisterIndex("provider-2");
        index.SetScore(0, 75);
        index.SetScore(1, 50);

        ReadOnlySpan<int> result = index.GetAllScores();

        Assert.Equal(2, result.Length);
        Assert.Equal(75, result[0]);
        Assert.Equal(50, result[1]);
    }

    [Fact]
    public async Task ConcurrentRegistration_HandlesRaceCondition()
    {
        var index = new FastProviderIndex();
        var tasks = new Task<int>[100];

        for (int i = 0; i < 100; i++)
        {
            int capturedI = i;
            tasks[i] = Task.Run(() => index.GetOrRegisterIndex($"provider-{capturedI % 10}"));
        }

        await Task.WhenAll(tasks);

        // Should have exactly 10 unique providers
        Assert.Equal(10, index.Count);
    }

    [Fact]
    public async Task ConcurrentScoreUpdates_ThreadSafe()
    {
        var index = new FastProviderIndex();
        index.GetOrRegisterIndex("provider-1");
        var tasks = new Task[1000];

        for (int i = 0; i < 1000; i++)
        {
            int score = i;
            tasks[i] = Task.Run(() => index.SetScore(0, score));
        }

        await Task.WhenAll(tasks);

        // Score should be one of the values set (thread-safe, last write wins)
        int finalScore = index.GetScore(0);
        Assert.True(finalScore >= 0 && finalScore < 1000);
    }

    [Fact]
    public void ArrayGrows_WhenCapacityExceeded()
    {
        var index = new FastProviderIndex();

        // Register more providers than initial capacity (16)
        for (int i = 0; i < 20; i++)
        {
            index.GetOrRegisterIndex($"provider-{i}");
        }

        Assert.Equal(20, index.Count);

        // Verify all providers are accessible
        for (int i = 0; i < 20; i++)
        {
            Assert.Equal($"provider-{i}", index.GetProviderId(i));
        }
    }
}
