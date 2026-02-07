# Jellyfin.Xtream - Claude Code Instructions

## Project Overview
This is a Jellyfin plugin for IPTV streaming from Xtream-compatible providers, featuring:
- Multi-reader circular buffer broadcasting
- C++/C# hybrid architecture with TsDuck native interop
- Health-based provider failover with immediate timeout switching
- Real-time stream quality monitoring (TR 101 290)
- Multi-provider channel registry with name normalization
- SIMD-optimized memory operations (AVX2, SSE2, ARM NEON)

## Learned Patterns (Auto-extracted)

When working on this codebase, apply these patterns:

### Native Interop (C#/C++ boundary)
- **SafeHandle wrapping**: Always wrap native handles with `SafeHandleZeroOrMinusOneIsInvalid`
- **Blittable structs**: Use `[StructLayout(LayoutKind.Sequential)]` with POD types only
- **Callbacks**: Use `void* user_data` pattern with `GCHandle` to prevent GC collection
- **Opaque handles**: Hide C++ implementation behind `typedef struct X* XHandle`

### Concurrency
- **Cache-line padding**: Use `alignas(64)` in C++ or `[StructLayout(Size=128)]` in C# for hot atomics
- **Seqlock pattern**: For single-writer multi-reader with infrequent writes
- **Ring buffers**: Always use power-of-2 capacity for fast modulo via bitmask

### SIMD Memory Operations
- **Size thresholds**: Use std::memcpy for <512B, SIMD for larger transfers
- **Non-temporal stores**: Use for transfers >256KB to avoid cache pollution
- **NTA prefetch**: Add `_MM_HINT_NTA` prefetch for >1MB transfers (hides latency, minimal cache pollution)
- **Prefetch distance**: 128 bytes (2 cache lines) for NT path, 512 bytes for temporal path
- **Vector pipelining**: Load 4 vectors before storing to maximize ILP (128B/iteration for AVX2)
- **Memory fence**: Always call `_mm_sfence()` after non-temporal stores
- **Full cache lines**: NT stores must write complete 64-byte cache lines to avoid penalties
- **Alignment checks**: Verify 32-byte alignment for AVX2 NT stores at runtime

### Streaming
- **MPEG-TS alignment**: Buffer incoming data, scan for 0x47 sync byte pairs at 188-byte intervals
- **Discontinuity handling**: Track `LastDiscontinuityOffset` when provider switches
- **SIMD threshold**: Only use SIMD for chunks above threshold (AVX2: 512B, SSE2: 1KB)

### Resilience
- **Circuit breaker**: Three states (Closed → Open → HalfOpen) with failure thresholds
- **Health scoring**: Sliding window with weighted factors (quality 35%, reliability 35%, latency 15%, recency 15%)
- **Exponential backoff**: `delay = min(2^attempt * base, max)` with random jitter
- **Immediate timeout switch**: On curl timeout (code 28), switch URLs immediately instead of retrying

### Channel Registry
- **Name normalization**: Strip country prefixes, quality indicators, streaming suffixes, diacritics
- **Quality scoring**: 4K=100, FHD=80, HD=60, SD=40, Unknown=50, +10 for icon
- **GUID generation**: Deterministic from normalized name + provider hash for stable IDs
- **Thread safety**: Use `std::shared_mutex` for concurrent read access

### C++ Modern (native/ directory)
- **Concepts**: Use `concept PidType = std::unsigned_integral<T>` for type constraints
- **[[nodiscard]]**: Mark all value-returning functions
- **static_assert**: Validate `std::is_trivially_copyable_v<T>` for lock-free types
- **[[gnu::hot]]**: Mark hot path functions for aggressive optimization

## Code Style
- C#: Follow Jellyfin coding standards
- C++: Modern C++20/23, prefer constexpr/consteval
- Always add explicit padding in FFI structs
- Use RAII for all resource management

## Key Files

### C# Managed Code
- `Service/CircularBufferWriteStream.cs` - Main streaming buffer with SIMD copy
- `Service/Restream.cs` - Stream lifecycle and failover management
- `Service/Streaming/Native/NativeStreamer.cs` - P/Invoke wrapper for streaming
- `Service/Streaming/Native/NativeChannelRegistry.cs` - P/Invoke wrapper for registry
- `Service/Streaming/SharedMemory/SharedMemoryConsumer.cs` - IPC consumer

### Native C++ Code
- `native/tsduck_interop/src/streaming/stream_pipeline.cpp` - HTTP streaming pipeline
- `native/tsduck_interop/src/streaming/failover_manager.hpp` - URL failover logic
- `native/tsduck_interop/src/ipc/shared_memory_channel.cpp` - Lock-free IPC
- `native/tsduck_interop/src/platform/simd_memcpy.hpp` - SIMD memory operations
- `native/tsduck_interop/src/platform/simd_search.hpp` - SIMD sync byte search
- `native/tsduck_interop/src/registry/channel_registry.hpp` - Channel registry
- `native/tsduck_interop/src/concurrency/seqlock.hpp` - Lock-free primitives

## Build

### Native Library (Linux via Docker)
```bash
# Build outputs to native/tsduck_interop/output/
dotnet build  # Triggers Docker build automatically
```

### Manual Docker Build
```bash
cd native/tsduck_interop
docker build --target artifacts -o type=local,dest=output .
```

### Compiler Flags (Release)
```
-O3 -ftree-vectorize -funroll-loops -march=x86-64-v2 -mavx2 -msse4.2 -mbmi2 -mpopcnt
```

## Testing

### C# Unit Tests
```bash
dotnet test Jellyfin.Xtream.Tests
```

### E2E Tests (requires Docker)
```bash
docker compose -f docker-compose.e2e.yaml up --build
```

### Native C++ Tests (in Docker)
```bash
cd native/tsduck_interop
docker build -f Dockerfile.test --target test-runner .
```

### Test Results Location
- C# tests: Console output
- E2E tests: `./e2e-results/results.trx`
- Native tests: `build/test-results/` (XML format)
