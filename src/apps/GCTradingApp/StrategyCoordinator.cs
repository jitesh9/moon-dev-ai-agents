/*
 * Strategy Coordinator
 * Manages strategy lifecycle, data routing, and coordination
 * Extracted from MainForm to improve separation of concerns
 */

using System.Threading.Channels;

namespace GCTradingApp;

/// <summary>
/// Manages multiple strategy engines, routing market data and coordinating lifecycle
/// Processes bars on background thread for better thread safety and UI responsiveness
/// </summary>
public class StrategyCoordinator : IDisposable
{
    private readonly IBKRClient _dataClient;
    private readonly RiskManager? _riskManager;
    private readonly Func<string, IOrderClient> _orderClientFactory;
    private readonly object _lock = new();

    // Strategy registry
    private readonly Dictionary<string, IStrategyEngine> _strategies = new();
    private readonly Dictionary<string, CircuitBreaker> _circuitBreakers = new();

    // Bar processing queue and background task
    private readonly Channel<BarData> _barChannel;
    private readonly ChannelWriter<BarData> _barWriter;
    private readonly ChannelReader<BarData> _barReader;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private Task? _barProcessingTask;

    // Events
    public event Action<string>? OnLog;
    public event Action<string, StrategyState>? OnStrategyStateChanged;
    public event Action<string, CircuitState>? OnCircuitBreakerStateChanged;

    public StrategyCoordinator(
        IBKRClient dataClient,
        RiskManager? riskManager,
        Func<string, IOrderClient> orderClientFactory)
    {
        _dataClient = dataClient ?? throw new ArgumentNullException(nameof(dataClient));
        _riskManager = riskManager;
        _orderClientFactory = orderClientFactory ?? throw new ArgumentNullException(nameof(orderClientFactory));

        // Create unbounded channel for bar data queue
        var options = new UnboundedChannelOptions
        {
            SingleReader = false,  // Multiple strategies may process concurrently
            SingleWriter = true    // Only MainForm writes bars
        };
        _barChannel = Channel.CreateUnbounded<BarData>(options);
        _barWriter = _barChannel.Writer;
        _barReader = _barChannel.Reader;

        // Start background processing task
        _barProcessingTask = Task.Run(ProcessBarsAsync, _cancellationTokenSource.Token);
        Log("Strategy coordinator initialized with background bar processing");
    }

    /// <summary>
    /// Register a strategy with the coordinator
    /// </summary>
    public void RegisterStrategy(IStrategyEngine strategy, CircuitBreaker? circuitBreaker = null)
    {
        if (strategy == null) throw new ArgumentNullException(nameof(strategy));

        lock (_lock)
        {
            var name = strategy.Name;
            if (_strategies.ContainsKey(name))
            {
                throw new InvalidOperationException($"Strategy '{name}' is already registered");
            }

            _strategies[name] = strategy;

            // Subscribe to strategy events
            strategy.OnLog += msg => OnLog?.Invoke($"[{name}] {msg}");
            strategy.OnStateChanged += state => OnStrategyStateChanged?.Invoke(name, state);

            // Register circuit breaker if provided
            if (circuitBreaker != null)
            {
                _circuitBreakers[name] = circuitBreaker;
                circuitBreaker.OnStateChanged += (cbName, state) =>
                {
                    OnCircuitBreakerStateChanged?.Invoke(name, state);
                    OnLog?.Invoke($"[CIRCUIT BREAKER] {name} state changed to {state}");
                };
            }

            Log($"Strategy '{name}' registered");
        }
    }

    /// <summary>
    /// Unregister a strategy
    /// </summary>
    public void UnregisterStrategy(string name)
    {
        lock (_lock)
        {
            if (_strategies.TryGetValue(name, out var strategy))
            {
                strategy.Stop();
                _strategies.Remove(name);
                _circuitBreakers.Remove(name);
                Log($"Strategy '{name}' unregistered");
            }
        }
    }

    /// <summary>
    /// Start all registered strategies
    /// </summary>
    public void StartAll()
    {
        lock (_lock)
        {
            foreach (var strategy in _strategies.Values)
            {
                strategy.Start();
                Log($"Strategy '{strategy.Name}' started");
            }
        }
    }

    /// <summary>
    /// Stop all registered strategies
    /// </summary>
    public void StopAll()
    {
        lock (_lock)
        {
            foreach (var strategy in _strategies.Values)
            {
                strategy.Stop();
            }

            // Reset circuit breakers
            foreach (var cb in _circuitBreakers.Values)
            {
                cb.Reset();
            }

            Log("All strategies stopped");
        }
    }

    /// <summary>
    /// Get a strategy by name
    /// </summary>
    public IStrategyEngine? GetStrategy(string name)
    {
        lock (_lock)
        {
            return _strategies.TryGetValue(name, out var strategy) ? strategy : null;
        }
    }

    /// <summary>
    /// Get all registered strategies
    /// </summary>
    public IReadOnlyDictionary<string, IStrategyEngine> GetAllStrategies()
    {
        lock (_lock)
        {
            return new Dictionary<string, IStrategyEngine>(_strategies);
        }
    }

    /// <summary>
    /// Get strategy state for persistence
    /// </summary>
    public Dictionary<string, StrategyState> GetAllStrategyStates()
    {
        lock (_lock)
        {
            var states = new Dictionary<string, StrategyState>();
            foreach (var kvp in _strategies)
            {
                states[kvp.Key] = kvp.Value.GetState();
            }
            return states;
        }
    }

    /// <summary>
    /// Queue a bar for processing on background thread
    /// Called from MainForm when new bar arrives (non-blocking)
    /// </summary>
    public void ProcessBar(BarData bar)
    {
        // Write to channel (non-blocking, returns false if channel is closed)
        if (!_barWriter.TryWrite(bar))
        {
            OnLog?.Invoke("[WARN] Bar processing channel is closed - bar dropped");
        }
    }

    /// <summary>
    /// Background task that processes bars from the queue
    /// </summary>
    private async Task ProcessBarsAsync()
    {
        try
        {
            await foreach (var bar in _barReader.ReadAllAsync(_cancellationTokenSource.Token))
            {
                await ProcessBarInternalAsync(bar);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when shutting down
            Log("Bar processing task cancelled");
        }
        catch (Exception ex)
        {
            Logger.Error("Error in bar processing task", ex);
            OnLog?.Invoke($"[ERROR] Bar processing task error: {ex.Message}");
        }
    }

    /// <summary>
    /// Process a bar through all registered strategies with circuit breaker protection
    /// Runs on background thread
    /// </summary>
    private async Task ProcessBarInternalAsync(BarData bar)
    {
        // Get snapshot of strategies under lock
        List<(IStrategyEngine strategy, CircuitBreaker? circuitBreaker)> strategies;
        lock (_lock)
        {
            strategies = _strategies.Select(kvp =>
            {
                _circuitBreakers.TryGetValue(kvp.Key, out var cb);
                return (kvp.Value, cb);
            }).ToList();
        }

        // Process each strategy (outside lock to avoid deadlock)
        // Use Task.Run for parallel processing if desired, or sequential for now
        foreach (var (strategy, circuitBreaker) in strategies)
        {
            await Task.Run(() => ProcessStrategyWithCircuitBreaker(strategy, circuitBreaker, bar));
        }
    }

    /// <summary>
    /// Process a strategy with circuit breaker protection
    /// </summary>
    private void ProcessStrategyWithCircuitBreaker(IStrategyEngine strategy, CircuitBreaker? circuitBreaker, BarData bar)
    {
        if (circuitBreaker != null)
        {
            try
            {
                var executed = circuitBreaker.Execute(() =>
                {
                    strategy.ProcessBar(bar);
                });

                if (!executed)
                {
                    OnLog?.Invoke($"[CIRCUIT BREAKER] {strategy.Name} blocked - circuit is OPEN");
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[ERROR] Exception in {strategy.Name}: {ex.Message}");
                Logger.Error($"Exception in {strategy.Name} strategy", ex);
            }
        }
        else
        {
            // Fallback if circuit breaker not initialized
            try
            {
                strategy.ProcessBar(bar);
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[ERROR] Exception in {strategy.Name}: {ex.Message}");
                Logger.Error($"Exception in {strategy.Name} strategy", ex);
            }
        }
    }

    private void Log(string message)
    {
        Logger.Info($"[StrategyCoordinator] {message}");
        OnLog?.Invoke(message);
    }

    public void Dispose()
    {
        StopAll();

        // Signal completion and wait for background task
        _barWriter.Complete();
        _cancellationTokenSource.Cancel();

        try
        {
            _barProcessingTask?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
        {
            // Expected
        }

        _cancellationTokenSource.Dispose();
    }
}

