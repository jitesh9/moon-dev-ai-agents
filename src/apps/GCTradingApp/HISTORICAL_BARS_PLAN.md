# Plan: Request Historical Bars from IBKR API

## Overview
Request enough historical bars from IBKR API when the application connects, so all strategies can start making entry/exit decisions immediately without waiting for realtime bars to accumulate.

## Strategy Bar Requirements Analysis

### 1. GCStrategyEngine (Aggressive/Conservative)
- **Requirement**: Minimum 50 five-second bars
- **Purpose**: Calculate indicators (RSI, MACD, EMA, SMA, SuperTrend, ATR)
- **Lookback**: 100 bars (but only needs 50 to start)

### 2. MTFStrategyEngine
- **Requirement**: 
  - Minimum 50 five-second bars for its own indicators
  - MTF Manager needs at least `SuperTrendPeriod` (default 10) bars per timeframe
  - Largest timeframe determines total requirement

#### MTF Timeframe Presets:
- **Preset_5m_15m_1H**: Largest = 1H (3600 sec) → Need 10 bars = 10 * (3600/5) = **7,200 five-second bars**
- **Preset_1m_5m_15m**: Largest = 15m (900 sec) → Need 10 bars = 10 * (900/5) = **1,800 five-second bars**
- **Preset_15m_1H_4H**: Largest = 4H (14400 sec) → Need 10 bars = 10 * (14400/5) = **28,800 five-second bars**
- **Preset_5m_1H_Daily**: Largest = Daily (86400 sec) → Need 10 bars = 10 * (86400/5) = **172,800 five-second bars**

**Maximum Requirement**: 172,800 five-second bars (for Daily timeframe preset)

## IBKR API Historical Data Limitations

### Request Limits:
- IBKR API has limits on historical data requests
- Maximum duration depends on bar size:
  - For 5-second bars: Typically limited to ~1 day of data
  - For 1-minute bars: Can request up to ~1 month
  - For 1-hour bars: Can request up to ~1 year

### Strategy:
Since requesting 172,800 five-second bars may exceed API limits, we should:
1. **Request at multiple bar sizes** and aggregate:
   - Request 1-hour bars (up to 1 year) for MTF Daily timeframe
   - Request 5-minute bars for MTF 1H/4H timeframes  
   - Request 5-second bars for immediate strategy needs (50 bars)

2. **Or request the maximum allowed 5-second bars** and let strategies wait for MTF to warm up

## Implementation Plan

### Phase 1: Create HistoricalBarRequestManager
**File**: `HistoricalBarRequestManager.cs`

**Responsibilities**:
- Calculate required bars based on active strategies
- Coordinate multiple historical data requests
- Track request completion
- Feed historical bars to strategies in chronological order
- Signal when all historical data is loaded

**Key Methods**:
```csharp
public class HistoricalBarRequestManager
{
    // Calculate requirements based on active strategies
    public HistoricalBarRequirements CalculateRequirements(
        bool aggressiveEnabled,
        bool conservativeEnabled, 
        MTFStrategyConfig? mtfConfig);
    
    // Request all needed historical data
    public void RequestHistoricalData(IBKRClient client);
    
    // Check if all requests completed
    public bool IsComplete { get; }
    
    // Get collected bars (sorted by time)
    public List<BarData> GetCollectedBars();
    
    // Event fired when all data loaded
    public event Action? OnAllDataLoaded;
}
```

### Phase 2: Enhance IBKRClient
**File**: `IBKRClient.cs`

**Changes Needed**:
1. Track active historical data requests (reqId → request info)
2. Handle `historicalDataEnd` callback properly
3. Add request tracking to know when all requests complete
4. Support multiple concurrent requests with different reqIds

**New Properties/Methods**:
```csharp
// Track active requests
private Dictionary<int, HistoricalRequestInfo> _activeHistoricalRequests;

// Request info structure
private class HistoricalRequestInfo
{
    public int ReqId { get; set; }
    public string BarSize { get; set; }
    public string Duration { get; set; }
    public List<BarData> CollectedBars { get; set; }
    public bool IsComplete { get; set; }
}

// Enhanced historicalDataEnd handler
public void historicalDataEnd(int reqId, string start, string end)
{
    // Mark request as complete
    // Check if all requests done
    // Fire completion event
}
```

### Phase 3: Integration into Connection Flow
**File**: `MainForm.cs`

**Flow**:
1. User clicks Connect
2. Connection established (`IbClient_OnConnected`)
3. **NEW**: Request historical bars (before subscribing to realtime)
4. Wait for historical data to load
5. Feed historical bars to strategies
6. Subscribe to realtime bars
7. Start strategies

**Modified Method**: `IbClient_OnConnected`
```csharp
private void IbClient_OnConnected()
{
    // ... existing code ...
    
    // NEW: Request historical bars
    _historicalBarManager = new HistoricalBarRequestManager();
    _historicalBarManager.OnAllDataLoaded += OnHistoricalDataLoaded;
    _historicalBarManager.RequestHistoricalData(_ibClient);
    
    // Don't subscribe to realtime yet - wait for historical data
}

private void OnHistoricalDataLoaded()
{
    // Feed historical bars to strategies
    var bars = _historicalBarManager.GetCollectedBars();
    foreach (var bar in bars)
    {
        _aggressiveEngine?.ProcessBar(bar);
        _conservativeEngine?.ProcessBar(bar);
        _mtfEngine?.ProcessBar(bar);
    }
    
    // Now subscribe to realtime bars
    _ibClient?.SubscribeToGCData();
    
    // Start strategies if enabled
    // ...
}
```

### Phase 4: Request Strategy

**For 5-second bars (GCStrategyEngine needs 50 bars)**:
- Request: Duration = "1 D", BarSize = "5 secs"
- This should give ~17,280 bars (24 hours * 60 min * 60 sec / 5)

**For MTF Daily timeframe (if enabled)**:
- Request: Duration = "1 Y", BarSize = "1 hour"  
- This gives 8,760 hourly bars (365 days * 24 hours)
- MTF manager can aggregate these into daily bars

**For MTF 4H timeframe (if enabled)**:
- Request: Duration = "1 M", BarSize = "5 mins"
- This gives ~8,640 five-minute bars (30 days * 24 hours * 12)
- MTF manager can aggregate these into 4H bars

**For MTF 1H timeframe (if enabled)**:
- Request: Duration = "1 W", BarSize = "5 mins"  
- This gives ~2,016 five-minute bars (7 days * 24 hours * 12)
- MTF manager can aggregate these into 1H bars

**Simplified Approach** (Recommended):
- Request maximum 5-second bars: Duration = "1 D", BarSize = "5 secs"
- This gives ~17,280 bars which is enough for:
  - GCStrategyEngine (needs 50)
  - MTFStrategyEngine with Preset_5m_15m_1H (needs 7,200)
  - MTFStrategyEngine with Preset_1m_5m_15m (needs 1,800)
- For larger timeframes (4H, Daily), strategies will need to wait for realtime bars to accumulate, OR we make additional requests at larger bar sizes

## Request ID Management

**Current**: `_gcReqId = 1001` (used for realtime bars)

**New**: Use separate reqId range for historical requests
- Historical requests: 2000-2999
- Realtime bars: 1001 (keep existing)

## Error Handling

1. **API Limits**: If request fails due to limits, log warning and proceed with realtime-only
2. **Partial Data**: If some requests fail, use what we got and let strategies wait for more
3. **Timeout**: Set timeout (e.g., 30 seconds) for historical data loading
4. **Invalid Data**: Validate bars before feeding to strategies

## Testing Considerations

1. Test with all strategy combinations enabled
2. Test with different MTF presets
3. Test API limit scenarios
4. Test connection during market hours vs. outside market hours
5. Verify strategies can make decisions immediately after historical load

## Implementation Order

1. ✅ Analyze requirements (this document)
2. Create `HistoricalBarRequestManager` class
3. Enhance `IBKRClient` to track historical requests
4. Integrate into `MainForm` connection flow
5. Test with single strategy
6. Test with multiple strategies
7. Test with different MTF presets
8. Handle edge cases and errors

## Notes

- IBKR API may return bars in reverse chronological order (newest first)
- Need to sort bars by time before feeding to strategies
- Historical bars should be fed BEFORE realtime subscription starts
- Consider caching historical data to disk for faster subsequent startups

