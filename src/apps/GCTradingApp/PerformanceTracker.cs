/*
 * Performance Tracker for GC Trading Application
 * Calculates trading metrics: win rate, profit factor, Sharpe ratio, etc.
 */

namespace GCTradingApp;

/// <summary>
/// Represents a completed round-trip trade
/// </summary>
public class CompletedTrade
{
    public string Strategy { get; set; } = "";
    public DateTime EntryTime { get; set; }
    public DateTime ExitTime { get; set; }
    public double EntryPrice { get; set; }
    public double ExitPrice { get; set; }
    public decimal Quantity { get; set; }
    public double GrossPnL { get; set; }
    public double Commission { get; set; }
    public double NetPnL => GrossPnL - Commission;
    public bool IsWinner => NetPnL > 0;
    public double ReturnPct => EntryPrice > 0 ? (ExitPrice - EntryPrice) / EntryPrice * 100 : 0;
    public TimeSpan Duration => ExitTime - EntryTime;
}

/// <summary>
/// Equity curve data point
/// </summary>
public class EquityPoint
{
    public DateTime Time { get; set; }
    public double Equity { get; set; }
    public double Drawdown { get; set; }
    public double DrawdownPct { get; set; }
}

/// <summary>
/// Performance metrics summary
/// </summary>
public class PerformanceMetrics
{
    // Trade counts
    public int TotalTrades { get; set; }
    public int WinningTrades { get; set; }
    public int LosingTrades { get; set; }

    // Win rate
    public double WinRate => TotalTrades > 0 ? (double)WinningTrades / TotalTrades * 100 : 0;

    // PnL metrics
    public double TotalNetPnL { get; set; }
    public double TotalGrossPnL { get; set; }
    public double TotalCommission { get; set; }
    public double AverageWin { get; set; }
    public double AverageLoss { get; set; }
    public double LargestWin { get; set; }
    public double LargestLoss { get; set; }

    // Ratios
    public double ProfitFactor { get; set; }
    public double ExpectedValue { get; set; }
    public double PayoffRatio => AverageLoss != 0 ? Math.Abs(AverageWin / AverageLoss) : 0;

    // Risk metrics
    public double SharpeRatio { get; set; }
    public double SortinoRatio { get; set; }
    public double MaxDrawdown { get; set; }
    public double MaxDrawdownPct { get; set; }

    // Time metrics
    public TimeSpan AverageHoldTime { get; set; }
    public TimeSpan LongestWin { get; set; }
    public TimeSpan LongestLoss { get; set; }

    // Streaks
    public int CurrentStreak { get; set; }
    public int MaxWinStreak { get; set; }
    public int MaxLoseStreak { get; set; }

    // Daily metrics
    public double TodayPnL { get; set; }
    public int TodayTrades { get; set; }
}

/// <summary>
/// Performance settings
/// </summary>
public class PerformanceSettings
{
    public double RiskFreeRate { get; set; } = 0.05; // 5% annual risk-free rate
    public int SharpeAnnualizationFactor { get; set; } = 252; // Trading days per year
}

/// <summary>
/// Tracks and calculates trading performance metrics
/// </summary>
public class PerformanceTracker : IDisposable
{
    private readonly object _lock = new();
    private readonly List<CompletedTrade> _trades = new();
    private readonly List<EquityPoint> _equityCurve = new();
    private readonly Dictionary<string, List<CompletedTrade>> _tradesByStrategy = new();
    private readonly PerformanceSettings _settings;

    private double _startingEquity;
    private double _currentEquity;
    private double _peakEquity;

    // Events
    public event Action<CompletedTrade>? OnTradeCompleted;
    public event Action<PerformanceMetrics>? OnMetricsUpdated;
    public event Action<string>? OnLog;

    public PerformanceSettings Settings => _settings;

    public PerformanceTracker(PerformanceSettings? settings = null, double startingEquity = 0)
    {
        _settings = settings ?? new PerformanceSettings();
        _startingEquity = startingEquity;
        _currentEquity = startingEquity;
        _peakEquity = startingEquity;
    }

    /// <summary>
    /// Record a completed trade
    /// </summary>
    public void RecordTrade(CompletedTrade trade)
    {
        lock (_lock)
        {
            _trades.Add(trade);

            // Track by strategy
            if (!_tradesByStrategy.ContainsKey(trade.Strategy))
            {
                _tradesByStrategy[trade.Strategy] = new List<CompletedTrade>();
            }
            _tradesByStrategy[trade.Strategy].Add(trade);

            // Update equity
            _currentEquity += trade.NetPnL;
            if (_currentEquity > _peakEquity)
            {
                _peakEquity = _currentEquity;
            }

            // Add equity point
            _equityCurve.Add(new EquityPoint
            {
                Time = trade.ExitTime,
                Equity = _currentEquity,
                Drawdown = _peakEquity - _currentEquity,
                DrawdownPct = _peakEquity > 0 ? (_peakEquity - _currentEquity) / _peakEquity * 100 : 0
            });

            Log($"Trade recorded: {trade.Strategy} {(trade.IsWinner ? "WIN" : "LOSS")} ${trade.NetPnL:F2}");

            OnTradeCompleted?.Invoke(trade);
            OnMetricsUpdated?.Invoke(GetMetrics());
        }
    }

    /// <summary>
    /// Record a trade from entry/exit fills
    /// </summary>
    public void RecordTrade(string strategy, DateTime entryTime, double entryPrice,
        DateTime exitTime, double exitPrice, decimal quantity, double commission)
    {
        var grossPnL = (double)quantity * (exitPrice - entryPrice);
        // For GC futures, multiply by contract multiplier (100 oz)
        grossPnL *= 100;

        var trade = new CompletedTrade
        {
            Strategy = strategy,
            EntryTime = entryTime,
            ExitTime = exitTime,
            EntryPrice = entryPrice,
            ExitPrice = exitPrice,
            Quantity = quantity,
            GrossPnL = grossPnL,
            Commission = commission
        };

        RecordTrade(trade);
    }

    /// <summary>
    /// Update current equity from account
    /// </summary>
    public void UpdateEquity(double equity)
    {
        lock (_lock)
        {
            _currentEquity = equity;
            if (_currentEquity > _peakEquity)
            {
                _peakEquity = _currentEquity;
            }

            // Add equity point periodically (e.g., on each update)
            if (_equityCurve.Count == 0 ||
                (DateTime.Now - _equityCurve[^1].Time).TotalMinutes >= 5)
            {
                _equityCurve.Add(new EquityPoint
                {
                    Time = DateTime.Now,
                    Equity = _currentEquity,
                    Drawdown = _peakEquity - _currentEquity,
                    DrawdownPct = _peakEquity > 0 ? (_peakEquity - _currentEquity) / _peakEquity * 100 : 0
                });
            }
        }
    }

    /// <summary>
    /// Get overall performance metrics
    /// </summary>
    public PerformanceMetrics GetMetrics()
    {
        lock (_lock)
        {
            return CalculateMetrics(_trades);
        }
    }

    /// <summary>
    /// Get performance metrics for a specific strategy
    /// </summary>
    public PerformanceMetrics GetMetrics(string strategy)
    {
        lock (_lock)
        {
            if (_tradesByStrategy.TryGetValue(strategy, out var trades))
            {
                return CalculateMetrics(trades);
            }
            return new PerformanceMetrics();
        }
    }

    /// <summary>
    /// Get today's performance metrics
    /// </summary>
    public PerformanceMetrics GetTodayMetrics()
    {
        lock (_lock)
        {
            var todayTrades = _trades.Where(t => t.ExitTime.Date == DateTime.Today).ToList();
            return CalculateMetrics(todayTrades);
        }
    }

    /// <summary>
    /// Get the equity curve data
    /// </summary>
    public List<EquityPoint> GetEquityCurve()
    {
        lock (_lock)
        {
            return _equityCurve.ToList();
        }
    }

    /// <summary>
    /// Get all completed trades
    /// </summary>
    public List<CompletedTrade> GetTrades()
    {
        lock (_lock)
        {
            return _trades.ToList();
        }
    }

    /// <summary>
    /// Get trades for a specific strategy
    /// </summary>
    public List<CompletedTrade> GetTrades(string strategy)
    {
        lock (_lock)
        {
            if (_tradesByStrategy.TryGetValue(strategy, out var trades))
            {
                return trades.ToList();
            }
            return new List<CompletedTrade>();
        }
    }

    /// <summary>
    /// Get recent trades
    /// </summary>
    public List<CompletedTrade> GetRecentTrades(int count = 10)
    {
        lock (_lock)
        {
            return _trades.OrderByDescending(t => t.ExitTime).Take(count).ToList();
        }
    }

    /// <summary>
    /// Calculate metrics from a list of trades
    /// </summary>
    private PerformanceMetrics CalculateMetrics(List<CompletedTrade> trades)
    {
        var metrics = new PerformanceMetrics();

        if (trades.Count == 0)
            return metrics;

        var winners = trades.Where(t => t.IsWinner).ToList();
        var losers = trades.Where(t => !t.IsWinner).ToList();

        // Trade counts
        metrics.TotalTrades = trades.Count;
        metrics.WinningTrades = winners.Count;
        metrics.LosingTrades = losers.Count;

        // PnL totals
        metrics.TotalNetPnL = trades.Sum(t => t.NetPnL);
        metrics.TotalGrossPnL = trades.Sum(t => t.GrossPnL);
        metrics.TotalCommission = trades.Sum(t => t.Commission);

        // Averages
        metrics.AverageWin = winners.Count > 0 ? winners.Average(t => t.NetPnL) : 0;
        metrics.AverageLoss = losers.Count > 0 ? losers.Average(t => t.NetPnL) : 0;

        // Extremes
        metrics.LargestWin = winners.Count > 0 ? winners.Max(t => t.NetPnL) : 0;
        metrics.LargestLoss = losers.Count > 0 ? losers.Min(t => t.NetPnL) : 0;

        // Profit Factor
        var grossWins = winners.Sum(t => t.NetPnL);
        var grossLosses = Math.Abs(losers.Sum(t => t.NetPnL));
        metrics.ProfitFactor = grossLosses > 0 ? grossWins / grossLosses : grossWins > 0 ? double.PositiveInfinity : 0;

        // Expected Value (Expectancy)
        metrics.ExpectedValue = metrics.TotalNetPnL / trades.Count;

        // Sharpe Ratio
        metrics.SharpeRatio = CalculateSharpeRatio(trades);

        // Sortino Ratio
        metrics.SortinoRatio = CalculateSortinoRatio(trades);

        // Max Drawdown
        CalculateMaxDrawdown(trades, out var maxDD, out var maxDDPct);
        metrics.MaxDrawdown = maxDD;
        metrics.MaxDrawdownPct = maxDDPct;

        // Time metrics
        if (trades.Count > 0)
        {
            var durations = trades.Select(t => t.Duration).ToList();
            metrics.AverageHoldTime = TimeSpan.FromTicks((long)durations.Average(d => d.Ticks));

            if (winners.Count > 0)
                metrics.LongestWin = winners.Max(t => t.Duration);
            if (losers.Count > 0)
                metrics.LongestLoss = losers.Max(t => t.Duration);
        }

        // Streaks
        CalculateStreaks(trades, out var current, out var maxWin, out var maxLose);
        metrics.CurrentStreak = current;
        metrics.MaxWinStreak = maxWin;
        metrics.MaxLoseStreak = maxLose;

        // Today's metrics
        var todayTrades = trades.Where(t => t.ExitTime.Date == DateTime.Today).ToList();
        metrics.TodayPnL = todayTrades.Sum(t => t.NetPnL);
        metrics.TodayTrades = todayTrades.Count;

        return metrics;
    }

    /// <summary>
    /// Calculate Sharpe Ratio
    /// </summary>
    private double CalculateSharpeRatio(List<CompletedTrade> trades)
    {
        if (trades.Count < 2)
            return 0;

        var returns = trades.Select(t => t.NetPnL).ToList();
        var avgReturn = returns.Average();
        var stdDev = CalculateStdDev(returns);

        if (stdDev == 0)
            return 0;

        // Annualized Sharpe Ratio
        var dailyRiskFreeRate = _settings.RiskFreeRate / _settings.SharpeAnnualizationFactor;
        var excessReturn = avgReturn - dailyRiskFreeRate;
        var sharpe = excessReturn / stdDev * Math.Sqrt(_settings.SharpeAnnualizationFactor);

        return sharpe;
    }

    /// <summary>
    /// Calculate Sortino Ratio (only considers downside volatility)
    /// </summary>
    private double CalculateSortinoRatio(List<CompletedTrade> trades)
    {
        if (trades.Count < 2)
            return 0;

        var returns = trades.Select(t => t.NetPnL).ToList();
        var avgReturn = returns.Average();

        // Downside deviation (only negative returns)
        var negativeReturns = returns.Where(r => r < 0).ToList();
        if (negativeReturns.Count == 0)
            return double.PositiveInfinity;

        var downsideDev = Math.Sqrt(negativeReturns.Average(r => r * r));
        if (downsideDev == 0)
            return 0;

        var dailyRiskFreeRate = _settings.RiskFreeRate / _settings.SharpeAnnualizationFactor;
        var excessReturn = avgReturn - dailyRiskFreeRate;
        var sortino = excessReturn / downsideDev * Math.Sqrt(_settings.SharpeAnnualizationFactor);

        return sortino;
    }

    /// <summary>
    /// Calculate maximum drawdown
    /// </summary>
    private void CalculateMaxDrawdown(List<CompletedTrade> trades, out double maxDD, out double maxDDPct)
    {
        maxDD = 0;
        maxDDPct = 0;

        if (trades.Count == 0)
            return;

        double peak = _startingEquity;
        double equity = _startingEquity;

        foreach (var trade in trades.OrderBy(t => t.ExitTime))
        {
            equity += trade.NetPnL;
            if (equity > peak)
            {
                peak = equity;
            }

            var dd = peak - equity;
            var ddPct = peak > 0 ? dd / peak * 100 : 0;

            if (dd > maxDD)
            {
                maxDD = dd;
                maxDDPct = ddPct;
            }
        }
    }

    /// <summary>
    /// Calculate win/loss streaks
    /// </summary>
    private void CalculateStreaks(List<CompletedTrade> trades, out int current, out int maxWin, out int maxLose)
    {
        current = 0;
        maxWin = 0;
        maxLose = 0;

        if (trades.Count == 0)
            return;

        int winStreak = 0;
        int loseStreak = 0;

        foreach (var trade in trades.OrderBy(t => t.ExitTime))
        {
            if (trade.IsWinner)
            {
                winStreak++;
                loseStreak = 0;
                maxWin = Math.Max(maxWin, winStreak);
            }
            else
            {
                loseStreak++;
                winStreak = 0;
                maxLose = Math.Max(maxLose, loseStreak);
            }
        }

        // Current streak (positive = wins, negative = losses)
        current = winStreak > 0 ? winStreak : -loseStreak;
    }

    /// <summary>
    /// Calculate standard deviation
    /// </summary>
    private double CalculateStdDev(List<double> values)
    {
        if (values.Count < 2)
            return 0;

        var avg = values.Average();
        var sumSquares = values.Sum(v => (v - avg) * (v - avg));
        return Math.Sqrt(sumSquares / (values.Count - 1));
    }

    /// <summary>
    /// Get a formatted performance summary
    /// </summary>
    public string GetSummary()
    {
        var m = GetMetrics();
        return $@"=== PERFORMANCE SUMMARY ===
Trades: {m.TotalTrades} (W: {m.WinningTrades} / L: {m.LosingTrades})
Win Rate: {m.WinRate:F1}%
Net PnL: ${m.TotalNetPnL:F2}
Profit Factor: {m.ProfitFactor:F2}
Sharpe Ratio: {m.SharpeRatio:F2}
Max Drawdown: ${m.MaxDrawdown:F2} ({m.MaxDrawdownPct:F1}%)
Avg Win: ${m.AverageWin:F2} | Avg Loss: ${m.AverageLoss:F2}
Payoff Ratio: {m.PayoffRatio:F2}
Current Streak: {m.CurrentStreak}
Today: {m.TodayTrades} trades, ${m.TodayPnL:F2}
==============================";
    }

    /// <summary>
    /// Clear all trade history
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _trades.Clear();
            _tradesByStrategy.Clear();
            _equityCurve.Clear();
            _currentEquity = _startingEquity;
            _peakEquity = _startingEquity;
            Log("Performance tracker cleared");
        }
    }

    /// <summary>
    /// Load trades from fills history
    /// </summary>
    public void LoadFromFills(List<FillRecord> fills, Dictionary<string, StrategyState> strategyStates)
    {
        // Group fills by strategy and match entries with exits
        var fillsByStrategy = fills.GroupBy(f => f.Strategy);

        foreach (var group in fillsByStrategy)
        {
            var strategyFills = group.OrderBy(f => f.Time).ToList();
            var entries = new Queue<FillRecord>();

            foreach (var fill in strategyFills)
            {
                if (fill.Action == "BUY")
                {
                    entries.Enqueue(fill);
                }
                else if (fill.Action == "SELL" && entries.Count > 0)
                {
                    var entry = entries.Dequeue();
                    RecordTrade(
                        group.Key,
                        entry.Time,
                        entry.Price,
                        fill.Time,
                        fill.Price,
                        Math.Min(entry.Quantity, fill.Quantity),
                        entry.Commission + fill.Commission
                    );
                }
            }
        }

        Log($"Loaded {_trades.Count} trades from fill history");
    }

    private void Log(string message)
    {
        Logger.Info($"[PerformanceTracker] {message}");
        OnLog?.Invoke(message);
    }

    public void Dispose()
    {
        // Nothing to dispose
    }
}
