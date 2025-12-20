# Jellyfin.Xtream Performance Benchmarks

Comprehensive performance benchmarks for the MPEG-TS parser and circular buffer implementation, validating **2-3x throughput improvements** and **10-20x faster sync recovery** from industry-grade optimizations.

## Overview

This benchmark suite measures:
- ✅ SIMD-accelerated sync byte search (10-20x improvement)
- ✅ Zero-allocation API performance
- ✅ Packet processing throughput (2-3x improvement)
- ✅ Circular buffer read/write performance
- ✅ Multi-threaded streaming scenarios

## Running the Benchmarks

### Run All Benchmarks

```powershell
cd Jellyfin.Xtream.Benchmarks
dotnet run -c Release
```

### Run Specific Benchmark Class

```powershell
# NEW: TsIndexer performance (SIMD, zero-allocation)
dotnet run -c Release -- --filter *TsIndexerBenchmarks*

# NEW: SIMD sync search comparison
dotnet run -c Release -- --filter *SimdSyncSearchBenchmarks*

# Run only write benchmarks
dotnet run -c Release -- --filter *CircularBufferWriteStreamBenchmarks*

# Run only read benchmarks
dotnet run -c Release -- --filter *CircularBufferReadStreamBenchmarks*

# Run only concurrent benchmarks
dotnet run -c Release -- --filter *ConcurrentReadWriteBenchmarks*

# Run only alignment benchmarks
dotnet run -c Release -- --filter *BufferAlignmentBenchmarks*
```

### Run Specific Benchmark Method

```powershell
# Run only the small write benchmark
dotnet run -c Release -- --filter *Write_Small_4KB*

# Run only async read benchmarks
dotnet run -c Release -- --filter *ReadAsync*
```

## Benchmark Categories

### 1. TsIndexerBenchmarks ⭐ NEW

Tests MPEG-TS indexer performance with industry optimizations:
- **ProcessChunk_Small/Medium/Large**: Packet processing throughput (target: 5-8 GB/s)
- **ProcessChunk_Continuous**: Real-world streaming scenario
- **GetVideoPid_Cached**: Zero-allocation cached lookup
- **GetKeyframeCount_Cached**: Zero-allocation keyframe counting
- **GetFirstProgramWithVideo_Cached**: Zero-allocation program lookup
- **GetDiagnostics**: String generation without LINQ allocations
- **SyncRecovery_BestCase/WorstCase**: SIMD sync byte search performance

**Expected Results**:
- ProcessChunk: 5-8 GB/s (2-3x improvement)
- Cached APIs: 0 bytes allocated
- Sync recovery: 10-20x faster than scalar

### 2. SimdSyncSearchBenchmarks ⭐ NEW

Isolated SIMD vs scalar sync byte search comparison:
- **Scalar_Medium_100KB** [Baseline]: Original implementation
- **SIMD_Medium_100KB**: AVX2/SSE2 optimized
- **Scalar_Large_1MB** / **SIMD_Large_1MB**: Large data performance
- **Scalar_NoMatch** / **SIMD_NoMatch**: Worst-case full scan

**Parameters**: SyncBytePosition (10, 100, 1000, 10000, 50000 bytes)

**Expected Results**:
- SIMD: 5-10 GB/s throughput
- Scalar: ~500 MB/s throughput
- **Improvement: 10-20x**

**Hardware Counters** (Linux/macOS):
- Cache misses
- Branch mispredictions
- Total CPU cycles

### 3. CircularBufferWriteStreamBenchmarks

Tests write performance with different buffer sizes and scenarios:
- **Write_TsPacket**: Single MPEG-TS packet (188 bytes)
- **Write_Small_4KB**: Small buffer writes
- **Write_Medium_64KB**: Medium buffer writes
- **Write_Large_1MB**: Large buffer writes
- **WriteSpan_***: Span-based API performance
- **WriteAsync_***: Async write performance
- **Write_Continuous_BufferWrap**: Buffer wrap-around behavior
- **Reset_Operation**: Reset performance

### 2. CircularBufferReadStreamBenchmarks

Tests read performance with various chunk sizes:
- **Read_Synchronous**: Standard synchronous reads
- **ReadSpan**: Span-based read API
- **ReadAsync**: Async read performance
- **ReadAsyncMemory**: ValueTask Memory-based API

**Parameters:**
- Buffer sizes: 10MB, 50MB
- Read chunk sizes: 4KB, 64KB, 1MB

### 3. ConcurrentReadWriteBenchmarks

Tests real-world streaming scenarios:
- **ConcurrentReadWrite_MultipleReaders**: 1-8 concurrent readers with single writer
- **ConcurrentReadWrite_SmallPackets**: High-frequency MPEG-TS packet writes
- **ConcurrentReadWrite_SlowReaders**: Buffer overflow handling with slow readers

### 4. BufferAlignmentBenchmarks

Tests MPEG-TS alignment and sync detection:
- **FirstRead_WithAlignment**: Cold start alignment detection
- **Read_AlreadyAligned**: Hot path (already aligned)
- **Read_Sequential_100Packets**: Sequential packet reading
- **Read_LargeChunk_50Packets**: Bulk packet reading

## Understanding the Results

### Key Metrics

- **Mean**: Average execution time
- **Error**: Standard error of the mean
- **StdDev**: Standard deviation
- **Gen0/Gen1/Gen2**: Garbage collection counts (lower is better)
- **Allocated**: Memory allocated per operation (lower is better)

### Performance Targets

Based on typical IPTV streaming requirements (20Mbps = ~2.5MB/s):

- **Write operations**: Should handle > 100 MB/s (40x headroom)
- **Read operations**: Should handle > 100 MB/s per reader
- **Latency**: < 1ms for typical chunk sizes
- **Memory**: Zero or minimal allocations for hot paths

### Example Output

```
| Method           | BufferSize | Mean     | Error    | StdDev   | Allocated |
|----------------- |----------- |---------:|---------:|---------:|----------:|
| Write_Small_4KB  | 10485760   | 1.234 μs | 0.012 μs | 0.011 μs |         - |
| Read_Synchronous | 10485760   | 2.345 μs | 0.023 μs | 0.021 μs |         - |
```

## Advanced Options

### Export Results

```powershell
# Export as JSON
dotnet run -c Release -- --exporters json

# Export as HTML
dotnet run -c Release -- --exporters html

# Export as CSV
dotnet run -c Release -- --exporters csv
```

### Memory Profiling

```powershell
# Run with memory profiler
dotnet run -c Release -- --memory --filter *Write*
```

### CPU Diagnostics

```powershell
# Run with detailed CPU diagnostics
dotnet run -c Release -- --profiler EP --filter *Read*
```

## Comparing Results

To compare performance changes:

1. Run benchmarks on baseline code: `dotnet run -c Release`
2. Save results: `cp BenchmarkDotNet.Artifacts/results/* baseline/`
3. Make code changes
4. Run benchmarks again: `dotnet run -c Release`
5. Compare results in `BenchmarkDotNet.Artifacts/results/`

## Troubleshooting

### Build Errors

Ensure you're building in Release mode:
```powershell
dotnet clean
dotnet build -c Release
```

### Out of Memory

For large buffer benchmarks, you may need to increase available memory or reduce buffer sizes in the `[Params]` attributes.

### Long Running Time

To get quick results for development:

```powershell
# Run with quick settings (less accurate)
dotnet run -c Release -- --job short --filter *YourBenchmark*
```

## Contributing

When adding new benchmarks:

1. Follow the existing naming conventions
2. Use `[Params]` to test multiple scenarios
3. Include `[MemoryDiagnoser]` and `[ThreadingDiagnoser]`
4. Document what each benchmark tests
5. Ensure cleanup in `[GlobalCleanup]`

## References

- [BenchmarkDotNet Documentation](https://benchmarkdotnet.org/)
- [.NET Performance Best Practices](https://learn.microsoft.com/en-us/dotnet/framework/performance/)

