# MPEG-TS Module

## Overview

Production-grade MPEG-TS processing module for live streaming applications. Provides real-time transport stream analysis, keyframe indexing, quality monitoring per TR 101 290, and seamless provider switching with IDR frame alignment.

## Architecture

The module follows Clean Architecture principles with strict dependency rules:

```mermaid
graph TB
    subgraph Infrastructure["Infrastructure Layer"]
        TsIndexer[TsIndexer]
        TimestampTracker[TimestampTracker]
        TsDuckAnalyzer[TsDuck Native Analyzer]
        PcrTimingTracker[PcrTimingTracker]
        ProgramInfoService[ProgramInfoService]
        FFmpegContext[FFmpegContext]
        FFmpegDemuxer[FFmpegStreamDemuxer]
        FFmpegRemuxer[FFmpegStreamRemuxer]
        ProcessorPool[FFmpegProcessorPool]
    end

    subgraph UseCases["UseCases Layer"]
        ITsQualityMonitor[ITsQualityMonitor]
        ITsDemuxer[ITsDemuxer]
        ITsRemuxer[ITsRemuxer]
        IFFmpegContext[IFFmpegContext]
        IProgramInfoService[IProgramInfoService]
        ISystemClock[ISystemClock]
        Events[Events & EventArgs]
    end

    subgraph Parsing["Parsing Layer"]
        PesParser[PesParser]
        CatParser[CatParser]
        StreamTypeClassifier[StreamTypeClassifier]
        TsPacketHelper[TsPacketHelper]
    end

    subgraph Core["Core Layer"]
        RingBuffer[RingBuffer]
        StreamTimestamp[StreamTimestamp]
        SyncPoint[SyncPoint]
        TsConstants[TsConstants]
        AudioCodec[AudioCodec]
        NalUnitType[NalUnitType]
        KeyframeInfo[KeyframeInfo]
    end

    subgraph Models["Models Layer"]
        ProgramInfo[ProgramInfo]
        AudioStreamInfo[AudioStreamInfo]
        CaSystemInfo[CaSystemInfo]
        IdrDetectionResult[IdrDetectionResult]
        StreamSwitchContext[StreamSwitchContext]
        TsPacket[TsPacket]
    end

    Infrastructure --> UseCases
    Infrastructure --> Parsing
    Infrastructure --> Core
    Infrastructure --> Models
    UseCases --> Core
    UseCases --> Models
    Parsing --> Core
    Models --> Core
```

## Data Flow

### Stream Processing Pipeline

```mermaid
flowchart LR
    subgraph Input["Network Input"]
        HTTP[HTTP Response Stream]
    end

    subgraph Buffer["Circular Buffer"]
        Write[WriteStream]
        Ring[(Ring Buffer<br/>32-128 MB)]
        Read1[ReadStream 1]
        Read2[ReadStream 2]
        ReadN[ReadStream N]
    end

    subgraph Processing["MPEG-TS Processing"]
        Indexer[TsIndexer]
        Quality[TsDuck Native Analyzer]
        Timing[PcrTimingTracker]
        Program[ProgramInfoService]
    end

    subgraph Output["Client Output"]
        Client1[Jellyfin Client 1]
        Client2[Jellyfin Client 2]
        ClientN[Jellyfin Client N]
    end

    HTTP --> Write
    Write --> Ring
    Write --> Indexer
    Indexer --> Quality
    Indexer --> Timing
    Indexer --> Program
    Ring --> Read1
    Ring --> Read2
    Ring --> ReadN
    Read1 --> Client1
    Read2 --> Client2
    ReadN --> ClientN
```

### Keyframe Detection Flow

```mermaid
sequenceDiagram
    participant Net as Network
    participant Buf as CircularBuffer
    participant Idx as TsIndexer
    participant PAT as PAT Parser
    participant PMT as PMT Parser
    participant PES as PES Parser
    participant KF as Keyframe Ring

    Net->>Buf: Write TS packets
    Buf->>Idx: ProcessChunk(data)

    loop For each 188-byte packet
        Idx->>Idx: Check sync byte (0x47)

        alt PID 0x0000 (PAT)
            Idx->>PAT: Parse Program Association
            PAT-->>Idx: PMT PIDs
        else PMT PID
            Idx->>PMT: Parse Program Map
            PMT-->>Idx: Video/Audio/PCR PIDs
        else Video PID with RAI=1
            Idx->>PES: Extract PTS/DTS
            PES-->>Idx: Timestamps
            Idx->>KF: Store KeyframeInfo
        else PCR PID
            Idx->>Idx: Track PCR timing
        end
    end

    Note over Idx,KF: Ring buffer holds ~30 keyframes
```

### Provider Switch Flow

```mermaid
sequenceDiagram
    participant TsDuck as TsDuck Analyzer
    participant Trigger as ViolationSwitchTrigger
    participant Switch as ProviderSwitchService
    participant Align as AlignedStreamSwitcher
    participant Pool as PreconnectPool
    participant New as New Provider

    TsDuck->>TsDuck: Detect quality violation
    TsDuck->>Trigger: OnQualityViolation
    Trigger->>Trigger: Check threshold (3 violations/5s)
    Trigger->>Switch: TriggerSwitch(channelId)

    Switch->>Pool: GetWarmConnection(nextProvider)
    Pool-->>Switch: Pre-established connection

    Switch->>Align: PrepareSwitch(oldStream, newStream)
    Align->>Align: Wait for IDR frame
    Align->>Align: Calculate timestamp offset

    Note over Align: Ensures decoder continuity

    Align-->>Switch: SwitchContext ready
    Switch->>New: Activate new stream
    Switch->>Switch: Update routing
```

## Module Structure

```
MpegTs/
├── Core/                    # Domain primitives (no dependencies)
│   ├── AudioCodec.cs        # Audio codec enumeration
│   ├── ClockStatus.cs       # PCR clock state
│   ├── KeyframeInfo.cs      # Keyframe metadata
│   ├── NalUnitType.cs       # H.264/HEVC NAL types
│   ├── PictureCodingType.cs # MPEG-2 picture types
│   ├── RingBuffer.cs        # Lock-free circular buffer
│   ├── StreamCategory.cs    # Stream classification
│   ├── StreamTimestamp.cs   # PTS/DTS wrapper
│   ├── SyncPoint.cs         # Buffer sync position
│   ├── SyncStatus.cs        # A/V sync state
│   └── TsConstants.cs       # MPEG-TS magic numbers
│
├── Models/                  # Data transfer objects
│   ├── AudioFrameInfo.cs    # Audio frame metadata
│   ├── AudioStreamInfo.cs   # Audio stream details
│   ├── CaSystemInfo.cs      # Conditional access info
│   ├── CachedParameterSets.cs # SPS/PPS cache
│   ├── IdrDetectionResult.cs  # IDR frame detection
│   ├── ProgramInfo.cs       # Program/stream mapping
│   ├── RemappingResult.cs   # Timestamp remapping
│   ├── StreamSwitchContext.cs # Provider switch state
│   ├── StreamSyncPoint.cs   # Aligned sync position
│   ├── TimestampOffset.cs   # PTS/DTS offset
│   ├── TsIndexerMetrics.cs  # Performance counters
│   └── TsPacket.cs          # Parsed packet structure
│
├── Parsing/                 # Pure parsing utilities
│   ├── CatParser.cs         # Conditional Access Table
│   ├── PesParser.cs         # PES header extraction
│   ├── StreamTypeClassifier.cs # Stream type detection
│   └── TsPacketHelper.cs    # Low-level packet ops
│
├── UseCases/                # Interface contracts
│   ├── IContinuityMonitor.cs
│   ├── ICrcMonitor.cs
│   ├── IEncryptionDetector.cs
│   ├── IFFmpegContext.cs
│   ├── IInProcessRemuxer.cs
│   ├── IProgramInfoService.cs
│   ├── IQualityEventSource.cs
│   ├── IStreamRemuxer.cs
│   ├── IStreamStatistics.cs
│   ├── ISyncMonitor.cs
│   ├── ISystemClock.cs
│   ├── ITsDemuxer.cs
│   ├── ITsQualityMonitor.cs
│   ├── ITsRemuxer.cs
│   ├── ITsTimestampPatcher.cs
│   ├── DemuxedPacketEventArgs.cs
│   ├── DemuxerProgramEventArgs.cs
│   ├── PtsDiscontinuityEventArgs.cs
│   ├── StreamQualityViolationEventArgs.cs
│   └── SyncDriftEventArgs.cs
│
└── Infrastructure/          # Service implementations
    ├── DiscontinuityInjector.cs
    ├── FFmpegContext.cs
    ├── FFmpegContextAdapter.cs
    ├── FFmpegFrameDetector.cs
    ├── FFmpegInitializationService.cs
    ├── FFmpegParameterSetExtractor.cs
    ├── FFmpegProcessorBase.cs
    ├── FFmpegStreamDemuxer.cs
    ├── FFmpegStreamRemuxer.cs
    ├── PacketStatistics.cs
    ├── PcrTimingTracker.cs
    ├── ProgramInfoService.cs
    ├── StopwatchClock.cs
    ├── TimestampTracker.cs
    ├── TsIndexer.cs
    ├── TsTimestampPatcher.cs
    └── Pooling/
        ├── FFmpegProcessorKind.cs
        ├── FFmpegProcessorPool.cs
        ├── IPoolable.cs
        ├── IPooledFFmpegProcessor.cs
        ├── ObjectPool.cs
        ├── PoolLease.cs
        ├── PoolOptions.cs
        ├── PoolStatistics.cs
        └── PooledFFmpegProcessor.cs
```

## Features

### Industry-Grade Capabilities

| Feature | Description |
|---------|-------------|
| **Multi-Program (MPTS)** | Independent tracking per program in multi-program streams |
| **Packet Reassembly** | Handles TCP fragmentation across 188-byte boundaries |
| **Lock-Free Design** | Zero contention using atomic operations and ring buffers |
| **SIMD Optimization** | AVX-512/AVX2/SSE2 accelerated sync byte search |
| **Zero-Allocation** | Hot paths avoid GC through fixed buffers and unsafe code |
| **TR 101 290** | Broadcast quality monitoring per ETSI standard |
| **A/V Sync** | Real-time audio/video drift detection (EBU R37) |
| **FFmpeg Integration** | Native demuxing via FFmpeg.AutoGen bindings |

### Solves Common Problems

```mermaid
flowchart TD
    subgraph Problem1["Sound But No Video"]
        P1A[Client connects mid-stream]
        P1B[Starts at P-frame or B-frame]
        P1C[Decoder waits for I-frame]
        P1D[2-5 second delay]
        P1A --> P1B --> P1C --> P1D
    end

    subgraph Solution1["Keyframe-Aligned Start"]
        S1A[Client connects]
        S1B[TsIndexer.GetBestSyncPoint]
        S1C[Seek to nearest I-frame]
        S1D[Instant video playback]
        S1A --> S1B --> S1C --> S1D
    end

    subgraph Problem2["False Quality Alerts"]
        P2A[HTTP reconnection]
        P2B[Old PTS compared to new PTS]
        P2C[Impossible drift readings]
        P2A --> P2B --> P2C
    end

    subgraph Solution2["Reconnection-Aware"]
        S2A[HTTP reconnection]
        S2B[ResetTimingState called]
        S2C[Fresh baseline established]
        S2A --> S2B --> S2C
    end

    Problem1 -.->|Solved by| Solution1
    Problem2 -.->|Solved by| Solution2
```

## Usage

### Initialization

```csharp
// TsIndexer is automatically created by CircularBufferWriteStream
var writeStream = new CircularBufferWriteStream(
    bufferSize: 64 * 1024 * 1024,  // 64MB for HD
    loggerFactory: loggerFactory
);

// Access the indexer for monitoring
var indexer = writeStream.TsIndexer;
```

### Writing Data

```csharp
// Indexing happens automatically during writes
await writeStream.WriteAsync(networkData, cancellationToken);
```

### Reading from Optimal Position

```csharp
// ReadStream auto-seeks to nearest keyframe
var readStream = new CircularBufferReadStream(
    writeStream,
    logger,
    streamId,
    channelName,
    discordService,
    programNumber: -1  // -1 = first program
);
```

### Multi-Program Streams

```csharp
// Get all detected programs
int[] programs = indexer.GetProgramNumbers();

// Get info for specific program
int videoPid = indexer.GetVideoPid(programNumber: 2);
int keyframes = indexer.GetKeyframeCount(programNumber: 2);
SyncStatus status = indexer.GetSyncStatus(programNumber: 2);
double driftMs = indexer.GetCurrentDriftMs(programNumber: 2);
```

### Quality Monitoring

```csharp
// Subscribe to quality events
indexer.StreamQualityViolation += (sender, e) => {
    logger.LogWarning("Quality issue: {Type} - {Details}",
        e.ViolationType, e.Details);
};

indexer.SyncDriftDetected += (sender, e) => {
    logger.LogWarning("A/V drift: {DriftMs}ms, Status: {Status}",
        e.DriftMs, e.Status);
};

// Get diagnostics
string diagnostics = indexer.GetDiagnostics();
```

### Handling Reconnections

```csharp
// Prevent false drift readings after reconnection
if (isReconnection)
{
    writeStream.TsIndexer.ResetTimingState();
}
```

## Performance

### Optimizations

| Technique | Impact |
|-----------|--------|
| SIMD sync search | 10-40x faster than scalar |
| Unsafe pointer arithmetic | Bounds-check elimination |
| 32-bit start code detection | Single comparison vs 3 bytes |
| Fixed-size ring buffers | Zero allocations in hot path |
| Lookup table classification | O(1) stream type detection |
| Cache-line padding | No false sharing |
| Power-of-2 buffers | Bitwise AND vs modulo |

### Metrics

| Metric | Value |
|--------|-------|
| CPU Overhead | ~0.03-0.05% @ 20 Mbps |
| Memory per Program | ~2-4 KB |
| Keyframe Lookup | < 1 us |
| Sync Byte Search | ~10-40 GB/s (SIMD) |
| Packet Parsing | ~5-10 GB/s |
| Hot Path Allocations | 0 bytes |

## TR 101 290 Compliance

### Priority 1 Checks

| Check | Description | Threshold |
|-------|-------------|-----------|
| Transport Error (TEI) | Uncorrectable packet errors | Any occurrence |
| Continuity Counter | Packet sequence gaps | Any discontinuity |
| PCR Jitter | Clock stability | +/- 500ns |
| PCR PID | Declared PID carries PCR | Missing PCR |

### Priority 2 Checks

| Check | Description | Threshold |
|-------|-------------|-----------|
| PAT Version | Program table changes | Version increment |
| PMT Version | Stream map changes | Version increment |
| PCR Interval | Clock update frequency | > 100ms |

### A/V Synchronization States

| Status | Description | Threshold |
|--------|-------------|-----------|
| Synchronized | Within spec | +/- 20ms (EBU R37) |
| AudioAhead | Audio leads video | > 20ms |
| AudioBehind | Video leads audio | > 20ms |
| Drifting | Accumulating offset | > 40ms/minute |

## Supported Codecs

### Video (stream_type)

| Codec | Type | Notes |
|-------|------|-------|
| H.264/AVC | 0x1B | Most common |
| HEVC/H.265 | 0x24 | 4K/HDR |
| VVC/H.266 | 0x33 | Next-gen |
| MPEG-2 | 0x02 | Legacy broadcast |
| MPEG-4 Visual | 0x10 | Rare |

### Audio (stream_type)

| Codec | Type | Notes |
|-------|------|-------|
| AAC ADTS | 0x0F | Common |
| AAC LATM | 0x11 | DVB standard |
| AC-3 | 0x81 | Dolby Digital |
| E-AC-3 | 0x84/0x87 | Dolby Digital+ |
| DTS | 0x82 | Lossless option |
| MP2 | 0x04 | Legacy |

## Dependency Rules

```mermaid
graph BT
    Core["Core<br/>(No deps)"]
    Models["Models"]
    Parsing["Parsing"]
    UseCases["UseCases"]
    Infrastructure["Infrastructure"]

    Models --> Core
    Parsing --> Core
    UseCases --> Core
    UseCases --> Models
    Infrastructure --> Core
    Infrastructure --> Models
    Infrastructure --> Parsing
    Infrastructure --> UseCases

    style Core fill:#90EE90
    style Models fill:#87CEEB
    style Parsing fill:#87CEEB
    style UseCases fill:#FFD700
    style Infrastructure fill:#FFA07A
```

**Rules:**
- Core has no dependencies (only BCL)
- Inner layers cannot reference outer layers
- External libraries (FFmpeg) isolated in Infrastructure
- Interfaces defined in UseCases, implemented in Infrastructure

## Areas of Improvement

### Current Limitations

```mermaid
flowchart TD
    subgraph Limitations["Current Limitations"]
        L1[RAI-based keyframe detection]
        L2[No SPS/PPS caching]
        L3[Single-threaded PSI parsing]
        L4[5MB FFmpeg probesize]
        L5[No HEVC tile awareness]
        L6[Basic CA detection only]
    end

    subgraph Impact["Impact"]
        I1[Cannot inject parameter sets]
        I2[Decoder errors on switch]
        I3[CPU bottleneck at high bitrate]
        I4[0.5-2s startup delay]
        I5[Suboptimal 4K switching]
        I6[No decryption support]
    end

    L1 --> I1
    L2 --> I2
    L3 --> I3
    L4 --> I4
    L5 --> I5
    L6 --> I6
```

| Limitation | Current State | Desired State |
|------------|---------------|---------------|
| Keyframe Detection | RAI flag only | NAL unit parsing for IDR/CRA |
| Parameter Sets | Not cached | Cache SPS/PPS/VPS per stream |
| PSI Parsing | Synchronous | Async with pipeline |
| FFmpeg Init | Blocking 5MB | Streaming probe with timeout |
| HEVC Support | Basic | Tile-aware slice detection |
| Encryption | Detect only | DVB-CI decryption hooks |
| Subtitles | Passthrough | DVB-SUB/teletext extraction |

### Technical Debt

1. **TsIndexer Size** - Single file with multiple responsibilities, should be split into:
   - `PatPmtParser` - PSI table handling
   - `KeyframeIndexer` - RAI/IDR detection
   - `QualityMonitor` - TR 101 290 checks
   - `TimingTracker` - PCR/PTS/DTS management

2. **FFmpeg Coupling** - Infrastructure layer tightly coupled to FFmpeg.AutoGen:
   - Should abstract behind `IFrameDetector` interface
   - Allow pure managed fallback for simple cases

3. **Ring Buffer Generics** - `RingBuffer<T>` is specialized for `KeyframeInfo`:
   - Could be generic for reuse in other contexts
   - Memory layout optimization opportunities

## Future Plans

### Short-Term Improvements

```mermaid
gantt
    title MPEG-TS Module Roadmap
    dateFormat YYYY-MM
    section Parsing
        NAL unit parsing     :2025-01, 2025-02
        SPS/PPS caching      :2025-02, 2025-03
        HEVC slice detection :2025-03, 2025-04
    section Performance
        Async PSI pipeline   :2025-02, 2025-03
        SIMD PES parsing     :2025-03, 2025-04
        Parallel demux       :2025-04, 2025-05
    section Features
        DVB subtitle extract :2025-03, 2025-04
        SCTE-35 markers      :2025-04, 2025-05
        CA module hooks      :2025-05, 2025-06
```

### Planned Enhancements

| Feature | Description | Priority |
|---------|-------------|----------|
| NAL Unit Parser | Parse H.264/HEVC NAL units for precise IDR detection | High |
| Parameter Set Cache | Cache SPS/PPS/VPS per PID for injection on switch | High |
| Async PSI Pipeline | Non-blocking PAT/PMT parsing with events | Medium |
| SIMD PES Parsing | AVX2-accelerated PES header extraction | Medium |
| DVB-SUB Extraction | Parse DVB subtitles for Jellyfin display | Medium |
| SCTE-35 Detection | Ad marker detection for future ad replacement | Low |
| CA Module Hooks | Interface for external decryption modules | Low |

### Architecture Goals

```mermaid
graph TB
    subgraph Current["Current Design"]
        TsIndexer1[TsIndexer<br/>~2500 lines]
    end

    subgraph Future["Target Design"]
        subgraph Parsers["Parsing Layer"]
            PatParser[PatPmtParser]
            PesParser2[PesParser]
            NalParser[NalUnitParser]
        end

        subgraph Indexers["Indexing Layer"]
            KeyframeIdx[KeyframeIndexer]
            SubtitleIdx[SubtitleIndexer]
            MarkerIdx[ScteMarkerIndexer]
        end

        subgraph Monitors["Monitoring Layer"]
            QualityMon[QualityMonitor]
            TimingMon[TimingMonitor]
            SyncMon[SyncMonitor]
        end

        subgraph Orchestrator["Orchestration"]
            Pipeline[StreamPipeline]
        end

        Pipeline --> Parsers
        Parsers --> Indexers
        Indexers --> Monitors
    end

    Current -.->|Refactor| Future
```

## References

- ISO/IEC 13818-1: MPEG-2 Systems
- ITU-T H.222.0: Generic coding of moving pictures
- DVB BlueBook A038: Random Access Indicator
- ETSI TR 101 290: DVB measurement guidelines
- EBU R37: Audio/video sync tolerance
- ITU-T H.264: AVC NAL unit specification
- ITU-T H.265: HEVC NAL unit specification
- SCTE-35: Digital Program Insertion Cueing
