using Jellyfin.Xtream.Service.Streaming.Native;
using Xunit.Abstractions;

namespace Jellyfin.Xtream.E2ETests.Infrastructure;

/// <summary>
/// Base class for all E2E tests that need a <see cref="NativeStreamer"/>.
/// Derived classes pass a <see cref="StreamerTestSetup"/> from their constructor
/// to declare what configuration they need. The streamer is created once per test
/// instance (xUnit "before each") and disposed automatically ("after each").
/// </summary>
/// <remarks>
/// <para>
/// xUnit 2 lifecycle: constructor = BeforeEach, Dispose = AfterEach.
/// Each [Fact] gets a fresh instance of the derived class.
/// </para>
/// <para>
/// Native C++ logs are captured via <see cref="NativeLogCapture"/> and dumped
/// to xUnit output on failure.
/// </para>
/// </remarks>
public abstract class NativeE2ETestBase : IDisposable
{
    private const string NativeUnavailableMessage =
        "Failed to create NativeStreamer. " + "E2E tests require the native TsDuck library (run in Docker).";

    private readonly NativeLogCapture _logCapture;

    /// <summary>
    /// Gets the xUnit test output helper.
    /// </summary>
    protected ITestOutputHelper Output { get; }

    /// <summary>
    /// Gets the streamer for this test. Created from the config passed by the
    /// derived constructor. Do NOT wrap in a <c>using</c> statement — the base
    /// class disposes it in <see cref="Dispose(bool)"/>.
    /// </summary>
    protected NativeStreamer Streamer { get; }

    /// <summary>
    /// Initializes a new instance with the specified configuration.
    /// Derived constructors declare their config:
    /// <c>: NativeE2ETestBase(output, TestConfigs.Analyzer)</c>.
    /// </summary>
    private protected NativeE2ETestBase(ITestOutputHelper output, StreamerTestSetup setup)
    {
        Output = output;
        _logCapture = new NativeLogCapture(output);
        _logCapture.Initialize();
        Streamer = BuildStreamer(setup);
    }

    /// <summary>
    /// Initializes a new instance with <see cref="TestConfigs.Default"/>.
    /// </summary>
    protected NativeE2ETestBase(ITestOutputHelper output)
        : this(output, TestConfigs.Default) { }

    /// <summary>
    /// Creates a streamer from a <see cref="StreamerTestSetup"/>.
    /// Use for tests that need an additional streamer beyond <see cref="Streamer"/>.
    /// The caller is responsible for disposal (use in a <c>using var</c> block).
    /// </summary>
    private protected static NativeStreamer BuildStreamer(StreamerTestSetup setup)
    {
        if (setup.AnalyzerConfig is { } analyzerConfig)
        {
            return NativeStreamer.TryCreate(setup.StreamerConfig, analyzerConfig)
                ?? throw new InvalidOperationException(NativeUnavailableMessage);
        }

        return NativeStreamer.TryCreate(setup.StreamerConfig)
            ?? throw new InvalidOperationException(NativeUnavailableMessage);
    }

    /// <summary>
    /// Waits for the streamer to reach the Streaming state.
    /// </summary>
    protected static Task<bool> WaitForConnection(NativeStreamer streamer, TimeSpan timeout)
    {
        return TestHelpers.WaitForStreamingAsync(streamer, timeout);
    }

    /// <summary>
    /// Releases resources. Derived classes should override <see cref="Dispose(bool)"/> instead.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Disposes the streamer and dumps any buffered native logs.
    /// Derived classes that override this method must call <c>base.Dispose(disposing)</c>.
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            Streamer.Dispose();

            if (_logCapture.Count > 0)
            {
                _logCapture.DumpLogs();
            }

            _logCapture.Dispose();
        }
    }
}
