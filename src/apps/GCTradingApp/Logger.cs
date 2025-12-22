/*
 * File Logger for GC Trading Application
 * Writes timestamped logs to daily log files with rotation
 */

namespace GCTradingApp;

/// <summary>
/// Thread-safe file logger with daily rotation and per-strategy logging
/// </summary>
public static class Logger
{
    private static readonly object _lock = new();
    private static readonly string _logDirectory;
    private static string _currentLogFile = "";
    private static DateTime _currentLogDate;
    private static int _retentionDays = 7;

    // Per-strategy loggers
    private static readonly Dictionary<string, string> _strategyLogFiles = new();

    static Logger()
    {
        _logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");

        try
        {
            if (!Directory.Exists(_logDirectory))
            {
                Directory.CreateDirectory(_logDirectory);
            }

            // Create strategy subdirectory
            var strategyDir = Path.Combine(_logDirectory, "strategies");
            if (!Directory.Exists(strategyDir))
            {
                Directory.CreateDirectory(strategyDir);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to create log directory: {ex.Message}");
        }

        UpdateLogFile();
        CleanupOldLogs();
    }

    /// <summary>
    /// Set the number of days to retain logs
    /// </summary>
    public static void SetRetentionDays(int days)
    {
        _retentionDays = Math.Max(1, days);
    }

    private static void UpdateLogFile()
    {
        var today = DateTime.Today;
        if (_currentLogDate != today)
        {
            _currentLogDate = today;
            _currentLogFile = Path.Combine(_logDirectory, $"gc_trading_{today:yyyy-MM-dd}.log");

            // Update strategy log files
            foreach (var strategy in _strategyLogFiles.Keys.ToList())
            {
                var strategyDir = Path.Combine(_logDirectory, "strategies");
                _strategyLogFiles[strategy] = Path.Combine(strategyDir, $"{strategy}_{today:yyyy-MM-dd}.log");
            }
        }
    }

    /// <summary>
    /// Clean up log files older than retention period
    /// </summary>
    private static void CleanupOldLogs()
    {
        try
        {
            var cutoffDate = DateTime.Today.AddDays(-_retentionDays);

            // Clean main log files
            CleanupDirectory(_logDirectory, cutoffDate);

            // Clean strategy log files
            var strategyDir = Path.Combine(_logDirectory, "strategies");
            if (Directory.Exists(strategyDir))
            {
                CleanupDirectory(strategyDir, cutoffDate);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Log cleanup error: {ex.Message}");
        }
    }

    private static void CleanupDirectory(string directory, DateTime cutoffDate)
    {
        foreach (var file in Directory.GetFiles(directory, "*.log"))
        {
            try
            {
                var fileInfo = new FileInfo(file);
                if (fileInfo.CreationTime < cutoffDate)
                {
                    File.Delete(file);
                    Console.WriteLine($"Deleted old log: {Path.GetFileName(file)}");
                }
            }
            catch
            {
                // Ignore deletion errors
            }
        }
    }

    /// <summary>
    /// Log a message to the main log file
    /// </summary>
    public static void Log(string message, LogLevel level = LogLevel.Info)
    {
        try
        {
            lock (_lock)
            {
                UpdateLogFile();
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                var logLine = $"[{timestamp}] [{level}] {message}";

                File.AppendAllText(_currentLogFile, logLine + Environment.NewLine);

                // Also write to console for debugging
                Console.WriteLine(logLine);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Logger error: {ex.Message}");
        }
    }

    /// <summary>
    /// Log a message for a specific strategy
    /// </summary>
    public static void LogStrategy(string strategy, string message, LogLevel level = LogLevel.Info)
    {
        try
        {
            lock (_lock)
            {
                UpdateLogFile();

                // Ensure strategy log file is tracked
                if (!_strategyLogFiles.ContainsKey(strategy))
                {
                    var strategyDir = Path.Combine(_logDirectory, "strategies");
                    _strategyLogFiles[strategy] = Path.Combine(strategyDir, $"{strategy}_{DateTime.Today:yyyy-MM-dd}.log");
                }

                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                var logLine = $"[{timestamp}] [{level}] {message}";

                // Write to strategy-specific log
                File.AppendAllText(_strategyLogFiles[strategy], logLine + Environment.NewLine);

                // Also write to main log with strategy prefix
                var mainLogLine = $"[{timestamp}] [{level}] [{strategy}] {message}";
                File.AppendAllText(_currentLogFile, mainLogLine + Environment.NewLine);

                Console.WriteLine(mainLogLine);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Logger error: {ex.Message}");
        }
    }

    /// <summary>
    /// Log performance metrics
    /// </summary>
    public static void LogPerformance(PerformanceMetrics metrics)
    {
        var performanceLog = Path.Combine(_logDirectory, $"performance_{DateTime.Today:yyyy-MM-dd}.log");

        try
        {
            lock (_lock)
            {
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                var logLine = $"[{timestamp}] " +
                    $"Trades={metrics.TotalTrades} " +
                    $"WinRate={metrics.WinRate:F1}% " +
                    $"PnL=${metrics.TotalNetPnL:F2} " +
                    $"PF={metrics.ProfitFactor:F2} " +
                    $"Sharpe={metrics.SharpeRatio:F2} " +
                    $"MaxDD=${metrics.MaxDrawdown:F2}";

                File.AppendAllText(performanceLog, logLine + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Performance log error: {ex.Message}");
        }
    }

    /// <summary>
    /// Log a trade execution
    /// </summary>
    public static void LogTrade(string strategy, string action, decimal quantity, double price, double pnl = 0)
    {
        var tradeLog = Path.Combine(_logDirectory, $"trades_{DateTime.Today:yyyy-MM-dd}.log");

        try
        {
            lock (_lock)
            {
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                var logLine = $"[{timestamp}] {strategy} {action} {quantity} @ {price:F2}";
                if (pnl != 0)
                {
                    logLine += $" PnL=${pnl:F2}";
                }

                File.AppendAllText(tradeLog, logLine + Environment.NewLine);

                // Also log to main and strategy logs
                LogStrategy(strategy, $"{action} {quantity} @ {price:F2}" + (pnl != 0 ? $" PnL=${pnl:F2}" : ""), LogLevel.Info);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Trade log error: {ex.Message}");
        }
    }

    /// <summary>
    /// Log a daily summary
    /// </summary>
    public static void LogDailySummary(PerformanceMetrics metrics)
    {
        var summaryLog = Path.Combine(_logDirectory, "daily_summaries.log");

        try
        {
            lock (_lock)
            {
                var date = DateTime.Today.ToString("yyyy-MM-dd");
                var logLine = $"[{date}] " +
                    $"Trades={metrics.TodayTrades} " +
                    $"PnL=${metrics.TodayPnL:F2} " +
                    $"TotalTrades={metrics.TotalTrades} " +
                    $"TotalPnL=${metrics.TotalNetPnL:F2} " +
                    $"WinRate={metrics.WinRate:F1}% " +
                    $"PF={metrics.ProfitFactor:F2}";

                File.AppendAllText(summaryLog, logLine + Environment.NewLine);

                Info($"Daily summary logged: {metrics.TodayTrades} trades, ${metrics.TodayPnL:F2} PnL");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Summary log error: {ex.Message}");
        }
    }

    public static void Info(string message) => Log(message, LogLevel.Info);
    public static void Warn(string message) => Log(message, LogLevel.Warn);
    public static void Error(string message) => Log(message, LogLevel.Error);
    public static void Debug(string message) => Log(message, LogLevel.Debug);

    public static void Error(string message, Exception ex)
    {
        Log($"{message}: {ex.Message}", LogLevel.Error);
        Log($"Stack trace: {ex.StackTrace}", LogLevel.Error);

        if (ex.InnerException != null)
        {
            Log($"Inner exception: {ex.InnerException.Message}", LogLevel.Error);
            Log($"Inner stack trace: {ex.InnerException.StackTrace}", LogLevel.Error);
        }
    }

    public static string GetLogFilePath() => _currentLogFile;
    public static string GetLogDirectory() => _logDirectory;

    /// <summary>
    /// Get the log file path for a specific strategy
    /// </summary>
    public static string GetStrategyLogPath(string strategy)
    {
        if (_strategyLogFiles.TryGetValue(strategy, out var path))
        {
            return path;
        }
        var strategyDir = Path.Combine(_logDirectory, "strategies");
        return Path.Combine(strategyDir, $"{strategy}_{DateTime.Today:yyyy-MM-dd}.log");
    }

    /// <summary>
    /// Get all log files for today
    /// </summary>
    public static List<string> GetTodayLogFiles()
    {
        var files = new List<string>();
        var pattern = $"*_{DateTime.Today:yyyy-MM-dd}.log";

        try
        {
            files.AddRange(Directory.GetFiles(_logDirectory, pattern));

            var strategyDir = Path.Combine(_logDirectory, "strategies");
            if (Directory.Exists(strategyDir))
            {
                files.AddRange(Directory.GetFiles(strategyDir, pattern));
            }
        }
        catch
        {
            // Ignore errors
        }

        return files;
    }

    /// <summary>
    /// Force cleanup of old logs (can be called manually)
    /// </summary>
    public static void ForceCleanup()
    {
        CleanupOldLogs();
    }
}

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error
}
