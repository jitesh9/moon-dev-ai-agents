/*
 * MTF (Multi-Timeframe) Strategy Engine for GC Trading Application
 * Implements SuperTrend alignment across multiple timeframes with divergence entry
 */

namespace GCTradingApp;

/// <summary>
/// MTF Strategy configuration
/// </summary>
public class MTFStrategyConfig
{
    public string Name { get; set; } = "MTF";
    public TimeframePreset TimeframePreset { get; set; } = TimeframePreset.Preset_5m_15m_1H;
    public double PositionScale { get; set; } = 0.80;
    public bool DrawdownProtection { get; set; } = true;
    public double MaxDrawdown { get; set; } = 0.11;
    public int FixedContracts { get; set; } = 1;
    public double CapitalAllocation { get; set; } = 0;
    public bool AllowShorts { get; set; } = false;

    // SuperTrend parameters
    public int SuperTrendPeriod { get; set; } = 10;
    public double SuperTrendMultiplier { get; set; } = 3.0;
}

/// <summary>
/// MTF Strategy state for persistence (extends StrategyState)
/// </summary>
public class MTFStrategyState : StrategyState
{
    public int PositionDirection { get; set; }  // 1 = long, -1 = short, 0 = flat
    public TimeframePreset ActivePreset { get; set; }
}

/// <summary>
/// MTF Strategy Engine - Uses multi-timeframe SuperTrend alignment with divergence entry
/// </summary>
public class MTFStrategyEngine
{
    private readonly IBKRClient _dataClient;      // For market data (always real IBKR)
    private readonly IOrderClient _orderClient;   // For orders (real or paper)
    private readonly MTFStrategyConfig _config;
    private readonly MultiTimeframeBarManager _mtfManager;

    // Strategy parameters (from existing GCStrategyEngine)
    private readonly double _stopMult = 1.5;
    private readonly double _targetMult = 2.5;
    private readonly double _trailStartPct = 1.5;
    private readonly double _trailAtrMult = 1.0;
    private readonly int _maxHoldBars = 60;
    private readonly int _minConfirmations = 4;

    // Drawdown protection thresholds
    private readonly double _ddReductionThreshold = 0.08;
    private readonly double _ddPauseThreshold = 0.10;

    // State
    private bool _isRunning;
    private bool _inPosition;
    private bool _pendingEntry;
    private bool _pendingExit;
    private double _entryPrice;
    private DateTime _entryTime;
    private int _entryBarCount;
    private double _stopPrice;
    private double _targetPrice;
    private int _currentOrderId;
    private decimal _positionQuantity;
    private int _positionDirection;  // 1 = long, -1 = short

    // Bar data for divergence/confirmation calculations (5-second bars)
    private readonly List<BarData> _bars = new();
    private readonly object _barsLock = new();
    private readonly int _lookback = 100;

    // Calculated indicators (from 5-second bars for entry timing)
    private double _atr;
    private double _rsi;
    private double _macdHist;
    private double _prevMacdHist;
    private double _ema13;
    private double _ema34;
    private double _sma50;
    private double _superTrend;
    private bool _bullRegime;

    // MACD history for proper signal line calculation
    private readonly List<double> _macdHistory = new();

    // Drawdown tracking
    private double _peakEquity;
    private double _currentDrawdown;

    // Bar count for time-based exit
    private int _barCount;

    // Events
    public event Action<string>? OnLog;
    public event Action<MTFStrategyState>? OnStateChanged;
    public event Action<MTFAlignmentResult>? OnAlignmentUpdated;

    public MTFStrategyEngine(IBKRClient dataClient, IOrderClient orderClient, MTFStrategyConfig config, MTFStrategyState? savedState = null)
    {
        _dataClient = dataClient;
        _orderClient = orderClient;
        _config = config;

        // Create MTF manager with config parameters
        _mtfManager = new MultiTimeframeBarManager(config.SuperTrendPeriod, config.SuperTrendMultiplier);
        _mtfManager.Configure(config.TimeframePreset);
        _mtfManager.OnAlignmentChanged += alignment => OnAlignmentUpdated?.Invoke(alignment);
        _mtfManager.OnLog += msg => Log(msg);

        // Restore saved state if available
        if (savedState != null && savedState.InPosition)
        {
            _inPosition = savedState.InPosition;
            _entryPrice = savedState.EntryPrice;
            _entryTime = savedState.EntryTime;
            _entryBarCount = savedState.EntryBarCount;
            _stopPrice = savedState.StopPrice;
            _targetPrice = savedState.TargetPrice;
            _currentOrderId = savedState.CurrentOrderId;
            _positionQuantity = savedState.PositionQuantity;
            _positionDirection = savedState.PositionDirection;
            Log($"Restored position state - Direction: {(_positionDirection == 1 ? "LONG" : "SHORT")}, Entry: {_entryPrice:F2}");
        }

        _dataClient.OnRealtimeBar += OnBar;
        _orderClient.OnOrderStatus += OnOrderStatus;
        _dataClient.OnAccountUpdate += OnAccountUpdate;
    }

    /// <summary>
    /// Gets the current strategy state for persistence
    /// </summary>
    public MTFStrategyState GetState()
    {
        return new MTFStrategyState
        {
            InPosition = _inPosition,
            EntryPrice = _entryPrice,
            EntryTime = _entryTime,
            EntryBarCount = _entryBarCount,
            StopPrice = _stopPrice,
            TargetPrice = _targetPrice,
            CurrentOrderId = _currentOrderId,
            PositionQuantity = _positionQuantity,
            PositionDirection = _positionDirection,
            ActivePreset = _config.TimeframePreset
        };
    }

    private void NotifyStateChanged()
    {
        OnStateChanged?.Invoke(GetState());
    }

    public void Start()
    {
        _isRunning = true;
        Log($"MTF Strategy started - Preset: {_config.TimeframePreset}, AllowShorts: {_config.AllowShorts}");
    }

    public void Stop()
    {
        _isRunning = false;

        // Unsubscribe from events
        _dataClient.OnRealtimeBar -= OnBar;
        _orderClient.OnOrderStatus -= OnOrderStatus;
        _dataClient.OnAccountUpdate -= OnAccountUpdate;

        Log("MTF Strategy stopped");
    }

    private void OnBar(BarData bar)
    {
        if (!_isRunning) return;

        try
        {
            ProcessBar(bar);
        }
        catch (Exception ex)
        {
            Log($"Error processing bar: {ex.Message}");
        }
    }

    private void ProcessBar(BarData bar)
    {
        try
        {
            _barCount++;

            // Store bar for divergence calculations
            lock (_barsLock)
            {
                _bars.Add(bar);
                while (_bars.Count > _lookback)
                    _bars.RemoveAt(0);
            }

            // Forward to MTF manager for aggregation
            _mtfManager.ProcessBar(bar);

            // Calculate indicators from 5-second bars (for entry timing)
            CalculateIndicators();

            // Check if MTF manager is warmed up
            if (!_mtfManager.IsWarmedUp())
            {
                return;  // Wait for enough bars
            }

            // Get MTF alignment
            var alignment = _mtfManager.GetAlignment();

            // Process entry or position management
            if (!_inPosition && !_pendingEntry && !_pendingExit)
            {
                if (alignment.AllBullish)
                {
                    CheckLongEntry(bar, alignment);
                }
                else if (alignment.AllBearish && _config.AllowShorts)
                {
                    CheckShortEntry(bar, alignment);
                }
            }
            else if (_inPosition && !_pendingExit)
            {
                ManagePosition(bar);
            }
        }
        catch (Exception ex)
        {
            Log($"ERROR in ProcessBar: {ex.Message}");
            Logger.Error($"Error processing bar in {_config.Name}", ex);
            // Re-throw to let circuit breaker handle it
            // This allows circuit breaker to track failures and disable strategy if needed
            throw;
        }
    }

    private void CalculateIndicators()
    {
        List<BarData> barsSnapshot;
        lock (_barsLock)
        {
            if (_bars.Count < 50) return;
            barsSnapshot = _bars.ToList();
        }

        var closes = barsSnapshot.Select(b => b.Close).ToArray();
        var highs = barsSnapshot.Select(b => b.High).ToArray();
        var lows = barsSnapshot.Select(b => b.Low).ToArray();

        _atr = TechnicalIndicators.CalculateATR(highs, lows, closes, 14);
        _rsi = TechnicalIndicators.CalculateRSI(closes, 14);

        _prevMacdHist = _macdHist;
        var (macd, signal, hist) = TechnicalIndicators.CalculateMACD(closes, _macdHistory);
        _macdHist = hist;

        _ema13 = TechnicalIndicators.CalculateEMA(closes, 13);
        _ema34 = TechnicalIndicators.CalculateEMA(closes, 34);
        _sma50 = TechnicalIndicators.CalculateSMA(closes, 50);
        _superTrend = TechnicalIndicators.CalculateSuperTrend(highs, lows, closes, 10, 3.0);

        _bullRegime = closes.Last() > _sma50;
    }

    private void CheckLongEntry(BarData bar, MTFAlignmentResult alignment)
    {
        // Check for bullish divergence
        if (!DetectBullishDivergence()) return;

        // Count confirmations
        int confirmations = CountBullishConfirmations(bar);
        if (confirmations < _minConfirmations) return;

        // Calculate position size
        var quantity = CalculatePositionSize();
        if (quantity <= 0) return;

        // Calculate stop and target
        var stopDist = _stopMult * _atr;
        var targetDist = _targetMult * _atr;

        _stopPrice = bar.Close - stopDist;
        _targetPrice = bar.Close + targetDist;
        _positionDirection = 1;

        Log($"MTF LONG ENTRY SIGNAL - Price: {bar.Close:F2}, Stop: {_stopPrice:F2}, Target: {_targetPrice:F2}");
        Log($"  Alignment: {alignment.AlignmentDescription}");
        Log($"  Confirmations: {confirmations}");

        _pendingEntry = true;
        _orderClient.PlaceMarketOrder("BUY", (decimal)quantity, _config.Name);
    }

    private void CheckShortEntry(BarData bar, MTFAlignmentResult alignment)
    {
        // Check for bearish divergence
        if (!DetectBearishDivergence()) return;

        // Count confirmations
        int confirmations = CountBearishConfirmations(bar);
        if (confirmations < _minConfirmations) return;

        // Calculate position size
        var quantity = CalculatePositionSize();
        if (quantity <= 0) return;

        // Calculate stop and target (inverted for shorts)
        var stopDist = _stopMult * _atr;
        var targetDist = _targetMult * _atr;

        _stopPrice = bar.Close + stopDist;      // Stop above for shorts
        _targetPrice = bar.Close - targetDist;  // Target below for shorts
        _positionDirection = -1;

        Log($"MTF SHORT ENTRY SIGNAL - Price: {bar.Close:F2}, Stop: {_stopPrice:F2}, Target: {_targetPrice:F2}");
        Log($"  Alignment: {alignment.AlignmentDescription}");
        Log($"  Confirmations: {confirmations}");

        _pendingEntry = true;
        _orderClient.PlaceMarketOrder("SELL", (decimal)quantity, _config.Name);
    }

    private bool DetectBullishDivergence()
    {
        List<BarData> barsSnapshot;
        lock (_barsLock)
        {
            if (_bars.Count < 20) return false;
            barsSnapshot = _bars.ToList();
        }

        var closes = barsSnapshot.Select(b => b.Close).ToArray();

        // Calculate RSI values for divergence detection
        var rsiValues = new double[closes.Length];
        for (int i = 14; i < closes.Length; i++)
        {
            var subset = closes.Take(i + 1).ToArray();
            rsiValues[i] = TechnicalIndicators.CalculateRSI(subset, 14);
        }

        return TechnicalIndicators.DetectBullishDivergence(closes, rsiValues, 20);
    }

    private bool DetectBearishDivergence()
    {
        List<BarData> barsSnapshot;
        lock (_barsLock)
        {
            if (_bars.Count < 20) return false;
            barsSnapshot = _bars.ToList();
        }

        var closes = barsSnapshot.Select(b => b.Close).ToArray();

        // Calculate RSI values for divergence detection
        var rsiValues = new double[closes.Length];
        for (int i = 14; i < closes.Length; i++)
        {
            var subset = closes.Take(i + 1).ToArray();
            rsiValues[i] = TechnicalIndicators.CalculateRSI(subset, 14);
        }

        return TechnicalIndicators.DetectBearishDivergence(closes, rsiValues, 20);
    }

    private int CountBullishConfirmations(BarData bar)
    {
        int count = 0;

        // Bull regime (price > SMA50)
        if (_bullRegime) count++;

        // EMA bullish (13 > 34)
        if (_ema13 > _ema34) count++;

        // SuperTrend bullish (price > SuperTrend)
        if (bar.Close > _superTrend) count++;

        // RSI in valid range (30-70)
        if (_rsi >= 30 && _rsi <= 70) count++;

        // MACD improving
        if (_macdHist > _prevMacdHist) count++;

        // Volatility OK (ATR > 80% of average)
        if (_atr > 0) count++;  // Simplified check

        return count;
    }

    private int CountBearishConfirmations(BarData bar)
    {
        int count = 0;

        // Bear regime (price < SMA50)
        if (!_bullRegime) count++;

        // EMA bearish (13 < 34)
        if (_ema13 < _ema34) count++;

        // SuperTrend bearish (price < SuperTrend)
        if (bar.Close < _superTrend) count++;

        // RSI in valid range (30-70)
        if (_rsi >= 30 && _rsi <= 70) count++;

        // MACD declining
        if (_macdHist < _prevMacdHist) count++;

        // Volatility OK
        if (_atr > 0) count++;

        return count;
    }

    private int CalculatePositionSize()
    {
        if (_config.FixedContracts > 0)
        {
            var size = _config.FixedContracts;

            // Apply drawdown reduction if enabled
            if (_config.DrawdownProtection && _currentDrawdown > _ddReductionThreshold)
            {
                size = (int)(size * _config.PositionScale);
                if (size < 1) size = 1;
            }

            return size;
        }

        return 1;  // Default
    }

    private void ManagePosition(BarData bar)
    {
        _entryBarCount++;

        if (_positionDirection == 1)
        {
            ManageLongPosition(bar);
        }
        else if (_positionDirection == -1)
        {
            ManageShortPosition(bar);
        }
    }

    private void ManageLongPosition(BarData bar)
    {
        // Stop loss hit
        if (bar.Low <= _stopPrice)
        {
            Log($"LONG STOP HIT at {bar.Close:F2}");
            ClosePosition("SELL");
            return;
        }

        // Target hit
        if (bar.High >= _targetPrice)
        {
            Log($"LONG TARGET HIT at {bar.Close:F2}");
            ClosePosition("SELL");
            return;
        }

        // Time exit
        if (_entryBarCount >= _maxHoldBars)
        {
            Log($"LONG TIME EXIT after {_entryBarCount} bars");
            ClosePosition("SELL");
            return;
        }

        // Trailing stop
        var profitPct = (bar.Close - _entryPrice) / _entryPrice * 100;
        if (profitPct > _trailStartPct)
        {
            var newStop = bar.Close - (_trailAtrMult * _atr);
            if (newStop > _stopPrice)
            {
                _stopPrice = newStop;
                NotifyStateChanged();
            }
        }
    }

    private void ManageShortPosition(BarData bar)
    {
        // Stop loss hit (price goes up for shorts)
        if (bar.High >= _stopPrice)
        {
            Log($"SHORT STOP HIT at {bar.Close:F2}");
            ClosePosition("BUY");
            return;
        }

        // Target hit (price goes down for shorts)
        if (bar.Low <= _targetPrice)
        {
            Log($"SHORT TARGET HIT at {bar.Close:F2}");
            ClosePosition("BUY");
            return;
        }

        // Time exit
        if (_entryBarCount >= _maxHoldBars)
        {
            Log($"SHORT TIME EXIT after {_entryBarCount} bars");
            ClosePosition("BUY");
            return;
        }

        // Trailing stop for shorts (stop moves down)
        var profitPct = (_entryPrice - bar.Close) / _entryPrice * 100;
        if (profitPct > _trailStartPct)
        {
            var newStop = bar.Close + (_trailAtrMult * _atr);
            if (newStop < _stopPrice)
            {
                _stopPrice = newStop;
                NotifyStateChanged();
            }
        }
    }

    private void ClosePosition(string action)
    {
        if (_positionQuantity <= 0 || _pendingExit) return;

        _pendingExit = true;
        _orderClient.PlaceMarketOrder(action, _positionQuantity, _config.Name);
    }

    private void OnOrderStatus(OrderStatusData status)
    {
        if (status.Status == "Filled")
        {
            if (_pendingEntry)
            {
                _pendingEntry = false;
                _inPosition = true;
                _entryPrice = status.AvgFillPrice;
                _entryTime = DateTime.Now;
                _entryBarCount = 0;
                _positionQuantity = status.Filled;

                var direction = _positionDirection == 1 ? "LONG" : "SHORT";
                Log($"MTF {direction} ENTRY FILLED at {_entryPrice:F2}, Qty: {_positionQuantity}");
                NotifyStateChanged();
            }
            else if (_pendingExit)
            {
                var pnl = _positionDirection == 1
                    ? (status.AvgFillPrice - _entryPrice) * (double)_positionQuantity * 100
                    : (_entryPrice - status.AvgFillPrice) * (double)_positionQuantity * 100;

                var direction = _positionDirection == 1 ? "LONG" : "SHORT";
                Log($"MTF {direction} EXIT FILLED at {status.AvgFillPrice:F2}, PnL: ${pnl:F2}");

                _pendingExit = false;
                _inPosition = false;
                _positionQuantity = 0;
                _positionDirection = 0;
                NotifyStateChanged();
            }
        }
        else if (status.Status == "Cancelled" || status.Status == "ApiCancelled")
        {
            Log($"Order {status.OrderId} cancelled");
            _pendingEntry = false;
            _pendingExit = false;
        }
    }

    private void OnAccountUpdate(string key, string value, string currency)
    {
        if (key == "NetLiquidation" && double.TryParse(value, out var equity))
        {
            if (equity > _peakEquity)
                _peakEquity = equity;

            if (_peakEquity > 0)
                _currentDrawdown = (_peakEquity - equity) / _peakEquity;
        }
    }

    /// <summary>
    /// Get current MTF alignment
    /// </summary>
    public MTFAlignmentResult GetAlignment()
    {
        return _mtfManager.GetAlignment();
    }

    /// <summary>
    /// Get timeframe names for this strategy
    /// </summary>
    public string[] GetTimeframeNames()
    {
        return _mtfManager.GetTimeframeNames();
    }

    private void Log(string message)
    {
        var logMessage = $"[{_config.Name}] {message}";
        Logger.LogStrategy(_config.Name, message);
        OnLog?.Invoke(logMessage);
    }
}
