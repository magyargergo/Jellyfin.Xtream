namespace Jellyfin.Xtream.E2ETests.Infrastructure;

/// <summary>
/// xUnit class fixture that manages the HTTP test server lifecycle.
/// All tests in a collection share this fixture instance.
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
}

/// <summary>
/// xUnit collection definition for tests sharing the HTTP test server.
/// </summary>
[CollectionDefinition("E2E")]
public class E2ETestCollection : ICollectionFixture<DockerTestFixture> { }
