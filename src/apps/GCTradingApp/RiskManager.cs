/*
 * Risk Manager for GC Trading Application
 * Handles daily loss limits, position size limits, and emergency flatten
 */

namespace GCTradingApp;

/// <summary>
/// Risk check result
/// </summary>
public class RiskCheckResult
{
    public bool Allowed { get; set; } = true;
    public string Reason { get; set; } = "";
    public decimal AdjustedQuantity { get; set; }
}

/// <summary>
/// Risk state for persistence
/// </summary>
public class RiskState
{
    public DateTime TradingDate { get; set; } = DateTime.Today;
    public double DailyPnL { get; set; } = 0;
    public double DailyHighWater { get; set; } = 0;
    public bool TradingPaused { get; set; } = false;
    public string PauseReason { get; set; } = "";
    public int TradesExecutedToday { get; set; } = 0;
    public decimal TotalPositionSize { get; set; } = 0;
}

/// <summary>
/// Risk settings (configurable)
/// </summary>
public class RiskSettings
{
    // Daily loss limit
    public bool DailyLossLimitEnabled { get; set; } = true;
    public double MaxDailyLossUsd { get; set; } = 500.0;
    public double DailyLossWarningPct { get; set; } = 0.7; // Warn at 70% of limit

    // Position size limits
    public bool PositionLimitsEnabled { get; set; } = true;
    public int MaxContractsPerStrategy { get; set; } = 5;
    public int MaxTotalContracts { get; set; } = 10;

    // Trade limits
    public int MaxTradesPerDay { get; set; } = 20;

    // Emergency settings
    public bool AutoFlattenOnLimit { get; set; } = false; // Auto-flatten when limit hit
}

/// <summary>
/// Manages trading risk with daily limits and position controls
/// </summary>
public class RiskManager : IDisposable
{
    private readonly IBKRClient _client;
    private readonly object _lock = new();

    private RiskSettings _settings;
    private RiskState _state;
    private bool _isActive = false;

    // Track positions by strategy
    private readonly Dictionary<string, decimal> _strategyPositions = new();

    // Events
    public event Action<string>? OnLog;
    public event Action<string>? OnWarning;
    public event Action<string>? OnLimitHit;
    public event Action<RiskState>? OnStateChanged;

    public RiskSettings Settings
    {
        get { lock (_lock) return _settings; }
        set { lock (_lock) _settings = value; }
    }

    public RiskState State
    {
        get { lock (_lock) return _state; }
    }

    public bool IsTradingPaused
    {
        get { lock (_lock) return _state.TradingPaused; }
    }

    public RiskManager(IBKRClient client, RiskSettings? settings = null, RiskState? savedState = null)
    {
        _client = client;
        _settings = settings ?? new RiskSettings();

        // Restore or create new state
        if (savedState != null && savedState.TradingDate == DateTime.Today)
        {
            _state = savedState;
            Log($"Restored risk state - Daily PnL: ${_state.DailyPnL:F2}, Trades: {_state.TradesExecutedToday}");
        }
        else
        {
            _state = new RiskState { TradingDate = DateTime.Today };
            Log("Started new trading day - risk counters reset");
        }
    }

    /// <summary>
    /// Start risk monitoring
    /// </summary>
    public void Start()
    {
        _isActive = true;
        _client.OnAccountUpdate += HandleAccountUpdate;
        _client.OnExecution += HandleExecution;
        _client.OnPosition += HandlePosition;
        Log("Risk manager started");
    }

    /// <summary>
    /// Stop risk monitoring
    /// </summary>
    public void Stop()
    {
        _isActive = false;
        _client.OnAccountUpdate -= HandleAccountUpdate;
        _client.OnExecution -= HandleExecution;
        _client.OnPosition -= HandlePosition;
        Log("Risk manager stopped");
    }

    /// <summary>
    /// Check if a new trade is allowed
    /// </summary>
    public RiskCheckResult CheckNewTrade(string strategy, string action, decimal quantity)
    {
        lock (_lock)
        {
            var result = new RiskCheckResult { AdjustedQuantity = quantity };

            // Check if trading is paused
            if (_state.TradingPaused)
            {
                result.Allowed = false;
                result.Reason = $"Trading paused: {_state.PauseReason}";
                return result;
            }

            // Check daily trade limit
            if (_state.TradesExecutedToday >= _settings.MaxTradesPerDay)
            {
                result.Allowed = false;
                result.Reason = $"Daily trade limit reached ({_settings.MaxTradesPerDay} trades)";
                return result;
            }

            // Check position limits for new entries (BUY)
            if (_settings.PositionLimitsEnabled && action == "BUY")
            {
                // Check per-strategy limit
                var currentStrategyPos = _strategyPositions.GetValueOrDefault(strategy, 0);
                if (currentStrategyPos + quantity > _settings.MaxContractsPerStrategy)
                {
                    var allowed = _settings.MaxContractsPerStrategy - currentStrategyPos;
                    if (allowed <= 0)
                    {
                        result.Allowed = false;
                        result.Reason = $"Strategy position limit reached ({_settings.MaxContractsPerStrategy} contracts)";
                        return result;
                    }
                    result.AdjustedQuantity = allowed;
                    result.Reason = $"Quantity reduced to {allowed} (strategy limit)";
                }

                // Check total position limit
                if (_state.TotalPositionSize + result.AdjustedQuantity > _settings.MaxTotalContracts)
                {
                    var allowed = _settings.MaxTotalContracts - _state.TotalPositionSize;
                    if (allowed <= 0)
                    {
                        result.Allowed = false;
                        result.Reason = $"Total position limit reached ({_settings.MaxTotalContracts} contracts)";
                        return result;
                    }
                    result.AdjustedQuantity = allowed;
                    result.Reason = $"Quantity reduced to {allowed} (total limit)";
                }
            }

            // Check daily loss limit
            if (_settings.DailyLossLimitEnabled && _state.DailyPnL < 0)
            {
                var lossUsed = Math.Abs(_state.DailyPnL) / _settings.MaxDailyLossUsd;

                if (lossUsed >= 1.0)
                {
                    result.Allowed = false;
                    result.Reason = $"Daily loss limit reached (${Math.Abs(_state.DailyPnL):F2} / ${_settings.MaxDailyLossUsd:F2})";
                    return result;
                }

                if (lossUsed >= _settings.DailyLossWarningPct)
                {
                    OnWarning?.Invoke($"Warning: {lossUsed:P0} of daily loss limit used");
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Record a trade execution
    /// </summary>
    public void RecordTrade(string strategy, string action, decimal quantity, double pnl)
    {
        lock (_lock)
        {
            _state.TradesExecutedToday++;
            _state.DailyPnL += pnl;

            // Update strategy position tracking
            if (action == "BUY")
            {
                _strategyPositions[strategy] = _strategyPositions.GetValueOrDefault(strategy, 0) + quantity;
                _state.TotalPositionSize += quantity;
            }
            else if (action == "SELL")
            {
                _strategyPositions[strategy] = Math.Max(0, _strategyPositions.GetValueOrDefault(strategy, 0) - quantity);
                _state.TotalPositionSize = Math.Max(0, _state.TotalPositionSize - quantity);
            }

            // Update high water mark
            if (_state.DailyPnL > _state.DailyHighWater)
            {
                _state.DailyHighWater = _state.DailyPnL;
            }

            // Check if we hit the daily loss limit
            if (_settings.DailyLossLimitEnabled && _state.DailyPnL <= -_settings.MaxDailyLossUsd)
            {
                PauseTrading($"Daily loss limit hit: ${Math.Abs(_state.DailyPnL):F2}");

                if (_settings.AutoFlattenOnLimit)
                {
                    Log("Auto-flatten triggered by loss limit");
                    _ = EmergencyFlattenAsync();
                }
            }

            NotifyStateChanged();
        }
    }

    /// <summary>
    /// Update daily PnL from account updates
    /// </summary>
    public void UpdateDailyPnL(double dailyPnL)
    {
        lock (_lock)
        {
            _state.DailyPnL = dailyPnL;

            // Check loss limit
            if (_settings.DailyLossLimitEnabled && dailyPnL <= -_settings.MaxDailyLossUsd && !_state.TradingPaused)
            {
                PauseTrading($"Daily loss limit hit: ${Math.Abs(dailyPnL):F2}");
                OnLimitHit?.Invoke($"Daily loss limit reached: ${Math.Abs(dailyPnL):F2} loss");

                if (_settings.AutoFlattenOnLimit)
                {
                    Log("Auto-flatten triggered by loss limit");
                    _ = EmergencyFlattenAsync();
                }
            }

            // Check warning threshold
            if (dailyPnL < 0 && !_state.TradingPaused)
            {
                var lossUsed = Math.Abs(dailyPnL) / _settings.MaxDailyLossUsd;
                if (lossUsed >= _settings.DailyLossWarningPct)
                {
                    OnWarning?.Invoke($"Daily loss warning: ${Math.Abs(dailyPnL):F2} ({lossUsed:P0} of limit)");
                }
            }

            NotifyStateChanged();
        }
    }

    /// <summary>
    /// Pause trading with reason
    /// </summary>
    public void PauseTrading(string reason)
    {
        lock (_lock)
        {
            if (!_state.TradingPaused)
            {
                _state.TradingPaused = true;
                _state.PauseReason = reason;
                Log($"Trading PAUSED: {reason}");
                OnLimitHit?.Invoke(reason);
                NotifyStateChanged();
            }
        }
    }

    /// <summary>
    /// Resume trading
    /// </summary>
    public void ResumeTrading()
    {
        lock (_lock)
        {
            if (_state.TradingPaused)
            {
                _state.TradingPaused = false;
                _state.PauseReason = "";
                Log("Trading RESUMED");
                NotifyStateChanged();
            }
        }
    }

    /// <summary>
    /// Reset daily counters (call at start of new trading day)
    /// </summary>
    public void ResetDaily()
    {
        lock (_lock)
        {
            _state = new RiskState { TradingDate = DateTime.Today };
            _strategyPositions.Clear();
            Log("Daily risk counters reset");
            NotifyStateChanged();
        }
    }

    /// <summary>
    /// Emergency flatten - close all positions immediately
    /// </summary>
    public async Task<bool> EmergencyFlattenAsync()
    {
        Log("EMERGENCY FLATTEN initiated");

        try
        {
            // First, cancel all pending orders
            Log("Cancelling all pending orders...");
            _client.CancelAllOrders();

            // Wait for cancellations to process
            await Task.Delay(1000);

            // Get current positions and close them
            var positionsToClose = new List<(string strategy, decimal quantity)>();

            lock (_lock)
            {
                foreach (var kvp in _strategyPositions)
                {
                    if (kvp.Value > 0)
                    {
                        positionsToClose.Add((kvp.Key, kvp.Value));
                    }
                }
            }

            foreach (var (strategy, quantity) in positionsToClose)
            {
                Log($"Closing {strategy} position: {quantity} contracts");
                _client.PlaceMarketOrder("SELL", quantity, $"{strategy}_EmergencyFlatten");
                await Task.Delay(500); // Brief delay between orders
            }

            // Pause trading after flatten
            PauseTrading("Emergency flatten executed");

            Log("Emergency flatten complete");
            return true;
        }
        catch (Exception ex)
        {
            Log($"Emergency flatten error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Get current position for a strategy
    /// </summary>
    public decimal GetStrategyPosition(string strategy)
    {
        lock (_lock)
        {
            return _strategyPositions.GetValueOrDefault(strategy, 0);
        }
    }

    /// <summary>
    /// Get total position across all strategies
    /// </summary>
    public decimal GetTotalPosition()
    {
        lock (_lock)
        {
            return _state.TotalPositionSize;
        }
    }

    /// <summary>
    /// Get remaining daily loss allowance
    /// </summary>
    public double GetRemainingDailyLoss()
    {
        lock (_lock)
        {
            if (_state.DailyPnL >= 0)
                return _settings.MaxDailyLossUsd;
            return _settings.MaxDailyLossUsd - Math.Abs(_state.DailyPnL);
        }
    }

    /// <summary>
    /// Get percentage of daily loss limit used
    /// </summary>
    public double GetDailyLossUsedPct()
    {
        lock (_lock)
        {
            if (_state.DailyPnL >= 0)
                return 0;
            return Math.Min(1.0, Math.Abs(_state.DailyPnL) / _settings.MaxDailyLossUsd);
        }
    }

    private void HandleAccountUpdate(string key, string value, string currency)
    {
        if (key == "DailyPnL" && double.TryParse(value, out var dailyPnL))
        {
            UpdateDailyPnL(dailyPnL);
        }
    }

    private void HandleExecution(ExecutionData exec)
    {
        // Extract strategy from OrderRef
        var strategy = exec.OrderRef?.Split('_').FirstOrDefault() ?? "Unknown";
        RecordTrade(strategy, exec.Side, exec.Shares, exec.RealizedPnL);
    }

    private void HandlePosition(PositionData pos)
    {
        if (pos.Symbol != "GC") return;

        lock (_lock)
        {
            // Update total position from broker
            _state.TotalPositionSize = pos.Position;
        }
    }

    private void NotifyStateChanged()
    {
        OnStateChanged?.Invoke(_state);
    }

    private void Log(string message)
    {
        Logger.Info($"[RiskManager] {message}");
        OnLog?.Invoke(message);
    }

    public void Dispose()
    {
        Stop();
    }
}
