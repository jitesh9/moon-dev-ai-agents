/*
 * Data Models for GC Trading Application
 * Defines state persistence and data transfer objects
 */

namespace GCTradingApp;

/// <summary>
/// Application state - persisted to JSON file
/// </summary>
public class AppState
{
    // Connection settings
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 7497;  // 7497 for paper, 7496 for live
    public int ClientId { get; set; } = 1;

    // Strategy settings
    public bool AggressiveEnabled { get; set; } = false;
    public bool ConservativeEnabled { get; set; } = true;
    public string SizingMode { get; set; } = "Contracts";
    public double AggressiveSize { get; set; } = 1;
    public double ConservativeSize { get; set; } = 1;
    public bool StrategyRunning { get; set; } = false;
    
    // Trading hours (for Aggressive and Conservative strategies)
    public int TradingHoursStart { get; set; } = 8;  // 8 AM
    public int TradingHoursEnd { get; set; } = 17;   // 5 PM

    // Account tracking
    public double CurrentEquity { get; set; } = 0;
    public double PeakEquity { get; set; } = 0;
    public double CurrentDrawdown { get; set; } = 0;

    // Order tracking
    public Dictionary<int, OrderRecord> Orders { get; set; } = new();

    // Fill tracking
    public List<FillRecord> Fills { get; set; } = new();

    // Position tracking
    public Dictionary<string, PositionRecord> Positions { get; set; } = new();

    // Strategy state
    public StrategyState AggressiveState { get; set; } = new();
    public StrategyState ConservativeState { get; set; } = new();

    // Risk management
    public RiskSettings RiskSettings { get; set; } = new();
    public RiskState RiskState { get; set; } = new();

    // Monitoring & Alerts
    public AlertSettings AlertSettings { get; set; } = new();
    public PerformanceSettings PerformanceSettings { get; set; } = new();
    public List<CompletedTrade> CompletedTrades { get; set; } = new();

    // MTF Strategy settings
    public bool MTF_5m15m1H_Enabled { get; set; } = false;
    public bool MTF_1m5m15m_Enabled { get; set; } = false;
    public bool MTF_15m1H4H_Enabled { get; set; } = false;
    public bool MTF_5m1HDaily_Enabled { get; set; } = false;
    public int MTFSize { get; set; } = 1;
    public bool MTFAllowShorts { get; set; } = false;
    public MTFStrategyState? MTFState { get; set; }

    // Paper Trading settings
    public bool PaperTradingEnabled { get; set; } = false;
    public double PaperSlippageBps { get; set; } = 1.0;
    public int PaperFillDelayMs { get; set; } = 100;
    public double PaperInitialBalance { get; set; } = 100000;
    public PaperTradingState? PaperState { get; set; }
}

/// <summary>
/// Order record for tracking
/// </summary>
public class OrderRecord
{
    public int OrderId { get; set; }
    public DateTime Time { get; set; }
    public string Strategy { get; set; } = "";
    public string Action { get; set; } = "";
    public decimal Quantity { get; set; }
    public string OrderType { get; set; } = "";
    public double LimitPrice { get; set; }
    public double StopPrice { get; set; }
    public string Status { get; set; } = "";
    public decimal Filled { get; set; }
    public decimal Remaining { get; set; }
}

/// <summary>
/// Fill/Execution record
/// </summary>
public class FillRecord
{
    public string ExecId { get; set; } = "";
    public DateTime Time { get; set; }
    public string Strategy { get; set; } = "";
    public string Action { get; set; } = "";
    public decimal Quantity { get; set; }
    public double Price { get; set; }
    public double Commission { get; set; }
    public double RealizedPnL { get; set; }
}

/// <summary>
/// Position record
/// </summary>
public class PositionRecord
{
    public string Strategy { get; set; } = "";
    public string Symbol { get; set; } = "";
    public decimal Position { get; set; }
    public double AvgCost { get; set; }
    public double MarketPrice { get; set; }
    public double MarketValue { get; set; }
    public double UnrealizedPnL { get; set; }
    public double RealizedPnL { get; set; }
}

/// <summary>
/// Strategy state for persistence
/// </summary>
public class StrategyState
{
    public bool InPosition { get; set; } = false;
    public double EntryPrice { get; set; } = 0;
    public DateTime EntryTime { get; set; }
    public long EntryBarIndex { get; set; } = 0;  // Absolute bar index (not list count)
    public long TotalBarsProcessed { get; set; } = 0;  // For restoring absolute counter
    public double StopPrice { get; set; } = 0;
    public double TargetPrice { get; set; } = 0;
    public int CurrentOrderId { get; set; } = 0;
    public decimal PositionQuantity { get; set; } = 0;
}

// Event data classes for IBKR callbacks

public class OrderStatusData
{
    public int OrderId { get; set; }
    public string Status { get; set; } = "";
    public decimal Filled { get; set; }
    public decimal Remaining { get; set; }
    public double AvgFillPrice { get; set; }
    public long PermId { get; set; }
    public int ParentId { get; set; }
    public double LastFillPrice { get; set; }
    public int ClientId { get; set; }
    public string WhyHeld { get; set; } = "";
}

public class OpenOrderData
{
    public int OrderId { get; set; }
    public string Symbol { get; set; } = "";
    public string Action { get; set; } = "";
    public decimal Quantity { get; set; }
    public string OrderType { get; set; } = "";
    public double LimitPrice { get; set; }
    public double StopPrice { get; set; }
    public string Status { get; set; } = "";
    public string OrderRef { get; set; } = "";
}

public class ExecutionData
{
    public string ExecId { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Side { get; set; } = "";
    public decimal Shares { get; set; }
    public double Price { get; set; }
    public double Commission { get; set; }
    public double RealizedPnL { get; set; }
    public string OrderRef { get; set; } = "";
}

public class PositionData
{
    public string Account { get; set; } = "";
    public string Symbol { get; set; } = "";
    public decimal Position { get; set; }
    public double AvgCost { get; set; }
    public double MarketPrice { get; set; }
    public double MarketValue { get; set; }
    public double UnrealizedPnL { get; set; }
    public double RealizedPnL { get; set; }
}

public class BarData
{
    public DateTime Time { get; set; }
    public double Open { get; set; }
    public double High { get; set; }
    public double Low { get; set; }
    public double Close { get; set; }
    public decimal Volume { get; set; }
    public decimal WAP { get; set; }
    public int Count { get; set; }
}

/// <summary>
/// Entry condition status for UI display
/// </summary>
public class EntryConditionStatus
{
    public string ConditionName { get; set; } = "";
    public bool IsTrue { get; set; }
    public string Description { get; set; } = "";
    public string? Value { get; set; }  // Optional value to display (e.g., "RSI: 45.2")
}

/// <summary>
/// Complete entry condition evaluation result for a strategy
/// </summary>
public class EntryConditionsResult
{
    public string StrategyName { get; set; } = "";
    public bool CanEnter { get; set; }
    public List<EntryConditionStatus> Conditions { get; set; } = new();
    public int ConfirmationsCount { get; set; }
    public int RequiredConfirmations { get; set; }
    public string? BlockingReason { get; set; }  // Why entry is blocked (if CanEnter is false)
}

/// <summary>
/// Exit condition status for UI display
/// </summary>
public class ExitConditionStatus
{
    public string ConditionName { get; set; } = "";
    public bool IsTrue { get; set; }
    public string Description { get; set; } = "";
    public string? Value { get; set; }  // Optional value to display (e.g., "Stop: 2045.00")
}

/// <summary>
/// Complete exit condition evaluation result for a strategy
/// </summary>
public class ExitConditionsResult
{
    public string StrategyName { get; set; } = "";
    public bool InPosition { get; set; }
    public bool ShouldExit { get; set; }
    public List<ExitConditionStatus> Conditions { get; set; } = new();
    public string? ExitReason { get; set; }  // Why exit should occur (if ShouldExit is true)
    public double EntryPrice { get; set; }
    public double CurrentPrice { get; set; }
    public double StopPrice { get; set; }
    public double TargetPrice { get; set; }
    public double UnrealizedPnL { get; set; }
    public double UnrealizedPnLPct { get; set; }
    public int BarsHeld { get; set; }
}

/// <summary>
/// Settings for backtest simulation with realistic costs
/// </summary>
public class SimulationSettings
{
    /// <summary>
    /// Commission per contract per side (e.g., $2.25 for GC futures)
    /// </summary>
    public double CommissionPerContract { get; set; } = 2.25;

    /// <summary>
    /// Slippage in ticks per side (GC tick = $0.10 = $10 per contract)
    /// </summary>
    public double SlippageTicks { get; set; } = 1.0;

    /// <summary>
    /// Tick value in dollars (GC = $10 per tick, 100 oz * $0.10)
    /// </summary>
    public double TickValue { get; set; } = 10.0;

    /// <summary>
    /// Contract multiplier (GC = 100 oz)
    /// </summary>
    public double ContractMultiplier { get; set; } = 100.0;

    /// <summary>
    /// Starting equity for simulation
    /// </summary>
    public double StartingEquity { get; set; } = 100000.0;
}

/// <summary>
/// Represents a single simulated trade with all costs
/// </summary>
public class SimulatedTrade
{
    public string Strategy { get; set; } = "";
    public DateTime EntryTime { get; set; }
    public DateTime ExitTime { get; set; }
    public double EntryPrice { get; set; }
    public double ExitPrice { get; set; }
    public int Contracts { get; set; }
    public string ExitReason { get; set; } = "";

    // Gross P&L (before costs)
    public double GrossPnL { get; set; }

    // Costs
    public double Commission { get; set; }  // Total commission (entry + exit)
    public double Slippage { get; set; }    // Total slippage cost (entry + exit)

    // Net P&L (after costs)
    public double NetPnL => GrossPnL - Commission - Slippage;

    // Trade metrics
    public int BarsHeld { get; set; }
    public bool IsWinner => NetPnL > 0;
}

/// <summary>
/// Aggregate metrics for simulation performance
/// </summary>
public class SimulationMetrics
{
    public int TotalTrades { get; set; }
    public int WinningTrades { get; set; }
    public int LosingTrades { get; set; }

    public double GrossPnL { get; set; }
    public double TotalCommission { get; set; }
    public double TotalSlippage { get; set; }
    public double NetPnL => GrossPnL - TotalCommission - TotalSlippage;

    public double WinRate => TotalTrades > 0 ? (double)WinningTrades / TotalTrades * 100 : 0;

    public double LargestWin { get; set; }
    public double LargestLoss { get; set; }
    public double AverageWin { get; set; }
    public double AverageLoss { get; set; }

    public double ProfitFactor => AverageLoss != 0 ? (AverageWin * WinningTrades) / (Math.Abs(AverageLoss) * LosingTrades) : 0;

    public double MaxDrawdown { get; set; }
    public double MaxDrawdownPct { get; set; }

    public double StartingEquity { get; set; }
    public double EndingEquity => StartingEquity + NetPnL;
    public double ReturnPct => StartingEquity > 0 ? (NetPnL / StartingEquity) * 100 : 0;

    // Cost analysis
    public double CostPerTrade => TotalTrades > 0 ? (TotalCommission + TotalSlippage) / TotalTrades : 0;
    public double CostAsPctOfGross => GrossPnL != 0 ? ((TotalCommission + TotalSlippage) / Math.Abs(GrossPnL)) * 100 : 0;
}