// Copyright (C) 2025  Gergo Magyar
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Xml.Serialization;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Jellyfin.Xtream.Configuration;

namespace Jellyfin.Xtream.Benchmarks;

/// <summary>
/// Entry point for benchmark runner.
/// </summary>
public static class Program
{
    /// <summary>
    /// Main entry point.
    /// </summary>
    /// <param name="args">Command line arguments.</param>
    public static void Main(string[] args)
    {
        if (args.Length > 0)
        {
            switch (args[0])
            {
                case "test-xml":
                    TestXmlSerialization();
                    return;
                case "test-json":
                    TestJsonSerialization();
                    return;
                case "--list":
                    ListBenchmarks();
                    return;
                case "--quick":
                    RunQuickBenchmarks(args.Length > 1 ? args[1] : null);
                    return;
            }
        }

        // Run with custom configuration for better output
        var config = ManualConfig
            .Create(DefaultConfig.Instance)
            .AddDiagnoser(MemoryDiagnoser.Default)
            .AddExporter(MarkdownExporter.GitHub)
            .AddExporter(JsonExporter.Brief)
            .WithOptions(ConfigOptions.DisableOptimizationsValidator);

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
    }

    private static void ListBenchmarks()
    {
        Console.WriteLine("Available Benchmark Classes:");
        Console.WriteLine("============================");
        Console.WriteLine();
        Console.WriteLine("1. CircularBufferWriteStreamBenchmarks");
        Console.WriteLine("   - Write performance with different buffer/chunk sizes");
        Console.WriteLine("   - Span, async, and memory API variants");
        Console.WriteLine();
        Console.WriteLine("2. CircularBufferReadStreamBenchmarks");
        Console.WriteLine("   - Read performance across various scenarios");
        Console.WriteLine("   - Sync, async, Span, and Memory API variants");
        Console.WriteLine();
        Console.WriteLine("3. CircularBufferAlignmentBenchmarks");
        Console.WriteLine("   - MPEG-TS sync detection and alignment");
        Console.WriteLine("   - Cold-start vs hot-path performance");
        Console.WriteLine();
        Console.WriteLine("4. ConcurrentCircularBufferBenchmarks");
        Console.WriteLine("   - Multi-reader streaming scenarios (1-8 readers)");
        Console.WriteLine("   - Slow reader and buffer overflow handling");
        Console.WriteLine();
        Console.WriteLine("5. MpegTsParserBenchmarks");
        Console.WriteLine("   - PES parsing and PTS extraction");
        Console.WriteLine("   - RingBuffer, TimestampTracker, AudioStreamInfo");
        Console.WriteLine("   - Stream classification (O(1) lookup)");
        Console.WriteLine();
        Console.WriteLine("6. TsIndexerBenchmarks");
        Console.WriteLine("   - MPEG-TS packet processing throughput");
        Console.WriteLine("   - SIMD sync byte search performance");
        Console.WriteLine("   - Keyframe detection and PID caching");
        Console.WriteLine("   - TR 101 290 quality monitoring metrics");
        Console.WriteLine();
        Console.WriteLine("7. SimdSyncSearchBenchmarks");
        Console.WriteLine("   - SIMD vs scalar sync byte search comparison");
        Console.WriteLine("   - AVX2/SSE2/scalar performance analysis");
        Console.WriteLine();
        Console.WriteLine("8. Crc32Mpeg2Benchmarks");
        Console.WriteLine("   - MPEG-2 CRC-32 computation (ISO/IEC 13818-1)");
        Console.WriteLine("   - PAT/PMT/CAT section validation");
        Console.WriteLine("   - TR 101 290 compliance checking");
        Console.WriteLine();
        Console.WriteLine("9. EpgParsingBenchmarks");
        Console.WriteLine("   - XMLTV EPG parsing performance");
        Console.WriteLine("   - DateTime parsing overhead");
        Console.WriteLine("   - Large file handling (10-200 channels)");
        Console.WriteLine();
        Console.WriteLine("10. ProviderSelectionBenchmarks");
        Console.WriteLine("    - GetOrderedProviders with 5/10/20 providers");
        Console.WriteLine("    - GetSelectionScore single and batch");
        Console.WriteLine("    - IsAvailable and HasCapacity checks");
        Console.WriteLine("    - ShouldSwitchProvider decision logic");
        Console.WriteLine();
        Console.WriteLine("11. HealthTrackingBenchmarks");
        Console.WriteLine("    - EMA trend calculation and prediction");
        Console.WriteLine("    - RecordSample throughput");
        Console.WriteLine("    - GetSnapshot and GetAllSnapshots");
        Console.WriteLine("    - Combined health analysis");
        Console.WriteLine();
        Console.WriteLine("Usage Examples:");
        Console.WriteLine("---------------");
        Console.WriteLine("  dotnet run -c Release                              # Run all benchmarks");
        Console.WriteLine("  dotnet run -c Release -- --filter *Circular*       # Run circular buffer benchmarks");
        Console.WriteLine("  dotnet run -c Release -- --filter *MpegTs*         # Run MPEG-TS benchmarks");
        Console.WriteLine("  dotnet run -c Release -- --filter *Epg*            # Run EPG benchmarks");
        Console.WriteLine("  dotnet run -c Release -- --filter *Simd*           # Run SIMD benchmarks");
        Console.WriteLine("  dotnet run -c Release -- --filter *Crc32*          # Run CRC-32 benchmarks");
        Console.WriteLine("  dotnet run -c Release -- --filter *TR101290*       # Run TR 101 290 benchmarks");
        Console.WriteLine("  dotnet run -c Release -- --filter *Provider*       # Run provider selection benchmarks");
        Console.WriteLine("  dotnet run -c Release -- --filter *Health*         # Run health tracking benchmarks");
        Console.WriteLine("  dotnet run -c Release -- --quick                   # Quick smoke test");
        Console.WriteLine("  dotnet run -c Release -- --quick Circular          # Quick test specific category");
    }

    private static void RunQuickBenchmarks(string? filter)
    {
        Console.WriteLine("Running quick benchmark validation...");
        Console.WriteLine();

        var config = ManualConfig
            .Create(DefaultConfig.Instance)
            .AddDiagnoser(MemoryDiagnoser.Default)
            .AddJob(Job.Dry) // Minimal iterations for quick validation
            .WithOptions(ConfigOptions.DisableOptimizationsValidator);

        string[] args = string.IsNullOrEmpty(filter)
            ? ["--filter", "*Small*", "--filter", "*_Add*"]
            : ["--filter", $"*{filter}*"];

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
    }

    private static void TestJsonSerialization()
    {
        Console.WriteLine("=== Testing JSON Serialization ===\n");

        // Create test provider with data
        var provider = new XtreamProvider
        {
            Id = "test",
            Name = "Test Provider",
            BaseUrl = "http://example.com",
            Username = "user",
            Password = "pass",
            Enabled = true,
        };

        // Add some LiveTv data
        provider.LiveTv[1] = new HashSet<int> { 100, 101, 102 };
        provider.LiveTv[2] = new HashSet<int> { 200, 201 };

        // Create config with provider
        var config = new PluginConfiguration();
        config.Providers.Add(provider);
        config.LiveTv[10] = new HashSet<int> { 1000, 1001 };

        Console.WriteLine("Before serialization:");
        Console.WriteLine($"  Legacy LiveTv count: {config.LiveTv.Count}");
        Console.WriteLine($"  Provider LiveTv count: {config.Providers[0].LiveTv.Count}");

        // Serialize to JSON
        var options = new JsonSerializerOptions { WriteIndented = true };
        string json = JsonSerializer.Serialize(config, options);

        Console.WriteLine("\n=== JSON Output ===");
        Console.WriteLine(json);

        // Deserialize back
        Console.WriteLine("\n=== Deserialization Test ===");
        var deserialized = JsonSerializer.Deserialize<PluginConfiguration>(json, options);

        if (deserialized == null)
        {
            Console.WriteLine("ERROR: Deserialization returned null!");
            return;
        }

        Console.WriteLine($"Providers count: {deserialized.Providers.Count}");
        if (deserialized.Providers.Count > 0)
        {
            var p = deserialized.Providers[0];
            Console.WriteLine($"Provider LiveTv count: {p.LiveTv?.Count ?? 0}");
            if (p.LiveTv != null && p.LiveTv.Count > 0)
            {
                foreach (var kvp in p.LiveTv)
                {
                    Console.WriteLine($"  Category {kvp.Key}: [{string.Join(", ", kvp.Value)}]");
                }
            }
            else
            {
                Console.WriteLine("  WARNING: Provider LiveTv is empty!");
            }
        }

        Console.WriteLine($"Legacy LiveTv count: {deserialized.LiveTv?.Count ?? 0}");

        Console.WriteLine("\n=== Test Complete ===");
    }

    private static void TestXmlSerialization()
    {
        Console.WriteLine("=== Testing XML Serialization ===\n");

        // Create test provider with data
        var provider = new XtreamProvider
        {
            Id = "test",
            Name = "Test Provider",
            BaseUrl = "http://example.com",
            Username = "user",
            Password = "pass",
            Enabled = true,
        };

        // Add some LiveTv data
        provider.LiveTv[1] = new HashSet<int> { 100, 101, 102 };
        provider.LiveTv[2] = new HashSet<int> { 200, 201 };

        // Create config with provider
        var config = new PluginConfiguration();
        config.Providers.Add(provider);

        // Also add legacy LiveTv for comparison
        config.LiveTv[10] = new HashSet<int> { 1000, 1001 };

        Console.WriteLine("Before serialization:");
        Console.WriteLine($"  Legacy LiveTv count: {config.LiveTv.Count}");
        Console.WriteLine($"  Provider LiveTv count: {config.Providers[0].LiveTv.Count}");

        // Serialize to XML
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        string xml;
        using (var writer = new StringWriter())
        {
            serializer.Serialize(writer, config);
            xml = writer.ToString();
        }

        Console.WriteLine("\n=== XML Output ===");
        Console.WriteLine(xml);

        // Now deserialize it back
        Console.WriteLine("\n=== Deserialization Test ===");
        PluginConfiguration? deserialized;
        using (var reader = new StringReader(xml))
        {
            deserialized = (PluginConfiguration?)serializer.Deserialize(reader);
        }

        if (deserialized == null)
        {
            Console.WriteLine("ERROR: Deserialization returned null!");
            return;
        }

        Console.WriteLine($"Providers count: {deserialized.Providers.Count}");
        if (deserialized.Providers.Count > 0)
        {
            var p = deserialized.Providers[0];
            Console.WriteLine($"Provider LiveTv count: {p.LiveTv?.Count ?? 0}");
            if (p.LiveTv != null && p.LiveTv.Count > 0)
            {
                foreach (var kvp in p.LiveTv)
                {
                    Console.WriteLine($"  Category {kvp.Key}: [{string.Join(", ", kvp.Value)}]");
                }
            }
            else
            {
                Console.WriteLine("  WARNING: Provider LiveTv is empty!");
            }
        }

        Console.WriteLine($"Legacy LiveTv count: {deserialized.LiveTv?.Count ?? 0}");
        if (deserialized.LiveTv != null && deserialized.LiveTv.Count > 0)
        {
            foreach (var kvp in deserialized.LiveTv)
            {
                Console.WriteLine($"  Category {kvp.Key}: [{string.Join(", ", kvp.Value)}]");
            }
        }

        Console.WriteLine("\n=== Test Complete ===");
    }
}
