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
public class MTFStrategyEngine : IStrategyEngine
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

    // IStrategyEngine implementation
    public string Name => _config.Name;

    // Events
    public event Action<string>? OnLog;
    public event Action<StrategyState>? OnStateChanged;  // IStrategyEngine interface
    public event Action<MTFStrategyState>? OnMTFStateChanged;  // MTF-specific event
    public event Action<MTFAlignmentResult>? OnAlignmentUpdated;
    public event Action<EntryConditionsResult>? OnEntryConditionsUpdated;

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
            _entryBarCount = (int)savedState.EntryBarIndex;
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
    /// Gets the current strategy state for persistence (IStrategyEngine implementation)
    /// </summary>
    StrategyState IStrategyEngine.GetState()
    {
        return GetState();
    }

    /// <summary>
    /// Gets the current MTF strategy state for persistence
    /// </summary>
    public MTFStrategyState GetState()
    {
        return new MTFStrategyState
        {
            InPosition = _inPosition,
            EntryPrice = _entryPrice,
            EntryTime = _entryTime,
            EntryBarIndex = _entryBarCount,
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
        var state = GetState();
        // Fire both base interface event (as StrategyState) and MTF-specific event
        OnStateChanged?.Invoke(state);
        OnMTFStateChanged?.Invoke(state);
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
        ProcessBar(bar);  // ProcessBar now handles exceptions and re-throws for circuit breaker
    }

    public void ProcessBar(BarData bar)
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

            // Update entry condition status for UI
            var conditionsResult = EvaluateEntryConditions(bar, alignment);
            OnEntryConditionsUpdated?.Invoke(conditionsResult);
        }
        catch (Exception ex)
        {
            Log($"ERROR in ProcessBar: {ex.Message}");
            Logger.Error($"Error processing bar in {_config.Name}", ex);
            // Re-throw to let circuit breaker handle it
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

    /// <summary>
    /// Evaluates all entry conditions and returns their status
    /// </summary>
    public EntryConditionsResult EvaluateEntryConditions(BarData? bar = null, MTFAlignmentResult? alignment = null)
    {
        var result = new EntryConditionsResult
        {
            StrategyName = _config.Name,
            RequiredConfirmations = _minConfirmations,
            CanEnter = false
        };

        // If not running, return empty result
        if (!_isRunning)
        {
            result.Conditions.Add(new EntryConditionStatus
            {
                ConditionName = "Strategy Running",
                IsTrue = false,
                Description = "Strategy must be running"
            });
            return result;
        }

        // Check if already in position
        if (_inPosition)
        {
            result.Conditions.Add(new EntryConditionStatus
            {
                ConditionName = "Not In Position",
                IsTrue = false,
                Description = "Already in position"
            });
            result.BlockingReason = "Already in position";
            return result;
        }

        // Check if pending entry/exit
        if (_pendingEntry || _pendingExit)
        {
            result.Conditions.Add(new EntryConditionStatus
            {
                ConditionName = "No Pending Orders",
                IsTrue = false,
                Description = _pendingEntry ? "Entry order pending" : "Exit order pending"
            });
            result.BlockingReason = _pendingEntry ? "Entry order pending" : "Exit order pending";
            return result;
        }

        // Need enough bars
        int barCount;
        lock (_barsLock)
        {
            barCount = _bars.Count;
        }

        if (barCount < 50)
        {
            result.Conditions.Add(new EntryConditionStatus
            {
                ConditionName = "Enough Bars",
                IsTrue = false,
                Description = $"Need 50 bars, have {barCount}",
                Value = $"{barCount}/50"
            });
            result.BlockingReason = "Not enough bars";
            return result;
        }
        result.Conditions.Add(new EntryConditionStatus
        {
            ConditionName = "Enough Bars",
            IsTrue = true,
            Description = $"Have {barCount} bars",
            Value = $"{barCount}/50"
        });

        // Check MTF manager warmup
        if (!_mtfManager.IsWarmedUp())
        {
            result.Conditions.Add(new EntryConditionStatus
            {
                ConditionName = "MTF Warmed Up",
                IsTrue = false,
                Description = "MTF manager not warmed up"
            });
            result.BlockingReason = "MTF manager not warmed up";
            return result;
        }
        result.Conditions.Add(new EntryConditionStatus
        {
            ConditionName = "MTF Warmed Up",
            IsTrue = true,
            Description = "MTF manager ready"
        });

        // Get alignment if not provided
        if (alignment == null)
        {
            alignment = _mtfManager.GetAlignment();
        }

        // Check MTF alignment
        bool isLongCandidate = alignment.AllBullish;
        bool isShortCandidate = alignment.AllBearish && _config.AllowShorts;
        bool alignmentOk = isLongCandidate || isShortCandidate;

        string alignmentDesc = alignmentOk
            ? (isLongCandidate ? "All timeframes bullish" : "All timeframes bearish")
            : "Timeframes not aligned";

        result.Conditions.Add(new EntryConditionStatus
        {
            ConditionName = "MTF Alignment",
            IsTrue = alignmentOk,
            Description = alignmentDesc,
            Value = alignment.AlignmentDescription
        });

        if (!alignmentOk)
        {
            result.BlockingReason = "Timeframes not aligned";
            return result;
        }

        // Use latest bar if not provided
        if (bar == null)
        {
            lock (_barsLock)
            {
                bar = _bars.Count > 0 ? _bars.Last() : null;
            }
        }

        if (bar == null)
        {
            result.BlockingReason = "No bar data";
            return result;
        }

        // Check divergence (direction-specific)
        bool hasDivergence;
        string divergenceName;
        if (isLongCandidate)
        {
            hasDivergence = DetectBullishDivergence();
            divergenceName = "Bullish Divergence";
        }
        else
        {
            hasDivergence = DetectBearishDivergence();
            divergenceName = "Bearish Divergence";
        }

        result.Conditions.Add(new EntryConditionStatus
        {
            ConditionName = divergenceName,
            IsTrue = hasDivergence,
            Description = hasDivergence ? $"{divergenceName} detected" : $"No {divergenceName.ToLower()}"
        });

        if (!hasDivergence)
        {
            result.BlockingReason = $"No {divergenceName.ToLower()}";
            return result;
        }

        // Evaluate confirmations (direction-specific)
        List<EntryConditionStatus> confirmations;
        if (isLongCandidate)
        {
            confirmations = EvaluateBullishConfirmations(bar);
        }
        else
        {
            confirmations = EvaluateBearishConfirmations(bar);
        }

        result.ConfirmationsCount = confirmations.Count(c => c.IsTrue);
        result.Conditions.AddRange(confirmations);

        if (result.ConfirmationsCount < _minConfirmations)
        {
            result.BlockingReason = $"Only {result.ConfirmationsCount}/{_minConfirmations} confirmations";
            return result;
        }

        // Check position size
        var quantity = CalculatePositionSize();
        bool sizeOk = quantity > 0;
        result.Conditions.Add(new EntryConditionStatus
        {
            ConditionName = "Position Size",
            IsTrue = sizeOk,
            Description = sizeOk ? $"Position size: {quantity}" : "Position size is 0",
            Value = quantity.ToString()
        });

        if (!sizeOk)
        {
            result.BlockingReason = "Position size is 0";
            return result;
        }

        // All conditions met
        result.CanEnter = true;
        return result;
    }

    /// <summary>
    /// Evaluates individual bullish confirmation conditions
    /// </summary>
    private List<EntryConditionStatus> EvaluateBullishConfirmations(BarData bar)
    {
        var conditions = new List<EntryConditionStatus>();

        // Bull regime (price > SMA50)
        conditions.Add(new EntryConditionStatus
        {
            ConditionName = "Bull Regime",
            IsTrue = _bullRegime,
            Description = _bullRegime ? "Price > SMA50" : "Price < SMA50",
            Value = $"SMA50: {_sma50:F2}"
        });

        // EMA bullish (13 > 34)
        bool emaBullish = _ema13 > _ema34;
        conditions.Add(new EntryConditionStatus
        {
            ConditionName = "EMA Alignment",
            IsTrue = emaBullish,
            Description = emaBullish ? "EMA13 > EMA34" : "EMA13 < EMA34",
            Value = $"EMA13: {_ema13:F2}, EMA34: {_ema34:F2}"
        });

        // SuperTrend bullish
        bool superTrendBullish = bar.Close > _superTrend;
        conditions.Add(new EntryConditionStatus
        {
            ConditionName = "SuperTrend",
            IsTrue = superTrendBullish,
            Description = superTrendBullish ? "Price > SuperTrend" : "Price < SuperTrend",
            Value = $"ST: {_superTrend:F2}"
        });

        // RSI in valid range (30-70)
        bool rsiOk = _rsi >= 30 && _rsi <= 70;
        conditions.Add(new EntryConditionStatus
        {
            ConditionName = "RSI Range",
            IsTrue = rsiOk,
            Description = rsiOk ? "RSI in range (30-70)" : $"RSI out of range: {_rsi:F1}",
            Value = $"{_rsi:F1}"
        });

        // MACD improving
        bool macdImproving = _macdHist > _prevMacdHist;
        conditions.Add(new EntryConditionStatus
        {
            ConditionName = "MACD Improving",
            IsTrue = macdImproving,
            Description = macdImproving ? "MACD histogram increasing" : "MACD histogram declining",
            Value = $"Hist: {_macdHist:F4}"
        });

        // Volatility OK
        bool volatilityOk = _atr > 0;
        conditions.Add(new EntryConditionStatus
        {
            ConditionName = "Volatility",
            IsTrue = volatilityOk,
            Description = volatilityOk ? "ATR sufficient" : "ATR too low",
            Value = $"ATR: {_atr:F2}"
        });

        return conditions;
    }

    /// <summary>
    /// Evaluates individual bearish confirmation conditions
    /// </summary>
    private List<EntryConditionStatus> EvaluateBearishConfirmations(BarData bar)
    {
        var conditions = new List<EntryConditionStatus>();

        // Bear regime (price < SMA50)
        conditions.Add(new EntryConditionStatus
        {
            ConditionName = "Bear Regime",
            IsTrue = !_bullRegime,
            Description = !_bullRegime ? "Price < SMA50" : "Price > SMA50",
            Value = $"SMA50: {_sma50:F2}"
        });

        // EMA bearish (13 < 34)
        bool emaBearish = _ema13 < _ema34;
        conditions.Add(new EntryConditionStatus
        {
            ConditionName = "EMA Alignment",
            IsTrue = emaBearish,
            Description = emaBearish ? "EMA13 < EMA34" : "EMA13 > EMA34",
            Value = $"EMA13: {_ema13:F2}, EMA34: {_ema34:F2}"
        });

        // SuperTrend bearish
        bool superTrendBearish = bar.Close < _superTrend;
        conditions.Add(new EntryConditionStatus
        {
            ConditionName = "SuperTrend",
            IsTrue = superTrendBearish,
            Description = superTrendBearish ? "Price < SuperTrend" : "Price > SuperTrend",
            Value = $"ST: {_superTrend:F2}"
        });

        // RSI in valid range (30-70)
        bool rsiOk = _rsi >= 30 && _rsi <= 70;
        conditions.Add(new EntryConditionStatus
        {
            ConditionName = "RSI Range",
            IsTrue = rsiOk,
            Description = rsiOk ? "RSI in range (30-70)" : $"RSI out of range: {_rsi:F1}",
            Value = $"{_rsi:F1}"
        });

        // MACD declining
        bool macdDeclining = _macdHist < _prevMacdHist;
        conditions.Add(new EntryConditionStatus
        {
            ConditionName = "MACD Declining",
            IsTrue = macdDeclining,
            Description = macdDeclining ? "MACD histogram declining" : "MACD histogram increasing",
            Value = $"Hist: {_macdHist:F4}"
        });

        // Volatility OK
        bool volatilityOk = _atr > 0;
        conditions.Add(new EntryConditionStatus
        {
            ConditionName = "Volatility",
            IsTrue = volatilityOk,
            Description = volatilityOk ? "ATR sufficient" : "ATR too low",
            Value = $"ATR: {_atr:F2}"
        });

        return conditions;
    }

    /// <summary>
    /// Evaluates all exit conditions and returns their status
    /// </summary>
    public ExitConditionsResult? EvaluateExitConditions(BarData? bar = null)
    {
        var result = new ExitConditionsResult
        {
            StrategyName = _config.Name,
            InPosition = _inPosition,
            ShouldExit = false,
            EntryPrice = _entryPrice,
            StopPrice = _stopPrice,
            TargetPrice = _targetPrice,
            BarsHeld = _entryBarCount
        };

        // If not in position, return result indicating no position
        if (!_inPosition)
        {
            result.Conditions.Add(new ExitConditionStatus
            {
                ConditionName = "In Position",
                IsTrue = false,
                Description = "Not in position - no exit conditions to evaluate"
            });
            return result;
        }

        // Use latest bar if not provided
        if (bar == null)
        {
            lock (_barsLock)
            {
                bar = _bars.Count > 0 ? _bars.Last() : null;
            }
        }

        if (bar == null)
        {
            result.Conditions.Add(new ExitConditionStatus
            {
                ConditionName = "Bar Data",
                IsTrue = false,
                Description = "No bar data available"
            });
            return result;
        }

        var price = bar.Close;
        double pnl;
        double pnlPct;
        double currentStop = _stopPrice;
        bool isLong = _positionDirection == 1;

        if (isLong)
        {
            pnlPct = (price - _entryPrice) / _entryPrice * 100;
            pnl = (price - _entryPrice) * (double)_positionQuantity * 100; // 100 oz multiplier

            // Check trailing stop for longs
            if (pnlPct > _trailStartPct)
            {
                var newStop = price - (_trailAtrMult * _atr);
                if (newStop > _stopPrice)
                {
                    currentStop = newStop;
                }
            }

            // Check stop loss (price goes down)
            bool stopHit = bar.Low <= currentStop;
            result.Conditions.Add(new ExitConditionStatus
            {
                ConditionName = "Stop Loss",
                IsTrue = stopHit,
                Description = stopHit ? $"Stop loss hit at {bar.Low:F2}" : $"Stop loss not hit (Stop: {currentStop:F2})",
                Value = $"Stop: {currentStop:F2}, Low: {bar.Low:F2}"
            });

            if (stopHit)
            {
                result.ShouldExit = true;
                result.ExitReason = "Stop Loss";
            }

            // Check target (price goes up)
            bool targetHit = bar.High >= _targetPrice;
            result.Conditions.Add(new ExitConditionStatus
            {
                ConditionName = "Take Profit",
                IsTrue = targetHit,
                Description = targetHit ? $"Target hit at {bar.High:F2}" : $"Target not hit (Target: {_targetPrice:F2})",
                Value = $"Target: {_targetPrice:F2}, High: {bar.High:F2}"
            });

            if (targetHit)
            {
                result.ShouldExit = true;
                result.ExitReason = "Take Profit";
            }
        }
        else // Short position
        {
            pnlPct = (_entryPrice - price) / _entryPrice * 100;
            pnl = (_entryPrice - price) * (double)_positionQuantity * 100; // 100 oz multiplier

            // Check trailing stop for shorts
            if (pnlPct > _trailStartPct)
            {
                var newStop = price + (_trailAtrMult * _atr);
                if (newStop < _stopPrice)
                {
                    currentStop = newStop;
                }
            }

            // Check stop loss (price goes up for shorts)
            bool stopHit = bar.High >= currentStop;
            result.Conditions.Add(new ExitConditionStatus
            {
                ConditionName = "Stop Loss",
                IsTrue = stopHit,
                Description = stopHit ? $"Stop loss hit at {bar.High:F2}" : $"Stop loss not hit (Stop: {currentStop:F2})",
                Value = $"Stop: {currentStop:F2}, High: {bar.High:F2}"
            });

            if (stopHit)
            {
                result.ShouldExit = true;
                result.ExitReason = "Stop Loss";
            }

            // Check target (price goes down for shorts)
            bool targetHit = bar.Low <= _targetPrice;
            result.Conditions.Add(new ExitConditionStatus
            {
                ConditionName = "Take Profit",
                IsTrue = targetHit,
                Description = targetHit ? $"Target hit at {bar.Low:F2}" : $"Target not hit (Target: {_targetPrice:F2})",
                Value = $"Target: {_targetPrice:F2}, Low: {bar.Low:F2}"
            });

            if (targetHit)
            {
                result.ShouldExit = true;
                result.ExitReason = "Take Profit";
            }
        }

        result.CurrentPrice = price;
        result.UnrealizedPnL = pnl;
        result.UnrealizedPnLPct = pnlPct;
        result.StopPrice = currentStop; // Update with trailing stop if applicable

        // Check trailing stop
        bool trailingStopActive = pnlPct > _trailStartPct;
        result.Conditions.Add(new ExitConditionStatus
        {
            ConditionName = "Trailing Stop",
            IsTrue = trailingStopActive,
            Description = trailingStopActive ? "Trailing stop active" : $"Trailing stop not active (need {_trailStartPct}% profit)",
            Value = $"Current Stop: {currentStop:F2}"
        });

        // Check time exit
        bool timeExit = _entryBarCount >= _maxHoldBars;
        result.Conditions.Add(new ExitConditionStatus
        {
            ConditionName = "Time Exit",
            IsTrue = timeExit,
            Description = timeExit ? $"Max hold time reached ({_entryBarCount} bars)" : $"Bars held: {_entryBarCount}/{_maxHoldBars}",
            Value = $"{_entryBarCount}/{_maxHoldBars} bars"
        });

        if (timeExit)
        {
            result.ShouldExit = true;
            result.ExitReason = "Time Exit";
        }

        // Add position summary
        var direction = isLong ? "LONG" : "SHORT";
        result.Conditions.Add(new ExitConditionStatus
        {
            ConditionName = "Position Summary",
            IsTrue = true,
            Description = $"{direction} - Entry: {_entryPrice:F2}, Current: {price:F2}, PnL: ${pnl:F2} ({pnlPct:F2}%)",
            Value = $"Qty: {_positionQuantity}"
        });

        return result;
    }

    private void Log(string message)
    {
        var logMessage = $"[{_config.Name}] {message}";
        Logger.LogStrategy(_config.Name, message);
        OnLog?.Invoke(logMessage);
    }
}
