# TSDuck Native Integration

This module provides broadcast-grade MPEG-TS stream analysis using [TSDuck](https://tsduck.io/) via native P/Invoke, implementing TR 101 290 monitoring for IPTV quality assurance.

## Architecture

```
TsDuckAnalyzerFactory.Create()
        │
        ├─→ NativeTsDuckAnalyzer (preferred)
        │       └─→ libtsduck_interop.so → libtsduck.so
        │
        └─→ NullTsDuckAnalyzer (fallback)
                └─→ No-op when native unavailable
```

The integration uses native P/Invoke to `libtsduck_interop`, a C wrapper around TSDuck's C++ API, bundled with the plugin for zero-configuration deployment. If the native library is unavailable, the factory gracefully falls back to a no-op analyzer that allows the application to function without metrics.

## Folder Structure

```
TsDuck/
├── README.md                    # This file
├── ITsDuckAnalyzer.cs           # Analyzer interface
├── TsDuckConfiguration.cs       # Configuration options
├── TsDuckMetrics.cs             # Metrics data models (TR 101 290 + Phase 2a)
├── TsDuckAnalyzerFactory.cs     # Factory with fallback
├── NullTsDuckAnalyzer.cs        # No-op fallback analyzer
│
└── Native/                      # P/Invoke implementation
    ├── NativeTsDuckAnalyzer.cs  # Native analyzer
    ├── TsDuckNativeMethods.cs   # P/Invoke declarations
    ├── TsDuckNativeStructures.cs # Blittable structs
    └── TsDuckSafeHandles.cs     # SafeHandle wrappers
```

## Usage

### Basic Usage

```csharp
// Create native analyzer
using var analyzer = TsDuckAnalyzerFactory.Create(config, logger);

// Feed MPEG-TS data
analyzer.FeedData(tsPackets);

// Get metrics
var metrics = analyzer.GetMetrics();
```

### Event-Based Monitoring

```csharp
analyzer.MetricsUpdated += (sender, e) =>
{
    Console.WriteLine($"Bitrate: {e.Metrics.TsBitrate} bps");
};

analyzer.StreamQualityViolation += (sender, e) =>
{
    Console.WriteLine($"Violation: {e.ViolationType} - {e.Details}");
};
```

### Configuration

```csharp
var config = new TsDuckConfiguration
{
    MetricsIntervalSeconds = 5,
    EnableTr101290 = true,
    SampleSizeBytes = 188 * 100,
};
```

## TR 101 290 Metrics

### Priority 1 (Critical)

| Metric | Description |
|--------|-------------|
| SyncByteError | Sync byte (0x47) not found |
| SyncLoss | Loss of synchronization |
| PatError | PAT missing or corrupt |
| ContinuityCountError | Packet sequence errors |
| PmtError | PMT missing or corrupt |
| PidError | Referenced PID not found |

### Priority 2 (Important)

| Metric | Description |
|--------|-------------|
| TransportError | Transport error indicator set |
| CrcError | CRC validation failed |
| PcrRepetitionError | PCR interval too long |
| PcrDiscontinuityError | Unexpected PCR jump |
| PcrAccuracyError | PCR jitter exceeded |
| PtsError | PTS timing error |
| CatError | CAT section error |

## Enhanced Metrics (Phase 2a)

Beyond TR 101 290, the analyzer provides enhanced metrics for proactive quality monitoring.

### PCR Analysis

Program Clock Reference analysis for decoder timing verification.

| Metric | Description |
|--------|-------------|
| PcrJitterUs | Current PCR jitter in microseconds |
| PcrJitterMaxUs | Maximum observed jitter |
| PcrJitterAvgUs | Rolling average jitter |
| PcrIntervalMs | Time between PCRs |
| PcrDriftPpm | Clock drift in parts-per-million |

**Thresholds:**

- TR 101 290 limit: 500ns (0.5us) jitter
- PCR interval limit: 100ms

### IAT Analysis

Inter-packet Arrival Time analysis for network jitter detection.

| Metric | Description |
|--------|-------------|
| IatAvgUs | Average arrival time |
| IatJitterUs | Arrival time variation |
| IatStddevUs | Standard deviation |
| LatePackets | Packets arriving late |
| BurstCount | Packet burst events |

**Use Case:** Detects network congestion before it causes TS-level errors. Critical for UDP/IP IPTV streams.

### Bitrate Analysis

Detailed bandwidth utilization metrics.

| Metric | Description |
|--------|-------------|
| TsBitrateNominal | Measured bitrate |
| TsBitratePcr | PCR-derived bitrate |
| NullPacketRatio | Stuffing overhead (0.0-1.0) |
| UsefulBitrate | Actual content bitrate |
| BitrateAccuracy | PCR vs measured accuracy |

### Enhanced Metrics Example

```csharp
var metrics = analyzer.GetMetrics();

// PCR Analysis
if (metrics?.PcrAnalysis is { } pcr)
{
    if (pcr.HasJitterViolation)
        logger.LogWarning("PCR jitter {Jitter}us exceeds 500ns limit", pcr.PcrJitterUs);
}

// IAT Analysis
if (metrics?.IatAnalysis is { } iat)
{
    if (iat.HasHighJitter)
        logger.LogWarning("Network jitter {Jitter}us detected", iat.IatJitterUs);
}

// Enhanced quality score
int score = metrics?.CalculateQualityScoreV2(
    metrics.PcrAnalysis,
    metrics.IatAnalysis
) ?? 100;
```

## Native Library

### Building libtsduck_interop

```bash
cd native/tsduck_interop
./build.sh
```

### Docker Build

```bash
cd native/tsduck_interop
./build.sh --docker
```

The native library wraps TSDuck's C++ API with a C ABI suitable for P/Invoke.

### Runtime Loading

The native library is loaded from:
1. Assembly directory (plugin folder)
2. System library paths (`/usr/lib`, etc.)

## Performance

| Metric | Value |
|--------|-------|
| Latency | ~1ms |
| CPU Overhead | Low |
| Memory | ~10MB |

Native mode provides minimal overhead, suitable for high-throughput scenarios with many concurrent streams.
