/*
 * Alert Manager for GC Trading Application
 * Handles email notifications for trades, alerts, and daily summaries
 */

using System.Net;
using System.Net.Mail;

namespace GCTradingApp;

/// <summary>
/// Alert settings for notifications
/// </summary>
public class AlertSettings
{
    // Email settings
    public bool EmailEnabled { get; set; } = false;
    public string SmtpHost { get; set; } = "smtp.gmail.com";
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseSsl { get; set; } = true;
    public string SmtpUsername { get; set; } = "";
    public string SmtpPassword { get; set; } = "";
    public string FromEmail { get; set; } = "";
    public string ToEmail { get; set; } = "";

    // Alert preferences
    public bool AlertOnTrade { get; set; } = true;
    public bool AlertOnStrategyPause { get; set; } = true;
    public bool AlertOnRiskLimit { get; set; } = true;
    public bool AlertOnConnection { get; set; } = true;
    public bool DailySummaryEnabled { get; set; } = true;
    public TimeSpan DailySummaryTime { get; set; } = new TimeSpan(17, 0, 0); // 5 PM
}

/// <summary>
/// Alert types for categorization
/// </summary>
public enum AlertType
{
    Trade,
    StrategyPause,
    RiskLimit,
    Connection,
    DailySummary,
    Error,
    Warning
}

/// <summary>
/// Alert record for history
/// </summary>
public class AlertRecord
{
    public DateTime Time { get; set; }
    public AlertType Type { get; set; }
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public bool EmailSent { get; set; }
}

/// <summary>
/// Manages alerts and email notifications
/// </summary>
public class AlertManager : IDisposable
{
    private readonly object _lock = new();
    private readonly List<AlertRecord> _alertHistory = new();
    private readonly AlertSettings _settings;
    private readonly System.Timers.Timer? _dailySummaryTimer;
    private SmtpClient? _smtpClient;
    private bool _isActive = false;
    private DateTime _lastDailySummary = DateTime.MinValue;

    // Events
    public event Action<AlertRecord>? OnAlert;
    public event Action<string>? OnLog;

    public AlertSettings Settings => _settings;

    public AlertManager(AlertSettings? settings = null)
    {
        _settings = settings ?? new AlertSettings();

        if (_settings.DailySummaryEnabled)
        {
            _dailySummaryTimer = new System.Timers.Timer(60000); // Check every minute
            _dailySummaryTimer.Elapsed += CheckDailySummary;
        }
    }

    /// <summary>
    /// Start the alert manager
    /// </summary>
    public void Start()
    {
        _isActive = true;

        if (_settings.EmailEnabled)
        {
            InitializeSmtpClient();
        }

        _dailySummaryTimer?.Start();
        Log("Alert manager started");
    }

    /// <summary>
    /// Stop the alert manager
    /// </summary>
    public void Stop()
    {
        _isActive = false;
        _dailySummaryTimer?.Stop();
        _smtpClient?.Dispose();
        _smtpClient = null;
        Log("Alert manager stopped");
    }

    /// <summary>
    /// Initialize SMTP client
    /// </summary>
    private void InitializeSmtpClient()
    {
        try
        {
            _smtpClient = new SmtpClient(_settings.SmtpHost, _settings.SmtpPort)
            {
                EnableSsl = _settings.SmtpUseSsl,
                Credentials = new NetworkCredential(_settings.SmtpUsername, _settings.SmtpPassword),
                Timeout = 10000
            };
            Log("SMTP client initialized");
        }
        catch (Exception ex)
        {
            Log($"Failed to initialize SMTP client: {ex.Message}");
        }
    }

    /// <summary>
    /// Send a trade alert
    /// </summary>
    public void AlertTrade(string strategy, string action, decimal quantity, double price, double pnl = 0)
    {
        if (!_settings.AlertOnTrade)
            return;

        var title = $"Trade Executed: {strategy}";
        var message = $"Strategy: {strategy}\n" +
                     $"Action: {action}\n" +
                     $"Quantity: {quantity}\n" +
                     $"Price: ${price:F2}";

        if (pnl != 0)
        {
            message += $"\nRealized PnL: ${pnl:F2}";
        }

        SendAlert(AlertType.Trade, title, message);
    }

    /// <summary>
    /// Send a strategy pause alert
    /// </summary>
    public void AlertStrategyPause(string strategy, string reason)
    {
        if (!_settings.AlertOnStrategyPause)
            return;

        var title = $"Strategy Paused: {strategy}";
        var message = $"Strategy {strategy} has been paused.\n\nReason: {reason}";

        SendAlert(AlertType.StrategyPause, title, message);
    }

    /// <summary>
    /// Send a risk limit alert
    /// </summary>
    public void AlertRiskLimit(string limitType, string details)
    {
        if (!_settings.AlertOnRiskLimit)
            return;

        var title = $"Risk Limit Hit: {limitType}";
        var message = $"A risk limit has been reached.\n\n" +
                     $"Limit Type: {limitType}\n" +
                     $"Details: {details}";

        SendAlert(AlertType.RiskLimit, title, message);
    }

    /// <summary>
    /// Send a connection status alert
    /// </summary>
    public void AlertConnection(string status, string details = "")
    {
        if (!_settings.AlertOnConnection)
            return;

        var title = $"Connection: {status}";
        var message = $"Connection status changed to: {status}";
        if (!string.IsNullOrEmpty(details))
        {
            message += $"\n\nDetails: {details}";
        }

        SendAlert(AlertType.Connection, title, message);
    }

    /// <summary>
    /// Send a warning alert
    /// </summary>
    public void AlertWarning(string warning)
    {
        var title = "Trading Warning";
        SendAlert(AlertType.Warning, title, warning);
    }

    /// <summary>
    /// Send an error alert
    /// </summary>
    public void AlertError(string error, Exception? ex = null)
    {
        var title = "Trading Error";
        var message = error;
        if (ex != null)
        {
            message += $"\n\nException: {ex.Message}\n{ex.StackTrace}";
        }

        SendAlert(AlertType.Error, title, message);
    }

    /// <summary>
    /// Send a daily summary
    /// </summary>
    public void SendDailySummary(PerformanceMetrics metrics, RiskState? riskState = null)
    {
        var title = $"Daily Trading Summary - {DateTime.Today:yyyy-MM-dd}";

        var message = $@"=== DAILY TRADING SUMMARY ===
Date: {DateTime.Today:yyyy-MM-dd}

TODAY'S PERFORMANCE:
Trades: {metrics.TodayTrades}
PnL: ${metrics.TodayPnL:F2}

OVERALL PERFORMANCE:
Total Trades: {metrics.TotalTrades}
Win Rate: {metrics.WinRate:F1}%
Total PnL: ${metrics.TotalNetPnL:F2}
Profit Factor: {metrics.ProfitFactor:F2}
Sharpe Ratio: {metrics.SharpeRatio:F2}
Max Drawdown: ${metrics.MaxDrawdown:F2} ({metrics.MaxDrawdownPct:F1}%)

STREAKS:
Current Streak: {metrics.CurrentStreak}
Max Win Streak: {metrics.MaxWinStreak}
Max Lose Streak: {metrics.MaxLoseStreak}
";

        if (riskState != null)
        {
            message += $@"
RISK STATUS:
Daily PnL: ${riskState.DailyPnL:F2}
Trading Paused: {riskState.TradingPaused}
Trades Today: {riskState.TradesExecutedToday}
Total Position: {riskState.TotalPositionSize}
";
        }

        message += "\n==============================";

        SendAlert(AlertType.DailySummary, title, message);
        _lastDailySummary = DateTime.Today;

        // Also log to file
        Logger.LogDailySummary(metrics);
    }

    /// <summary>
    /// Check if it's time to send daily summary
    /// </summary>
    private void CheckDailySummary(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (!_settings.DailySummaryEnabled)
            return;

        var now = DateTime.Now;
        var summaryTime = DateTime.Today.Add(_settings.DailySummaryTime);

        // Check if we should send summary (within 1 minute of scheduled time, and not sent today)
        if (now >= summaryTime && now < summaryTime.AddMinutes(1) && _lastDailySummary.Date != DateTime.Today)
        {
            // Request daily summary from main form
            OnLog?.Invoke("Daily summary time reached - requesting summary");
        }
    }

    /// <summary>
    /// Core alert sending method
    /// </summary>
    private void SendAlert(AlertType type, string title, string message)
    {
        var alert = new AlertRecord
        {
            Time = DateTime.Now,
            Type = type,
            Title = title,
            Message = message,
            EmailSent = false
        };

        lock (_lock)
        {
            _alertHistory.Add(alert);

            // Keep only last 1000 alerts
            if (_alertHistory.Count > 1000)
            {
                _alertHistory.RemoveRange(0, _alertHistory.Count - 1000);
            }
        }

        // Log the alert
        Log($"Alert [{type}]: {title}");

        // Send email if enabled
        if (_settings.EmailEnabled && _smtpClient != null && _isActive)
        {
            Task.Run(() => SendEmailAsync(alert));
        }

        // Raise event
        OnAlert?.Invoke(alert);
    }

    /// <summary>
    /// Send email asynchronously
    /// </summary>
    private async Task SendEmailAsync(AlertRecord alert)
    {
        try
        {
            if (_smtpClient == null || string.IsNullOrEmpty(_settings.FromEmail) || string.IsNullOrEmpty(_settings.ToEmail))
            {
                return;
            }

            var mailMessage = new MailMessage
            {
                From = new MailAddress(_settings.FromEmail, "GC Trading App"),
                Subject = $"[GC Trading] {alert.Title}",
                Body = $"Time: {alert.Time:yyyy-MM-dd HH:mm:ss}\n\n{alert.Message}",
                IsBodyHtml = false
            };
            mailMessage.To.Add(_settings.ToEmail);

            await _smtpClient.SendMailAsync(mailMessage);

            lock (_lock)
            {
                alert.EmailSent = true;
            }

            Log($"Email sent: {alert.Title}");
        }
        catch (Exception ex)
        {
            Log($"Failed to send email: {ex.Message}");
        }
    }

    /// <summary>
    /// Get alert history
    /// </summary>
    public List<AlertRecord> GetAlertHistory(int count = 50)
    {
        lock (_lock)
        {
            return _alertHistory.OrderByDescending(a => a.Time).Take(count).ToList();
        }
    }

    /// <summary>
    /// Get alerts by type
    /// </summary>
    public List<AlertRecord> GetAlertsByType(AlertType type, int count = 50)
    {
        lock (_lock)
        {
            return _alertHistory
                .Where(a => a.Type == type)
                .OrderByDescending(a => a.Time)
                .Take(count)
                .ToList();
        }
    }

    /// <summary>
    /// Get today's alerts
    /// </summary>
    public List<AlertRecord> GetTodayAlerts()
    {
        lock (_lock)
        {
            return _alertHistory
                .Where(a => a.Time.Date == DateTime.Today)
                .OrderByDescending(a => a.Time)
                .ToList();
        }
    }

    /// <summary>
    /// Test email configuration
    /// </summary>
    public async Task<bool> TestEmailAsync()
    {
        try
        {
            if (!_settings.EmailEnabled)
            {
                Log("Email not enabled");
                return false;
            }

            InitializeSmtpClient();

            if (_smtpClient == null)
            {
                Log("SMTP client not initialized");
                return false;
            }

            var mailMessage = new MailMessage
            {
                From = new MailAddress(_settings.FromEmail, "GC Trading App"),
                Subject = "[GC Trading] Test Email",
                Body = $"This is a test email from GC Trading App.\n\nTime: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                IsBodyHtml = false
            };
            mailMessage.To.Add(_settings.ToEmail);

            await _smtpClient.SendMailAsync(mailMessage);
            Log("Test email sent successfully");
            return true;
        }
        catch (Exception ex)
        {
            Log($"Test email failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Update settings
    /// </summary>
    public void UpdateSettings(AlertSettings newSettings)
    {
        lock (_lock)
        {
            _settings.EmailEnabled = newSettings.EmailEnabled;
            _settings.SmtpHost = newSettings.SmtpHost;
            _settings.SmtpPort = newSettings.SmtpPort;
            _settings.SmtpUseSsl = newSettings.SmtpUseSsl;
            _settings.SmtpUsername = newSettings.SmtpUsername;
            _settings.SmtpPassword = newSettings.SmtpPassword;
            _settings.FromEmail = newSettings.FromEmail;
            _settings.ToEmail = newSettings.ToEmail;
            _settings.AlertOnTrade = newSettings.AlertOnTrade;
            _settings.AlertOnStrategyPause = newSettings.AlertOnStrategyPause;
            _settings.AlertOnRiskLimit = newSettings.AlertOnRiskLimit;
            _settings.AlertOnConnection = newSettings.AlertOnConnection;
            _settings.DailySummaryEnabled = newSettings.DailySummaryEnabled;
            _settings.DailySummaryTime = newSettings.DailySummaryTime;
        }

        // Reinitialize SMTP client if email settings changed
        if (_settings.EmailEnabled && _isActive)
        {
            _smtpClient?.Dispose();
            InitializeSmtpClient();
        }

        Log("Alert settings updated");
    }

    /// <summary>
    /// Clear alert history
    /// </summary>
    public void ClearHistory()
    {
        lock (_lock)
        {
            _alertHistory.Clear();
            Log("Alert history cleared");
        }
    }

    private void Log(string message)
    {
        Logger.Info($"[AlertManager] {message}");
        OnLog?.Invoke(message);
    }

    public void Dispose()
    {
        Stop();
        _dailySummaryTimer?.Dispose();
    }
}
