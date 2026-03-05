using Jellyfin.Xtream.Service.Streaming.Native;

namespace Jellyfin.Xtream.E2ETests.Infrastructure;

/// <summary>
/// Combines streamer and optional analyzer configuration for test setup.
/// Pass to <see cref="NativeE2ETestBase"/> constructor to declare
/// what configuration this test class needs.
/// </summary>
internal record StreamerTestSetup(TsDuckStreamerConfigNative StreamerConfig, TsDuckConfigNative? AnalyzerConfig = null);
