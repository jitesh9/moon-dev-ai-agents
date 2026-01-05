/*
 * GC Divergence Strategy Engine
 * Implements RSI/MACD divergence detection with confirmations
 * Supports both Aggressive and Conservative modes
 */

namespace GCTradingApp;

/// <summary>
/// Strategy engine for GC divergence trading
/// </summary>
public class GCStrategyEngine : IStrategyEngine
{
    private readonly IBKRClient _dataClient;      // For market data (always real IBKR)
    private readonly IOrderClient _orderClient;   // For orders (real or paper)
    private readonly string _strategyName;
    private readonly double _positionScale;
    private readonly bool _ddProtection;
    private readonly int _fixedContracts;
    private readonly double _capitalAllocation;
    private readonly double _maxDrawdown;

    // Strategy parameters (from Python implementation)
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
    private bool _pendingEntry;  // True while waiting for entry order to fill
    private bool _pendingExit;   // True while waiting for exit order to fill
    private double _entryPrice;
    private double _pendingEntryPrice;  // Expected entry price (for logging)
    private DateTime _entryTime;
    private int _entryBarCount;
    private double _stopPrice;
    private double _targetPrice;
    private int _currentOrderId;
    private decimal _positionQuantity;  // Track actual position size

    // Market data storage (thread-safe access via _barsLock)
    private readonly List<BarData> _bars = new();
    private readonly object _barsLock = new();
    private readonly int _lookback = 100;

    // Calculated indicators
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
    private const int MacdSignalPeriod = 9;

    // Drawdown tracking
    private double _peakEquity;
    private double _currentDrawdown;

    // IStrategyEngine implementation
    public string Name => _strategyName;

    // Logging
    public event Action<string>? OnLog;

    // State change notification (for persistence)
    public event Action<StrategyState>? OnStateChanged;

    // Entry condition status notification
    public event Action<EntryConditionsResult>? OnEntryConditionsUpdated;

    public GCStrategyEngine(
        IBKRClient dataClient,
        IOrderClient orderClient,
        string strategyName,
        double positionScale,
        bool ddProtection,
        int fixedContracts = 0,
        double capitalAllocation = 0,
        double maxDrawdown = 0.11,
        StrategyState? savedState = null)
    {
        _dataClient = dataClient;
        _orderClient = orderClient;
        _strategyName = strategyName;
        _positionScale = positionScale;
        _ddProtection = ddProtection;
        _fixedContracts = fixedContracts;
        _capitalAllocation = capitalAllocation;
        _maxDrawdown = maxDrawdown;

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
            Log($"Restored position state - Entry: {_entryPrice:F2}, Stop: {_stopPrice:F2}, Target: {_targetPrice:F2}");
        }

        _dataClient.OnRealtimeBar += OnBar;
        _orderClient.OnOrderStatus += OnOrderStatus;
        _dataClient.OnAccountUpdate += OnAccountUpdate;
    }

    /// <summary>
    /// Gets the current strategy state for persistence
    /// </summary>
    public StrategyState GetState()
    {
        return new StrategyState
        {
            InPosition = _inPosition,
            EntryPrice = _entryPrice,
            EntryTime = _entryTime,
            EntryBarCount = _entryBarCount,
            StopPrice = _stopPrice,
            TargetPrice = _targetPrice,
            CurrentOrderId = _currentOrderId,
            PositionQuantity = _positionQuantity
        };
    }

    private void NotifyStateChanged()
    {
        OnStateChanged?.Invoke(GetState());
    }

    public void Start()
    {
        _isRunning = true;
        Log($"Strategy started - Scale: {_positionScale:P0}, DD Protection: {_ddProtection}");
    }

    public void Stop()
    {
        _isRunning = false;

        // Unsubscribe from events to prevent memory leak
        _dataClient.OnRealtimeBar -= OnBar;
        _orderClient.OnOrderStatus -= OnOrderStatus;
        _dataClient.OnAccountUpdate -= OnAccountUpdate;

        Log("Strategy stopped");
    }

    private void OnAccountUpdate(string key, string value, string currency)
    {
        if (key == "NetLiquidation" && double.TryParse(value, out var equity))
        {
            if (equity > _peakEquity) _peakEquity = equity;

            // Guard against division by zero
            if (_peakEquity > 0)
            {
                _currentDrawdown = (_peakEquity - equity) / _peakEquity;
            }
            else
            {
                _currentDrawdown = 0;
            }
        }
    }

    public void ProcessBar(BarData bar)
    {
        try
        {
            if (!_isRunning) return;

            int barCount;
            lock (_barsLock)
            {
                // Store bar data
                _bars.Add(bar);
                if (_bars.Count > _lookback) _bars.RemoveAt(0);
                barCount = _bars.Count;
            }

            // Need enough bars for indicators
            if (barCount < 50) return;

            // Calculate indicators (takes snapshot inside lock)
            CalculateIndicators();

            // Check if it's regular trading hours (8 AM - 5 PM)
            var hour = bar.Time.Hour;
            bool inTradingHours = hour >= 8 && hour < 17;

            if (_inPosition && !_pendingExit)
            {
                ManagePosition(bar);
            }
            else if (!_inPosition && !_pendingEntry && inTradingHours)
            {
                CheckEntry(bar);
            }

            // Update entry condition status for UI
            var conditionsResult = EvaluateEntryConditions(bar);
            OnEntryConditionsUpdated?.Invoke(conditionsResult);
        }
        catch (Exception ex)
        {
            Log($"ERROR in ProcessBar: {ex.Message}");
            Logger.Error($"Error processing bar in {_strategyName}", ex);
            // Re-throw to let circuit breaker handle it
            throw;
        }
    }

    private void OnBar(BarData bar)
    {
        ProcessBar(bar);
    }

    private void CalculateIndicators()
    {
        double[] closes;
        double[] highs;
        double[] lows;

        // Take snapshot of bar data under lock
        lock (_barsLock)
        {
            closes = _bars.Select(b => b.Close).ToArray();
            highs = _bars.Select(b => b.High).ToArray();
            lows = _bars.Select(b => b.Low).ToArray();
        }

        if (closes.Length == 0) return;

        // ATR (14 period)
        _atr = CalculateATR(highs, lows, closes, 14);

        // RSI (14 period)
        _rsi = CalculateRSI(closes, 14);

        // MACD
        var (macd, signal, hist) = CalculateMACD(closes);
        _prevMacdHist = _macdHist;
        _macdHist = hist;

        // EMAs
        _ema13 = CalculateEMA(closes, 13);
        _ema34 = CalculateEMA(closes, 34);

        // SMA 50
        _sma50 = closes.TakeLast(50).Average();

        // SuperTrend (simplified)
        _superTrend = CalculateSuperTrend(highs, lows, closes, 10, 3.0);

        // Bull regime
        _bullRegime = closes.Last() > _sma50;
    }

    private void CheckEntry(BarData bar)
    {
        // Don't enter if we're already in position or have a pending order
        if (_pendingEntry || _pendingExit) return;

        // Check if we should pause trading due to drawdown
        if (_ddProtection && _currentDrawdown >= _ddPauseThreshold)
        {
            return;
        }

        // Check for bullish divergence signal
        if (!DetectBullishDivergence()) return;

        // Count confirmations
        int confirmations = CountConfirmations(bar);
        if (confirmations < _minConfirmations) return;

        // Calculate position size
        var quantity = CalculatePositionSize();
        if (quantity <= 0) return;

        // Calculate stop and target
        var stopDist = _stopMult * _atr;
        var targetDist = _targetMult * _atr;

        _pendingEntryPrice = bar.Close;
        _stopPrice = bar.Close - stopDist;
        _targetPrice = bar.Close + targetDist;

        // Place market order - position state will be set on fill confirmation
        _positionQuantity = (decimal)quantity;
        Log($"ENTRY SIGNAL - Price: {bar.Close:F2}, Stop: {_stopPrice:F2}, Target: {_targetPrice:F2}, Qty: {_positionQuantity}, Confirmations: {confirmations}");

        _pendingEntry = true;  // Mark as pending until fill confirmed
        _orderClient.PlaceMarketOrder("BUY", _positionQuantity, _strategyName);
    }

    private bool DetectBullishDivergence()
    {
        List<BarData> barsSnapshot;
        lock (_barsLock)
        {
            if (_bars.Count < 20) return false;
            barsSnapshot = _bars.ToList();  // Take snapshot
        }

        // Look for price making lower low while RSI/MACD making higher low
        var recentLows = barsSnapshot.TakeLast(20).ToList();
        var closes = recentLows.Select(b => b.Close).ToArray();
        var rsiValues = new double[20];

        // Calculate RSI for recent bars
        for (int i = 0; i < 20; i++)
        {
            var subset = barsSnapshot.Take(barsSnapshot.Count - 20 + i + 1).Select(b => b.Close).ToArray();
            rsiValues[i] = CalculateRSI(subset, 14);
        }

        // Find local lows
        int priceLow1 = -1, priceLow2 = -1;
        for (int i = 5; i < 15; i++)
        {
            if (closes[i] < closes[i - 1] && closes[i] < closes[i + 1] &&
                closes[i] < closes[i - 2] && closes[i] < closes[i + 2])
            {
                if (priceLow1 < 0) priceLow1 = i;
                else priceLow2 = i;
            }
        }

        if (priceLow1 < 0 || priceLow2 < 0) return false;

        // Check for divergence: price lower low, RSI higher low
        bool priceLowerLow = closes[priceLow2] < closes[priceLow1];
        bool rsiHigherLow = rsiValues[priceLow2] > rsiValues[priceLow1];

        // Also check MACD divergence
        bool macdImproving = _macdHist > _prevMacdHist;

        return (priceLowerLow && rsiHigherLow) || (priceLowerLow && macdImproving);
    }

    private int CountConfirmations(BarData bar)
    {
        int count = 0;

        // 1. Bull regime (price > 50 SMA)
        if (_bullRegime) count++;

        // 2. EMA bullish (13 > 34)
        if (_ema13 > _ema34) count++;

        // 3. SuperTrend bullish
        if (bar.Close > _superTrend) count++;

        // 4. RSI in valid range (30-70)
        if (_rsi >= 30 && _rsi <= 70) count++;

        // 5. MACD improving
        if (_macdHist > _prevMacdHist) count++;

        // 6. Volatility OK (ATR > threshold)
        double avgAtr;
        lock (_barsLock)
        {
            avgAtr = _bars.TakeLast(20).Average(b => CalculateBarATR(b));
        }
        if (_atr > avgAtr * 0.8) count++;

        return count;
    }

    private double CalculatePositionSize()
    {
        // If fixed contracts specified, use that
        if (_fixedContracts > 0) return _fixedContracts;

        // Otherwise calculate based on capital allocation
        // GC contract: 100 oz, ~$2,000 margin per contract
        double baseSize = 1;

        if (_capitalAllocation > 0)
        {
            // Estimate contracts based on capital and current price
            double price;
            lock (_barsLock)
            {
                price = _bars.Count > 0 ? _bars.Last().Close : 0;
            }
            if (price > 0)
            {
                var notionalPerContract = price * 100;  // 100 oz multiplier
                baseSize = Math.Floor(_capitalAllocation / (notionalPerContract * 0.1));  // 10% margin
            }
        }

        // Apply position scale
        var scaledSize = baseSize * _positionScale;

        // Apply drawdown reduction if protection enabled
        if (_ddProtection && _currentDrawdown >= _ddReductionThreshold)
        {
            var reduction = (_currentDrawdown - _ddReductionThreshold) / (_ddPauseThreshold - _ddReductionThreshold);
            scaledSize *= (1 - 0.7 * reduction);
        }

        return Math.Max(1, Math.Floor(scaledSize));
    }

    private void ManagePosition(BarData bar)
    {
        var price = bar.Close;
        var pnlPct = ((price / _entryPrice) - 1) * 100;
        int barsHeld;
        lock (_barsLock)
        {
            barsHeld = _bars.Count - _entryBarCount;
        }

        // Trailing stop
        if (pnlPct >= _trailStartPct)
        {
            var newStop = price - (_trailAtrMult * _atr);
            if (newStop > _stopPrice)
            {
                _stopPrice = newStop;
                Log($"Trailing stop updated to {_stopPrice:F2}");
            }
        }

        // Check stop hit
        if (price <= _stopPrice)
        {
            Log($"STOP HIT at {price:F2} (Stop: {_stopPrice:F2})");
            ClosePosition("Stop Loss");
            return;
        }

        // Check target hit
        if (price >= _targetPrice)
        {
            Log($"TARGET HIT at {price:F2} (Target: {_targetPrice:F2})");
            ClosePosition("Take Profit");
            return;
        }

        // Time exit
        if (barsHeld >= _maxHoldBars)
        {
            Log($"TIME EXIT after {barsHeld} bars");
            ClosePosition("Time Exit");
            return;
        }

        // Momentum reversal exit
        if (pnlPct > 0.5)
        {
            // Check if MACD histogram declining for 3 bars
            // Simplified check
            if (_macdHist < _prevMacdHist * 0.9)
            {
                Log($"MOMENTUM EXIT - MACD declining while profitable");
                ClosePosition("Momentum Exit");
                return;
            }
        }

        // Emergency exit for conservative strategy
        if (_ddProtection && _currentDrawdown >= _maxDrawdown * 0.95)
        {
            Log($"EMERGENCY EXIT - Approaching max drawdown ({_currentDrawdown:P1})");
            ClosePosition("Emergency Exit");
            return;
        }
    }

    private void ClosePosition(string reason)
    {
        if (_pendingExit)
        {
            Log($"WARNING: ClosePosition called but exit already pending");
            return;
        }

        if (_positionQuantity <= 0)
        {
            Log($"WARNING: ClosePosition called but _positionQuantity is {_positionQuantity}");
            _positionQuantity = 1;  // Fallback to 1 contract
        }

        Log($"Closing position - Qty: {_positionQuantity}, Reason: {reason}");
        _pendingExit = true;  // Mark as pending until fill confirmed
        _orderClient.PlaceMarketOrder("SELL", _positionQuantity, $"{_strategyName}_{reason}");
    }

    private void OnOrderStatus(OrderStatusData status)
    {
        if (status.Status == "Filled")
        {
            Log($"Order {status.OrderId} filled at {status.AvgFillPrice:F2}");

            if (_pendingEntry)
            {
                // Entry order filled - now we're in position
                _pendingEntry = false;
                _inPosition = true;
                _entryPrice = status.AvgFillPrice;
                _entryTime = DateTime.Now;
                lock (_barsLock)
                {
                    _entryBarCount = _bars.Count;
                }
                Log($"ENTRY CONFIRMED at {_entryPrice:F2}");
                NotifyStateChanged();  // Persist position state
            }
            else if (_pendingExit)
            {
                // Exit order filled - position closed
                _pendingExit = false;
                _inPosition = false;
                var exitPrice = status.AvgFillPrice;
                var pnl = (exitPrice - _entryPrice) * (double)_positionQuantity * 100;  // 100 oz multiplier
                Log($"EXIT CONFIRMED at {exitPrice:F2}, PnL: ${pnl:F2}");

                // Reset state
                _entryPrice = 0;
                _stopPrice = 0;
                _targetPrice = 0;
                _positionQuantity = 0;
                NotifyStateChanged();  // Persist position state
            }
        }
        else if (status.Status == "Cancelled" || status.Status == "ApiCancelled")
        {
            if (_pendingEntry)
            {
                Log($"Entry order {status.OrderId} cancelled");
                _pendingEntry = false;
            }
            else if (_pendingExit)
            {
                Log($"Exit order {status.OrderId} cancelled - position may still be open!");
                _pendingExit = false;
            }
        }
        else if (status.Status == "Inactive" || status.Status == "ApiPending")
        {
            // Order rejected or pending - log but don't change state yet
            Log($"Order {status.OrderId} status: {status.Status}");
        }
    }

    // Technical indicator calculations

    private double CalculateATR(double[] highs, double[] lows, double[] closes, int period)
    {
        if (closes.Length < period + 1) return 0;

        var trValues = new List<double>();
        for (int i = 1; i < closes.Length; i++)
        {
            var tr = Math.Max(highs[i] - lows[i],
                     Math.Max(Math.Abs(highs[i] - closes[i - 1]),
                              Math.Abs(lows[i] - closes[i - 1])));
            trValues.Add(tr);
        }

        return trValues.TakeLast(period).Average();
    }

    private double CalculateBarATR(BarData bar)
    {
        return bar.High - bar.Low;
    }

    private double CalculateRSI(double[] closes, int period)
    {
        if (closes.Length < period + 1) return 50;

        var gains = new List<double>();
        var losses = new List<double>();

        for (int i = 1; i < closes.Length; i++)
        {
            var change = closes[i] - closes[i - 1];
            gains.Add(Math.Max(0, change));
            losses.Add(Math.Max(0, -change));
        }

        var avgGain = gains.TakeLast(period).Average();
        var avgLoss = losses.TakeLast(period).Average();

        if (avgLoss == 0) return 100;
        var rs = avgGain / avgLoss;
        return 100 - (100 / (1 + rs));
    }

    private (double macd, double signal, double hist) CalculateMACD(double[] closes)
    {
        var ema12 = CalculateEMA(closes, 12);
        var ema26 = CalculateEMA(closes, 26);
        var macd = ema12 - ema26;

        // Store MACD value for signal line calculation
        _macdHistory.Add(macd);
        if (_macdHistory.Count > _lookback)
            _macdHistory.RemoveAt(0);

        // Signal line is 9-period EMA of MACD values
        double signal;
        if (_macdHistory.Count >= MacdSignalPeriod)
        {
            signal = CalculateEMA(_macdHistory.ToArray(), MacdSignalPeriod);
        }
        else
        {
            signal = _macdHistory.Average();
        }

        var hist = macd - signal;

        return (macd, signal, hist);
    }

    private double CalculateEMA(double[] values, int period)
    {
        if (values.Length < period) return values.Last();

        var multiplier = 2.0 / (period + 1);
        var ema = values.Take(period).Average();

        for (int i = period; i < values.Length; i++)
        {
            ema = (values[i] - ema) * multiplier + ema;
        }

        return ema;
    }

    private double CalculateSuperTrend(double[] highs, double[] lows, double[] closes, int period, double multiplier)
    {
        if (closes.Length < period) return closes.Last();

        var atr = CalculateATR(highs, lows, closes, period);
        var hl2 = (highs.Last() + lows.Last()) / 2;

        var upperBand = hl2 + (multiplier * atr);
        var lowerBand = hl2 - (multiplier * atr);

        // Simplified: return lower band for bullish
        return closes.Last() > hl2 ? lowerBand : upperBand;
    }

    /// <summary>
    /// Evaluates all entry conditions and returns their status
    /// </summary>
    public EntryConditionsResult EvaluateEntryConditions(BarData? bar = null)
    {
        var result = new EntryConditionsResult
        {
            StrategyName = _strategyName,
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

        // Check trading hours
        var hour = bar.Time.Hour;
        bool inTradingHours = hour >= 8 && hour < 17;
        result.Conditions.Add(new EntryConditionStatus
        {
            ConditionName = "Trading Hours",
            IsTrue = inTradingHours,
            Description = inTradingHours ? "Within trading hours (8 AM - 5 PM)" : "Outside trading hours",
            Value = $"{hour:00}:00"
        });

        if (!inTradingHours)
        {
            result.BlockingReason = "Outside trading hours";
            return result;
        }

        // Check drawdown protection
        if (_ddProtection)
        {
            bool ddOk = _currentDrawdown < _ddPauseThreshold;
            result.Conditions.Add(new EntryConditionStatus
            {
                ConditionName = "Drawdown Protection",
                IsTrue = ddOk,
                Description = ddOk ? "Drawdown within limits" : $"Drawdown too high ({_currentDrawdown:P1} >= {_ddPauseThreshold:P1})",
                Value = $"{_currentDrawdown:P1}"
            });

            if (!ddOk)
            {
                result.BlockingReason = "Drawdown protection active";
                return result;
            }
        }

        // Check divergence
        bool hasDivergence = DetectBullishDivergence();
        result.Conditions.Add(new EntryConditionStatus
        {
            ConditionName = "Bullish Divergence",
            IsTrue = hasDivergence,
            Description = hasDivergence ? "Bullish divergence detected" : "No bullish divergence"
        });

        if (!hasDivergence)
        {
            result.BlockingReason = "No bullish divergence";
            return result;
        }

        // Evaluate confirmations
        var confirmations = EvaluateConfirmations(bar);
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
    /// Evaluates individual confirmation conditions
    /// </summary>
    private List<EntryConditionStatus> EvaluateConfirmations(BarData bar)
    {
        var conditions = new List<EntryConditionStatus>();

        // 1. Bull regime (price > 50 SMA)
        conditions.Add(new EntryConditionStatus
        {
            ConditionName = "Bull Regime",
            IsTrue = _bullRegime,
            Description = _bullRegime ? "Price > SMA50" : "Price < SMA50",
            Value = $"SMA50: {_sma50:F2}"
        });

        // 2. EMA bullish (13 > 34)
        bool emaBullish = _ema13 > _ema34;
        conditions.Add(new EntryConditionStatus
        {
            ConditionName = "EMA Alignment",
            IsTrue = emaBullish,
            Description = emaBullish ? "EMA13 > EMA34" : "EMA13 < EMA34",
            Value = $"EMA13: {_ema13:F2}, EMA34: {_ema34:F2}"
        });

        // 3. SuperTrend bullish
        bool superTrendBullish = bar.Close > _superTrend;
        conditions.Add(new EntryConditionStatus
        {
            ConditionName = "SuperTrend",
            IsTrue = superTrendBullish,
            Description = superTrendBullish ? "Price > SuperTrend" : "Price < SuperTrend",
            Value = $"ST: {_superTrend:F2}"
        });

        // 4. RSI in valid range (30-70)
        bool rsiOk = _rsi >= 30 && _rsi <= 70;
        conditions.Add(new EntryConditionStatus
        {
            ConditionName = "RSI Range",
            IsTrue = rsiOk,
            Description = rsiOk ? "RSI in range (30-70)" : $"RSI out of range: {_rsi:F1}",
            Value = $"{_rsi:F1}"
        });

        // 5. MACD improving
        bool macdImproving = _macdHist > _prevMacdHist;
        conditions.Add(new EntryConditionStatus
        {
            ConditionName = "MACD Improving",
            IsTrue = macdImproving,
            Description = macdImproving ? "MACD histogram increasing" : "MACD histogram declining",
            Value = $"Hist: {_macdHist:F4}"
        });

        // 6. Volatility OK (ATR > threshold)
        double avgAtr;
        lock (_barsLock)
        {
            avgAtr = _bars.TakeLast(20).Average(b => CalculateBarATR(b));
        }
        bool volatilityOk = _atr > avgAtr * 0.8;
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
            StrategyName = _strategyName,
            InPosition = _inPosition,
            ShouldExit = false,
            EntryPrice = _entryPrice,
            StopPrice = _stopPrice,
            TargetPrice = _targetPrice,
            BarsHeld = 0
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
        var pnlPct = ((price / _entryPrice) - 1) * 100;
        var pnl = (price - _entryPrice) * (double)_positionQuantity * 100; // 100 oz multiplier

        int barsHeld;
        lock (_barsLock)
        {
            barsHeld = _bars.Count - _entryBarCount;
        }

        result.CurrentPrice = price;
        result.UnrealizedPnL = pnl;
        result.UnrealizedPnLPct = pnlPct;
        result.BarsHeld = barsHeld;

        // Check trailing stop
        double currentStop = _stopPrice;
        if (pnlPct >= _trailStartPct)
        {
            var newStop = price - (_trailAtrMult * _atr);
            if (newStop > _stopPrice)
            {
                currentStop = newStop;
            }
        }

        bool trailingStopActive = pnlPct >= _trailStartPct;
        result.Conditions.Add(new ExitConditionStatus
        {
            ConditionName = "Trailing Stop",
            IsTrue = trailingStopActive,
            Description = trailingStopActive ? "Trailing stop active" : $"Trailing stop not active (need {_trailStartPct}% profit)",
            Value = $"Current Stop: {currentStop:F2}"
        });

        // Check stop loss hit
        bool stopHit = price <= currentStop;
        result.Conditions.Add(new ExitConditionStatus
        {
            ConditionName = "Stop Loss",
            IsTrue = stopHit,
            Description = stopHit ? $"Stop loss hit at {price:F2}" : $"Stop loss not hit (Stop: {currentStop:F2})",
            Value = $"Stop: {currentStop:F2}, Price: {price:F2}"
        });

        if (stopHit)
        {
            result.ShouldExit = true;
            result.ExitReason = "Stop Loss";
        }

        // Check target hit
        bool targetHit = price >= _targetPrice;
        result.Conditions.Add(new ExitConditionStatus
        {
            ConditionName = "Take Profit",
            IsTrue = targetHit,
            Description = targetHit ? $"Target hit at {price:F2}" : $"Target not hit (Target: {_targetPrice:F2})",
            Value = $"Target: {_targetPrice:F2}, Price: {price:F2}"
        });

        if (targetHit)
        {
            result.ShouldExit = true;
            result.ExitReason = "Take Profit";
        }

        // Check time exit
        bool timeExit = barsHeld >= _maxHoldBars;
        result.Conditions.Add(new ExitConditionStatus
        {
            ConditionName = "Time Exit",
            IsTrue = timeExit,
            Description = timeExit ? $"Max hold time reached ({barsHeld} bars)" : $"Bars held: {barsHeld}/{_maxHoldBars}",
            Value = $"{barsHeld}/{_maxHoldBars} bars"
        });

        if (timeExit)
        {
            result.ShouldExit = true;
            result.ExitReason = "Time Exit";
        }

        // Check momentum exit
        bool momentumExit = false;
        if (pnlPct > 0.5)
        {
            momentumExit = _macdHist < _prevMacdHist * 0.9;
            result.Conditions.Add(new ExitConditionStatus
            {
                ConditionName = "Momentum Exit",
                IsTrue = momentumExit,
                Description = momentumExit ? "MACD declining while profitable" : "Momentum exit conditions not met",
                Value = $"MACD Hist: {_macdHist:F4}, Prev: {_prevMacdHist:F4}"
            });

            if (momentumExit)
            {
                result.ShouldExit = true;
                result.ExitReason = "Momentum Exit";
            }
        }
        else
        {
            result.Conditions.Add(new ExitConditionStatus
            {
                ConditionName = "Momentum Exit",
                IsTrue = false,
                Description = $"Not profitable enough for momentum exit (need >0.5%, have {pnlPct:F2}%)",
                Value = $"PnL: {pnlPct:F2}%"
            });
        }

        // Check emergency exit (for conservative strategy)
        if (_ddProtection)
        {
            bool emergencyExit = _currentDrawdown >= _maxDrawdown * 0.95;
            result.Conditions.Add(new ExitConditionStatus
            {
                ConditionName = "Emergency Exit",
                IsTrue = emergencyExit,
                Description = emergencyExit ? $"Approaching max drawdown ({_currentDrawdown:P1})" : $"Drawdown OK ({_currentDrawdown:P1})",
                Value = $"DD: {_currentDrawdown:P1}, Max: {_maxDrawdown:P1}"
            });

            if (emergencyExit)
            {
                result.ShouldExit = true;
                result.ExitReason = "Emergency Exit";
            }
        }

        // Add position summary
        result.Conditions.Add(new ExitConditionStatus
        {
            ConditionName = "Position Summary",
            IsTrue = true,
            Description = $"Entry: {_entryPrice:F2}, Current: {price:F2}, PnL: ${pnl:F2} ({pnlPct:F2}%)",
            Value = $"Qty: {_positionQuantity}"
        });

        return result;
    }

    private void Log(string message)
    {
        Logger.Info($"[{_strategyName}] {message}");
        OnLog?.Invoke(message);
    }
}
