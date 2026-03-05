# Event-Driven Provider Discovery Pipeline Architecture

## Design Document - Senior Principal Architect Review

### Executive Summary

This document outlines a lock-free, event-driven pipeline architecture for Xtream provider discovery.
The design follows a progressive filtering funnel where providers are eliminated at each stage,
reducing computational overhead and providing real-time visibility into the discovery process.

---

## 1. Pipeline Philosophy

### The Funnel Principle

```
┌─────────────────────────────────────────────────────────────────┐
│                    DISCOVERED CREDENTIALS                        │
│                         (~500-2000)                              │
└─────────────────────────────────┬───────────────────────────────┘
                                  │
                    ┌─────────────▼─────────────┐
                    │   Stage 1: CONNECTIVITY   │  Fast TCP check
                    │      (~100-500 pass)      │  ~50ms timeout
                    └─────────────┬─────────────┘
                                  │
                    ┌─────────────▼─────────────┐
                    │  Stage 2: AUTHENTICATION  │  API login test
                    │       (~50-200 pass)      │  Active accounts only
                    └─────────────┬─────────────┘
                                  │
                    ┌─────────────▼─────────────┐
                    │   Stage 3: STREAM TEST    │  Validates playback
                    │       (~30-100 pass)      │  Working streams
                    └─────────────┬─────────────┘
                                  │
                    ┌─────────────▼─────────────┐
                    │   Stage 4: EPG CHECK      │  Guide availability
                    │       (~20-50 pass)       │  Has program data
                    └─────────────┬─────────────┘
                                  │
                    ┌─────────────▼─────────────┐
                    │  Stage 5: POLISH FILTER   │  Target channels
                    │       (~10-30 pass)       │  Polish content
                    └─────────────┬─────────────┘
                                  │
                    ┌─────────────▼─────────────┐
                    │  Stage 6: QUALITY SCORE   │  Trust assessment
                    │        (~5-15 pass)       │  Excellent rating
                    └─────────────┴─────────────┘
```

### Key Benefits

1. **Early Exit**: Failed providers don't consume resources in later stages
2. **Real-time Progress**: Each stage reports independently
3. **Lock-free**: Uses System.Threading.Channels for coordination
4. **Backpressure**: Slow consumers don't block fast producers
5. **Cancellation**: Any stage can be cancelled without blocking

---

## 2. Core Abstractions

### 2.1 Pipeline Item State Machine

```
    ┌──────────┐
    │ PENDING  │──────────────────────────────────┐
    └────┬─────┘                                  │
         │ Enter Stage                            │
    ┌────▼─────┐                                  │
    │PROCESSING│                                  │
    └────┬─────┘                                  │
         │                                        │
    ┌────┴────┐                                   │
    │         │                                   │
┌───▼───┐ ┌───▼───┐                          ┌───▼───┐
│PASSED │ │FAILED │                          │SKIPPED│
└───┬───┘ └───────┘                          └───────┘
    │
    ▼ Next Stage
```

### 2.2 Channel-Based Stage Communication

```csharp
// Each stage is a channel transformer
Channel<PipelineItem> Input  →  [Stage Logic]  →  Channel<PipelineItem> Output
                                     │
                                     ▼
                              Channel<StageEvent> Events
```

### 2.3 Event Types

| Event | Description | Contains |
|-------|-------------|----------|
| `ItemEntered` | Item started processing | Stage, ItemId |
| `ItemPassed` | Item passed stage checks | Stage, ItemId, Duration |
| `ItemFailed` | Item failed stage checks | Stage, ItemId, Reason |
| `StageCompleted` | All items processed | Stage, Stats |
| `PipelineCompleted` | All stages finished | FinalResults |

---

## 3. Lock-Free Design Patterns

### 3.1 Using System.Threading.Channels

```csharp
// Unbounded for high-throughput stages
Channel.CreateUnbounded<T>(new UnboundedChannelOptions
{
    SingleWriter = false,  // Multiple producers
    SingleReader = false,  // Multiple consumers
    AllowSynchronousContinuations = false  // Prevent stack overflow
});

// Bounded for backpressure control
Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
{
    FullMode = BoundedChannelFullMode.Wait,  // Block producer
    SingleWriter = true,
    SingleReader = true
});
```

### 3.2 Atomic Counters with Interlocked

```csharp
// Lock-free statistics
private long _processed;
private long _passed;
private long _failed;

public void RecordPass()
{
    Interlocked.Increment(ref _processed);
    Interlocked.Increment(ref _passed);
}

public (long Processed, long Passed, long Failed) GetStats()
{
    return (
        Interlocked.Read(ref _processed),
        Interlocked.Read(ref _passed),
        Interlocked.Read(ref _failed)
    );
}
```

### 3.3 Immutable Pipeline Items

```csharp
// Items are immutable records - no synchronization needed
public sealed record PipelineItem
{
    public required DiscoveredCredential Credential { get; init; }
    public required PipelineStage CurrentStage { get; init; }
    public required ImmutableDictionary<string, object> Properties { get; init; }

    // Create new item with updated properties (immutable)
    public PipelineItem WithProperty(string key, object value) =>
        this with { Properties = Properties.SetItem(key, value) };
}
```

---

## 4. Stage Implementations

### 4.1 Base Stage Pattern

```csharp
public abstract class PipelineStage<TInput, TOutput>
{
    private readonly Channel<TInput> _input;
    private readonly Channel<TOutput> _output;
    private readonly Channel<StageEvent> _events;

    // Atomic counters
    private long _entered;
    private long _exited;
    private long _passed;

    protected abstract ValueTask<StageResult<TOutput>> ProcessAsync(
        TInput item,
        CancellationToken ct);

    public async Task RunAsync(int concurrency, CancellationToken ct)
    {
        // Process items with bounded parallelism
        await Parallel.ForEachAsync(
            _input.Reader.ReadAllAsync(ct),
            new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = ct },
            async (item, token) =>
            {
                Interlocked.Increment(ref _entered);

                var result = await ProcessAsync(item, token);

                if (result.Success)
                {
                    Interlocked.Increment(ref _passed);
                    await _output.Writer.WriteAsync(result.Value!, token);
                }

                Interlocked.Increment(ref _exited);
                await _events.Writer.WriteAsync(result.Event, token);
            });

        _output.Writer.Complete();
    }
}
```

### 4.2 Stage-Specific Implementations

#### Stage 1: Connectivity Check
```csharp
// Fast TCP probe - 50ms timeout
// Eliminates ~70% of dead hosts immediately
public class ConnectivityStage : PipelineStage<PipelineItem, PipelineItem>
{
    protected override async ValueTask<StageResult<PipelineItem>> ProcessAsync(
        PipelineItem item, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(item.Credential.Server, item.Credential.Port, cts.Token);
            return StageResult.Pass(item);
        }
        catch
        {
            return StageResult.Fail(item, "Unreachable");
        }
    }
}
```

#### Stage 2: Authentication
```csharp
// API authentication - validates account
public class AuthenticationStage : PipelineStage<PipelineItem, PipelineItem>
{
    protected override async ValueTask<StageResult<PipelineItem>> ProcessAsync(
        PipelineItem item, CancellationToken ct)
    {
        using var client = new XtreamClient(item.Credential.BaseUrl);
        var info = await client.GetUserAndServerInfoAsync(
            item.Credential.Username,
            item.Credential.Password,
            ct);

        if (info?.UserInfo?.Status != "Active")
            return StageResult.Fail(item, $"Status: {info?.UserInfo?.Status}");

        // Attach auth info to item for downstream stages
        return StageResult.Pass(item.WithProperty("AuthInfo", info));
    }
}
```

#### Stage 3: Stream Test
```csharp
// Stream playback validation
public class StreamTestStage : PipelineStage<PipelineItem, PipelineItem>
{
    protected override async ValueTask<StageResult<PipelineItem>> ProcessAsync(
        PipelineItem item, CancellationToken ct)
    {
        // Test single stream first (fail-fast)
        var streams = await GetStreamsAsync(item, ct);
        var testStream = streams.FirstOrDefault();

        if (testStream == null)
            return StageResult.Fail(item, "No streams");

        var result = await TestStreamAsync(testStream, ct);
        if (!result.Works)
            return StageResult.Fail(item, result.Status);

        return StageResult.Pass(item
            .WithProperty("Streams", streams)
            .WithProperty("StreamQuality", result.Quality));
    }
}
```

#### Stage 4: EPG Check
```csharp
// EPG availability check
public class EpgCheckStage : PipelineStage<PipelineItem, PipelineItem>
{
    protected override async ValueTask<StageResult<PipelineItem>> ProcessAsync(
        PipelineItem item, CancellationToken ct)
    {
        // Single EPG call - just verify data exists
        var streams = item.GetProperty<List<StreamInfo>>("Streams");
        var testId = streams.First().StreamId;

        var epg = await client.GetShortEpgAsync(testId, ct);

        if (epg?.EpgListings == null || epg.EpgListings.Count == 0)
            return StageResult.Fail(item, "No EPG data");

        return StageResult.Pass(item
            .WithProperty("EpgCount", epg.EpgListings.Count));
    }
}
```

#### Stage 5: Polish Channel Filter
```csharp
// Polish channel detection (pre-compiled regex)
public class PolishFilterStage : PipelineStage<PipelineItem, PipelineItem>
{
    private static readonly CompiledPolishDetector Detector = new();

    protected override ValueTask<StageResult<PipelineItem>> ProcessAsync(
        PipelineItem item, CancellationToken ct)
    {
        var streams = item.GetProperty<List<StreamInfo>>("Streams");
        var polishChannels = Detector.FindPolishChannels(streams);

        if (polishChannels.Count == 0)
            return ValueTask.FromResult(StageResult.Fail(item, "No Polish channels"));

        return ValueTask.FromResult(StageResult.Pass(item
            .WithProperty("PolishChannels", polishChannels)));
    }
}
```

#### Stage 6: Quality Scoring
```csharp
// Final trust score calculation
public class QualityScoringStage : PipelineStage<PipelineItem, ProviderTestResult>
{
    protected override ValueTask<StageResult<ProviderTestResult>> ProcessAsync(
        PipelineItem item, CancellationToken ct)
    {
        var result = BuildTestResult(item);
        result.CalculateTrustScore();

        // All items pass this stage but with their score attached
        return ValueTask.FromResult(StageResult.Pass(result));
    }
}
```

---

## 5. Pipeline Orchestrator

### 5.1 Pipeline Builder Pattern

```csharp
var pipeline = new DiscoveryPipeline.Builder()
    .AddStage(new ConnectivityStage(), concurrency: 50)   // Lots of parallel connections
    .AddStage(new AuthenticationStage(), concurrency: 20)  // API rate limiting
    .AddStage(new PolishFilterStage(), concurrency: 4)     // Filter early to avoid testing non-Polish providers
    .AddStage(new StreamTestStage(), concurrency: 10)      // Bandwidth intensive
    .AddStage(new EpgCheckStage(), concurrency: 10)        // API calls
    .AddStage(new QualityScoringStage(), concurrency: 4)   // CPU bound
    .Build();
```

### 5.2 Event Aggregator

```csharp
public class PipelineEventAggregator
{
    private readonly Channel<StageEvent> _events;

    // Lock-free counters per stage
    private readonly ConcurrentDictionary<PipelineStage, StageStats> _stats;

    public IAsyncEnumerable<DiscoveryProgress> StreamProgressAsync(CancellationToken ct)
    {
        return _events.Reader.ReadAllAsync(ct)
            .Select(UpdateStatsAndCreateProgress);
    }

    private DiscoveryProgress UpdateStatsAndCreateProgress(StageEvent evt)
    {
        var stats = _stats.AddOrUpdate(
            evt.Stage,
            _ => new StageStats().Apply(evt),
            (_, existing) => existing.Apply(evt)
        );

        return new DiscoveryProgress
        {
            Phase = MapToPhase(evt.Stage),
            CurrentItem = stats.Processed,
            TotalItems = stats.Total,
            // Funnel stats
            WorkingProviders = GetStageStats(PipelineStage.StreamTest).Passed,
            WorkingWithEpg = GetStageStats(PipelineStage.EpgCheck).Passed,
            FullyWorking = GetStageStats(PipelineStage.PolishFilter).Passed,
            Excellent = GetExcellentCount(),
        };
    }
}
```

---

## 6. Memory Efficiency

### 6.1 Object Pooling for High-Churn Objects

```csharp
// Pool HttpRequestMessage instances
private static readonly ObjectPool<HttpRequestMessage> RequestPool =
    new DefaultObjectPool<HttpRequestMessage>(new HttpRequestMessagePolicy());

// Pool byte arrays for stream testing
private static readonly ArrayPool<byte> BufferPool = ArrayPool<byte>.Shared;
```

### 6.2 Struct-Based Stage Events

```csharp
// Events are value types to avoid heap allocation
public readonly struct StageEvent
{
    public readonly PipelineStage Stage;
    public readonly int ItemId;
    public readonly StageEventType Type;
    public readonly long TimestampTicks;

    // No heap allocation for passing events
}
```

---

## 7. Cancellation and Cleanup

### 7.1 Graceful Shutdown

```csharp
public async Task CancelAsync()
{
    // Signal cancellation to all stages
    _cts.Cancel();

    // Complete all input channels to drain queues
    foreach (var stage in _stages)
    {
        stage.InputChannel.Writer.TryComplete();
    }

    // Wait for all stage tasks to complete
    await Task.WhenAll(_stageTasks);

    // Collect partial results
    return CollectPartialResults();
}
```

### 7.2 Resource Disposal

```csharp
public async ValueTask DisposeAsync()
{
    await CancelAsync();

    foreach (var stage in _stages)
    {
        if (stage is IAsyncDisposable disposable)
            await disposable.DisposeAsync();
    }
}
```

---

## 8. Performance Characteristics

### 8.1 Throughput Expectations

| Stage | Concurrency | Latency | Throughput |
|-------|-------------|---------|------------|
| Connectivity | 50 | 50ms | 1000/sec |
| Authentication | 20 | 200ms | 100/sec |
| Stream Test | 10 | 500ms | 20/sec |
| EPG Check | 10 | 300ms | 33/sec |
| Polish Filter | 4 | 5ms | 800/sec |
| Quality Score | 4 | 1ms | 4000/sec |

### 8.2 Memory Usage

- Base: ~50MB for channel infrastructure
- Per-item: ~2KB (credential + properties)
- Peak: ~100MB with 500 items in flight
- Events: ~64 bytes per event (struct)

---

## 9. Comparison with Current Architecture

| Aspect | Current | New Pipeline |
|--------|---------|--------------|
| Locking | Single global lock | Lock-free channels |
| Parallelism | Fixed workers | Stage-specific tuning |
| Early Exit | None (tests all) | Immediate elimination |
| Progress | Coarse (per credential) | Fine (per stage) |
| Memory | High (all results) | Streaming (partial results) |
| Cancellation | Request-level | Stage-level |

---

## 10. Implementation Phases

### Phase 1: Core Infrastructure
- [ ] PipelineItem record
- [ ] StageEvent struct
- [ ] Base PipelineStage class
- [ ] Channel factory

### Phase 2: Stage Implementations
- [ ] ConnectivityStage
- [ ] AuthenticationStage
- [ ] StreamTestStage
- [ ] EpgCheckStage
- [ ] PolishFilterStage
- [ ] QualityScoringStage

### Phase 3: Orchestration
- [ ] PipelineBuilder
- [ ] EventAggregator
- [ ] ProgressReporter

### Phase 4: Integration
- [ ] Update ProviderDiscoveryService
- [ ] Update API controllers
- [ ] Update UI for stage visibility

---

## Author Notes

This architecture prioritizes:
1. **Correctness**: Immutable data, no shared mutable state
2. **Performance**: Lock-free, early-exit, pooled resources
3. **Observability**: Per-stage metrics, real-time progress
4. **Maintainability**: Single responsibility stages, clear interfaces
