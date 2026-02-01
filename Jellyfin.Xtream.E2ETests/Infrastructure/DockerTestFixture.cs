namespace Jellyfin.Xtream.E2ETests.Infrastructure;

/// <summary>
/// xUnit class fixture that manages the HTTP test server lifecycle.
/// All tests in a collection share this fixture instance.
/// Supports both simple streaming endpoints and per-provider configurable behaviors for health system testing.
/// </summary>
public sealed class DockerTestFixture : IAsyncLifetime
{
    private SimpleHttpTestServer? _server;

    /// <summary>
    /// Gets the base URL of the test server.
    /// </summary>
    public string BaseUrl => _server?.BaseUrl ?? throw new InvalidOperationException("Server not started");

    /// <summary>
    /// Gets the total number of connections served.
    /// </summary>
    public int ConnectionCount => _server?.ConnectionCount ?? 0;

    /// <summary>
    /// Gets the total number of provider connections across all providers.
    /// </summary>
    public int TotalProviderConnectionCount => _server?.TotalProviderConnectionCount ?? 0;

    /// <summary>
    /// Gets or sets how many milliseconds the unstable endpoint streams before dropping.
    /// </summary>
    public int UnstableDropAfterMs
    {
        get => _server?.UnstableDropAfterMs ?? 2000;
        set
        {
            if (_server != null)
            {
                _server.UnstableDropAfterMs = value;
            }
        }
    }

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        _server = new SimpleHttpTestServer();
        await _server.StartAsync();
    }

    /// <inheritdoc/>
    public async Task DisposeAsync()
    {
        if (_server != null)
        {
            await _server.DisposeAsync();
            _server = null;
        }
    }

    /// <summary>
    /// Resets the connection counter.
    /// </summary>
    public void ResetConnectionCount()
    {
        _server?.ResetConnectionCount();
    }

    /// <summary>
    /// Gets the URL for a specific provider.
    /// </summary>
    /// <param name="providerId">The provider identifier.</param>
    /// <returns>The full URL for streaming from this provider.</returns>
    public string GetProviderUrl(string providerId) =>
        _server?.GetProviderUrl(providerId) ?? throw new InvalidOperationException("Server not started");

    /// <summary>
    /// Configures the behavior for a specific provider.
    /// </summary>
    /// <param name="providerId">The provider identifier.</param>
    /// <param name="behavior">The behavior configuration.</param>
    public void ConfigureProvider(string providerId, ProviderBehavior behavior)
    {
        if (_server == null)
        {
            throw new InvalidOperationException("Server not started");
        }

        _server.ConfigureProvider(providerId, behavior);
    }

    /// <summary>
    /// Gets the statistics for a specific provider.
    /// </summary>
    /// <param name="providerId">The provider identifier.</param>
    /// <returns>The provider statistics.</returns>
    public ProviderStats GetStats(string providerId) =>
        _server?.GetStats(providerId) ?? throw new InvalidOperationException("Server not started");

    /// <summary>
    /// Configures the provider to fail the next N requests.
    /// </summary>
    /// <param name="providerId">The provider identifier.</param>
    /// <param name="count">Number of requests to fail.</param>
    public void FailNextRequests(string providerId, int count)
    {
        if (_server == null)
        {
            throw new InvalidOperationException("Server not started");
        }

        _server.FailNextRequests(providerId, count);
    }

    /// <summary>
    /// Resets all provider statistics.
    /// </summary>
    public void ResetAllProviderStats()
    {
        _server?.ResetAllProviderStats();
    }
}

/// <summary>
/// xUnit collection definitions for parallel test execution.
/// Each collection gets its own DockerTestFixture (HTTP server on unique port).
/// </summary>
[CollectionDefinition("E2E")]
public class E2ETestCollection : ICollectionFixture<DockerTestFixture> { }

[CollectionDefinition("E2E-Restream")]
public class E2ERestreamCollection : ICollectionFixture<DockerTestFixture> { }

[CollectionDefinition("E2E-Failover")]
public class E2EFailoverCollection : ICollectionFixture<DockerTestFixture> { }

[CollectionDefinition("E2E-Analysis")]
public class E2EAnalysisCollection : ICollectionFixture<DockerTestFixture> { }

[CollectionDefinition("E2E-SharedMemory")]
public class E2ESharedMemoryCollection : ICollectionFixture<DockerTestFixture> { }

[CollectionDefinition("E2E-Streaming")]
public class E2EStreamingCollection : ICollectionFixture<DockerTestFixture> { }

[CollectionDefinition("E2E-ProviderHealth")]
public class E2EProviderHealthCollection : ICollectionFixture<DockerTestFixture> { }
