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
    
    // Strategy engines (simulation mode - no real orders)
    private readonly List<IStrategyEngine> _strategies = new();
    
    // Current simulation state
    private BarData? _currentBar;
    private DateTime _simulationTime;
    private bool _isRunning = false;
    
    // Price manipulation mode
    private bool _manualPriceMode = false;
    private double _manualPrice = 0;
    
    // Events
    public event Action<BarData>? OnBarChanged;
    public event Action<Dictionary<string, EntryConditionsResult>>? OnEntryConditionsUpdated;
    public event Action<Dictionary<string, ExitConditionsResult>>? OnExitConditionsUpdated;
    public event Action<string, string>? OnSimulationEvent; // Strategy, Event message
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
            _manualPriceMode = false;
            
            if (_historicalBars.Count > 0)
            {
                _currentBar = _historicalBars[0];
                _simulationTime = _currentBar.Time;
                Log($"Loaded {_historicalBars.Count} bars from {Path.GetFileName(filePath)}");
                
                // Evaluate strategies with first bar
                EvaluateAllStrategies();
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

        // Process bar through strategies
        ProcessBar(_currentBar);
        
        OnBarChanged?.Invoke(_currentBar);
        return true;
    }

    /// <summary>
    /// Step backward to previous historical bar
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

        // Process bar through strategies
        ProcessBar(_currentBar);
        
        OnBarChanged?.Invoke(_currentBar);
        return true;
    }

    /// <summary>
    /// Jump to a specific bar index
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

        // Process bar through strategies
        ProcessBar(_currentBar);
        
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
    /// Check for entry/exit signals and log events
    /// </summary>
    private void CheckForSignals(IStrategyEngine strategy, EntryConditionsResult? entryResult, ExitConditionsResult? exitResult)
    {
        if (entryResult != null && entryResult.CanEnter)
        {
            OnSimulationEvent?.Invoke(strategy.Name, $"Entry signal triggered at {_currentBar?.Close:F2}");
        }

        if (exitResult != null && exitResult.ShouldExit)
        {
            OnSimulationEvent?.Invoke(strategy.Name, $"Exit signal triggered: {exitResult.ExitReason} at {_currentBar?.Close:F2}");
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
        _currentBar = null;
        _manualPriceMode = false;
        _manualPrice = 0;
        Log("Simulation data cleared");
    }

    private void Log(string message)
    {
        Logger.Info($"[SimulationEngine] {message}");
        OnLog?.Invoke(message);
    }
}

