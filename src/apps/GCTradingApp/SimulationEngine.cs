/*
 * Simulation Engine
 * Manages simulation state, historical data, and strategy evaluation for testing entry/exit conditions
 */

namespace GCTradingApp;

/// <summary>
/// Manages simulation mode for testing strategies without live market data
/// </summary>
public class SimulationEngine
{
    // Historical data management
    private List<BarData> _historicalBars = new();
    private int _currentBarIndex = -1;
    private int _highestProcessedIndex = -1;  // Track highest bar index processed by strategies

    // Strategy engines (simulation mode - no real orders)
    private readonly List<IStrategyEngine> _strategies = new();

    // Current simulation state
    private BarData? _currentBar;
    private DateTime _simulationTime;
    private bool _isRunning = false;

    // Price manipulation mode
    private bool _manualPriceMode = false;
    private double _manualPrice = 0;

    // Trade tracking with costs
    private SimulationSettings _settings = new();
    private readonly List<SimulatedTrade> _completedTrades = new();
    private readonly Dictionary<string, SimulatedTrade> _openTrades = new();  // Strategy -> Open trade
    private double _peakEquity;
    private double _maxDrawdown;
    private double _maxDrawdownPct;

    // Events
    public event Action<BarData>? OnBarChanged;
    public event Action<Dictionary<string, EntryConditionsResult>>? OnEntryConditionsUpdated;
    public event Action<Dictionary<string, ExitConditionsResult>>? OnExitConditionsUpdated;
    public event Action<string, string>? OnSimulationEvent; // Strategy, Event message
    public event Action<SimulatedTrade>? OnTradeCompleted;  // Fired when a trade is closed
    public event Action<string>? OnLog;

    /// <summary>
    /// Gets the current bar being simulated
    /// </summary>
    public BarData? CurrentBar => _currentBar;

    /// <summary>
    /// Gets the current bar index (0-based)
    /// </summary>
    public int CurrentBarIndex => _currentBarIndex;

    /// <summary>
    /// Gets the total number of bars loaded
    /// </summary>
    public int TotalBars => _historicalBars.Count;

    /// <summary>
    /// Gets whether historical data is loaded
    /// </summary>
    public bool HasHistoricalData => _historicalBars.Count > 0;

    /// <summary>
    /// Gets whether simulation is running
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Gets or sets the simulation settings (commission, slippage, etc.)
    /// </summary>
    public SimulationSettings Settings
    {
        get => _settings;
        set => _settings = value ?? new SimulationSettings();
    }

    /// <summary>
    /// Gets the list of completed trades
    /// </summary>
    public IReadOnlyList<SimulatedTrade> CompletedTrades => _completedTrades.AsReadOnly();

    /// <summary>
    /// Gets current simulation metrics
    /// </summary>
    public SimulationMetrics GetMetrics()
    {
        var metrics = new SimulationMetrics
        {
            StartingEquity = _settings.StartingEquity,
            TotalTrades = _completedTrades.Count,
            MaxDrawdown = _maxDrawdown,
            MaxDrawdownPct = _maxDrawdownPct
        };

        if (_completedTrades.Count == 0) return metrics;

        var winners = _completedTrades.Where(t => t.IsWinner).ToList();
        var losers = _completedTrades.Where(t => !t.IsWinner).ToList();

        metrics.WinningTrades = winners.Count;
        metrics.LosingTrades = losers.Count;
        metrics.GrossPnL = _completedTrades.Sum(t => t.GrossPnL);
        metrics.TotalCommission = _completedTrades.Sum(t => t.Commission);
        metrics.TotalSlippage = _completedTrades.Sum(t => t.Slippage);

        if (winners.Count > 0)
        {
            metrics.LargestWin = winners.Max(t => t.NetPnL);
            metrics.AverageWin = winners.Average(t => t.NetPnL);
        }

        if (losers.Count > 0)
        {
            metrics.LargestLoss = losers.Min(t => t.NetPnL);
            metrics.AverageLoss = losers.Average(t => t.NetPnL);
        }

        return metrics;
    }

    /// <summary>
    /// Register a strategy engine for simulation
    /// </summary>
    public void RegisterStrategy(IStrategyEngine strategy)
    {
        if (strategy == null) throw new ArgumentNullException(nameof(strategy));
        
        if (!_strategies.Any(s => s.Name == strategy.Name))
        {
            _strategies.Add(strategy);
            strategy.Start();
            Log($"Strategy '{strategy.Name}' registered for simulation");
        }
    }

    /// <summary>
    /// Unregister a strategy engine
    /// </summary>
    public void UnregisterStrategy(string strategyName)
    {
        var strategy = _strategies.FirstOrDefault(s => s.Name == strategyName);
        if (strategy != null)
        {
            strategy.Stop();
            _strategies.Remove(strategy);
            Log($"Strategy '{strategyName}' unregistered from simulation");
        }
    }

    /// <summary>
    /// Load historical data from a file
    /// </summary>
    public void LoadHistoricalData(string filePath)
    {
        try
        {
            _historicalBars = HistoricalDataLoader.LoadFromFile(filePath);
            _currentBarIndex = _historicalBars.Count > 0 ? 0 : -1;
            _highestProcessedIndex = -1;  // Reset - no bars processed yet
            _manualPriceMode = false;

            if (_historicalBars.Count > 0)
            {
                _currentBar = _historicalBars[0];
                _simulationTime = _currentBar.Time;
                Log($"Loaded {_historicalBars.Count} bars from {Path.GetFileName(filePath)}");

                // Process the first bar and evaluate strategies
                ProcessBar(_currentBar);
                _highestProcessedIndex = 0;
            }
            else
            {
                Log("No bars loaded from file");
            }
        }
        catch (Exception ex)
        {
            Log($"Error loading historical data: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Set manual price mode (overrides historical data)
    /// </summary>
    public void SetPrice(double price)
    {
        if (price <= 0)
        {
            throw new ArgumentException("Price must be greater than 0", nameof(price));
        }

        _manualPrice = price;
        _manualPriceMode = true;

        // Create a synthetic bar with the manual price
        var baseBar = _currentBar ?? new BarData
        {
            Time = DateTime.Now,
            Open = price,
            High = price,
            Low = price,
            Close = price,
            Volume = 0,
            WAP = (decimal)price,
            Count = 0
        };

        _currentBar = new BarData
        {
            Time = baseBar.Time,
            Open = price,
            High = price,
            Low = price,
            Close = price,
            Volume = baseBar.Volume,
            WAP = (decimal)price,
            Count = baseBar.Count
        };

        _simulationTime = _currentBar.Time;
        Log($"Price set to {price:F2}");

        // Evaluate strategies with new price
        EvaluateAllStrategies();
        OnBarChanged?.Invoke(_currentBar);
    }

    /// <summary>
    /// Step forward to next historical bar
    /// </summary>
    public bool StepForward()
    {
        if (_historicalBars.Count == 0)
        {
            Log("No historical data loaded");
            return false;
        }

        if (_currentBarIndex >= _historicalBars.Count - 1)
        {
            Log("Already at last bar");
            return false;
        }

        _currentBarIndex++;
        _currentBar = _historicalBars[_currentBarIndex];
        _simulationTime = _currentBar.Time;
        _manualPriceMode = false;

        Log($"Stepped forward to bar {_currentBarIndex + 1}/{_historicalBars.Count} ({_currentBar.Time:yyyy-MM-dd HH:mm:ss})");

        // Only process bar if this is a new bar we haven't seen before
        // This prevents duplicate bars when stepping forward after stepping backward
        if (_currentBarIndex > _highestProcessedIndex)
        {
            ProcessBar(_currentBar);
            _highestProcessedIndex = _currentBarIndex;
        }
        else
        {
            // Just evaluate conditions without adding bar to strategy state
            EvaluateAllStrategies();
        }

        OnBarChanged?.Invoke(_currentBar);
        return true;
    }

    /// <summary>
    /// Step backward to previous historical bar
    /// NOTE: Stepping backward only evaluates conditions at that bar.
    /// It does NOT re-process bars through strategies (which would corrupt indicator state).
    /// Use JumpToBar(0) to reset and replay from the beginning.
    /// </summary>
    public bool StepBackward()
    {
        if (_historicalBars.Count == 0)
        {
            Log("No historical data loaded");
            return false;
        }

        if (_currentBarIndex <= 0)
        {
            Log("Already at first bar");
            return false;
        }

        _currentBarIndex--;
        _currentBar = _historicalBars[_currentBarIndex];
        _simulationTime = _currentBar.Time;
        _manualPriceMode = false;

        Log($"Stepped backward to bar {_currentBarIndex + 1}/{_historicalBars.Count} ({_currentBar.Time:yyyy-MM-dd HH:mm:ss})");

        // Only evaluate conditions - do NOT process bar through strategies
        // Processing would add duplicate bars and corrupt indicator calculations
        EvaluateAllStrategies();

        OnBarChanged?.Invoke(_currentBar);
        return true;
    }

    /// <summary>
    /// Jump to a specific bar index
    /// NOTE: Jumping to a previously processed bar only evaluates conditions.
    /// Jumping forward to a new bar will process all bars up to that point.
    /// </summary>
    public bool JumpToBar(int index)
    {
        if (_historicalBars.Count == 0)
        {
            Log("No historical data loaded");
            return false;
        }

        if (index < 0 || index >= _historicalBars.Count)
        {
            Log($"Invalid bar index: {index} (valid range: 0-{_historicalBars.Count - 1})");
            return false;
        }

        _currentBarIndex = index;
        _currentBar = _historicalBars[_currentBarIndex];
        _simulationTime = _currentBar.Time;
        _manualPriceMode = false;

        Log($"Jumped to bar {_currentBarIndex + 1}/{_historicalBars.Count} ({_currentBar.Time:yyyy-MM-dd HH:mm:ss})");

        // If jumping forward past highest processed, process intermediate bars
        if (_currentBarIndex > _highestProcessedIndex)
        {
            // Process all bars from highest+1 to current to maintain proper indicator state
            for (int i = _highestProcessedIndex + 1; i <= _currentBarIndex; i++)
            {
                ProcessBar(_historicalBars[i]);
            }
            _highestProcessedIndex = _currentBarIndex;
        }
        else
        {
            // Jumping backward or to already-processed bar - just evaluate
            EvaluateAllStrategies();
        }

        OnBarChanged?.Invoke(_currentBar);
        return true;
    }

    /// <summary>
    /// Jump to first bar
    /// </summary>
    public bool JumpToFirst()
    {
        return JumpToBar(0);
    }

    /// <summary>
    /// Jump to last bar
    /// </summary>
    public bool JumpToLast()
    {
        if (_historicalBars.Count == 0) return false;
        return JumpToBar(_historicalBars.Count - 1);
    }

    /// <summary>
    /// Process a bar through all registered strategies
    /// </summary>
    private void ProcessBar(BarData bar)
    {
        foreach (var strategy in _strategies)
        {
            try
            {
                strategy.ProcessBar(bar);
            }
            catch (Exception ex)
            {
                Log($"Error processing bar in {strategy.Name}: {ex.Message}");
            }
        }

        // Evaluate conditions after processing
        EvaluateAllStrategies();
    }

    /// <summary>
    /// Evaluate entry and exit conditions for all strategies
    /// </summary>
    public void EvaluateAllStrategies()
    {
        if (_currentBar == null) return;

        var entryResults = new Dictionary<string, EntryConditionsResult>();
        var exitResults = new Dictionary<string, ExitConditionsResult>();

        foreach (var strategy in _strategies)
        {
            try
            {
                // Evaluate entry conditions
                if (strategy is GCStrategyEngine gcEngine)
                {
                    var entryResult = gcEngine.EvaluateEntryConditions(_currentBar);
                    entryResults[strategy.Name] = entryResult;
                }
                else if (strategy is MTFStrategyEngine mtfEngine)
                {
                    var alignment = mtfEngine.GetAlignment();
                    var entryResult = mtfEngine.EvaluateEntryConditions(_currentBar, alignment);
                    entryResults[strategy.Name] = entryResult;
                }

                // Evaluate exit conditions
                var exitResult = strategy.EvaluateExitConditions(_currentBar);
                if (exitResult != null)
                {
                    exitResults[strategy.Name] = exitResult;
                }

                // Check for entry/exit signals
                CheckForSignals(strategy, entryResults.GetValueOrDefault(strategy.Name), exitResult);
            }
            catch (Exception ex)
            {
                Log($"Error evaluating conditions for {strategy.Name}: {ex.Message}");
            }
        }

        OnEntryConditionsUpdated?.Invoke(entryResults);
        OnExitConditionsUpdated?.Invoke(exitResults);
    }

    /// <summary>
    /// Check for entry/exit signals, record trades with costs, and log events
    /// </summary>
    private void CheckForSignals(IStrategyEngine strategy, EntryConditionsResult? entryResult, ExitConditionsResult? exitResult)
    {
        if (_currentBar == null) return;

        // Check for exit first (close existing position before opening new one)
        if (exitResult != null && exitResult.ShouldExit && _openTrades.ContainsKey(strategy.Name))
        {
            var openTrade = _openTrades[strategy.Name];
            CompleteTrade(openTrade, _currentBar.Close, exitResult.ExitReason ?? "Unknown", exitResult.BarsHeld);
            _openTrades.Remove(strategy.Name);

            OnSimulationEvent?.Invoke(strategy.Name,
                $"EXIT: {exitResult.ExitReason} at {_currentBar.Close:F2}, " +
                $"Net P&L: ${openTrade.NetPnL:F2}");
        }

        // Check for entry (only if not already in position)
        if (entryResult != null && entryResult.CanEnter && !_openTrades.ContainsKey(strategy.Name))
        {
            var state = strategy.GetState();
            var contracts = Math.Max(1, (int)state.PositionQuantity);

            var trade = new SimulatedTrade
            {
                Strategy = strategy.Name,
                EntryTime = _currentBar.Time,
                EntryPrice = _currentBar.Close,
                Contracts = contracts
            };

            _openTrades[strategy.Name] = trade;

            OnSimulationEvent?.Invoke(strategy.Name,
                $"ENTRY: {contracts} contracts at {_currentBar.Close:F2}");
        }
    }

    /// <summary>
    /// Complete a trade, calculating costs and updating metrics
    /// </summary>
    private void CompleteTrade(SimulatedTrade trade, double exitPrice, string exitReason, int barsHeld)
    {
        trade.ExitTime = _currentBar?.Time ?? DateTime.Now;
        trade.ExitPrice = exitPrice;
        trade.ExitReason = exitReason;
        trade.BarsHeld = barsHeld;

        // Calculate gross P&L (price difference * contracts * multiplier)
        var priceDiff = trade.ExitPrice - trade.EntryPrice;
        trade.GrossPnL = priceDiff * trade.Contracts * _settings.ContractMultiplier;

        // Calculate commission (both sides)
        trade.Commission = _settings.CommissionPerContract * trade.Contracts * 2;

        // Calculate slippage (both sides, in dollars)
        trade.Slippage = _settings.SlippageTicks * _settings.TickValue * trade.Contracts * 2;

        _completedTrades.Add(trade);

        // Update equity tracking for drawdown calculation
        UpdateDrawdown();

        // Fire event
        OnTradeCompleted?.Invoke(trade);

        Log($"Trade completed: {trade.Strategy} - Gross: ${trade.GrossPnL:F2}, " +
            $"Comm: ${trade.Commission:F2}, Slip: ${trade.Slippage:F2}, Net: ${trade.NetPnL:F2}");
    }

    /// <summary>
    /// Update drawdown tracking based on completed trades
    /// </summary>
    private void UpdateDrawdown()
    {
        var currentEquity = _settings.StartingEquity + _completedTrades.Sum(t => t.NetPnL);

        if (currentEquity > _peakEquity)
        {
            _peakEquity = currentEquity;
        }

        var drawdown = _peakEquity - currentEquity;
        if (drawdown > _maxDrawdown)
        {
            _maxDrawdown = drawdown;
            _maxDrawdownPct = _peakEquity > 0 ? (drawdown / _peakEquity) * 100 : 0;
        }
    }

    /// <summary>
    /// Start simulation
    /// </summary>
    public void Start()
    {
        _isRunning = true;
        foreach (var strategy in _strategies)
        {
            strategy.Start();
        }
        Log("Simulation started");
    }

    /// <summary>
    /// Stop simulation
    /// </summary>
    public void Stop()
    {
        _isRunning = false;
        foreach (var strategy in _strategies)
        {
            strategy.Stop();
        }
        Log("Simulation stopped");
    }

    /// <summary>
    /// Clear all historical data and reset state
    /// </summary>
    public void Clear()
    {
        _historicalBars.Clear();
        _currentBarIndex = -1;
        _highestProcessedIndex = -1;
        _currentBar = null;
        _manualPriceMode = false;
        _manualPrice = 0;

        // Clear trade tracking
        ClearTradeHistory();

        Log("Simulation data cleared");
    }

    /// <summary>
    /// Clear trade history and reset metrics (without clearing historical bars)
    /// </summary>
    public void ClearTradeHistory()
    {
        _completedTrades.Clear();
        _openTrades.Clear();
        _peakEquity = _settings.StartingEquity;
        _maxDrawdown = 0;
        _maxDrawdownPct = 0;
        Log("Trade history cleared");
    }

    /// <summary>
    /// Reset simulation to beginning and replay from first bar.
    /// This re-registers all strategies to clear their internal state.
    /// </summary>
    public void ResetAndReplay()
    {
        if (_historicalBars.Count == 0)
        {
            Log("No historical data to replay");
            return;
        }

        // Store strategy references
        var strategies = _strategies.ToList();

        // Stop and unregister all strategies to reset their internal state
        foreach (var strategy in strategies)
        {
            strategy.Stop();
        }
        _strategies.Clear();

        // Reset tracking
        _currentBarIndex = 0;
        _highestProcessedIndex = -1;
        _currentBar = _historicalBars[0];
        _simulationTime = _currentBar.Time;
        _manualPriceMode = false;

        // Clear trade history for fresh replay
        ClearTradeHistory();

        // Re-register strategies (they start fresh)
        foreach (var strategy in strategies)
        {
            RegisterStrategy(strategy);
        }

        // Process first bar
        ProcessBar(_currentBar);
        _highestProcessedIndex = 0;

        Log($"Simulation reset and replaying from bar 1/{_historicalBars.Count}");
        OnBarChanged?.Invoke(_currentBar);
    }

    /// <summary>
    /// Gets the highest bar index that has been processed by strategies
    /// </summary>
    public int HighestProcessedIndex => _highestProcessedIndex;

    private void Log(string message)
    {
        Logger.Info($"[SimulationEngine] {message}");
        OnLog?.Invoke(message);
    }
}

