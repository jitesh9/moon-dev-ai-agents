/*
 * Historical Data Storage
 * Manages CSV file storage for historical bar data organized by bar size and date
 */

using System.Globalization;

namespace GCTradingApp;

/// <summary>
/// Manages storage and retrieval of historical bar data in CSV files
/// </summary>
public class HistoricalDataStorage
{
    private readonly string _baseDirectory;
    private readonly object _writeLock = new();
    private readonly Dictionary<string, List<BarData>> _pendingBars = new();
    private readonly System.Threading.Timer? _flushTimer;
    private const int FlushIntervalMs = 5000; // Flush every 5 seconds
    private const int BatchSize = 100; // Flush when batch reaches this size

    /// <summary>
    /// Number of days to retain historical data files (0 = keep forever)
    /// </summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>
    /// Event fired when bars are saved to disk
    /// </summary>
    public event Action<string, int>? OnBarsSaved;

    public HistoricalDataStorage(string? baseDirectory = null)
    {
        _baseDirectory = baseDirectory ?? Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "data",
            "historical");

        EnsureDirectoriesExist();

        // Start flush timer for batch writing
        _flushTimer = new System.Threading.Timer(FlushPendingBars, null, FlushIntervalMs, FlushIntervalMs);
    }

    /// <summary>
    /// Ensure all required directories exist
    /// </summary>
    private void EnsureDirectoriesExist()
    {
        try
        {
            if (!Directory.Exists(_baseDirectory))
            {
                Directory.CreateDirectory(_baseDirectory);
            }

            // Create subdirectories for common bar sizes
            var barSizes = new[] { "5sec", "1min", "5min", "1hour", "4hour", "daily" };
            foreach (var barSize in barSizes)
            {
                var dir = Path.Combine(_baseDirectory, barSize);
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to create historical data directories", ex);
            throw;
        }
    }

    /// <summary>
    /// Normalize bar size string (e.g., "5 secs" -> "5sec")
    /// </summary>
    private string NormalizeBarSize(string barSize)
    {
        return barSize.ToLowerInvariant()
            .Replace(" ", "")
            .Replace("secs", "sec")
            .Replace("mins", "min")
            .Replace("hours", "hour")
            .Replace("days", "day");
    }

    /// <summary>
    /// Get file path for a specific bar size and date
    /// </summary>
    private string GetFilePath(string barSize, DateTime date)
    {
        var normalizedSize = NormalizeBarSize(barSize);
        var dateStr = date.ToString("yyyy-MM-dd");
        var dir = Path.Combine(_baseDirectory, normalizedSize);
        
        // Ensure directory exists
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        return Path.Combine(dir, $"gc_{normalizedSize}_{dateStr}.csv");
    }

    /// <summary>
    /// Save bars to CSV files (grouped by date)
    /// </summary>
    public void SaveBars(string barSize, List<BarData> bars)
    {
        if (bars == null || bars.Count == 0) return;

        try
        {
            lock (_writeLock)
            {
                // Group bars by date
                var barsByDate = bars.GroupBy(b => b.Time.Date).ToList();

                foreach (var dateGroup in barsByDate)
                {
                    var filePath = GetFilePath(barSize, dateGroup.Key);
                    var barsForDate = dateGroup.ToList();

                    // Add to pending batch
                    if (!_pendingBars.ContainsKey(filePath))
                    {
                        _pendingBars[filePath] = new List<BarData>();
                    }
                    _pendingBars[filePath].AddRange(barsForDate);

                    // Flush if batch is large enough
                    if (_pendingBars[filePath].Count >= BatchSize)
                    {
                        FlushBarsToFile(filePath, _pendingBars[filePath]);
                        _pendingBars.Remove(filePath);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to save bars for bar size {barSize}", ex);
        }
    }

    /// <summary>
    /// Save a single bar immediately (for realtime bars that need immediate persistence)
    /// </summary>
    public void SaveBarImmediate(string barSize, BarData bar)
    {
        SaveBars(barSize, new List<BarData> { bar });
        FlushPendingBars(null); // Force immediate flush
    }

    /// <summary>
    /// Flush all pending bars to disk
    /// </summary>
    public void FlushPendingBars(object? state = null)
    {
        lock (_writeLock)
        {
            if (_pendingBars.Count == 0) return;

            var filesToFlush = new List<string>(_pendingBars.Keys);
            foreach (var filePath in filesToFlush)
            {
                var bars = _pendingBars[filePath];
                if (bars.Count > 0)
                {
                    FlushBarsToFile(filePath, bars);
                    _pendingBars.Remove(filePath);
                }
            }
        }
    }

    /// <summary>
    /// Flush bars to a specific file (merge with existing data)
    /// </summary>
    private void FlushBarsToFile(string filePath, List<BarData> newBars)
    {
        try
        {
            // Load existing bars if file exists
            List<BarData> existingBars = new();
            if (File.Exists(filePath))
            {
                existingBars = LoadBarsFromFile(filePath);
            }

            // Create lookup by time for deduplication
            var existingByTime = existingBars.ToDictionary(b => b.Time);

            // Merge new bars (update if exists, add if new)
            foreach (var bar in newBars)
            {
                if (existingByTime.ContainsKey(bar.Time))
                {
                    // Update existing bar (newer data takes precedence)
                    existingByTime[bar.Time] = bar;
                }
                else
                {
                    existingBars.Add(bar);
                }
            }

            // Sort by time (oldest first)
            existingBars = existingBars.OrderBy(b => b.Time).ToList();

            // Write to file
            WriteBarsToCsv(filePath, existingBars, append: false);

            OnBarsSaved?.Invoke(filePath, existingBars.Count);
            Logger.Debug($"Saved {newBars.Count} bars to {filePath} (total: {existingBars.Count})");
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to flush bars to file {filePath}", ex);
        }
    }

    /// <summary>
    /// Load bars from a CSV file
    /// </summary>
    private List<BarData> LoadBarsFromFile(string filePath)
    {
        var bars = new List<BarData>();

        if (!File.Exists(filePath))
        {
            return bars;
        }

        try
        {
            var lines = File.ReadAllLines(filePath);
            if (lines.Length == 0)
            {
                return bars;
            }

            // Skip header row if present
            int startIndex = 0;
            if (lines[0].StartsWith("Time", StringComparison.OrdinalIgnoreCase) ||
                lines[0].StartsWith("Date", StringComparison.OrdinalIgnoreCase))
            {
                startIndex = 1;
            }

            for (int i = startIndex; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var bar = ParseCsvLine(line);
                if (bar != null)
                {
                    bars.Add(bar);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to load bars from {filePath}: {ex.Message}");
        }

        return bars;
    }

    /// <summary>
    /// Parse a CSV line into BarData
    /// </summary>
    private BarData? ParseCsvLine(string line)
    {
        var parts = line.Split(',');
        if (parts.Length < 5)
        {
            return null;
        }

        // Parse time
        if (!DateTime.TryParse(parts[0].Trim(), out var time))
        {
            return null;
        }

        // Parse OHLC
        if (!double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var open) ||
            !double.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var high) ||
            !double.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var low) ||
            !double.TryParse(parts[4].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var close))
        {
            return null;
        }

        // Parse volume (optional)
        decimal volume = 0;
        if (parts.Length > 5)
        {
            decimal.TryParse(parts[5].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out volume);
        }

        // Parse WAP (optional)
        decimal wap = 0;
        if (parts.Length > 6)
        {
            decimal.TryParse(parts[6].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out wap);
        }
        if (wap == 0)
        {
            wap = (decimal)((open + high + low + close) / 4.0);
        }

        // Parse count (optional)
        int count = 0;
        if (parts.Length > 7)
        {
            int.TryParse(parts[7].Trim(), out count);
        }

        // Validate OHLC
        if (high < low || high < open || high < close || low > open || low > close)
        {
            Logger.Warn($"Invalid OHLC data in line: {line}");
            return null;
        }

        return new BarData
        {
            Time = time,
            Open = open,
            High = high,
            Low = low,
            Close = close,
            Volume = volume,
            WAP = wap,
            Count = count
        };
    }

    /// <summary>
    /// Write bars to CSV file
    /// </summary>
    private void WriteBarsToCsv(string filePath, List<BarData> bars, bool append)
    {
        try
        {
            using var writer = new StreamWriter(filePath, append: false, System.Text.Encoding.UTF8);
            
            // Write header
            writer.WriteLine("Time,Open,High,Low,Close,Volume,WAP,Count");

            // Write bars
            foreach (var bar in bars)
            {
                var timeStr = bar.Time.ToString("yyyy-MM-dd HH:mm:ss");
                var line = $"{timeStr}," +
                          $"{bar.Open.ToString("F2", CultureInfo.InvariantCulture)}," +
                          $"{bar.High.ToString("F2", CultureInfo.InvariantCulture)}," +
                          $"{bar.Low.ToString("F2", CultureInfo.InvariantCulture)}," +
                          $"{bar.Close.ToString("F2", CultureInfo.InvariantCulture)}," +
                          $"{bar.Volume}," +
                          $"{bar.WAP.ToString("F4", CultureInfo.InvariantCulture)}," +
                          $"{bar.Count}";
                writer.WriteLine(line);
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to write CSV file {filePath}", ex);
            throw;
        }
    }

    /// <summary>
    /// Load bars from CSV files for a specific bar size and date range
    /// </summary>
    public List<BarData> LoadBars(string barSize, DateTime? startDate = null, DateTime? endDate = null)
    {
        var allBars = new List<BarData>();
        var normalizedSize = NormalizeBarSize(barSize);
        var dir = Path.Combine(_baseDirectory, normalizedSize);

        if (!Directory.Exists(dir))
        {
            return allBars;
        }

        try
        {
            var files = Directory.GetFiles(dir, $"gc_{normalizedSize}_*.csv")
                .OrderBy(f => f)
                .ToList();

            foreach (var file in files)
            {
                // Extract date from filename
                var fileName = Path.GetFileNameWithoutExtension(file);
                var datePart = fileName.Split('_').LastOrDefault();
                if (string.IsNullOrEmpty(datePart) || !DateTime.TryParse(datePart, out var fileDate))
                {
                    continue;
                }

                // Check if file is in date range
                if (startDate.HasValue && fileDate < startDate.Value.Date)
                {
                    continue;
                }
                if (endDate.HasValue && fileDate > endDate.Value.Date)
                {
                    continue;
                }

                var bars = LoadBarsFromFile(file);
                allBars.AddRange(bars);
            }

            // Sort by time and remove duplicates
            allBars = allBars
                .GroupBy(b => b.Time)
                .Select(g => g.First())
                .OrderBy(b => b.Time)
                .ToList();

            Logger.Info($"Loaded {allBars.Count} bars for {barSize} from {files.Count} files");
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to load bars for {barSize}", ex);
        }

        return allBars;
    }

    /// <summary>
    /// Load bars from cache (recent data within specified time span)
    /// </summary>
    public List<BarData> LoadFromCache(string barSize, TimeSpan timeSpan)
    {
        var endDate = DateTime.Now;
        var startDate = endDate - timeSpan;
        return LoadBars(barSize, startDate, endDate);
    }

    /// <summary>
    /// Clean up old files based on retention policy
    /// </summary>
    public void CleanupOldFiles()
    {
        if (RetentionDays <= 0) return;

        try
        {
            var cutoffDate = DateTime.Today.AddDays(-RetentionDays);
            var allDirs = Directory.GetDirectories(_baseDirectory);

            foreach (var dir in allDirs)
            {
                var files = Directory.GetFiles(dir, "*.csv");
                foreach (var file in files)
                {
                    var fileInfo = new FileInfo(file);
                    if (fileInfo.LastWriteTime < cutoffDate)
                    {
                        try
                        {
                            File.Delete(file);
                            Logger.Info($"Deleted old historical data file: {file}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn($"Failed to delete old file {file}: {ex.Message}");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to cleanup old files", ex);
        }
    }

    /// <summary>
    /// Dispose resources
    /// </summary>
    public void Dispose()
    {
        _flushTimer?.Dispose();
        FlushPendingBars(); // Flush any remaining bars
    }
}

