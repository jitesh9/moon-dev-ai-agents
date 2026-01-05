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

---

# Historical Data CSV Storage Plan

## Overview
Store all historical data received from IBKR API to CSV files organized by bar size in a dedicated data directory. This enables:
- **Persistence**: Don't need to re-request data on every startup
- **Analysis**: Review historical data for backtesting and analysis
- **Debugging**: Inspect what data was received
- **Performance**: Load from disk on subsequent startups (faster than API requests)

## Directory Structure

```
{AppDirectory}/
  data/
    historical/
      5sec/
        gc_5sec_2026-01-05.csv
        gc_5sec_2026-01-06.csv
        ...
      1min/
        gc_1min_2026-01-05.csv
        gc_1min_2026-01-06.csv
        ...
      5min/
        gc_5min_2026-01-05.csv
        gc_5min_2026-01-06.csv
        ...
      1hour/
        gc_1hour_2026-01-05.csv
        gc_1hour_2026-01-06.csv
        ...
      4hour/
        gc_4hour_2026-01-05.csv
        ...
      daily/
        gc_daily_2026-01-05.csv
        ...
```

**Directory Path**: `{AppDomain.CurrentDomain.BaseDirectory}/data/historical/{barSize}/`

## File Naming Convention

**Format**: `gc_{barSize}_{date}.csv`

**Examples**:
- `gc_5sec_2026-01-05.csv` - 5-second bars for Jan 5, 2026
- `gc_1min_2026-01-05.csv` - 1-minute bars for Jan 5, 2026
- `gc_1hour_2026-01-05.csv` - 1-hour bars for Jan 5, 2026

**Bar Size Mapping**:
- `5 secs` → `5sec`
- `1 min` → `1min`
- `5 mins` → `5min`
- `1 hour` → `1hour`
- `4 hours` → `4hour`
- `1 day` → `daily`

**Date**: Based on the date of the bars in the file (not the request date)

## CSV File Format

**Header Row** (always included):
```csv
Time,Open,High,Low,Close,Volume,WAP,Count
```

**Data Rows**:
```csv
2026-01-05 08:00:00,2650.50,2651.25,2650.00,2650.75,150,2650.625,25
2026-01-05 08:00:05,2650.75,2651.50,2650.50,2651.00,200,2650.9375,30
...
```

**Format Details**:
- **Time**: ISO format `yyyy-MM-dd HH:mm:ss` (or `yyyy-MM-dd HH:mm:ss.fff` if milliseconds available)
- **Open, High, Low, Close**: Decimal with 2 decimal places
- **Volume**: Integer
- **WAP**: Weighted Average Price, decimal with 4 decimal places
- **Count**: Integer (number of trades in bar)

**Sorting**: Bars sorted chronologically (oldest first)

## Implementation: HistoricalDataStorage Class

**File**: `HistoricalDataStorage.cs`

**Responsibilities**:
- Write historical bars to CSV files
- Organize files by bar size and date
- Load historical data from CSV files
- Merge/append new data to existing files
- Deduplicate bars (avoid storing same bar twice)
- Manage file lifecycle (cleanup old files if needed)

**Key Methods**:
```csharp
public class HistoricalDataStorage
{
    private readonly string _baseDirectory;
    
    // Constructor
    public HistoricalDataStorage(string? baseDirectory = null)
    {
        _baseDirectory = baseDirectory ?? Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, 
            "data", 
            "historical");
        EnsureDirectoriesExist();
    }
    
    // Save bars to CSV (grouped by date)
    public void SaveBars(string barSize, List<BarData> bars);
    
    // Load bars from CSV files (for a date range)
    public List<BarData> LoadBars(string barSize, DateTime? startDate = null, DateTime? endDate = null);
    
    // Get file path for a specific bar size and date
    private string GetFilePath(string barSize, DateTime date);
    
    // Normalize bar size string (e.g., "5 secs" -> "5sec")
    private string NormalizeBarSize(string barSize);
    
    // Ensure directory structure exists
    private void EnsureDirectoriesExist();
    
    // Merge new bars with existing file (deduplicate by time)
    private void MergeBarsToFile(string filePath, List<BarData> newBars);
    
    // Write bars to CSV file
    private void WriteBarsToCsv(string filePath, List<BarData> bars, bool append);
}
```

## Integration with HistoricalBarRequestManager

**Modified Flow**:
1. Request historical data from IBKR
2. As bars arrive, store them to CSV files
3. After all requests complete, load from CSV (or use in-memory collection)
4. Feed to strategies

**Modified HistoricalBarRequestManager**:
```csharp
public class HistoricalBarRequestManager
{
    private readonly HistoricalDataStorage _storage;
    
    public HistoricalBarRequestManager()
    {
        _storage = new HistoricalDataStorage();
    }
    
    // When historical bar received
    private void OnHistoricalBar(BarData bar, string barSize)
    {
        // Store to CSV immediately (or batch)
        _storage.SaveBars(barSize, new List<BarData> { bar });
        
        // Also keep in memory for immediate use
        _collectedBars.Add(bar);
    }
    
    // After all requests complete
    private void OnAllRequestsComplete()
    {
        // Ensure all bars are saved
        FlushPendingBars();
        
        // Fire completion event
        OnAllDataLoaded?.Invoke();
    }
}
```

## Startup Flow Enhancement

**Modified Connection Flow**:
1. User clicks Connect
2. Connection established
3. **Check for existing CSV files**:
   - Load recent historical data from CSV (e.g., last 24 hours for 5-second bars)
   - If sufficient data exists, use it and skip API request
   - If insufficient, request from API
4. Request missing historical data from IBKR (if needed)
5. Store received bars to CSV
6. Feed all bars (from CSV + API) to strategies
7. Subscribe to realtime bars
8. Start strategies

**Implementation**:
```csharp
private void IbClient_OnConnected()
{
    // ... existing code ...
    
    _historicalBarManager = new HistoricalBarRequestManager();
    
    // Try loading from CSV first
    var cachedBars = _historicalBarManager.LoadFromCache("5sec", TimeSpan.FromHours(24));
    
    if (cachedBars.Count >= 50) // Enough for strategies
    {
        // Use cached data
        OnHistoricalDataLoaded(cachedBars);
    }
    else
    {
        // Request from API
        _historicalBarManager.OnAllDataLoaded += OnHistoricalDataLoaded;
        _historicalBarManager.RequestHistoricalData(_ibClient);
    }
}

private void OnHistoricalDataLoaded(List<BarData> bars)
{
    // Feed to strategies
    foreach (var bar in bars)
    {
        _aggressiveEngine?.ProcessBar(bar);
        _conservativeEngine?.ProcessBar(bar);
        _mtfEngine?.ProcessBar(bar);
    }
    
    // Subscribe to realtime
    _ibClient?.SubscribeToGCData();
}
```

## Data Deduplication Strategy

**Problem**: Same bar might be received multiple times (e.g., reconnection, multiple requests)

**Solution**: 
1. When saving, check if bar with same timestamp already exists in file
2. If exists, update (or skip if identical)
3. Use dictionary/set to track seen bars during batch operations

**Implementation**:
```csharp
private void MergeBarsToFile(string filePath, List<BarData> newBars)
{
    // Load existing bars
    List<BarData> existingBars = new();
    if (File.Exists(filePath))
    {
        existingBars = LoadBarsFromFile(filePath);
    }
    
    // Create lookup by time
    var existingByTime = existingBars.ToDictionary(b => b.Time);
    
    // Merge new bars (update if exists, add if new)
    foreach (var bar in newBars)
    {
        if (existingByTime.ContainsKey(bar.Time))
        {
            // Update existing (or skip if identical)
            existingByTime[bar.Time] = bar;
        }
        else
        {
            existingBars.Add(bar);
        }
    }
    
    // Sort by time
    existingBars = existingBars.OrderBy(b => b.Time).ToList();
    
    // Write back
    WriteBarsToCsv(filePath, existingBars, append: false);
}
```

## File Management

### Daily Files
- Each day gets its own file
- Bars are grouped by date when saving
- Easy to load specific date ranges

### File Cleanup (Optional)
- Keep last N days of data (e.g., 30 days)
- Archive older data to separate directory
- Or delete old files to save space

**Configuration**:
```csharp
public class HistoricalDataStorage
{
    public int RetentionDays { get; set; } = 30; // Keep last 30 days
    
    public void CleanupOldFiles()
    {
        var cutoffDate = DateTime.Today.AddDays(-RetentionDays);
        // Delete files older than cutoff
    }
}
```

## Performance Considerations

### Batch Writing
- Don't write every single bar immediately
- Collect bars and write in batches (e.g., every 100 bars or every 5 seconds)
- Flush on request completion

### Async Writing
- Use async file I/O to avoid blocking
- Queue writes and process in background thread

**Implementation**:
```csharp
private readonly Queue<(string barSize, List<BarData> bars)> _writeQueue = new();
private readonly SemaphoreSlim _writeSemaphore = new(1, 1);

public async Task SaveBarsAsync(string barSize, List<BarData> bars)
{
    _writeQueue.Enqueue((barSize, bars));
    _ = Task.Run(ProcessWriteQueue);
}

private async Task ProcessWriteQueue()
{
    while (_writeQueue.Count > 0)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            if (_writeQueue.TryDequeue(out var item))
            {
                SaveBars(item.barSize, item.bars);
            }
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }
}
```

## Error Handling

1. **File Write Errors**: Log error, continue processing (don't crash)
2. **Disk Full**: Log warning, stop writing, continue with in-memory only
3. **Corrupted Files**: Skip corrupted files, log error
4. **Permission Issues**: Log error, fall back to in-memory only

## Testing Considerations

1. Test with multiple bar sizes
2. Test file creation and directory structure
3. Test deduplication (same bar received twice)
4. Test loading from CSV on startup
5. Test merging new data with existing files
6. Test with large datasets (performance)
7. Test file cleanup/retention
8. Test error scenarios (disk full, permissions, etc.)

## Implementation Order

1. ✅ Create plan (this document)
2. Create `HistoricalDataStorage` class
3. Implement CSV writing (single file, single bar size)
4. Implement bar size normalization and directory structure
5. Implement date-based file grouping
6. Implement deduplication/merging
7. Integrate with `HistoricalBarRequestManager`
8. Add CSV loading on startup
9. Add batch/async writing for performance
10. Add file cleanup/retention
11. Test with real data
12. Handle edge cases and errors

