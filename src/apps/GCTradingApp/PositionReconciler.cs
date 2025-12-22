/*
 * Position Reconciler for IBKR TWS
 * Compares broker positions with saved state on startup
 */

namespace GCTradingApp;

/// <summary>
/// Result of position reconciliation
/// </summary>
public class ReconciliationResult
{
    public bool HasMismatch { get; set; }
    public List<PositionMismatch> Mismatches { get; set; } = new();
    public List<PositionData> BrokerPositions { get; set; } = new();
    public Dictionary<string, StrategyState> SavedStates { get; set; } = new();
}

/// <summary>
/// Details of a position mismatch
/// </summary>
public class PositionMismatch
{
    public string Symbol { get; set; } = "";
    public string Strategy { get; set; } = "";
    public decimal BrokerPosition { get; set; }
    public decimal SavedPosition { get; set; }
    public double BrokerAvgCost { get; set; }
    public double SavedEntryPrice { get; set; }
    public string Description { get; set; } = "";
}

/// <summary>
/// Reconciles positions between broker and saved state
/// </summary>
public class PositionReconciler
{
    private readonly IBKRClient _client;
    private readonly List<PositionData> _brokerPositions = new();
    private readonly object _lock = new();
    private TaskCompletionSource<bool>? _positionsTcs;
    private bool _isReconciling = false;

    // Events
    public event Action<string>? OnLog;
    public event Action<ReconciliationResult>? OnReconciliationComplete;

    public PositionReconciler(IBKRClient client)
    {
        _client = client;
    }

    /// <summary>
    /// Start reconciliation process
    /// </summary>
    public async Task<ReconciliationResult> ReconcileAsync(
        StrategyState? aggressiveState,
        StrategyState? conservativeState,
        int timeoutMs = 5000)
    {
        lock (_lock)
        {
            if (_isReconciling)
            {
                throw new InvalidOperationException("Reconciliation already in progress");
            }
            _isReconciling = true;
            _brokerPositions.Clear();
        }

        Log("Starting position reconciliation...");

        // Subscribe to position events
        _client.OnPosition += HandlePosition;
        _positionsTcs = new TaskCompletionSource<bool>();

        try
        {
            // Request positions from broker
            _client.RequestAccountUpdates();

            // Wait for positions with timeout
            var timeoutTask = Task.Delay(timeoutMs);
            var positionsTask = _positionsTcs.Task;

            // Wait a bit for positions to arrive (IBKR sends positionEnd when done)
            await Task.Delay(2000); // Give IBKR time to send positions

            Log($"Received {_brokerPositions.Count} positions from broker");

            // Build result
            var result = BuildReconciliationResult(aggressiveState, conservativeState);

            if (result.HasMismatch)
            {
                Log($"MISMATCH DETECTED: {result.Mismatches.Count} position mismatches found");
                foreach (var mismatch in result.Mismatches)
                {
                    Log($"  - {mismatch.Description}");
                }
            }
            else
            {
                Log("Positions reconciled successfully - no mismatches");
            }

            OnReconciliationComplete?.Invoke(result);
            return result;
        }
        finally
        {
            _client.OnPosition -= HandlePosition;
            lock (_lock)
            {
                _isReconciling = false;
            }
        }
    }

    private void HandlePosition(PositionData pos)
    {
        // Only care about GC positions
        if (pos.Symbol != "GC") return;

        lock (_lock)
        {
            _brokerPositions.Add(pos);
        }

        Log($"Broker position: {pos.Symbol} {pos.Position} @ {pos.AvgCost:F2}");
    }

    private ReconciliationResult BuildReconciliationResult(
        StrategyState? aggressiveState,
        StrategyState? conservativeState)
    {
        var result = new ReconciliationResult();

        lock (_lock)
        {
            result.BrokerPositions = _brokerPositions.ToList();
        }

        // Calculate total broker GC position
        decimal totalBrokerPosition = result.BrokerPositions
            .Where(p => p.Symbol == "GC")
            .Sum(p => p.Position);

        // Calculate expected positions from saved states
        decimal expectedAggressive = aggressiveState?.InPosition == true ? aggressiveState.PositionQuantity : 0;
        decimal expectedConservative = conservativeState?.InPosition == true ? conservativeState.PositionQuantity : 0;
        decimal totalExpected = expectedAggressive + expectedConservative;

        // Store saved states
        if (aggressiveState != null)
        {
            result.SavedStates["Aggressive"] = aggressiveState;
        }
        if (conservativeState != null)
        {
            result.SavedStates["Conservative"] = conservativeState;
        }

        // Check for mismatches
        if (totalBrokerPosition != totalExpected)
        {
            result.HasMismatch = true;

            // Determine the nature of the mismatch
            if (totalBrokerPosition == 0 && totalExpected > 0)
            {
                // Broker has no position but we expect one
                if (expectedAggressive > 0)
                {
                    result.Mismatches.Add(new PositionMismatch
                    {
                        Symbol = "GC",
                        Strategy = "Aggressive",
                        BrokerPosition = 0,
                        SavedPosition = expectedAggressive,
                        SavedEntryPrice = aggressiveState?.EntryPrice ?? 0,
                        Description = $"Aggressive: Expected {expectedAggressive} contracts, broker has 0 (position may have been closed externally)"
                    });
                }
                if (expectedConservative > 0)
                {
                    result.Mismatches.Add(new PositionMismatch
                    {
                        Symbol = "GC",
                        Strategy = "Conservative",
                        BrokerPosition = 0,
                        SavedPosition = expectedConservative,
                        SavedEntryPrice = conservativeState?.EntryPrice ?? 0,
                        Description = $"Conservative: Expected {expectedConservative} contracts, broker has 0 (position may have been closed externally)"
                    });
                }
            }
            else if (totalBrokerPosition > 0 && totalExpected == 0)
            {
                // Broker has position but we don't expect one
                result.Mismatches.Add(new PositionMismatch
                {
                    Symbol = "GC",
                    Strategy = "Unknown",
                    BrokerPosition = totalBrokerPosition,
                    SavedPosition = 0,
                    BrokerAvgCost = result.BrokerPositions.FirstOrDefault(p => p.Symbol == "GC")?.AvgCost ?? 0,
                    Description = $"Unexpected broker position: {totalBrokerPosition} contracts (may have been opened manually or by another system)"
                });
            }
            else
            {
                // Positions exist but quantities don't match
                result.Mismatches.Add(new PositionMismatch
                {
                    Symbol = "GC",
                    Strategy = "Combined",
                    BrokerPosition = totalBrokerPosition,
                    SavedPosition = totalExpected,
                    Description = $"Position quantity mismatch: Broker has {totalBrokerPosition}, expected {totalExpected} (Agg: {expectedAggressive}, Cons: {expectedConservative})"
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Adopt broker position into strategy state
    /// </summary>
    public StrategyState AdoptBrokerPosition(PositionData brokerPos, string strategyName)
    {
        Log($"Adopting broker position into {strategyName}: {brokerPos.Position} @ {brokerPos.AvgCost:F2}");

        return new StrategyState
        {
            InPosition = brokerPos.Position != 0,
            PositionQuantity = brokerPos.Position,
            EntryPrice = brokerPos.AvgCost,
            StopPrice = 0,  // Will need to be set based on strategy rules
            TargetPrice = 0,
            EntryTime = DateTime.Now  // Approximate - we don't know actual entry time
        };
    }

    /// <summary>
    /// Clear saved state (when broker has no position)
    /// </summary>
    public StrategyState ClearState(string strategyName)
    {
        Log($"Clearing {strategyName} state (no broker position)");
        return new StrategyState();
    }

    private void Log(string message)
    {
        Logger.Info($"[PositionReconciler] {message}");
        OnLog?.Invoke(message);
    }
}
