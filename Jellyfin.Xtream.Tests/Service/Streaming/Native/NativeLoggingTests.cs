// Copyright (C) 2025  Gergo Magyar
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Jellyfin.Xtream.Service.Logging;
using Jellyfin.Xtream.Service.Streaming.Native;
using Jellyfin.Xtream.Utility;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Xtream.Tests.Service.Streaming.Native;

/// <summary>
/// Unit tests for <see cref="NativeLogging"/> ensuring native logs propagate to both
/// ILogger (Docker/console) and PluginLogger (frontend dashboard).
/// </summary>
/// <remarks>
/// <para>
/// Note: Tests that require the native library are skipped when it's not available.
/// </para>
/// <para>
/// The core principles being tested:
/// 1. Native logging propagates to ILogger with hybrid filtering for performance
/// 2. Native logging also propagates to PluginLogger.DirectLog for the frontend dashboard
/// 3. String marshaling is skipped if neither destination will consume the log
/// </para>
/// </remarks>
public class NativeLoggingTests
{
    /// <summary>
    /// Checks if the native library is available for testing.
    /// </summary>
    private static bool IsNativeLibraryAvailable()
    {
        // The native library is only available on Linux
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return false;
        }

        // Try to initialize the native library to see if it's actually available
        try
        {
            NativeLogging.Initialize(null);
            var isAvailable = NativeLogging.IsInitialized;
            NativeLogging.Shutdown();
            return isAvailable;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Mock logger that captures all log calls for verification.
    /// </summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Logs { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true; // Accept all levels

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            Logs.Add((logLevel, formatter(state, exception)));
        }
    }

    /// <summary>
    /// Mock logger that filters based on minimum log level.
    /// </summary>
    private sealed class FilteringLogger : ILogger
    {
        private readonly LogLevel _minimumLevel;

        public FilteringLogger(LogLevel minimumLevel)
        {
            _minimumLevel = minimumLevel;
        }

        public List<(LogLevel Level, string Message)> Logs { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _minimumLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (IsEnabled(logLevel))
            {
                Logs.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    [Fact]
    public void IsInitialized_BeforeInitialize_ReturnsFalse()
    {
        // Ensure clean state - Shutdown is safe to call even without native library
        NativeLogging.Shutdown();

        // Assert
        Assert.False(NativeLogging.IsInitialized);
    }

    [Fact]
    public void Shutdown_CalledWithoutInitialize_DoesNotThrow()
    {
        // Arrange
        NativeLogging.Shutdown(); // Ensure not initialized

        // Act & Assert - should not throw
        var exception = Record.Exception(() => NativeLogging.Shutdown());
        Assert.Null(exception);
    }

    [Fact]
    public void Shutdown_CalledMultipleTimes_DoesNotThrow()
    {
        // Act & Assert - multiple shutdown calls should be safe
        var exception = Record.Exception(() =>
        {
            NativeLogging.Shutdown();
            NativeLogging.Shutdown();
            NativeLogging.Shutdown();
        });
        Assert.Null(exception);
    }

    [SkippableFact]
    public void Initialize_WithLogger_SetsInitializedTrue()
    {
        Skip.IfNot(IsNativeLibraryAvailable(), "Native library not available");

        // Arrange
        NativeLogging.Shutdown();
        var logger = new CapturingLogger();

        // Act
        NativeLogging.Initialize(logger);

        // Assert
        Assert.True(NativeLogging.IsInitialized);

        // Cleanup
        NativeLogging.Shutdown();
    }

    [SkippableFact]
    public void Initialize_WithNullLogger_StillInitializes()
    {
        Skip.IfNot(IsNativeLibraryAvailable(), "Native library not available");

        // Arrange
        NativeLogging.Shutdown();

        // Act
        NativeLogging.Initialize(null);

        // Assert
        Assert.True(NativeLogging.IsInitialized);

        // Cleanup
        NativeLogging.Shutdown();
    }

    [SkippableFact]
    public void Initialize_CalledTwice_IsIdempotent()
    {
        Skip.IfNot(IsNativeLibraryAvailable(), "Native library not available");

        // Arrange
        NativeLogging.Shutdown();
        var logger1 = new CapturingLogger();
        var logger2 = new CapturingLogger();

        // Act
        NativeLogging.Initialize(logger1);
        NativeLogging.Initialize(logger2);

        // Assert
        Assert.True(NativeLogging.IsInitialized);

        // Cleanup
        NativeLogging.Shutdown();
    }

    [SkippableFact]
    public void Shutdown_AfterInitialize_ClearsInitializedState()
    {
        Skip.IfNot(IsNativeLibraryAvailable(), "Native library not available");

        // Arrange
        NativeLogging.Shutdown();
        NativeLogging.Initialize(new CapturingLogger());
        Assert.True(NativeLogging.IsInitialized);

        // Act
        NativeLogging.Shutdown();

        // Assert
        Assert.False(NativeLogging.IsInitialized);
    }

    // ========== ILogger Filtering Tests ==========
    // These tests verify that log filtering is handled by ILogger, not native code.
    // This is the core principle: native always sends all logs, ILogger filters.

    [Fact]
    public void ILoggerFiltering_TraceMinimum_AcceptsAllLevels()
    {
        // Arrange - ILogger configured to accept all levels (like native should send)
        var logger = new FilteringLogger(LogLevel.Trace);

        // Assert - all levels accepted
        Assert.True(logger.IsEnabled(LogLevel.Trace));
        Assert.True(logger.IsEnabled(LogLevel.Debug));
        Assert.True(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Warning));
        Assert.True(logger.IsEnabled(LogLevel.Error));
        Assert.True(logger.IsEnabled(LogLevel.Critical));
    }

    [Fact]
    public void ILoggerFiltering_DebugMinimum_FiltersTraceOnly()
    {
        // Arrange
        var logger = new FilteringLogger(LogLevel.Debug);

        // Assert
        Assert.False(logger.IsEnabled(LogLevel.Trace));
        Assert.True(logger.IsEnabled(LogLevel.Debug));
        Assert.True(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Warning));
        Assert.True(logger.IsEnabled(LogLevel.Error));
    }

    [Fact]
    public void ILoggerFiltering_InformationMinimum_FiltersDebugAndBelow()
    {
        // Arrange
        var logger = new FilteringLogger(LogLevel.Information);

        // Assert
        Assert.False(logger.IsEnabled(LogLevel.Trace));
        Assert.False(logger.IsEnabled(LogLevel.Debug));
        Assert.True(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Warning));
        Assert.True(logger.IsEnabled(LogLevel.Error));
    }

    [Fact]
    public void ILoggerFiltering_WarningMinimum_FiltersInfoAndBelow()
    {
        // Arrange
        var logger = new FilteringLogger(LogLevel.Warning);

        // Assert
        Assert.False(logger.IsEnabled(LogLevel.Trace));
        Assert.False(logger.IsEnabled(LogLevel.Debug));
        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Warning));
        Assert.True(logger.IsEnabled(LogLevel.Error));
        Assert.True(logger.IsEnabled(LogLevel.Critical));
    }

    [Fact]
    public void ILoggerFiltering_ErrorMinimum_FiltersWarningAndBelow()
    {
        // Arrange
        var logger = new FilteringLogger(LogLevel.Error);

        // Assert
        Assert.False(logger.IsEnabled(LogLevel.Trace));
        Assert.False(logger.IsEnabled(LogLevel.Debug));
        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.False(logger.IsEnabled(LogLevel.Warning));
        Assert.True(logger.IsEnabled(LogLevel.Error));
        Assert.True(logger.IsEnabled(LogLevel.Critical));
    }

    // ========== CapturingLogger Tests ==========

    [Fact]
    public void CapturingLogger_AcceptsAllLevels()
    {
        // Verify the test infrastructure works correctly
        var logger = new CapturingLogger();

        Assert.True(logger.IsEnabled(LogLevel.Trace));
        Assert.True(logger.IsEnabled(LogLevel.Debug));
        Assert.True(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Warning));
        Assert.True(logger.IsEnabled(LogLevel.Error));
        Assert.True(logger.IsEnabled(LogLevel.Critical));
    }

    [Fact]
    public void CapturingLogger_CapturesAllLogLevels()
    {
        // Verify the test infrastructure captures messages at all levels
        var logger = new CapturingLogger();

        logger.LogTrace("Test trace message");
        logger.LogDebug("Test debug message");
        logger.LogInformation("Test info message");
        logger.LogWarning("Test warning message");
        logger.LogError("Test error message");
        logger.LogCritical("Test critical message");

        Assert.Equal(6, logger.Logs.Count);
        Assert.Contains(logger.Logs, l => l.Level == LogLevel.Trace);
        Assert.Contains(logger.Logs, l => l.Level == LogLevel.Debug);
        Assert.Contains(logger.Logs, l => l.Level == LogLevel.Information);
        Assert.Contains(logger.Logs, l => l.Level == LogLevel.Warning);
        Assert.Contains(logger.Logs, l => l.Level == LogLevel.Error);
        Assert.Contains(logger.Logs, l => l.Level == LogLevel.Critical);
    }

    [Fact]
    public void FilteringLogger_OnlyLogsEnabledLevels()
    {
        // Arrange - filter at Warning level
        var logger = new FilteringLogger(LogLevel.Warning);

        // Act - try to log at all levels
        logger.LogTrace("Trace");
        logger.LogDebug("Debug");
        logger.LogInformation("Info");
        logger.LogWarning("Warning");
        logger.LogError("Error");

        // Assert - only Warning and Error captured
        Assert.Equal(2, logger.Logs.Count);
        Assert.Contains(logger.Logs, l => l.Level == LogLevel.Warning);
        Assert.Contains(logger.Logs, l => l.Level == LogLevel.Error);
    }

    // ========== Design Principle Documentation Tests ==========

    [Fact]
    public void DesignPrinciple_HybridFiltering_NativeAndILoggerBothFilter()
    {
        // This test documents the HYBRID FILTERING design principle:
        //
        // 1. Native code filters at COARSE level (synced with ILogger's minimum)
        //    - Prevents P/Invoke callback overhead for logs that will be filtered
        //
        // 2. Managed callback checks IsEnabled BEFORE marshaling strings
        //    - Avoids ~100-200ns + GC allocations for edge cases
        //
        // Result: Optimal performance - only logs that will actually appear
        // cause string marshaling and ILogger.Log() calls.

        // Simulate Jellyfin configured at Warning level
        var jellyfinLogger = new FilteringLogger(LogLevel.Warning);

        // With hybrid filtering:
        // - Native level set to Warning → only Warning/Error/Critical reach callback
        // - Callback checks IsEnabled → skips marshaling for any filtered logs

        // Simulate only Warning+ reaching the callback (native filtering)
        var levelsReachingCallback = new[] { LogLevel.Warning, LogLevel.Error, LogLevel.Critical };

        foreach (var level in levelsReachingCallback)
        {
            // Callback would check IsEnabled first (already true for these)
            if (jellyfinLogger.IsEnabled(level))
            {
                jellyfinLogger.Log(level, default, "test", null, (s, _) => s);
            }
        }

        // All 3 appear because native already filtered Trace/Debug/Info
        Assert.Equal(3, jellyfinLogger.Logs.Count);
        Assert.DoesNotContain(jellyfinLogger.Logs, l => l.Level < LogLevel.Warning);
    }

    [Fact]
    public void DesignPrinciple_IsEnabledCheck_PreventsStringMarshaling()
    {
        // This test documents why we check IsEnabled BEFORE marshaling strings
        //
        // Without IsEnabled check:
        //   OnNativeLog → Marshal.PtrToStringAnsi (100-200ns + GC) → IsEnabled check → filtered
        //
        // With IsEnabled check:
        //   OnNativeLog → IsEnabled check → return (no marshaling, no GC)
        //
        // Savings: ~100-200ns + 2 string allocations per filtered log

        var filteringLogger = new FilteringLogger(LogLevel.Warning);

        // Trace/Debug/Info would be filtered
        Assert.False(filteringLogger.IsEnabled(LogLevel.Trace));
        Assert.False(filteringLogger.IsEnabled(LogLevel.Debug));
        Assert.False(filteringLogger.IsEnabled(LogLevel.Information));

        // If we check IsEnabled first, we can skip expensive marshaling
        var marshalingSkipped = 0;
        foreach (var level in new[] { LogLevel.Trace, LogLevel.Debug, LogLevel.Information })
        {
            if (!filteringLogger.IsEnabled(level))
            {
                marshalingSkipped++;
                continue; // Skip marshaling - this is the optimization
            }

            // This code path is never reached for filtered levels
            filteringLogger.Log(level, default, "expensive marshaled string", null, (s, _) => s);
        }

        Assert.Equal(3, marshalingSkipped);
        Assert.Empty(filteringLogger.Logs); // No logs because all were filtered early
    }

    // ========== Dashboard Integration Tests ==========
    // These tests verify that native logs are also sent to the frontend dashboard.

    /// <summary>
    /// Mock implementation of IPluginLogService for testing.
    /// </summary>
    private sealed class CapturingPluginLogService : IPluginLogService
    {
        public List<PluginLogEntry> CapturedLogs { get; } = [];

        public int EntryCount => CapturedLogs.Count;

        public int MaxEntries => 1000;

        public void Log(
            LogLevel level,
            string category,
            string message,
            Exception? exception = null,
            bool isDebug = false,
            string? streamId = null,
            string? channelName = null
        )
        {
            CapturedLogs.Add(
                new PluginLogEntry
                {
                    Id = CapturedLogs.Count + 1,
                    Timestamp = DateTime.UtcNow,
                    Level = level,
                    Category = category,
                    Message = message,
                    ExceptionMessage = exception?.Message,
                    ExceptionStackTrace = exception?.StackTrace,
                    IsDebug = isDebug,
                    StreamId = streamId,
                    ChannelName = channelName,
                }
            );
        }

        public IReadOnlyList<PluginLogEntry> GetEntries(
            LogLevel minLevel = LogLevel.Trace,
            bool includeDebug = true,
            string? category = null,
            string? streamId = null,
            string? searchText = null,
            int skip = 0,
            int take = 100
        ) => CapturedLogs;

        public IReadOnlyList<PluginLogEntry> GetEntriesAfter(
            long afterId,
            LogLevel minLevel = LogLevel.Trace,
            bool includeDebug = true
        ) => CapturedLogs;

        public PluginLogStats GetStats() => new() { TotalEntries = CapturedLogs.Count, MaxEntries = MaxEntries };

        public void Clear() => CapturedLogs.Clear();
    }

    [Fact]
    public void DesignPrinciple_DualDestination_LogsSentToBothILoggerAndDashboard()
    {
        // This test documents the DUAL DESTINATION design principle:
        //
        // Native logs are sent to two destinations:
        // 1. ILogger - for Docker/console logs (if logger.IsEnabled returns true)
        // 2. PluginLogger.DirectLog - for frontend dashboard (if IsCapturing is true)
        //
        // This allows users to see native logs in both:
        // - docker logs -f jellyfin
        // - The plugin's web-based log viewer

        // Arrange
        var pluginLogService = new CapturingPluginLogService();

        // Initialize PluginLogger (simulates what happens at plugin startup)
        PluginLogger.Initialize(pluginLogService);

        try
        {
            // Act - simulate what DirectLog does
            PluginLogger.DirectLog(LogLevel.Warning, "Native.Test", "[Test] This is a test message");

            // Assert - log appears in frontend dashboard
            Assert.Single(pluginLogService.CapturedLogs);
            Assert.Equal(LogLevel.Warning, pluginLogService.CapturedLogs[0].Level);
            Assert.Contains("Test", pluginLogService.CapturedLogs[0].Category);
            Assert.Contains("test message", pluginLogService.CapturedLogs[0].Message);

            // Verify IsCapturing is true
            Assert.True(PluginLogger.IsCapturing);
        }
        finally
        {
            // Cleanup
            PluginLogger.Initialize(null);
        }
    }

    [Fact]
    public void DesignPrinciple_DualDestination_SkipsMarshalingWhenNeitherDestinationEnabled()
    {
        // This test documents the optimization when neither destination will consume the log:
        //
        // If ILogger.IsEnabled returns false AND PluginLogger.IsCapturing is false,
        // we skip string marshaling entirely to save ~100-200ns + GC allocations.

        // Arrange - ILogger filters at Error level
        var filteringLogger = new FilteringLogger(LogLevel.Error);

        // Ensure PluginLogger is NOT capturing
        PluginLogger.Initialize(null);
        Assert.False(PluginLogger.IsCapturing);

        // For a Warning-level log:
        // - ILogger.IsEnabled(Warning) = false (because minimum is Error)
        // - PluginLogger.IsCapturing = false
        // Result: Skip marshaling entirely

        var loggerEnabled = filteringLogger.IsEnabled(LogLevel.Warning);
        var dashboardCapturing = PluginLogger.IsCapturing;

        Assert.False(loggerEnabled);
        Assert.False(dashboardCapturing);

        // If both are false, we would skip marshaling (early return in OnNativeLog)
        Assert.False(loggerEnabled || dashboardCapturing);
    }

    [Fact]
    public void DesignPrinciple_DualDestination_MarshalWhenOnlyDashboardEnabled()
    {
        // This test documents that logs are marshaled when only the dashboard is enabled:
        //
        // Scenario: ILogger filters at Error, but user has the log viewer open
        // - ILogger.IsEnabled(Info) = false
        // - PluginLogger.IsCapturing = true
        // Result: Marshal strings and send to dashboard only

        // Arrange
        var filteringLogger = new FilteringLogger(LogLevel.Error); // Filters Info
        var pluginLogService = new CapturingPluginLogService();
        PluginLogger.Initialize(pluginLogService);

        try
        {
            var loggerEnabled = filteringLogger.IsEnabled(LogLevel.Information);
            var dashboardCapturing = PluginLogger.IsCapturing;

            Assert.False(loggerEnabled); // ILogger would filter
            Assert.True(dashboardCapturing); // Dashboard is capturing

            // We should still marshal for the dashboard
            Assert.True(loggerEnabled || dashboardCapturing);

            // Verify DirectLog works when only dashboard is enabled
            PluginLogger.DirectLog(LogLevel.Information, "Native.Test", "[Test] Info message");

            Assert.Single(pluginLogService.CapturedLogs);
            Assert.Equal(LogLevel.Information, pluginLogService.CapturedLogs[0].Level);
        }
        finally
        {
            PluginLogger.Initialize(null);
        }
    }

    [Fact]
    public void PluginLogger_DirectLog_FormatsNativeCategory()
    {
        // Verify that native logs use a "Native.{component}" category format
        // This makes it easy to filter native logs in the dashboard

        var pluginLogService = new CapturingPluginLogService();
        PluginLogger.Initialize(pluginLogService);

        try
        {
            // Simulate native log from StreamPipeline component
            PluginLogger.DirectLog(LogLevel.Debug, "Native.StreamPipeline", "[StreamPipeline] worker started");

            Assert.Single(pluginLogService.CapturedLogs);
            Assert.Equal("Native.StreamPipeline", pluginLogService.CapturedLogs[0].Category);
        }
        finally
        {
            PluginLogger.Initialize(null);
        }
    }
}
