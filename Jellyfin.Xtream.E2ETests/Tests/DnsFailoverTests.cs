using Jellyfin.Xtream.E2ETests.Infrastructure;
using Jellyfin.Xtream.Service.Streaming.Native;
using Xunit.Abstractions;

namespace Jellyfin.Xtream.E2ETests.Tests;

/// <summary>
/// Tests for the DNS failure handling system.
/// Verifies that DNS failures are tracked, retried, and eventually lead to provider ejection.
/// </summary>
[Collection("E2E-ProviderHealth")]
public class DnsFailoverTests(DockerTestFixture fixture, ITestOutputHelper output)
    : NativeE2ETestBase(output, TestConfigs.FastIsolation)
{
    /// <summary>
    /// Tests that DNS failures are tracked correctly per provider.
    /// Each call to SimulateDnsFailure should increment the counter.
    /// </summary>
    [Fact]
    public void DnsFailure_TracksFailureCount_Correctly()
    {
        // Arrange
        const string provider1 = "dns-track-1";
        const string provider2 = "dns-track-2";

        fixture.ConfigureProvider(provider1, new ProviderBehavior { BitrateKbps = 5000 });
        fixture.ConfigureProvider(provider2, new ProviderBehavior { BitrateKbps = 5000 });

        using var streamer = BuildStreamer(TestConfigs.FastIsolation);

        streamer.AddUrl(fixture.GetProviderUrl(provider1));
        streamer.AddUrl(fixture.GetProviderUrl(provider2));

        // Act - Simulate DNS failures
        var count0Initial = streamer.GetDnsFailureCount(0);
        var count1Initial = streamer.GetDnsFailureCount(1);

        streamer.SimulateDnsFailure(0);
        var count0After1 = streamer.GetDnsFailureCount(0);

        streamer.SimulateDnsFailure(0);
        var count0After2 = streamer.GetDnsFailureCount(0);

        streamer.SimulateDnsFailure(1);
        var count1After1 = streamer.GetDnsFailureCount(1);

        // Assert
        Output.WriteLine($"Provider 0 initial count: {count0Initial}");
        Output.WriteLine($"Provider 0 after 1 failure: {count0After1}");
        Output.WriteLine($"Provider 0 after 2 failures: {count0After2}");
        Output.WriteLine($"Provider 1 initial count: {count1Initial}");
        Output.WriteLine($"Provider 1 after 1 failure: {count1After1}");

        Assert.Equal(0, count0Initial);
        Assert.Equal(1, count0After1);
        Assert.Equal(2, count0After2);
        Assert.Equal(0, count1Initial);
        Assert.Equal(1, count1After1);
    }

    /// <summary>
    /// Tests that transient DNS failures (below threshold) return Switch policy.
    /// </summary>
    [Fact]
    public void DnsFailure_BelowThreshold_ReturnsSwitchPolicy()
    {
        // Arrange
        const string provider = "dns-transient-1";
        fixture.ConfigureProvider(provider, new ProviderBehavior { BitrateKbps = 5000 });

        using var streamer = BuildStreamer(TestConfigs.FastIsolation);

        streamer.AddUrl(fixture.GetProviderUrl(provider));

        // Act - First failure (threshold is 3)
        var policy1 = streamer.SimulateDnsFailure(0);
        var count1 = streamer.GetDnsFailureCount(0);

        // Second failure
        var policy2 = streamer.SimulateDnsFailure(0);
        var count2 = streamer.GetDnsFailureCount(0);

        // Assert
        Output.WriteLine($"After 1 failure: policy={policy1}, count={count1}");
        Output.WriteLine($"After 2 failures: policy={policy2}, count={count2}");

        Assert.Equal(DnsFailurePolicy.Switch, policy1);
        Assert.Equal(DnsFailurePolicy.Switch, policy2);
        Assert.Equal(1, count1);
        Assert.Equal(2, count2);
    }

    /// <summary>
    /// Tests that reaching the DNS failure threshold triggers EjectAndSwitch policy.
    /// Default threshold is 3 failures.
    /// </summary>
    [Fact]
    public void DnsFailure_ReachesThreshold_ReturnsEjectPolicy()
    {
        // Arrange
        const string provider = "dns-eject-1";
        fixture.ConfigureProvider(provider, new ProviderBehavior { BitrateKbps = 5000 });

        using var streamer = BuildStreamer(TestConfigs.FastIsolation);

        streamer.AddUrl(fixture.GetProviderUrl(provider));

        // Act - Simulate failures up to threshold (default = 3)
        var policy1 = streamer.SimulateDnsFailure(0);
        var policy2 = streamer.SimulateDnsFailure(0);
        var policy3 = streamer.SimulateDnsFailure(0);

        var state = streamer.GetProviderState(0);
        var countAfterEjection = streamer.GetDnsFailureCount(0);

        // Assert
        Output.WriteLine($"Policy after 1 failure: {policy1}");
        Output.WriteLine($"Policy after 2 failures: {policy2}");
        Output.WriteLine($"Policy after 3 failures: {policy3}");
        Output.WriteLine($"Provider state after ejection: {state}");
        Output.WriteLine($"DNS failure count after ejection: {countAfterEjection}");

        Assert.Equal(DnsFailurePolicy.Switch, policy1);
        Assert.Equal(DnsFailurePolicy.Switch, policy2);
        Assert.Equal(DnsFailurePolicy.EjectAndSwitch, policy3);
        Assert.Equal(ProviderState.Ejected, state);
        // Counter should be reset after ejection
        Assert.Equal(0, countAfterEjection);
    }

    /// <summary>
    /// Tests that successful connection resets the DNS failure count.
    /// </summary>
    [Fact]
    public void DnsFailure_SuccessResetsCount()
    {
        // Arrange
        const string provider = "dns-reset-1";
        fixture.ConfigureProvider(provider, new ProviderBehavior { BitrateKbps = 5000 });

        using var streamer = BuildStreamer(TestConfigs.FastIsolation);

        streamer.AddUrl(fixture.GetProviderUrl(provider));

        // Act - Accumulate failures
        streamer.SimulateDnsFailure(0);
        streamer.SimulateDnsFailure(0);
        var countBeforeReset = streamer.GetDnsFailureCount(0);

        // Simulate successful connection
        streamer.ResetDnsFailureCount(0);
        var countAfterReset = streamer.GetDnsFailureCount(0);

        // Assert
        Output.WriteLine($"Count before reset: {countBeforeReset}");
        Output.WriteLine($"Count after reset: {countAfterReset}");

        Assert.Equal(2, countBeforeReset);
        Assert.Equal(0, countAfterReset);
    }

    /// <summary>
    /// Tests that DNS failures are tracked independently per provider.
    /// </summary>
    [Fact]
    public void DnsFailure_TrackedIndependentlyPerProvider()
    {
        // Arrange
        const string provider1 = "dns-indep-1";
        const string provider2 = "dns-indep-2";

        fixture.ConfigureProvider(provider1, new ProviderBehavior { BitrateKbps = 5000 });
        fixture.ConfigureProvider(provider2, new ProviderBehavior { BitrateKbps = 5000 });

        using var streamer = BuildStreamer(TestConfigs.FastIsolation);

        streamer.AddUrl(fixture.GetProviderUrl(provider1));
        streamer.AddUrl(fixture.GetProviderUrl(provider2));

        // Act - Fail provider 0 up to ejection, while provider 1 stays healthy
        streamer.SimulateDnsFailure(0);
        streamer.SimulateDnsFailure(0);
        var policy3 = streamer.SimulateDnsFailure(0); // Ejects provider 0

        var state0 = streamer.GetProviderState(0);
        var state1 = streamer.GetProviderState(1);
        var count0 = streamer.GetDnsFailureCount(0);
        var count1 = streamer.GetDnsFailureCount(1);

        // Assert
        Output.WriteLine($"Provider 0: state={state0}, count={count0}, policy3={policy3}");
        Output.WriteLine($"Provider 1: state={state1}, count={count1}");

        Assert.Equal(ProviderState.Ejected, state0);
        Assert.Equal(ProviderState.Active, state1);
        Assert.Equal(0, count0); // Reset after ejection
        Assert.Equal(0, count1); // Never failed
    }

    /// <summary>
    /// Tests the full DNS failure flow during actual streaming.
    /// When a provider fails DNS resolution, the system should switch to a healthy provider.
    /// </summary>
    [Fact]
    public async Task DnsFailure_DuringStreaming_SwitchesToHealthyProvider()
    {
        // Arrange - two providers: one we'll force-eject, one healthy
        const string primaryProvider = "dns-stream-primary";
        const string backupProvider = "dns-stream-backup";

        fixture.ConfigureProvider(primaryProvider, new ProviderBehavior { BitrateKbps = 5000 });
        fixture.ConfigureProvider(backupProvider, new ProviderBehavior { BitrateKbps = 5000 });

        using var streamer = BuildStreamer(TestConfigs.FastIsolation);

        streamer.AddUrl(fixture.GetProviderUrl(primaryProvider));
        streamer.AddUrl(fixture.GetProviderUrl(backupProvider));

        // Start streaming
        Assert.True(streamer.Start(), "Streamer should start successfully");

        // Wait for connection to establish
        await TestHelpers.WaitForStreamingAsync(streamer, TimeSpan.FromSeconds(3));

        // Act - Simulate DNS failures on primary provider to trigger ejection
        var policy1 = streamer.SimulateDnsFailure(0);
        var policy2 = streamer.SimulateDnsFailure(0);
        var policy3 = streamer.SimulateDnsFailure(0);

        // Wait for system to stabilize
        await Task.Delay(500);

        var primaryState = streamer.GetProviderState(0);
        var backupState = streamer.GetProviderState(1);
        var status = streamer.GetStatus();

        streamer.Stop();

        // Assert
        Output.WriteLine($"Primary provider state: {primaryState}");
        Output.WriteLine($"Backup provider state: {backupState}");
        Output.WriteLine($"Current URL index: {status.CurrentUrlIndex}");
        Output.WriteLine($"Bytes received: {status.BytesReceived:N0}");
        Output.WriteLine($"Policy 3 (should be EjectAndSwitch): {policy3}");

        Assert.Equal(ProviderState.Ejected, primaryState);
        Assert.Equal(DnsFailurePolicy.EjectAndSwitch, policy3);
        Assert.True(status.BytesReceived > 0, "Should have received data");
    }

    /// <summary>
    /// Tests that ejected DNS provider can eventually recover after quarantine.
    /// </summary>
    [Fact]
    public async Task DnsFailure_AfterQuarantine_ProviderCanRecover()
    {
        // Arrange
        const string provider = "dns-recover-1";
        fixture.ConfigureProvider(provider, new ProviderBehavior { BitrateKbps = 5000 });

        using var streamer = BuildStreamer(TestConfigs.HighlyReactive);

        streamer.AddUrl(fixture.GetProviderUrl(provider));

        // Act - Trigger ejection via DNS failures
        streamer.SimulateDnsFailure(0);
        streamer.SimulateDnsFailure(0);
        streamer.SimulateDnsFailure(0);

        var stateAfterEject = streamer.GetProviderState(0);
        Output.WriteLine($"State after ejection: {stateAfterEject}");
        Assert.Equal(ProviderState.Ejected, stateAfterEject);

        // Start streaming to allow recovery process
        Assert.True(streamer.Start(), "Streamer should start");

        // Wait for quarantine to expire and recovery
        var recovered = await TestHelpers.WaitForConditionAsync(
            () =>
            {
                // Trigger ejection expiry check before getting state
                streamer.CheckRecovery();
                var s = streamer.GetProviderState(0);
                return s == ProviderState.Probation || s == ProviderState.Active;
            },
            TimeSpan.FromSeconds(10)
        );

        streamer.CheckRecovery();
        var finalState = streamer.GetProviderState(0);
        streamer.Stop();

        // Assert
        Output.WriteLine($"Recovered: {recovered}");
        Output.WriteLine($"Final state: {finalState}");

        Assert.True(
            recovered || finalState != ProviderState.Ejected,
            $"Provider should eventually recover from ejection, but state is {finalState}"
        );
    }
}
