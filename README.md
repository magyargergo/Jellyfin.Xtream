# Jellyfin.Xtream

![GitHub Downloads (all assets, all releases)](https://img.shields.io/github/downloads/Kevinjil/Jellyfin.Xtream/total)
![GitHub Downloads (all assets, latest release)](https://img.shields.io/github/downloads/Kevinjil/Jellyfin.Xtream/latest/total)
![GitHub commits since latest release](https://img.shields.io/github/commits-since/Kevinjil/Jellyfin.Xtream/latest)
![Dynamic YAML Badge](https://img.shields.io/badge/dynamic/yaml?url=https%3A%2F%2Fraw.githubusercontent.com%2FKevinjil%2FJellyfin.Xtream%2Frefs%2Fheads%2Fmaster%2Fbuild.yaml&query=targetAbi&label=Jellyfin%20ABI)
![Dynamic YAML Badge](https://img.shields.io/badge/dynamic/yaml?url=https%3A%2F%2Fraw.githubusercontent.com%2FKevinjil%2FJellyfin.Xtream%2Frefs%2Fheads%2Fmaster%2Fbuild.yaml&query=framework&label=.NET%20framework)

The Jellyfin.Xtream plugin integrates content from [Xtream-compatible APIs](https://xtream-ui.org/api-xtreamui-xtreamcode/) into your [Jellyfin](https://jellyfin.org/) instance.

## Acknowledgments

This plugin was originally created by [Kevinjil](https://github.com/Kevinjil). Thank you for the excellent foundation and continued work on this project!

## Features

- **Multi-Provider Support**: Configure multiple Xtream providers with automatic failover and quality-based channel deduplication
- **Live TV, VOD, Series & Catch-up**: Full support for all Xtream content types
- **Smart Restreaming**: Single connection to provider with multi-client broadcasting (solves HTTP 406 errors)
- **EPG Support**: Multiple EPG providers with fallback chain and external XMLTV support
- **Stream Quality Monitoring**: Real-time MPEG-TS quality analysis with TR 101 290 compliance
- **SCTE-35 Ad Marker Detection**: Real-time splice point detection for ad break notifications
- **NAL Unit Parsing**: H.264/H.265 parameter set extraction for faster decoder initialization
- **Automatic Provider Switching**: Quality-driven failover with IDR frame alignment
- **Fast Channel Change**: Connection pre-warming for sub-100ms provider switches
- **Discord Notifications**: Stream health alerts, EPG refresh status, and periodic health reports
- **Proxy Support**: Route traffic through HTTP proxy with authentication
- **User-Agent Rotation**: Rotate through realistic browser User-Agents to avoid WAF blocking
- **Rate Limiting**: Token bucket rate limiting to prevent provider bans
- **Connection Management**: State tracking, connection limits, and auto-kill of oldest streams

## System Architecture

### High-Level Overview

```mermaid
graph TB
    subgraph Jellyfin["Jellyfin Server"]
        LiveTV[LiveTvService]
        API[XtreamController]
        VOD[XtreamVodProvider]
    end

    subgraph Plugin["Jellyfin.Xtream Plugin"]
        subgraph Streaming["Streaming Layer"]
            StreamService[StreamService]
            Restream[Restream Manager]
            WriteBuffer[CircularBufferWriteStream]
            ReadBuffer[CircularBufferReadStream]
        end

        subgraph Providers["Provider Management"]
            Failover[AutomaticFailoverService]
            Switch[ProviderSwitchService]
            Trigger[ViolationSwitchTrigger]
            Pool[PreconnectPool]
            Warmup[ChannelWarmupService]
        end

        subgraph Quality["Quality Monitoring"]
            TsIndexer[TsIndexer]
            TsDuck[TsDuck Native Analyzer]
            PcrTracker[PcrTimingTracker]
            Scte35[SCTE-35 Monitor]
            NalParser[NAL Parser]
        end

        subgraph EPG["EPG Services"]
            Composite[CompositeEpgProvider]
            Xtream[XtreamEpgProvider]
            External[ExternalXmltvEpgProvider]
        end
    end

    subgraph External["External Services"]
        Provider1[(Provider 1)]
        Provider2[(Provider 2)]
        ProviderN[(Provider N)]
        Discord[Discord Webhook]
        EPGSource[XMLTV Source]
    end

    subgraph Clients["Jellyfin Clients"]
        Client1[Client 1]
        Client2[Client 2]
        ClientN[Client N]
    end

    LiveTV --> StreamService
    API --> StreamService
    StreamService --> Restream
    Restream --> WriteBuffer
    WriteBuffer --> ReadBuffer
    ReadBuffer --> Client1
    ReadBuffer --> Client2
    ReadBuffer --> ClientN

    WriteBuffer --> TsIndexer
    TsIndexer --> TsDuck
    TsDuck --> Trigger
    Trigger --> Switch
    Switch --> Pool
    Pool --> Provider1
    Pool --> Provider2

    Failover --> Provider1
    Failover --> Provider2
    Failover --> ProviderN

    Composite --> Xtream
    Composite --> External
    External --> EPGSource

    TsDuck -.-> Discord
```

### Restreaming Architecture

The plugin uses a native C++ streaming layer that handles HTTP connections, failover, and quality monitoring. Data flows from the native layer to C# via direct callbacks (188-byte aligned TS packets), then broadcasts to multiple Jellyfin clients using an optimized circular buffer.

```mermaid
flowchart LR
    subgraph Provider["Xtream Providers"]
        P1[(Provider 1)]
        P2[(Provider 2)]
        PN[(Provider N)]
    end

    subgraph Native["TsDuck Native Layer (C++)"]
        direction TB
        Pipeline[StreamPipeline]
        Source[StreamSource<br/>Health-based URL selection]
        Failover[FailoverManager<br/>Automatic rotation]
        Quality[QualityTrigger<br/>TR 101 290]
        Aligner[PacketAligner<br/>188-byte alignment]
        Restamper[Restamper<br/>Timestamp correction]
        Analyzer[Analyzer<br/>SCTE-35 + NAL]
    end

    subgraph Callbacks["Native Callbacks"]
        DataCB[OnNativeDataReceived<br/>TS packets]
        EventCB[OnStreamEvent<br/>State changes]
    end

    subgraph Managed["Managed Layer (C#)"]
        direction TB
        Restream[Restream Manager]
        HealthScorer[ProviderHealthScorer]
        Write[CircularBuffer<br/>WriteStream]
        Ring[(Ring Buffer<br/>32-128 MB)]
        Sessions[ReaderSessionManager]
        Read1[ReadStream 1]
        Read2[ReadStream 2]
        ReadN[ReadStream N]
    end

    subgraph Clients["Jellyfin Clients"]
        C1[Client 1<br/>Web]
        C2[Client 2<br/>Mobile]
        CN[Client N<br/>TV]
    end

    P1 --> Source
    P2 --> Source
    PN --> Source
    Source --> Pipeline
    Pipeline --> Failover
    Pipeline --> Quality
    Pipeline --> Aligner
    Pipeline --> Restamper
    Pipeline --> Analyzer

    Aligner --> DataCB
    Pipeline --> EventCB

    DataCB --> Restream
    EventCB --> Restream
    EventCB --> HealthScorer
    Restream --> Write
    Write --> Ring

    Ring --> Sessions
    Sessions --> Read1
    Sessions --> Read2
    Sessions --> ReadN

    Read1 --> C1
    Read2 --> C2
    ReadN --> CN
```

**Buffer sizes by quality:**
- SD streams: 32 MB (~45 seconds at 6 Mbps)
- HD streams: 64 MB (~25 seconds at 20 Mbps)
- 4K/UHD streams: 128 MB (~20 seconds at 50 Mbps)

### Provider Failover System

```mermaid
stateDiagram-v2
    [*] --> Healthy: Provider Online

    Healthy --> Degraded: Quality Violations
    Healthy --> Failed: Connection Error

    Degraded --> Healthy: Quality Restored
    Degraded --> Switching: Threshold Exceeded

    Failed --> Switching: Immediate

    Switching --> WaitIDR: Find Sync Point
    WaitIDR --> Aligned: IDR Frame Found
    Aligned --> Healthy: New Provider Active

    Switching --> Cooldown: All Providers Failed
    Cooldown --> Switching: Cooldown Expired

    note right of Degraded
        TR 101 290 violations:
        - PCR jitter > 500ns
        - Continuity errors
        - Transport errors
    end note

    note right of WaitIDR
        IDR frame alignment
        prevents decoder glitches
    end note
```

### Provider Switch Sequence

```mermaid
sequenceDiagram
    participant TsDuck as TsDuck Analyzer
    participant Trig as ViolationSwitchTrigger
    participant Svc as ProviderSwitchService
    participant Fail as AutomaticFailoverService
    participant Pool as PreconnectPool
    participant Align as AlignedStreamSwitcher
    participant Old as Current Provider
    participant New as Backup Provider

    TsDuck->>TsDuck: Detect quality violation
    TsDuck->>Trig: OnStreamQualityViolation

    Trig->>Trig: Increment violation counter
    Note over Trig: 3 violations in 5 seconds

    Trig->>Svc: TriggerSwitch(channelId)

    Svc->>Fail: GetNextProvider(channelId)
    Fail-->>Svc: Provider info

    Svc->>Pool: GetWarmConnection(provider)
    Note over Pool: Pre-established connection
    Pool-->>Svc: Warm HTTP stream

    Svc->>Align: PrepareSwitch(oldStream, newStream)

    Align->>New: Read until IDR frame
    Note over Align: Scan for RAI=1 or<br/>NAL unit type 5 (H.264)

    Align->>Align: Calculate PTS/DTS offset
    Note over Align: Ensure timestamp continuity

    Align-->>Svc: StreamSwitchContext

    Svc->>Old: Dispose connection
    Svc->>Svc: Route to new stream

    Svc-->>TsDuck: Switch complete
```

### EPG Provider Chain

```mermaid
flowchart TD
    subgraph Request["EPG Request"]
        Channel[Channel ID]
    end

    subgraph Composite["CompositeEpgProvider"]
        direction TB
        Check1{Xtream EPG<br/>Available?}
        Check2{External XMLTV<br/>Available?}
        Check3{Cached Data<br/>Valid?}
    end

    subgraph Sources["EPG Sources"]
        Xtream[XtreamEpgProvider<br/>get_short_epg API]
        External[ExternalXmltvEpgProvider<br/>epg.ovh / custom]
        Cache[(Memory Cache)]
    end

    subgraph Result["EPG Data"]
        Programs[Program Listings]
        Logos[Channel Logos]
    end

    Channel --> Check1
    Check1 -->|Yes| Xtream
    Check1 -->|No| Check2
    Check2 -->|Yes| External
    Check2 -->|No| Check3
    Check3 -->|Yes| Cache
    Check3 -->|No| Empty[Empty Result]

    Xtream --> Programs
    External --> Programs
    Cache --> Programs
    Programs --> Logos
```

## Installation

The plugin can be installed using a custom plugin repository.

### Add Repository

1. Open your admin dashboard and navigate to `Plugins`.
2. Select the `Repositories` tab on the top of the page.
3. Click the `+` symbol to add a repository.
4. Enter `Jellyfin.Xtream` as the repository name.
5. Enter [`https://kevinjil.github.io/Jellyfin.Xtream/repository.json`](https://kevinjil.github.io/Jellyfin.Xtream/repository.json) as the repository URL.
6. Click save.

### Install Plugin

1. Open your admin dashboard and navigate to `Plugins`.
2. Select the `Catalog` tab on the top of the page.
3. Under `Live TV`, select `Jellyfin Xtream`.
4. (Optional) Select the desired plugin version.
5. Click `Install`.
6. Restart your Jellyfin server to complete the installation.

## Configuration

### Provider Setup

Configure one or more Xtream-compatible providers in the plugin settings.

| Property | Description |
|----------|-------------|
| Base URL | The API endpoint URL (excluding trailing slash), including protocol (http/https) |
| Username | The username for API authentication |
| Password | The password for API authentication |

### Content Selection

#### Live TV

1. Open the `Live TV` configuration tab.
2. Select the categories or individual channels you want available.
3. Click `Save`.
4. (Optional) Open `TV Overrides` to customize channel numbers, names, and icons.

#### Video On-Demand

1. Open the `Video On-Demand` configuration tab.
2. Enable `Show this channel to users`.
3. Select the categories or individual videos.
4. Click `Save`.

#### Series

1. Open the `Series` configuration tab.
2. Enable `Show this channel to users`.
3. Select the categories or individual series.
4. Click `Save`.

#### TV Catch-up

1. Open the `Live TV` configuration tab.
2. Enable `Show the catch-up channel to users`.
3. Click `Save`.

### Advanced Configuration

#### Multi-Provider Settings

| Setting | Description | Default |
|---------|-------------|---------|
| Merge Duplicate Channels | Combine same-name channels from different providers | Off |
| Enable Provider Failover | Automatically switch to backup provider on failure | Off |
| Max Failover Attempts | Maximum retry attempts before giving up | 3 |
| Quality-Driven Switching | Switch providers based on TR 101 290 violations | Off |
| Violation Threshold | Number of violations before triggering switch | 3 |
| Violation Window | Time window for counting violations (seconds) | 5 |

#### Fast Channel Change

| Setting | Description | Default |
|---------|-------------|---------|
| Enable Preconnection | Warm backup provider connections | Off |
| Preconnect Channels | Number of channels to pre-warm | 5 |
| Connection TTL | How long to keep warm connections (seconds) | 30 |

#### Proxy Settings

| Setting | Description | Default |
|---------|-------------|---------|
| Enable Proxy | Route traffic through HTTP proxy | Off |
| Proxy Address | Proxy server hostname or IP | - |
| Proxy Port | Proxy server port | 8080 |
| Proxy Username | Optional proxy authentication | - |
| Proxy Password | Optional proxy authentication | - |
| Bypass Local | Skip proxy for local addresses | On |

#### User-Agent Settings

| Setting | Description | Default |
|---------|-------------|---------|
| Custom User-Agent | Override with specific User-Agent string | - |
| Enable Rotation | Rotate through browser User-Agents | Off |
| Random Selection | Use random (weighted by market share) vs sequential | On |

#### Rate Limiting

| Setting | Description | Default |
|---------|-------------|---------|
| Enable Rate Limiting | Limit API requests to prevent bans | Off |
| Requests Per Second | Maximum requests per second | 5 |
| Burst Size | Initial burst allowance | 20 |

#### Connection Limits

| Setting | Description | Default |
|---------|-------------|---------|
| Enforce Connection Limit | Limit concurrent streams | Off |
| Max Concurrent Streams | Maximum active streams (0 = use provider limit) | 0 |
| Auto-Kill Oldest | Terminate oldest stream when limit reached | On |

#### EPG Settings

| Setting | Description | Default |
|---------|-------------|---------|
| Enable External EPG | Use external XMLTV source as fallback | Off |
| External EPG URL | XMLTV source URL | - |
| Logo Base URL | Base URL for logo fallback | - |
| Use Logo Fallback | Fall back to external logos when missing | On |

#### Discord Notifications

| Setting | Description | Default |
|---------|-------------|---------|
| Enable Notifications | Send Discord webhook notifications | Off |
| Webhook URL | Discord webhook endpoint | - |
| Notify on Buffer Overflow | Alert on restream buffer issues | On |
| Notify on Stream Error | Alert on stream failures | On |
| Notify on Stream Killed | Alert when streams are terminated | On |
| Notify on Quality Violation | Alert on TR 101 290 violations | On |
| Notify on A/V Drift | Alert on audio/video sync issues | On |
| Notify on EPG Refresh | Alert on EPG refresh completion | On |
| Notify on SCTE-35 Event | Alert on ad break start/end | Off |
| Periodic Health Reports | Send regular health summaries | Off |
| Health Report Interval | Minutes between health reports | 60 |

## Stream Quality Monitoring

Real-time MPEG-TS analysis following TR 101 290 standards:

```mermaid
flowchart TD
    subgraph Input["Transport Stream"]
        Packets[TS Packets<br/>188 bytes each]
    end

    subgraph Priority1["Priority 1 Checks"]
        TEI[Transport Error Indicator]
        CC[Continuity Counter]
        PCR[PCR Accuracy]
        PCRPID[PCR PID Validation]
    end

    subgraph Priority2["Priority 2 Checks"]
        PAT[PAT Version]
        PMT[PMT Version]
        PCRInt[PCR Interval]
    end

    subgraph Actions["Violation Actions"]
        Log[Log Warning]
        Discord[Discord Alert]
        Counter[Increment Counter]
        Switch{Threshold<br/>Exceeded?}
        Failover[Trigger Failover]
    end

    Packets --> TEI
    Packets --> CC
    Packets --> PCR
    Packets --> PCRPID
    Packets --> PAT
    Packets --> PMT
    Packets --> PCRInt

    TEI --> Log
    CC --> Log
    PCR --> Log
    PCRPID --> Log

    Log --> Discord
    Log --> Counter
    Counter --> Switch
    Switch -->|Yes| Failover
    Switch -->|No| Continue[Continue Monitoring]
```

### SCTE-35 Ad Marker Detection

Real-time detection of SCTE-35 splice points for ad break management:

| Event Type | Command | Description |
|------------|---------|-------------|
| Splice Insert | 0x05 | Primary ad insertion/return command |
| Time Signal | 0x06 | Time-based splice point |
| Splice Schedule | 0x04 | Scheduled future splice events |

Features:
- Automatic SCTE-35 PID detection from PMT (stream type 0x86 or CUEI descriptor)
- Splice state tracking (In Content, In Ad Break, Transitioning)
- Event ring buffer with thread-safe access
- Callback support for real-time notifications

### NAL Unit Parsing & Codec Detection

Automatic extraction of video codec parameters for optimized playback:

| Codec | Parameter Sets | Detection |
|-------|---------------|-----------|
| H.264/AVC | SPS, PPS | NAL types 7, 8 |
| H.265/HEVC | VPS, SPS, PPS | NAL types 32, 33, 34 |
| H.266/VVC | (Future) | - |

Extracted Information:
- Profile and level
- Resolution (with cropping/conformance window)
- Frame rate (from VUI timing info)
- Interlaced flag
- IDR frame detection (more accurate than RAI flag)

Benefits:
- Faster decoder initialization with cached parameter sets
- Accurate keyframe detection for seamless provider switching
- Codec information for quality reporting

### Monitored Metrics

| Check | Priority | Description | Threshold |
|-------|----------|-------------|-----------|
| Transport Error (TEI) | 1 | Uncorrectable packet errors | Any occurrence |
| Continuity Counter | 1 | Packet sequence discontinuities | Any gap |
| PCR Jitter | 1 | Clock reference stability | +/- 500ns |
| PCR PID | 1 | Declared PID carries PCR | Missing PCR |
| PAT Version | 2 | Program table changes | Version change |
| PMT Version | 2 | Stream map changes | Version change |
| PCR Interval | 2 | Clock update frequency | > 100ms |
| A/V Drift | - | Audio/video sync | > 20ms (EBU R37) |
| SCTE-35 Events | - | Ad break splice points | Event-based |
| IDR Frames | - | Keyframe detection | Per-PID tracking |
| Codec Info | - | Video parameters | SPS/VPS parsing |

## Project Structure

```mermaid
graph TD
    subgraph Plugin["Jellyfin.Xtream"]
        Root[Plugin Entry]

        subgraph API["API Layer"]
            Controller[XtreamController]
            Models[Request/Response Models]
        end

        subgraph Services["Service Layer"]
            Stream[StreamService]
            Channel[ChannelProviderMap]
            EPG[EPG Services]
        end

        subgraph Provider["Provider Management"]
            Failover[AutomaticFailoverService]
            Switch[ProviderSwitchService]
            Health[ProviderAvailabilityService]
            Metrics[ProviderMetricsTracker]
        end

        subgraph MpegTs["MPEG-TS Module"]
            Core[Core Types]
            Parsing[Parsers]
            Infra[Infrastructure]
            UseCases[Interfaces]
        end

        subgraph Buffer["Buffer System"]
            Write[WriteStream]
            Read[ReadStream]
            Session[SessionManager]
        end
    end

    subgraph Tests["Jellyfin.Xtream.Tests"]
        Unit[Unit Tests]
        Integration[Integration Tests]
    end

    subgraph Benchmarks["Jellyfin.Xtream.Benchmarks"]
        Perf[Performance Benchmarks]
    end

    Root --> API
    Root --> Services
    Services --> Provider
    Services --> MpegTs
    Services --> Buffer
    MpegTs --> Buffer
    Provider --> MpegTs
```

## Development Status

### Recent Changes (January 2025)

#### Native Streaming Architecture Refactor

The streaming architecture has been significantly refactored. C++ is now the main orchestrator for streaming, failover, and URL selection, while C# serves as a thin setup and consumption layer.

| Component | Status | Description |
|-----------|--------|-------------|
| **Callback-Based Data Transfer** | ✅ Complete | Direct callbacks from C++ worker thread replace shared memory |
| **P/Invoke Callback Methods** | ✅ Complete | `StreamerSetOutputCallback()`, `StreamerSetEventCallback()` |
| **GCHandle Management** | ✅ Complete | Prevents GC of callback delegates during native execution |
| **StreamPipeline (C++)** | ✅ Complete | Orchestrates URL selection, failover, quality monitoring |
| **StreamSource (C++)** | ✅ Complete | Health-based URL selection with quarantine logic |
| **Quarantine Logic** | ✅ Complete | Exponential backoff (30s initial, 5min max) for failed URLs |
| **V2 API Removal** | ✅ Complete | Removed SQLite metrics database and shared memory buffer |
| **E2E Test Coverage** | ✅ Complete | 107 tests passing with native interop validation |

#### Health-Based Provider Selection

Bidirectional health management: C# manages long-term patterns, C++ handles instant decisions.

| Component | Status | Description |
|-----------|--------|-------------|
| **ProviderHealthScorer** | ✅ Complete | Weighted health scoring for provider selection |
| **StreamingOutcomeRecorder** | ✅ Complete | Tracks streaming success/failure outcomes |
| **Health Score Algorithm** | ✅ Complete | 50.0 initial, +0.5 success, -5.0 failure, range 0-100 |
| **Quality-Based Switching** | ✅ Complete | Configurable thresholds trigger provider switches |

#### SCTE-35 and NAL Parser Integration

Integrated into the native analyzer API for real-time stream analysis.

| Component | Status | Description |
|-----------|--------|-------------|
| **SCTE-35 Event Retrieval** | ✅ Complete | `tsduck_analyzer_get_scte35_events()` for pending splice events |
| **SCTE-35 State Tracking** | ✅ Complete | `tsduck_analyzer_get_scte35_state()` for ad break state |
| **SCTE-35 Callbacks** | ✅ Complete | `tsduck_analyzer_set_scte35_callback()` for event notifications |
| **Video Codec Info** | ✅ Complete | `tsduck_analyzer_get_video_codec_info()` for H.264/H.265/H.266 |
| **Parameter Set Caching** | ✅ Complete | `tsduck_analyzer_get_parameter_sets()` for SPS/PPS/VPS |
| **NAL-Based IDR Detection** | ✅ Complete | `tsduck_analyzer_has_idr_frame()` more accurate than RAI flag |
| **Automatic PID Detection** | ✅ Complete | SCTE-35 and video PIDs detected from PMT |

#### TsDuck Native TR 101 290 Integration

| Component | Status | Description |
|-----------|--------|-------------|
| **TsDuck Native Library** | ✅ Complete | C++ interop library for broadcast-grade TR 101 290 monitoring |
| **Native Build System** | ✅ Complete | Docker-based build for Linux, MSBuild integration |
| **ITsDuckAnalyzer Interface** | ✅ Complete | Abstraction with NativeTsDuckAnalyzer and NullTsDuckAnalyzer |
| **TsDuckMetrics** | ✅ Complete | Priority 1/2 error tracking, PCR jitter analysis, quality scoring |
| **Streaming Integration** | ✅ Complete | Wired into Restream via native callbacks |
| **Discord Integration** | ✅ Complete | TsDuck metrics in stream health notifications |

#### Provider Management Enhancements

| Component | Status | Description |
|-----------|--------|-------------|
| **Auto-Priority Scoring** | ✅ Complete | Automatic priority calculation from streaming performance |
| **Priority Property** | ✅ Complete | XtreamProvider.Priority (0-100, lower = better) |
| **Predictive Failover** | ✅ Complete | Trend analysis in AutomaticFailoverService |
| **TsDuck Metrics Forwarding** | ✅ Complete | ProviderSwitchService records TsDuck quality data |
| **UI Priority Display** | ✅ Complete | Web UI shows priority badges, sorted by performance |

#### Channel Matching Improvements

| Component | Status | Description |
|-----------|--------|-------------|
| **Unicode Separators** | ✅ Complete | Support for various Unicode characters in channel name prefixes |
| **Country Fallback** | ✅ Complete | Country-prefixed source can match country-less target |

#### Code Quality & Infrastructure

| Component | Status | Description |
|-----------|--------|-------------|
| **TsIndexer Simplification** | ✅ Complete | Removed built-in TR 101 290, delegated to TsDuck |
| **Buffer Diagnostics** | ✅ Complete | Rate-limited progress logging (10MB intervals) |
| **LiveTV Timeout Fix** | ✅ Complete | 2s timeout for optional channel name lookup |
| **Logging Refactor** | ✅ Complete | IsDebugEnabled as extension method |

### Work In Progress

| Component | Status | Description |
|-----------|--------|-------------|
| **Real-time Metrics Dashboard** | 📋 Planned | Web UI for live stream quality visualization |
| **Historical Metrics Storage** | 📋 Planned | Persist quality metrics for trend analysis |

### Known Issues Being Addressed

| Issue | Priority | Status |
|-------|----------|--------|
| Channel name lookup can timeout on slow providers | Fixed | 2s timeout added in LiveTvService |

## Areas of Improvement

Current limitations and areas where the implementation could be enhanced:

```mermaid
mindmap
  root((Improvement Areas))
    Streaming
      No adaptive bitrate ABR
      Single-threaded PSI validation
      FFmpeg 5MB probesize delay
    Protocol Support
      No HLS/DASH output
      No WebRTC low-latency
      No DVB subtitles extraction
    Provider Management
      No geo-routing optimization
      Basic health scoring
      No ML-based prediction
      Manual failover thresholds
    Monitoring
      No real-time dashboard
      Limited historical metrics
      No anomaly detection
      Basic alerting only
```

### Current Limitations

| Area | Limitation | Impact |
|------|------------|--------|
| **FFmpeg Init** | 5MB probesize blocking | Initial 0.5-2s delay on stream start |
| **Bitrate** | Single bitrate per channel | No quality adaptation for slow clients |
| **Output Format** | MPEG-TS only | No HLS/DASH for web clients |
| **Subtitles** | Not extracted | DVB subtitles passed through raw |
| **Encryption** | Detection only | Cannot decrypt CA-protected streams |
| **Geographic** | No provider geo-awareness | Suboptimal routing for global users |

## Future Plans

### Roadmap

```mermaid
timeline
    title Development Roadmap
    section Near Term
        Q1 2025 : HLS Output Support
                : Adaptive bitrate streaming
                : Real-time metrics dashboard
    section Mid Term
        Q2 2025 : WebRTC low-latency mode
                : GPU-accelerated transcoding
                : SCTE-35 ad replacement
    section Long Term
        Q3 2025 : ML-based quality prediction
                : Multi-region geo-routing
                : Cloud hybrid failover
```

### Recently Completed Features

These features were previously planned and are now complete:

| Feature | Status | Description |
|---------|--------|-------------|
| **SCTE-35 Detection** | ✅ Complete | Real-time ad marker detection with state tracking and callbacks |
| **Parameter Set Caching** | ✅ Complete | NAL unit parsing for SPS/PPS/VPS, faster decoder initialization |
| **Health-Based Failover** | ✅ Complete | Bidirectional health scoring with C++/C# coordination |
| **Native Streaming Pipeline** | ✅ Complete | C++ orchestration with callback-based data transfer |

### Planned Features

#### Near-Term (Q1 2025)

| Feature | Description | Benefit |
|---------|-------------|---------|
| **HLS/DASH Output** | Segment MPEG-TS into HLS/DASH | Browser playback without plugins |
| **Adaptive Bitrate** | Multi-quality transcoding | Smooth playback on variable networks |
| **Metrics Dashboard** | Real-time web UI | Visual monitoring without Discord |
| **DVB Subtitle Extraction** | Parse DVB-SUB/teletext | Subtitle support in Jellyfin UI |

#### Mid-Term (Q2 2025)

| Feature | Description | Benefit |
|---------|-------------|---------|
| **WebRTC Mode** | Sub-second latency streaming | Live sports/events viewing |
| **GPU Transcoding** | NVENC/QSV/VAAPI acceleration | Lower CPU, higher quality |
| **SCTE-35 Ad Replacement** | Replace detected ad breaks with custom content | Custom ad insertion |

#### Long-Term (Q3+ 2025)

| Feature | Description | Benefit |
|---------|-------------|---------|
| **ML Quality Prediction** | Predict degradation before it happens | Proactive switching |
| **Geo-Aware Routing** | Route to nearest provider PoP | Lower latency globally |
| **Cloud Hybrid** | Failover to cloud transcoders | 100% uptime guarantee |
| **Multi-Audio** | Multiple audio track selection | Language selection per client |

### Architecture Evolution

```mermaid
graph LR
    subgraph Current["Current Architecture"]
        MPEG[MPEG-TS In] --> Buffer[Circular Buffer]
        Buffer --> TS[MPEG-TS Out]
    end

    subgraph Future["Future Architecture"]
        MPEG2[MPEG-TS In] --> Demux[FFmpeg Demux]
        Demux --> Transcode{Transcode?}
        Transcode -->|Yes| GPU[GPU Encoder]
        Transcode -->|No| Passthrough[Passthrough]
        GPU --> Mux[Adaptive Muxer]
        Passthrough --> Mux
        Mux --> HLS[HLS Segments]
        Mux --> DASH[DASH Segments]
        Mux --> WebRTC[WebRTC]
        Mux --> MPEGTS[MPEG-TS]
    end

    Current -.->|Evolution| Future
```

## Known Issues

### Loss of Confidentiality

Jellyfin publishes remote paths in the API and default user interface. As Xtream format paths include credentials, anyone with library access can see your provider credentials. Use this plugin with caution on shared servers.

## Troubleshooting

### Network Configuration

Ensure your [Jellyfin networking](https://jellyfin.org/docs/general/networking/) is correctly configured:

1. Open your admin dashboard and navigate to `Networking`.
2. Configure your `Published server URIs` correctly.
   Example: `all=https://jellyfin.example.com`

### Debug Logging

Enable `Debug Logging` in the plugin settings for verbose diagnostics when troubleshooting issues.

### Common Issues

```mermaid
flowchart TD
    Problem1[No Video, Only Sound]
    Solution1[Client starting at P/B frame<br/>Buffer seeks to nearest I-frame]

    Problem2[HTTP 406 Error]
    Solution2[Provider connection limit<br/>Restreaming shares single connection]

    Problem3[Frequent Buffering]
    Solution3[Check provider quality<br/>Enable failover to backup]

    Problem4[EPG Not Loading]
    Solution4[Check EPG URL<br/>Enable external XMLTV fallback]

    Problem5[High CPU Usage]
    Solution5[Reduce concurrent streams<br/>Check buffer sizes]

    Problem1 --> Solution1
    Problem2 --> Solution2
    Problem3 --> Solution3
    Problem4 --> Solution4
    Problem5 --> Solution5
```

## License

This project is licensed under the GNU General Public License v3.0 - see the [LICENSE](LICENSE) file for details.
