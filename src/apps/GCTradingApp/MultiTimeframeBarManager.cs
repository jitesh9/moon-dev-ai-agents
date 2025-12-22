/*
 * Multi-Timeframe Bar Manager for GC Trading Application
 * Aggregates 5-second bars into higher timeframes and calculates SuperTrend per timeframe
 */

namespace GCTradingApp;

/// <summary>
/// Timeframe preset configurations
/// </summary>
public enum TimeframePreset
{
    Preset_5m_15m_1H,      // Fast: 5-minute, 15-minute, 1-hour
    Preset_1m_5m_15m,      // Scalping: 1-minute, 5-minute, 15-minute
    Preset_15m_1H_4H,      // Swing: 15-minute, 1-hour, 4-hour
    Preset_5m_1H_Daily     // Position: 5-minute, 1-hour, Daily
}

/// <summary>
/// Individual timeframe configuration
/// </summary>
public class TimeframeConfig
{
    public string Name { get; set; } = "";
    public int SecondsPerBar { get; set; }
    public int LookbackBars { get; set; }
}

/// <summary>
/// Aggregated bar data and indicators for a specific timeframe
/// </summary>
public class TimeframeBarData
{
    public TimeframeConfig Config { get; set; } = new();
    public List<BarData> Bars { get; set; } = new();
    public BarData? CurrentPartialBar { get; set; }
    public DateTime CurrentBarStartTime { get; set; }

    // SuperTrend state for this timeframe
    public double SuperTrend { get; set; }
    public int SuperTrendDirection { get; set; }  // 1 = bullish, -1 = bearish, 0 = unknown
    public double PrevSuperTrend { get; set; }
    public double ATR { get; set; }
}

/// <summary>
/// Result of MTF alignment check
/// </summary>
public class MTFAlignmentResult
{
    public bool AllBullish { get; set; }      // All timeframes show bullish SuperTrend
    public bool AllBearish { get; set; }      // All timeframes show bearish SuperTrend
    public bool Aligned => AllBullish || AllBearish;
    public Dictionary<string, int> DirectionByTimeframe { get; set; } = new();
    public string AlignmentDescription { get; set; } = "";
}

/// <summary>
/// Manages multi-timeframe bar aggregation and SuperTrend calculation
/// </summary>
public class MultiTimeframeBarManager
{
    private readonly Dictionary<string, TimeframeBarData> _timeframes = new();
    private readonly object _lock = new();
    private readonly int _superTrendPeriod;
    private readonly double _superTrendMultiplier;
    private MTFAlignmentResult _lastAlignment = new();

    // Events
    public event Action<string, BarData>? OnTimeframeBarCompleted;
    public event Action<MTFAlignmentResult>? OnAlignmentChanged;
    public event Action<string>? OnLog;

    public MultiTimeframeBarManager(int superTrendPeriod = 10, double superTrendMultiplier = 3.0)
    {
        _superTrendPeriod = superTrendPeriod;
        _superTrendMultiplier = superTrendMultiplier;
    }

    /// <summary>
    /// Configure the manager with a timeframe preset
    /// </summary>
    public void Configure(TimeframePreset preset)
    {
        lock (_lock)
        {
            _timeframes.Clear();

            var configs = GetPresetConfigs(preset);
            foreach (var config in configs)
            {
                _timeframes[config.Name] = new TimeframeBarData
                {
                    Config = config,
                    Bars = new List<BarData>(),
                    SuperTrendDirection = 0
                };
            }

            Log($"Configured MTF manager with preset: {preset} ({string.Join(", ", configs.Select(c => c.Name))})");
        }
    }

    /// <summary>
    /// Get timeframe configurations for a preset
    /// </summary>
    public static TimeframeConfig[] GetPresetConfigs(TimeframePreset preset)
    {
        return preset switch
        {
            TimeframePreset.Preset_5m_15m_1H => new[]
            {
                new TimeframeConfig { Name = "5m", SecondsPerBar = 300, LookbackBars = 100 },
                new TimeframeConfig { Name = "15m", SecondsPerBar = 900, LookbackBars = 100 },
                new TimeframeConfig { Name = "1H", SecondsPerBar = 3600, LookbackBars = 50 }
            },

            TimeframePreset.Preset_1m_5m_15m => new[]
            {
                new TimeframeConfig { Name = "1m", SecondsPerBar = 60, LookbackBars = 200 },
                new TimeframeConfig { Name = "5m", SecondsPerBar = 300, LookbackBars = 100 },
                new TimeframeConfig { Name = "15m", SecondsPerBar = 900, LookbackBars = 100 }
            },

            TimeframePreset.Preset_15m_1H_4H => new[]
            {
                new TimeframeConfig { Name = "15m", SecondsPerBar = 900, LookbackBars = 100 },
                new TimeframeConfig { Name = "1H", SecondsPerBar = 3600, LookbackBars = 50 },
                new TimeframeConfig { Name = "4H", SecondsPerBar = 14400, LookbackBars = 30 }
            },

            TimeframePreset.Preset_5m_1H_Daily => new[]
            {
                new TimeframeConfig { Name = "5m", SecondsPerBar = 300, LookbackBars = 100 },
                new TimeframeConfig { Name = "1H", SecondsPerBar = 3600, LookbackBars = 50 },
                new TimeframeConfig { Name = "Daily", SecondsPerBar = 86400, LookbackBars = 50 }
            },

            _ => throw new ArgumentException($"Unknown preset: {preset}")
        };
    }

    /// <summary>
    /// Process a 5-second bar and aggregate into higher timeframes
    /// </summary>
    public void ProcessBar(BarData fiveSecondBar)
    {
        lock (_lock)
        {
            foreach (var tf in _timeframes.Values)
            {
                ProcessBarForTimeframe(tf, fiveSecondBar);
            }

            // Check for alignment changes
            var alignment = GetAlignmentInternal();
            if (AlignmentChanged(alignment))
            {
                _lastAlignment = alignment;
                OnAlignmentChanged?.Invoke(alignment);
            }
        }
    }

    /// <summary>
    /// Process a bar for a specific timeframe
    /// </summary>
    private void ProcessBarForTimeframe(TimeframeBarData tf, BarData bar)
    {
        var barPeriodStart = GetPeriodStart(bar.Time, tf.Config.SecondsPerBar);

        if (tf.CurrentPartialBar == null)
        {
            // Start new bar
            tf.CurrentPartialBar = new BarData
            {
                Time = barPeriodStart,
                Open = bar.Open,
                High = bar.High,
                Low = bar.Low,
                Close = bar.Close,
                Volume = bar.Volume
            };
            tf.CurrentBarStartTime = barPeriodStart;
        }
        else if (barPeriodStart > tf.CurrentBarStartTime)
        {
            // Complete current bar and start new one
            var completedBar = tf.CurrentPartialBar;
            tf.Bars.Add(completedBar);

            // Trim to lookback
            while (tf.Bars.Count > tf.Config.LookbackBars)
                tf.Bars.RemoveAt(0);

            // Recalculate SuperTrend for this timeframe
            RecalculateSuperTrend(tf);

            // Fire event
            OnTimeframeBarCompleted?.Invoke(tf.Config.Name, completedBar);

            // Start new partial bar
            tf.CurrentPartialBar = new BarData
            {
                Time = barPeriodStart,
                Open = bar.Open,
                High = bar.High,
                Low = bar.Low,
                Close = bar.Close,
                Volume = bar.Volume
            };
            tf.CurrentBarStartTime = barPeriodStart;
        }
        else
        {
            // Update current partial bar
            tf.CurrentPartialBar.High = Math.Max(tf.CurrentPartialBar.High, bar.High);
            tf.CurrentPartialBar.Low = Math.Min(tf.CurrentPartialBar.Low, bar.Low);
            tf.CurrentPartialBar.Close = bar.Close;
            tf.CurrentPartialBar.Volume += bar.Volume;
        }
    }

    /// <summary>
    /// Get the start time of a bar period
    /// </summary>
    private static DateTime GetPeriodStart(DateTime time, int secondsPerBar)
    {
        var ticks = time.Ticks;
        var ticksPerBar = (long)secondsPerBar * TimeSpan.TicksPerSecond;
        var periodStartTicks = (ticks / ticksPerBar) * ticksPerBar;
        return new DateTime(periodStartTicks, time.Kind);
    }

    /// <summary>
    /// Recalculate SuperTrend for a timeframe
    /// </summary>
    private void RecalculateSuperTrend(TimeframeBarData tf)
    {
        if (tf.Bars.Count < _superTrendPeriod)
            return;

        var highs = tf.Bars.Select(b => b.High).ToArray();
        var lows = tf.Bars.Select(b => b.Low).ToArray();
        var closes = tf.Bars.Select(b => b.Close).ToArray();

        // Use local variables for ref parameters (can't use ref on properties)
        double prevSuperTrend = tf.PrevSuperTrend;
        int prevDirection = tf.SuperTrendDirection;

        var (value, direction) = TechnicalIndicators.CalculateSuperTrendStateful(
            highs, lows, closes,
            _superTrendPeriod, _superTrendMultiplier,
            ref prevSuperTrend, ref prevDirection);

        // Assign back to properties
        tf.PrevSuperTrend = prevSuperTrend;
        tf.SuperTrend = value;
        tf.SuperTrendDirection = direction;
    }

    /// <summary>
    /// Get current MTF alignment status
    /// </summary>
    public MTFAlignmentResult GetAlignment()
    {
        lock (_lock)
        {
            return GetAlignmentInternal();
        }
    }

    /// <summary>
    /// Internal alignment check (must hold lock)
    /// </summary>
    private MTFAlignmentResult GetAlignmentInternal()
    {
        var result = new MTFAlignmentResult();
        var directions = new List<int>();

        foreach (var tf in _timeframes.Values)
        {
            result.DirectionByTimeframe[tf.Config.Name] = tf.SuperTrendDirection;
            if (tf.SuperTrendDirection != 0)
                directions.Add(tf.SuperTrendDirection);
        }

        if (directions.Count == _timeframes.Count && directions.Count > 0)
        {
            result.AllBullish = directions.All(d => d == 1);
            result.AllBearish = directions.All(d => d == -1);
        }

        // Build description
        var tfDescriptions = _timeframes.Values
            .Select(tf => $"{tf.Config.Name}:{(tf.SuperTrendDirection == 1 ? "BULL" : tf.SuperTrendDirection == -1 ? "BEAR" : "?")}")
            .ToList();

        result.AlignmentDescription = string.Join(" | ", tfDescriptions);

        if (result.AllBullish)
            result.AlignmentDescription += " => ALL BULLISH";
        else if (result.AllBearish)
            result.AlignmentDescription += " => ALL BEARISH";
        else
            result.AlignmentDescription += " => NOT ALIGNED";

        return result;
    }

    /// <summary>
    /// Check if alignment has changed
    /// </summary>
    private bool AlignmentChanged(MTFAlignmentResult newAlignment)
    {
        if (_lastAlignment.AllBullish != newAlignment.AllBullish)
            return true;
        if (_lastAlignment.AllBearish != newAlignment.AllBearish)
            return true;

        foreach (var kvp in newAlignment.DirectionByTimeframe)
        {
            if (!_lastAlignment.DirectionByTimeframe.TryGetValue(kvp.Key, out var oldDir))
                return true;
            if (oldDir != kvp.Value)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Get timeframe data for a specific timeframe
    /// </summary>
    public TimeframeBarData? GetTimeframe(string name)
    {
        lock (_lock)
        {
            return _timeframes.TryGetValue(name, out var tf) ? tf : null;
        }
    }

    /// <summary>
    /// Get all timeframe names
    /// </summary>
    public string[] GetTimeframeNames()
    {
        lock (_lock)
        {
            return _timeframes.Keys.ToArray();
        }
    }

    /// <summary>
    /// Get bar count for the smallest timeframe (used to check warmup)
    /// </summary>
    public int GetMinBarCount()
    {
        lock (_lock)
        {
            if (_timeframes.Count == 0) return 0;
            return _timeframes.Values.Min(tf => tf.Bars.Count);
        }
    }

    /// <summary>
    /// Check if all timeframes have enough bars for indicator calculation
    /// </summary>
    public bool IsWarmedUp()
    {
        lock (_lock)
        {
            return _timeframes.Values.All(tf => tf.Bars.Count >= _superTrendPeriod);
        }
    }

    /// <summary>
    /// Clear all bar data (for testing or reset)
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            foreach (var tf in _timeframes.Values)
            {
                tf.Bars.Clear();
                tf.CurrentPartialBar = null;
                tf.SuperTrend = 0;
                tf.SuperTrendDirection = 0;
                tf.PrevSuperTrend = 0;
            }
            _lastAlignment = new MTFAlignmentResult();
        }
    }

    private void Log(string message)
    {
        Logger.Info($"[MTFBarManager] {message}");
        OnLog?.Invoke(message);
    }
}
