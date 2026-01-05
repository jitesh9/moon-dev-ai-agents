/*
 * Historical Bar Request Manager
 * Coordinates historical data requests from IBKR API and manages data loading
 */

namespace GCTradingApp;

/// <summary>
/// Requirements for historical data based on active strategies
/// </summary>
public class HistoricalBarRequirements
{
    public bool Need5SecondBars { get; set; }
    public bool Need1MinuteBars { get; set; }
    public bool Need5MinuteBars { get; set; }
    public bool Need1HourBars { get; set; }
    public bool Need4HourBars { get; set; }
    public bool NeedDailyBars { get; set; }

    public int Min5SecondBars { get; set; } = 50;
    public int Min1MinuteBars { get; set; } = 50;
    public int Min5MinuteBars { get; set; } = 50;
    public int Min1HourBars { get; set; } = 10;
    public int Min4HourBars { get; set; } = 10;
    public int MinDailyBars { get; set; } = 10;
}

/// <summary>
/// Manages historical data requests and coordinates loading from cache/API
/// </summary>
public class HistoricalBarRequestManager
{
    private readonly HistoricalDataStorage _storage;
    private readonly IBKRClient _client;
    private readonly Dictionary<int, string> _reqIdToBarSize = new(); // Track which reqId corresponds to which bar size
    private readonly Dictionary<string, List<BarData>> _collectedBars = new(); // Bars by bar size
    private readonly object _lock = new();
    private bool _allRequestsComplete = false;
    private readonly List<int> _activeRequestIds = new();

    // Events
    public event Action? OnAllDataLoaded;
    public event Action<string, int>? OnBarsReceived; // barSize, count
    public event Action<string>? OnError;

    public HistoricalBarRequestManager(IBKRClient client, HistoricalDataStorage? storage = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _storage = storage ?? new HistoricalDataStorage();

        // Subscribe to client events
        _client.OnHistoricalBar += OnHistoricalBar;
        _client.OnHistoricalDataEnd += OnHistoricalDataEnd;
        _storage.OnBarsSaved += (filePath, count) => Logger.Debug($"Saved {count} bars to {filePath}");
    }

    /// <summary>
    /// Calculate requirements based on active strategies
    /// </summary>
    public static HistoricalBarRequirements CalculateRequirements(
        bool aggressiveEnabled,
        bool conservativeEnabled,
        MTFStrategyConfig? mtfConfig)
    {
        var requirements = new HistoricalBarRequirements();

        // GCStrategyEngine (Aggressive/Conservative) needs 5-second bars
        if (aggressiveEnabled || conservativeEnabled)
        {
            requirements.Need5SecondBars = true;
            requirements.Min5SecondBars = 50; // Minimum to start making decisions
        }

        // MTF Strategy requirements depend on preset
        if (mtfConfig != null)
        {
            requirements.Need5SecondBars = true; // Always need 5-second for entry timing
            requirements.Min5SecondBars = Math.Max(requirements.Min5SecondBars, 50);

            switch (mtfConfig.TimeframePreset)
            {
                case TimeframePreset.Preset_5m_15m_1H:
                    // Need enough 5-second bars to create 10 1-hour bars
                    // 1 hour = 3600 seconds = 720 five-second bars
                    // Need 10 * 720 = 7,200 five-second bars
                    requirements.Min5SecondBars = Math.Max(requirements.Min5SecondBars, 7200);
                    break;

                case TimeframePreset.Preset_1m_5m_15m:
                    // Need enough 5-second bars to create 10 15-minute bars
                    // 15 min = 900 seconds = 180 five-second bars
                    // Need 10 * 180 = 1,800 five-second bars
                    requirements.Min5SecondBars = Math.Max(requirements.Min5SecondBars, 1800);
                    break;

                case TimeframePreset.Preset_15m_1H_4H:
                    // Could request 4-hour bars directly, or use 5-second
                    // For efficiency, request 5-minute bars
                    requirements.Need5MinuteBars = true;
                    requirements.Min5MinuteBars = 100; // Enough to create 4H bars
                    break;

                case TimeframePreset.Preset_5m_1H_Daily:
                    // Request 1-hour bars for Daily aggregation
                    requirements.Need1HourBars = true;
                    requirements.Min1HourBars = 50; // Enough for daily bars
                    break;
            }
        }

        return requirements;
    }

    /// <summary>
    /// Load bars from cache (CSV files) for a specific bar size and time span
    /// </summary>
    public List<BarData> LoadFromCache(string barSize, TimeSpan timeSpan)
    {
        try
        {
            var bars = _storage.LoadFromCache(barSize, timeSpan);
            Logger.Info($"Loaded {bars.Count} {barSize} bars from cache (last {timeSpan.TotalHours:F1} hours)");
            return bars;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to load {barSize} bars from cache: {ex.Message}");
            return new List<BarData>();
        }
    }

    /// <summary>
    /// Request historical data from IBKR API based on requirements
    /// </summary>
    public void RequestHistoricalData(HistoricalBarRequirements requirements)
    {
        lock (_lock)
        {
            _allRequestsComplete = false;
            _activeRequestIds.Clear();
            _collectedBars.Clear();
            _reqIdToBarSize.Clear();
        }

        // Request 5-second bars (most common requirement)
        if (requirements.Need5SecondBars)
        {
            RequestBars("5 secs", "1 D", requirements.Min5SecondBars);
        }

        // Request 1-minute bars if needed
        if (requirements.Need1MinuteBars)
        {
            RequestBars("1 min", "1 W", requirements.Min1MinuteBars);
        }

        // Request 5-minute bars if needed
        if (requirements.Need5MinuteBars)
        {
            RequestBars("5 mins", "1 M", requirements.Min5MinuteBars);
        }

        // Request 1-hour bars if needed
        if (requirements.Need1HourBars)
        {
            RequestBars("1 hour", "1 Y", requirements.Min1HourBars);
        }

        // Request 4-hour bars if needed
        if (requirements.Need4HourBars)
        {
            RequestBars("4 hours", "1 M", requirements.Min4HourBars);
        }

        // Request daily bars if needed
        if (requirements.NeedDailyBars)
        {
            RequestBars("1 day", "1 Y", requirements.MinDailyBars);
        }

        Logger.Info($"Requested historical data for {_activeRequestIds.Count} bar sizes");
    }

    /// <summary>
    /// Request bars for a specific bar size
    /// </summary>
    private void RequestBars(string barSize, string duration, int minBars)
    {
        try
        {
            // First, try loading from cache
            var cachedBars = LoadFromCache(barSize, TimeSpan.FromDays(1));
            
            if (cachedBars.Count >= minBars)
            {
                Logger.Info($"Using cached {barSize} bars ({cachedBars.Count} >= {minBars} required)");
                lock (_lock)
                {
                    _collectedBars[barSize] = cachedBars;
                }
                OnBarsReceived?.Invoke(barSize, cachedBars.Count);
                return;
            }

            // Need to request from API
            Logger.Info($"Requesting {barSize} bars from API (have {cachedBars.Count}, need {minBars})");
            
            var reqId = _client.RequestHistoricalData(duration, barSize);
            
            lock (_lock)
            {
                _activeRequestIds.Add(reqId);
                _reqIdToBarSize[reqId] = barSize;
                
                // Add cached bars to collection
                if (cachedBars.Count > 0)
                {
                    if (!_collectedBars.ContainsKey(barSize))
                    {
                        _collectedBars[barSize] = new List<BarData>();
                    }
                    _collectedBars[barSize].AddRange(cachedBars);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to request {barSize} bars", ex);
            OnError?.Invoke($"Failed to request {barSize} bars: {ex.Message}");
        }
    }

    /// <summary>
    /// Handle historical bar received from IBKR
    /// Note: IBKRClient already stores bars in request info, but we track here too
    /// for immediate processing if needed
    /// </summary>
    private void OnHistoricalBar(BarData bar)
    {
        // Bars are already being collected in IBKRClient's request info
        // We'll process them when the request completes (in OnHistoricalDataEnd)
        // This allows for batch processing and better performance
    }

    /// <summary>
    /// Handle historical data end callback
    /// </summary>
    private void OnHistoricalDataEnd(int reqId, string start, string end)
    {
        lock (_lock)
        {
            if (!_reqIdToBarSize.TryGetValue(reqId, out var barSize))
            {
                Logger.Warn($"Received historical data end for unknown reqId: {reqId}");
                return;
            }

            // Get bars collected for this request
            var requestInfo = _client.GetHistoricalRequestInfo(reqId);
            if (requestInfo != null && requestInfo.CollectedBars.Count > 0)
            {
                // Add to collected bars
                if (!_collectedBars.ContainsKey(barSize))
                {
                    _collectedBars[barSize] = new List<BarData>();
                }

                var newBars = requestInfo.CollectedBars;
                _collectedBars[barSize].AddRange(newBars);

                // Sort and deduplicate
                _collectedBars[barSize] = _collectedBars[barSize]
                    .GroupBy(b => b.Time)
                    .Select(g => g.First())
                    .OrderBy(b => b.Time)
                    .ToList();

                // Save to CSV
                _storage.SaveBars(barSize, newBars);

                Logger.Info($"Received {newBars.Count} {barSize} bars (total: {_collectedBars[barSize].Count})");
                OnBarsReceived?.Invoke(barSize, _collectedBars[barSize].Count);
            }

            // Remove from active requests
            _activeRequestIds.Remove(reqId);

            // Check if all requests complete
            if (_activeRequestIds.Count == 0)
            {
                _allRequestsComplete = true;
                Logger.Info("All historical data requests completed");
                
                // Flush any pending writes
                _storage.FlushPendingBars();
                
                // Fire completion event
                OnAllDataLoaded?.Invoke();
            }
        }
    }

    /// <summary>
    /// Get all collected bars (all bar sizes combined, sorted by time)
    /// </summary>
    public List<BarData> GetCollectedBars()
    {
        lock (_lock)
        {
            var allBars = _collectedBars.Values
                .SelectMany(bars => bars)
                .GroupBy(b => b.Time)
                .Select(g => g.First())
                .OrderBy(b => b.Time)
                .ToList();

            return allBars;
        }
    }

    /// <summary>
    /// Get collected bars for a specific bar size
    /// </summary>
    public List<BarData> GetCollectedBars(string barSize)
    {
        lock (_lock)
        {
            return _collectedBars.TryGetValue(barSize, out var bars) 
                ? bars.ToList() 
                : new List<BarData>();
        }
    }

    /// <summary>
    /// Check if all requests are complete
    /// </summary>
    public bool IsComplete => _allRequestsComplete;

    /// <summary>
    /// Get count of active requests
    /// </summary>
    public int ActiveRequestCount
    {
        get
        {
            lock (_lock)
            {
                return _activeRequestIds.Count;
            }
        }
    }

    /// <summary>
    /// Cleanup resources
    /// </summary>
    public void Dispose()
    {
        _client.OnHistoricalBar -= OnHistoricalBar;
        _client.OnHistoricalDataEnd -= OnHistoricalDataEnd;
        _storage.Dispose();
    }
}

