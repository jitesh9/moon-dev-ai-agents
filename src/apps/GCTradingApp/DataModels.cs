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
    public int EntryBarCount { get; set; } = 0;
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
