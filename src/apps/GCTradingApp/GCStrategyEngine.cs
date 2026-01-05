/*
 * GC Divergence Strategy Engine
 * Implements RSI/MACD divergence detection with confirmations
 * Supports both Aggressive and Conservative modes
 */

namespace GCTradingApp;

/// <summary>
/// Strategy engine for GC divergence trading
/// </summary>
public class GCStrategyEngine
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

    // Logging
    public event Action<string>? OnLog;

    // State change notification (for persistence)
    public event Action<StrategyState>? OnStateChanged;

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
        if (!_isRunning) return;

        try
        {
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
        }
        catch (Exception ex)
        {
            Log($"ERROR in ProcessBar: {ex.Message}");
            Logger.Error($"Error processing bar in {_strategyName}", ex);
            // Re-throw to let circuit breaker handle it
            // This allows circuit breaker to track failures and disable strategy if needed
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

    private void Log(string message)
    {
        Logger.Info($"[{_strategyName}] {message}");
        OnLog?.Invoke(message);
    }
}
