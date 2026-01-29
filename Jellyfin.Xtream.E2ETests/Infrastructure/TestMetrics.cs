using System.Diagnostics;

namespace Jellyfin.Xtream.E2ETests.Infrastructure;

/// <summary>
/// Thread-safe metrics collection for E2E test verification.
/// Tracks throughput, latency, packet integrity, and PCR timing.
/// </summary>
internal sealed class TestMetrics
{
    private const int TsPacketSize = 188;
    private const byte SyncByte = 0x47;

    private long _totalBytesReceived;
    private long _totalPackets;
    private long _validSyncPackets;
    private long _continuityErrors;
    private long _firstByteTimeTicks;
    private readonly Stopwatch _elapsed = new();
    private readonly List<double> _latencyMeasurementsMs = new();
    private readonly List<long> _pcrValues = new();
    private readonly object _lock = new();

    // Per-PID continuity tracking
    private readonly int[] _lastContinuityCounter = new int[8192];
    private readonly bool[] _pidSeen = new bool[8192];

    /// <summary>
    /// Gets the total bytes received.
    /// </summary>
    public long TotalBytesReceived => Interlocked.Read(ref _totalBytesReceived);

    /// <summary>
    /// Gets the total TS packets processed.
    /// </summary>
    public long TotalPackets => Interlocked.Read(ref _totalPackets);

    /// <summary>
    /// Gets packets with valid sync byte.
    /// </summary>
    public long ValidSyncPackets => Interlocked.Read(ref _validSyncPackets);

    /// <summary>
    /// Gets the continuity counter errors detected.
    /// </summary>
    public long ContinuityErrors => Interlocked.Read(ref _continuityErrors);

    /// <summary>
    /// Gets the elapsed time since metrics collection started.
    /// </summary>
    public TimeSpan Elapsed => _elapsed.Elapsed;

    /// <summary>
    /// Gets the average throughput in bytes per second.
    /// </summary>
    public double ThroughputBytesPerSecond
    {
        get
        {
            var elapsed = _elapsed.Elapsed.TotalSeconds;
            return elapsed > 0 ? TotalBytesReceived / elapsed : 0;
        }
    }

    /// <summary>
    /// Gets the average throughput in Kbps.
    /// </summary>
    public double ThroughputKbps => ThroughputBytesPerSecond * 8.0 / 1000.0;

    /// <summary>
    /// Gets the PCR values collected (in 90kHz ticks).
    /// </summary>
    public IReadOnlyList<long> PcrValues
    {
        get
        {
            lock (_lock)
            {
                return _pcrValues.ToList();
            }
        }
    }

    /// <summary>
    /// Gets the latency measurements in milliseconds.
    /// </summary>
    public IReadOnlyList<double> LatencyMeasurementsMs
    {
        get
        {
            lock (_lock)
            {
                return _latencyMeasurementsMs.ToList();
            }
        }
    }

    /// <summary>
    /// Starts the metrics timer.
    /// </summary>
    public void Start()
    {
        _elapsed.Start();
    }

    /// <summary>
    /// Stops the metrics timer.
    /// </summary>
    public void Stop()
    {
        _elapsed.Stop();
    }

    /// <summary>
    /// Records a latency measurement.
    /// </summary>
    /// <param name="latencyMs">Latency in milliseconds.</param>
    public void RecordLatency(double latencyMs)
    {
        lock (_lock)
        {
            _latencyMeasurementsMs.Add(latencyMs);
        }
    }

    /// <summary>
    /// Records the first-byte arrival time.
    /// </summary>
    public void RecordFirstByte()
    {
        Interlocked.CompareExchange(ref _firstByteTimeTicks, _elapsed.ElapsedTicks, 0);
    }

    /// <summary>
    /// Processes received TS data, checking sync bytes and continuity counters.
    /// </summary>
    /// <param name="data">Buffer containing received TS data.</param>
    /// <param name="length">Number of valid bytes in the buffer.</param>
    public void ProcessReceivedData(byte[] data, int length)
    {
        Interlocked.Add(ref _totalBytesReceived, length);

        int packetCount = length / TsPacketSize;
        for (int i = 0; i < packetCount; i++)
        {
            int offset = i * TsPacketSize;
            Interlocked.Increment(ref _totalPackets);

            // Check sync byte
            if (data[offset] == SyncByte)
            {
                Interlocked.Increment(ref _validSyncPackets);
            }
            else
            {
                continue; // Can't parse a packet without valid sync
            }

            // Extract PID
            int pid = ((data[offset + 1] & 0x1F) << 8) | data[offset + 2];

            // Check continuity counter
            int cc = data[offset + 3] & 0x0F;
            bool hasPayload = (data[offset + 3] & 0x10) != 0;

            if (hasPayload && pid != 0x1FFF) // Skip null packets
            {
                lock (_lock)
                {
                    if (_pidSeen[pid])
                    {
                        int expectedCc = (_lastContinuityCounter[pid] + 1) & 0x0F;
                        if (cc != expectedCc)
                        {
                            Interlocked.Increment(ref _continuityErrors);
                        }
                    }

                    _pidSeen[pid] = true;
                    _lastContinuityCounter[pid] = cc;
                }
            }

            // Extract PCR if present
            bool hasAdaptation = (data[offset + 3] & 0x20) != 0;
            if (hasAdaptation && offset + 4 < length)
            {
                int adaptLen = data[offset + 4];
                if (adaptLen >= 7 && offset + 11 < length)
                {
                    bool hasPcr = (data[offset + 5] & 0x10) != 0;
                    if (hasPcr)
                    {
                        long pcrBase =
                            ((long)data[offset + 6] << 25)
                            | ((long)data[offset + 7] << 17)
                            | ((long)data[offset + 8] << 9)
                            | ((long)data[offset + 9] << 1)
                            | (long)((data[offset + 10] >> 7) & 0x01);

                        lock (_lock)
                        {
                            _pcrValues.Add(pcrBase);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Gets the p-th percentile of latency measurements.
    /// </summary>
    /// <param name="percentile">Percentile (0-100).</param>
    /// <returns>Latency value at the specified percentile.</returns>
    public double GetLatencyPercentile(int percentile)
    {
        lock (_lock)
        {
            if (_latencyMeasurementsMs.Count == 0)
            {
                return 0;
            }

            var sorted = _latencyMeasurementsMs.OrderBy(x => x).ToList();
            int index = (int)Math.Ceiling((percentile / 100.0) * sorted.Count) - 1;
            index = Math.Max(0, Math.Min(index, sorted.Count - 1));
            return sorted[index];
        }
    }

    /// <summary>
    /// Resets all metrics to zero.
    /// </summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _totalBytesReceived, 0);
        Interlocked.Exchange(ref _totalPackets, 0);
        Interlocked.Exchange(ref _validSyncPackets, 0);
        Interlocked.Exchange(ref _continuityErrors, 0);
        Interlocked.Exchange(ref _firstByteTimeTicks, 0);
        _elapsed.Reset();

        lock (_lock)
        {
            _latencyMeasurementsMs.Clear();
            _pcrValues.Clear();
            Array.Clear(_lastContinuityCounter);
            Array.Clear(_pidSeen);
        }
    }

    /// <summary>
    /// Returns a formatted summary of collected metrics.
    /// </summary>
    public override string ToString()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"=== E2E Test Metrics ===");
        sb.AppendLine($"Duration:          {Elapsed.TotalSeconds:F2}s");
        sb.AppendLine($"Bytes received:    {TotalBytesReceived:N0}");
        sb.AppendLine(
            $"Throughput:        {ThroughputKbps:F1} Kbps ({ThroughputBytesPerSecond / 1_000_000.0:F2} MB/s)"
        );
        sb.AppendLine($"Total packets:     {TotalPackets:N0}");
        sb.AppendLine(
            $"Valid sync:        {ValidSyncPackets:N0} ({(TotalPackets > 0 ? 100.0 * ValidSyncPackets / TotalPackets : 0):F1}%)"
        );
        sb.AppendLine($"Continuity errors: {ContinuityErrors:N0}");

        lock (_lock)
        {
            if (_pcrValues.Count > 1)
            {
                var intervals = new List<double>();
                for (int i = 1; i < _pcrValues.Count; i++)
                {
                    intervals.Add((_pcrValues[i] - _pcrValues[i - 1]) / 90.0); // Convert to ms
                }

                sb.AppendLine($"PCR count:         {_pcrValues.Count}");
                sb.AppendLine($"PCR interval avg:  {intervals.Average():F2}ms");
                sb.AppendLine($"PCR interval min:  {intervals.Min():F2}ms");
                sb.AppendLine($"PCR interval max:  {intervals.Max():F2}ms");
            }

            if (_latencyMeasurementsMs.Count > 0)
            {
                sb.AppendLine($"Latency p50:       {GetLatencyPercentile(50):F2}ms");
                sb.AppendLine($"Latency p95:       {GetLatencyPercentile(95):F2}ms");
                sb.AppendLine($"Latency p99:       {GetLatencyPercentile(99):F2}ms");
            }
        }

        return sb.ToString();
    }
}
