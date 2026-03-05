using System.Collections.Concurrent;
using Jellyfin.Xtream.Service.Streaming.Native;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Jellyfin.Xtream.E2ETests.Infrastructure;

/// <summary>
/// Captures native C++ (TsDuck) logs during E2E test execution and outputs them
/// to xUnit's <see cref="ITestOutputHelper"/> on demand (typically on test failure).
/// </summary>
/// <remarks>
/// <para>
/// Implements <see cref="ILogger"/> so it can be passed to <see cref="NativeLogging.Initialize"/>.
/// All log messages are buffered in a <see cref="ConcurrentQueue{T}"/> for thread safety,
/// since native callbacks fire on native threads via <c>[UnmanagedCallersOnly]</c>.
/// </para>
/// <para>
/// Logs are never written to <see cref="ITestOutputHelper"/> from the callback thread —
/// only from <see cref="DumpLogs"/> called synchronously in the test method's catch block.
/// This avoids writing to an invalidated output helper after test completion.
/// </para>
/// </remarks>
/// <remarks>
/// Initializes a new instance of the <see cref="NativeLogCapture"/> class.
/// </remarks>
/// <param name="output">The xUnit test output helper.</param>
/// <param name="minLevel">Minimum log level to capture. Defaults to <see cref="LogLevel.Debug"/>.</param>
public sealed class NativeLogCapture(ITestOutputHelper output, LogLevel minLevel = LogLevel.Debug)
    : ILogger,
        IDisposable
{
    private readonly ITestOutputHelper _output = output;
    private readonly LogLevel _minLevel = minLevel;
    private readonly ConcurrentQueue<(DateTime Time, LogLevel Level, string Message)> _logs = new();
    private volatile bool _disposed;

    /// <summary>
    /// Registers this logger with the static <see cref="NativeLogging"/> singleton.
    /// Safe to call multiple times — <see cref="NativeLogging.Initialize"/> is idempotent.
    /// </summary>
    public void Initialize()
    {
        NativeLogging.Initialize(this);
    }

    /// <inheritdoc/>
    public bool IsEnabled(LogLevel logLevel) => !_disposed && logLevel >= _minLevel;

    /// <inheritdoc/>
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    )
    {
        if (_disposed || !IsEnabled(logLevel))
        {
            return;
        }

        try
        {
            var message = formatter(state, exception);
            if (exception != null)
            {
                message = $"{message} | {exception.GetType().Name}: {exception.Message}";
            }

            _logs.Enqueue((DateTime.UtcNow, logLevel, message));
        }
        catch
        {
            // Never throw from callback — could crash native code
        }
    }

    /// <inheritdoc/>
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    /// <summary>
    /// Gets the number of buffered log entries.
    /// </summary>
    public int Count => _logs.Count;

    /// <summary>
    /// Dumps all buffered native logs to <see cref="ITestOutputHelper"/>.
    /// Call this synchronously in a catch block before re-throwing the assertion exception.
    /// </summary>
    public void DumpLogs()
    {
        if (_logs.IsEmpty)
        {
            _output.WriteLine("[No native logs captured]");
            return;
        }

        _output.WriteLine(string.Empty);
        _output.WriteLine("=== NATIVE LOGS ===");

        while (_logs.TryDequeue(out var entry))
        {
            var levelTag = entry.Level switch
            {
                LogLevel.Error => "ERR",
                LogLevel.Warning => "WRN",
                LogLevel.Information => "INF",
                LogLevel.Debug => "DBG",
                LogLevel.Trace => "TRC",
                _ => entry.Level.ToString()[..3].ToUpperInvariant(),
            };

            _output.WriteLine($"[{entry.Time:HH:mm:ss.fff}] [{levelTag}] {entry.Message}");
        }

        _output.WriteLine("=== END NATIVE LOGS ===");
        _output.WriteLine(string.Empty);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _disposed = true;
    }
}
