# TsDuck Interop Library

Native C++ library for MPEG-TS stream analysis, restamping, and HTTP streaming with automatic failover. Built on [TsDuck](https://tsduck.io/) for professional-grade transport stream handling.

## Modern C++ Standards

This library is written in modern C++20/23 with emphasis on:

- **Concepts and Constraints**: Type-safe template interfaces (`PidType`, `TimestampType`, `BitrateType`, `SeqlockReadable`)
- **`constexpr`/`consteval`**: Compile-time computation for constants and validation functions
- **`[[nodiscard]]`**: Enforced on all functions returning values to prevent ignored results
- **`std::span`**: Safe array passing without raw pointer/length pairs
- **Designated Initializers**: Clear struct initialization with named fields
- **RAII Patterns**: `SeqlockWriteGuard` for exception-safe lock management
- **`noexcept` Specifications**: Consistent marking for no-throw guarantees
- **Cache-Line Alignment**: `alignas(CACHE_LINE_SIZE)` for false-sharing prevention
- **Lock-Free Concurrency**: Seqlock pattern with proper memory ordering

## Features

### Stream Analysis
- **TR 101 290 Monitoring**: Priority 1 and 2 error detection per ETSI standard (including CAT timeout)
- **PCR Analysis**: Jitter, drift, accuracy, and ISO/IEC 13818-1 compliance tracking (+/-30 ppm offset, 10 ppm/hr drift rate)
- **A/V Sync Tracking**: Video-audio drift detection per EBU R37 thresholds
- **PSI Parsing**: PAT/PMT/CAT parsing via TsDuck's native `SectionDemux`
- **PID Timeout Detection**: PIDs referenced in PAT/PMT monitored for 5-second timeout

### Timestamp Correction (Restamping)
- **PCR Smoothing**: Eliminates jitter from HTTP chunked delivery
- **PTS/DTS Correction**: Maintains A/V sync across provider switches
- **Discontinuity Indicator Management**: Sets discontinuity_indicator flag per ISO 13818-1 Section 2.4.3.5 on provider switch
- **Seamless URL Switching**: Handles mid-stream failover with proper timestamp offset calculation

### HTTP Streaming
- **Multi-URL Failover**: Automatic rotation through backup URLs
- **Quality-Based Switching**: TR 101 290 error rate triggers URL rotation
- **Stall Detection**: Configurable timeout with exponential backoff
- **libcurl Integration**: Robust HTTP/HTTPS streaming

## Architecture

```
+------------------------------------------------------------------+
|                        StreamPipeline                             |
+------------------------------------------------------------------+
|  StreamSource     FailoverManager    QualitySwitchTrigger        |
|  (libcurl)        (state machine)    (TR 101 290 rates)          |
+------------------------------------------------------------------+
|                      AlignmentBuffer                              |
|                   (TS packet alignment)                           |
+------------------------------------------------------------------+
|                      TsDuckAnalyzer                               |
|  +-------------+--------------+--------------+----------------+  |
|  | Tr101290    | PcrAnalyzer  | AvSyncTracker| PsiMonitor     |  |
|  | Monitor     |              |              | (SectionDemux) |  |
|  +-------------+--------------+--------------+----------------+  |
+------------------------------------------------------------------+
|                        Restamper                                  |
|                  (PCR/PTS/DTS correction)                         |
+------------------------------------------------------------------+
```

## Building

### Prerequisites
- CMake 3.16+
- C++20 compiler (GCC 11+, Clang 14+, MSVC 2022+)
- TsDuck 3.43+
- libcurl (for streaming features)

### Docker Build (Recommended)

```bash
# Build the library
docker build -t tsduck-interop .

# Run tests
docker build -f Dockerfile.test --target test-runner -t tsduck-tests .
docker run --rm tsduck-tests
```

### Native Build

```bash
mkdir build && cd build
cmake .. -DCMAKE_BUILD_TYPE=Release
cmake --build . --parallel
```

## C API

The library exposes a C-compatible API for interop with managed languages (C#, Python, etc.).

### Analyzer API

```c
// Create analyzer
TsDuckHandle handle = tsduck_create(&config);

// Feed TS data
tsduck_feed(handle, data, length);

// Get metrics
TsDuckMetricsNative metrics;
tsduck_get_metrics(handle, &metrics);

// Get TR 101 290 counters
Tr101290Priority1Native p1;
Tr101290Priority2Native p2;
tsduck_get_tr101290(handle, &p1, &p2);

// Cleanup
tsduck_destroy(handle);
```

### Streamer API

```c
// Create streamer
TsDuckStreamerHandle streamer = tsduck_streamer_create(&config, &analyzer_config);

// Add URLs for failover
tsduck_streamer_add_url(streamer, "http://primary.example.com/stream");
tsduck_streamer_add_url(streamer, "http://backup.example.com/stream");

// Set callbacks
tsduck_streamer_set_event_callback(streamer, on_event, user_data);
tsduck_streamer_set_output_callback(streamer, on_data, user_data);

// Start streaming
tsduck_streamer_start(streamer);

// Request manual URL switch
tsduck_streamer_request_switch(streamer);

// Get status
TsDuckStreamerStatusNative status;
tsduck_streamer_get_status(streamer, &status);

// Stop and cleanup
tsduck_streamer_stop(streamer);
tsduck_streamer_destroy(streamer);
```

### Logging API

```c
// Set log level (default: TSDUCK_LOG_WARNING)
tsduck_set_log_level(TSDUCK_LOG_DEBUG);

// Check current log level
int32_t level = tsduck_get_log_level();

// Check if a level is enabled (avoid expensive formatting)
if (tsduck_is_log_enabled(TSDUCK_LOG_DEBUG)) {
    // ... prepare debug info
}

// Custom log callback (receives all log messages)
void my_log_handler(int32_t level, const char* component,
                    const char* message, void* user_data) {
    // Forward to your logging system
    printf("[%s] %s\n", component, message);
}
tsduck_set_log_callback(my_log_handler, NULL);

// Reset to default stderr output
tsduck_set_log_callback(NULL, NULL);
```

#### Log Levels

| Level | Value | Description |
|-------|-------|-------------|
| `TSDUCK_LOG_NONE` | 0 | No logging |
| `TSDUCK_LOG_ERROR` | 1 | Errors only |
| `TSDUCK_LOG_WARNING` | 2 | Warnings and errors (default) |
| `TSDUCK_LOG_INFO` | 3 | Info, warnings, and errors |
| `TSDUCK_LOG_DEBUG` | 4 | All messages including debug |
| `TSDUCK_LOG_TRACE` | 5 | Most verbose - detailed tracing |

## Configuration

### Analyzer Configuration

| Field | Default | Description |
|-------|---------|-------------|
| `metrics_interval_ms` | 1000 | Metrics update interval |
| `enable_tr101290` | 1 | Enable TR 101 290 monitoring |
| `enable_auto_restamp` | 1 | Enable integrated restamping |
| `restamp_mode` | 2 (Correct) | 0=Disabled, 1=Monitor, 2=Correct |
| `smooth_pcr` | 1 | Smooth PCR jitter |
| `fix_discontinuities` | 1 | Repair PTS discontinuities |

### Streamer Configuration

| Field | Default | Description |
|-------|---------|-------------|
| `connect_timeout_ms` | 5000 | TCP/TLS handshake timeout |
| `stall_timeout_ms` | 20000 | No-data threshold |
| `max_retries` | 10 | Retries before failure |
| `stalls_before_switch` | 2 | Consecutive failures before URL rotation |

### Quality-Based Switching (TR 101 290)

| Field | Default | Description |
|-------|---------|-------------|
| `enable_quality_switch` | 1 | Enable quality-based switching |
| `quality_check_interval_ms` | 1000 | How often to evaluate quality |
| `quality_window_seconds` | 10 | Sliding window for rate calculation |
| `max_sync_errors_per_window` | 1 | Any sync_loss triggers immediate switch |
| `max_continuity_errors_per_sec` | 20 | CC errors/sec (~1% packet loss) |
| `max_transport_errors_per_sec` | 10 | TEI bit errors/sec |
| `max_pcr_errors_per_sec` | 5 | PCR discontinuity+repetition/sec |

## Failover State Machine

```
IDLE -> CONNECTING -> STREAMING -> (stall/error) -> RECONNECTING -> CONNECTING
                                                           | (max retries)
                                                        FAILED

STREAMING -> (requestSwitch OR quality degradation) -> SWITCHING -> CONNECTING (next URL)
```

**Switch Triggers:**
1. **Manual**: `tsduck_streamer_request_switch()` API call
2. **Stall**: No data for `stall_timeout_ms`
3. **Connection Errors**: After `stalls_before_switch` consecutive failures
4. **Quality Degradation**: TR 101 290 error rates exceed thresholds

## TR 101 290 Error Priorities

### Priority 1 (Critical - affects decodability)
- `sync_byte_error`: Invalid 0x47 sync byte
- `sync_loss`: 2+ consecutive sync errors
- `pat_error`: PAT not received within 500ms
- `continuity_count_error`: Packet loss indicator
- `pmt_error`: PMT not received within 500ms
- `pid_error`: Referenced PID not seen for 5s
- `cat_error`: CAT not received within 500ms (when CA descriptors present)

### Priority 2 (Recommended monitoring)
- `transport_error`: TEI bit set
- `crc_error`: PSI section CRC failure
- `pcr_repetition_error`: >40ms between PCRs
- `pcr_discontinuity_error`: Unexpected PCR jump
- `pcr_accuracy_error`: >500ns deviation (13 ticks at 27MHz per ISO 13818-1)
- `pts_error`: >700ms between PTS

### ISO/IEC 13818-1 PCR Compliance

- `pcr_frequency_offset`: +/-30 ppm maximum deviation from nominal 27MHz
- `pcr_drift_rate`: 75 mHz/sec maximum (10 ppm/hour)

## Thread Safety

- **Analyzer**: Single-writer (feed), multiple-readers (metrics) via seqlock
- **Streamer**: Worker thread for I/O, lock-free status queries from any thread
- **Callbacks**: Invoked on worker thread; keep handlers fast and non-blocking

## Modern C++ Patterns Used

### Concepts (C++20)

```cpp
template <typename T>
concept PidType = std::integral<T> && (sizeof(T) >= 2);

template <typename T>
concept SeqlockReadable = std::is_trivially_copyable_v<T>;
```

### RAII Lock Guards

```cpp
class SeqlockWriteGuard {
public:
    explicit SeqlockWriteGuard(Seqlock& lock) noexcept
        : lock_(lock), expected_seq_(lock.begin_write()) {}
    ~SeqlockWriteGuard() noexcept { lock_.end_write(expected_seq_); }
    // Non-copyable, non-movable
};
```

### Compile-Time Validation

```cpp
template <std::integral T>
[[nodiscard]] consteval bool is_power_of_two(T value) noexcept {
    return value > 0 && (value & (value - 1)) == 0;
}

static_assert(is_power_of_two(IAT_SAMPLE_WINDOW), "Must be power of 2");
```

### Designated Initializers

```cpp
RestampingConfigNative cfg{
    .mode = RESTAMP_MODE_CORRECT,
    .smooth_pcr = 1,
    .fix_discontinuities = 1,
    .correction_threshold_ms = 45.0
};
```

## Testing

```bash
# Unit tests only
docker build -f Dockerfile.test --target unit-tests .

# Integration tests only
docker build -f Dockerfile.test --target integration-tests .

# Full test suite
docker build -f Dockerfile.test --target test-runner -t tsduck-tests .
docker run --rm tsduck-tests
```

### Test Coverage

| Component | Tests | Description |
|-----------|-------|-------------|
| Seqlock | 9 | Lock-free synchronization |
| PID Tracker | 23 | Per-PID statistics and timeout detection |
| PCR Analyzer | 14 | PCR jitter/drift/ISO 13818-1 compliance |
| IAT Analyzer | 16 | Inter-arrival time |
| Alignment Buffer | 14 | TS packet alignment |
| Failover Manager | 19 | State machine transitions |
| Quality Switch Trigger | 15 | TR 101 290 rate thresholds |
| DuckContext | 6 | TsDuck initialization |
| PSI Monitor | 22 | PAT/PMT/CAT parsing |
| TR 101 290 | 29 | Error detection (including CAT timeout) |
| Keyframe Aligner | 11 | I-frame detection and alignment |
| **Unit Tests Total** | **165** | |
| Analyzer Integration | 25 | Full analyzer pipeline |
| Restamper Integration | 22 | Timestamp correction with discontinuity |
| Streamer Integration | 18 | HTTP streaming and failover |
| **Integration Tests Total** | **65** | |

## Code Formatting

The project uses **clang-format** for consistent code style.

### Docker (Recommended)

```bash
# Run all analysis (formatting + static analysis) in Docker
docker build -f Dockerfile.analysis .

# Extract analysis reports
docker build -f Dockerfile.analysis --target results -o type=local,dest=./analysis-results .
```

### Local Build

```bash
# Configure with formatting enabled
cmake -B build -DENABLE_CODE_FORMATTING=ON

# Check formatting (reports issues without modifying files)
cmake --build build --target format-check

# Apply formatting to all source files
cmake --build build --target format
```

### Style Overview

The `.clang-format` configuration is based on LLVM style with project adjustments:

- **Indentation**: 4 spaces, no tabs
- **Line length**: 120 characters
- **Braces**: Attached (K&R style)
- **Pointer alignment**: Left (`int* ptr`)
- **Include ordering**: Project headers -> TsDuck -> CURL -> C++ stdlib -> C stdlib -> System

## Static Analysis

The project includes configuration for two static analysis tools: **clang-tidy** and **cppcheck**.

### Running Static Analysis

```bash
# Configure with static analysis enabled
cmake -B build -DENABLE_STATIC_ANALYSIS=ON

# Run clang-tidy
cmake --build build --target clang-tidy

# Run clang-tidy with auto-fix
cmake --build build --target clang-tidy-fix

# Run cppcheck
cmake --build build --target cppcheck

# Run cppcheck with XML output (for CI)
cmake --build build --target cppcheck-xml

# Run all static analysis
cmake --build build --target static-analysis
```

### Configuration Files

| File | Tool | Description |
| --- | --- | --- |
| `.clang-format` | clang-format | Code formatting style configuration |
| `.clang-tidy` | clang-tidy | Check configuration, naming conventions, enabled checks |
| `.cppcheck-suppressions` | cppcheck | Suppressed warnings, false positives |

### Enabled Checks

**clang-tidy** runs comprehensive checks including:

- `bugprone-*` - Common bug patterns
- `cert-*` - CERT secure coding guidelines
- `clang-analyzer-*` - Clang static analyzer
- `concurrency-*` - Thread safety issues
- `cppcoreguidelines-*` - C++ Core Guidelines
- `modernize-*` - Modern C++ idioms
- `performance-*` - Performance anti-patterns
- `readability-*` - Code readability

**cppcheck** runs with `--enable=all` for maximum coverage:

- Style issues
- Performance problems
- Portability concerns
- Unused code detection
- Memory leak detection

## Areas of Improvement

### Performance Optimizations

- **SIMD Packet Processing**: The sync byte search uses SSE2/AVX2 where available, but packet parsing could benefit from vectorized operations for bulk CC counter validation
- **Memory Pool for Callbacks**: Output callbacks currently copy data; a zero-copy ring buffer interface would reduce allocations in high-throughput scenarios
- **Batch TR 101 290 Updates**: Error counters are updated per-packet; batching updates per N packets could reduce atomic operation overhead

### Code Quality

- **Error Reporting**: Current error handling uses return codes; a richer error type with context (e.g., which URL failed, what HTTP status) would improve diagnostics
- **Configuration Validation**: No bounds checking on configuration values; invalid settings silently produce unexpected behavior

### Platform Support

- **Windows Native Build**: Currently Linux-focused; Windows builds require manual TsDuck installation and path configuration
- **ARM64 Optimization**: No ARM NEON intrinsics for sync byte search; falls back to scalar code on Raspberry Pi / Apple Silicon

### Standards Compliance

- **EBU R128 Loudness**: No audio loudness monitoring; could add integrated loudness (LUFS) tracking per EBU R128
- **SCTE-35 Splice Detection**: Ad insertion markers not parsed; would enable ad break detection and reporting
- **Closed Caption Passthrough**: No validation that CEA-608/708 captions survive restamping intact

## Future Plans

### Short Term (Next Release)

1. **Adaptive Quality Thresholds**: Learn "normal" error rates per stream and trigger on deviation rather than fixed thresholds
2. **Metrics Export**: Prometheus/OpenMetrics endpoint for monitoring integration
3. **Warm Standby Connections**: Pre-establish TCP connections to backup URLs to reduce switch latency
4. **PES-Level Analysis**: Extract codec parameters (resolution, frame rate) from PES headers for quality reporting

### Medium Term

1. **HLS/DASH Support**: Extend StreamSource to handle segmented streaming protocols with manifest parsing
2. **GPU-Accelerated Analysis**: CUDA/OpenCL kernels for bulk packet validation on high-density servers
3. **Distributed Failover**: Coordinate URL selection across multiple clients to avoid thundering herd on backup servers
4. **SCTE-35 Detection**: Parse splice_info_section for ad insertion point detection and reporting

### Long Term Vision

1. **WebAssembly Build**: Enable browser-based stream analysis for web-based monitoring dashboards
2. **ML-Based Prediction**: Train models on historical error patterns to predict failures before they occur
3. **Multi-Program Remuxing**: Handle MPTS (multi-program transport streams) with per-program failover
4. **SRT/RIST Support**: Add reliable UDP protocols alongside HTTP for professional contribution feeds

### API Evolution

- **Async/Await C# API**: Replace callback-based interface with modern async streams
- **gRPC Control Plane**: Remote configuration and metrics collection for fleet management
- **Plugin Architecture**: Allow custom analyzers and switch triggers without modifying core library

## Known Limitations

| Limitation | Impact | Workaround |
| --- | --- | --- |
| Single PCR PID | Cannot handle streams with multiple PCR PIDs | Use first PCR PID discovered |
| HTTP Only | No native RTMP/RTSP support | Transcode upstream with FFmpeg |
| No Encryption | Cannot analyze encrypted (CA) streams | Decrypt before analysis |
| 188-byte Packets | No support for 204-byte (FEC) packets | Strip FEC bytes upstream |
| IPv4 Bias | IPv6 URLs may have issues on dual-stack systems | Force IPv4 with curl options |

## License

GPL-3.0-or-later

## Dependencies

- [TsDuck](https://tsduck.io/) - LGPL-2.1
- [libcurl](https://curl.se/libcurl/) - MIT/X derivate
- [GoogleTest](https://github.com/google/googletest) - BSD-3-Clause (tests only)
