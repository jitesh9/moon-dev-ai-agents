/*
 * Metrics Collector
 * Collects and aggregates application metrics for observability
 */

using System.Collections.Concurrent;

namespace GCTradingApp;

/// <summary>
/// Collects and aggregates application metrics
/// Thread-safe metrics collection for observability
/// </summary>
public class MetricsCollector : IDisposable
{
    private readonly ConcurrentDictionary<string, Counter> _counters = new();
    private readonly ConcurrentDictionary<string, Gauge> _gauges = new();
    private readonly ConcurrentDictionary<string, Histogram> _histograms = new();
    private readonly System.Threading.Timer _metricsTimer;
    private bool _disposed = false;

    // Events
    public event Action<string>? OnLog;
    public event Action<MetricsSnapshot>? OnMetricsSnapshot;

    public MetricsCollector()
    {
        // Emit metrics snapshot every 60 seconds
        _metricsTimer = new System.Threading.Timer(EmitMetricsSnapshot, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        Log("MetricsCollector initialized");
    }

    /// <summary>
    /// Increment a counter metric
    /// </summary>
    public void IncrementCounter(string name, double value = 1.0, Dictionary<string, string>? tags = null)
    {
        var key = BuildKey(name, tags);
        _counters.AddOrUpdate(key, new Counter { Name = name, Value = value, Tags = tags ?? new() },
            (k, existing) => new Counter { Name = name, Value = existing.Value + value, Tags = existing.Tags });
    }

    /// <summary>
    /// Set a gauge metric (current value)
    /// </summary>
    public void SetGauge(string name, double value, Dictionary<string, string>? tags = null)
    {
        var key = BuildKey(name, tags);
        _gauges.AddOrUpdate(key, new Gauge { Name = name, Value = value, Tags = tags ?? new() },
            (k, existing) => new Gauge { Name = name, Value = value, Tags = existing.Tags });
    }

    /// <summary>
    /// Record a histogram value
    /// </summary>
    public void RecordHistogram(string name, double value, Dictionary<string, string>? tags = null)
    {
        var key = BuildKey(name, tags);
        _histograms.AddOrUpdate(key,
            new Histogram { Name = name, Values = new List<double> { value }, Tags = tags ?? new() },
            (k, existing) =>
            {
                lock (existing)
                {
                    existing.Values.Add(value);
                    // Keep only last 1000 values to prevent memory growth
                    if (existing.Values.Count > 1000)
                    {
                        existing.Values.RemoveAt(0);
                    }
                }
                return existing;
            });
    }

    /// <summary>
    /// Get current metrics snapshot
    /// </summary>
    public MetricsSnapshot GetSnapshot()
    {
        return new MetricsSnapshot
        {
            Timestamp = DateTime.UtcNow,
            Counters = _counters.Values.ToList(),
            Gauges = _gauges.Values.ToList(),
            Histograms = _histograms.Values.Select(h =>
            {
                lock (h)
                {
                    return new HistogramSnapshot
                    {
                        Name = h.Name,
                        Tags = h.Tags,
                        Count = h.Values.Count,
                        Min = h.Values.Count > 0 ? h.Values.Min() : 0,
                        Max = h.Values.Count > 0 ? h.Values.Max() : 0,
                        Mean = h.Values.Count > 0 ? h.Values.Average() : 0,
                        P50 = Percentile(h.Values, 0.5),
                        P95 = Percentile(h.Values, 0.95),
                        P99 = Percentile(h.Values, 0.99)
                    };
                }
            }).ToList()
        };
    }

    private double Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        var index = (int)Math.Ceiling(sorted.Count * percentile) - 1;
        return sorted[Math.Max(0, Math.Min(index, sorted.Count - 1))];
    }

    private string BuildKey(string name, Dictionary<string, string>? tags)
    {
        if (tags == null || tags.Count == 0)
            return name;

        var tagString = string.Join(",", tags.OrderBy(kvp => kvp.Key).Select(kvp => $"{kvp.Key}={kvp.Value}"));
        return $"{name}[{tagString}]";
    }

    private void EmitMetricsSnapshot(object? state)
    {
        if (_disposed) return;

        try
        {
            var snapshot = GetSnapshot();
            OnMetricsSnapshot?.Invoke(snapshot);

            // Log summary
            var counterCount = snapshot.Counters.Count;
            var gaugeCount = snapshot.Gauges.Count;
            var histogramCount = snapshot.Histograms.Count;
            Log($"Metrics snapshot: {counterCount} counters, {gaugeCount} gauges, {histogramCount} histograms");
        }
        catch (Exception ex)
        {
            Logger.Error("Error emitting metrics snapshot", ex);
        }
    }

    private void Log(string message)
    {
        Logger.Info($"[MetricsCollector] {message}");
        OnLog?.Invoke(message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _metricsTimer?.Dispose();
        Log("MetricsCollector disposed");
    }
}

/// <summary>
/// Counter metric (monotonically increasing)
/// </summary>
public class Counter
{
    public string Name { get; set; } = "";
    public double Value { get; set; }
    public Dictionary<string, string> Tags { get; set; } = new();
}

/// <summary>
/// Gauge metric (current value)
/// </summary>
public class Gauge
{
    public string Name { get; set; } = "";
    public double Value { get; set; }
    public Dictionary<string, string> Tags { get; set; } = new();
}

/// <summary>
/// Histogram metric (distribution of values)
/// </summary>
public class Histogram
{
    public string Name { get; set; } = "";
    public List<double> Values { get; set; } = new();
    public Dictionary<string, string> Tags { get; set; } = new();
    public readonly object LockObject = new();
}

/// <summary>
/// Snapshot of all metrics at a point in time
/// </summary>
public class MetricsSnapshot
{
    public DateTime Timestamp { get; set; }
    public List<Counter> Counters { get; set; } = new();
    public List<Gauge> Gauges { get; set; } = new();
    public List<HistogramSnapshot> Histograms { get; set; } = new();
}

/// <summary>
/// Histogram snapshot with computed statistics
/// </summary>
public class HistogramSnapshot
{
    public string Name { get; set; } = "";
    public Dictionary<string, string> Tags { get; set; } = new();
    public int Count { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
    public double Mean { get; set; }
    public double P50 { get; set; }
    public double P95 { get; set; }
    public double P99 { get; set; }
}

