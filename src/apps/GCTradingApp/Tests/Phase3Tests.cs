/*
 * Phase 3 Tests - Monitoring & Alerts
 * Tests for PerformanceTracker, Logger, and AlertManager
 */

using Xunit;

namespace GCTradingApp.Tests;

/// <summary>
/// Tests for CompletedTrade calculations
/// </summary>
public class CompletedTradeTests
{
    [Fact]
    public void CompletedTrade_CalculatesNetPnL()
    {
        var trade = new CompletedTrade
        {
            GrossPnL = 500.0,
            Commission = 4.50
        };

        Assert.Equal(495.50, trade.NetPnL);
    }

    [Fact]
    public void CompletedTrade_IsWinner_WhenProfitable()
    {
        var trade = new CompletedTrade
        {
            GrossPnL = 100.0,
            Commission = 5.0
        };

        Assert.True(trade.IsWinner);
    }

    [Fact]
    public void CompletedTrade_IsNotWinner_WhenUnprofitable()
    {
        var trade = new CompletedTrade
        {
            GrossPnL = 5.0,
            Commission = 10.0
        };

        Assert.False(trade.IsWinner);
    }

    [Fact]
    public void CompletedTrade_CalculatesReturnPct()
    {
        var trade = new CompletedTrade
        {
            EntryPrice = 2000.0,
            ExitPrice = 2100.0
        };

        Assert.Equal(5.0, trade.ReturnPct);
    }

    [Fact]
    public void CompletedTrade_CalculatesDuration()
    {
        var trade = new CompletedTrade
        {
            EntryTime = new DateTime(2025, 1, 1, 10, 0, 0),
            ExitTime = new DateTime(2025, 1, 1, 12, 30, 0)
        };

        Assert.Equal(TimeSpan.FromHours(2.5), trade.Duration);
    }
}

/// <summary>
/// Tests for PerformanceMetrics defaults
/// </summary>
public class PerformanceMetricsTests
{
    [Fact]
    public void PerformanceMetrics_DefaultsToZero()
    {
        var metrics = new PerformanceMetrics();

        Assert.Equal(0, metrics.TotalTrades);
        Assert.Equal(0, metrics.WinRate);
        Assert.Equal(0, metrics.TotalNetPnL);
        Assert.Equal(0, metrics.ProfitFactor);
        Assert.Equal(0, metrics.SharpeRatio);
    }

    [Fact]
    public void PerformanceMetrics_WinRateCalculation()
    {
        var metrics = new PerformanceMetrics
        {
            TotalTrades = 10,
            WinningTrades = 6,
            LosingTrades = 4
        };

        Assert.Equal(60.0, metrics.WinRate);
    }

    [Fact]
    public void PerformanceMetrics_PayoffRatioCalculation()
    {
        var metrics = new PerformanceMetrics
        {
            AverageWin = 200.0,
            AverageLoss = -100.0
        };

        Assert.Equal(2.0, metrics.PayoffRatio);
    }

    [Fact]
    public void PerformanceMetrics_PayoffRatioZeroLoss()
    {
        var metrics = new PerformanceMetrics
        {
            AverageWin = 200.0,
            AverageLoss = 0
        };

        Assert.Equal(0, metrics.PayoffRatio);
    }
}

/// <summary>
/// Tests for PerformanceTracker
/// </summary>
public class PerformanceTrackerTests
{
    [Fact]
    public void PerformanceTracker_StartsEmpty()
    {
        using var tracker = new PerformanceTracker();
        var metrics = tracker.GetMetrics();

        Assert.Equal(0, metrics.TotalTrades);
        Assert.Equal(0, metrics.TotalNetPnL);
    }

    [Fact]
    public void PerformanceTracker_RecordsTrade()
    {
        using var tracker = new PerformanceTracker();

        tracker.RecordTrade(new CompletedTrade
        {
            Strategy = "Aggressive",
            EntryTime = DateTime.Now.AddHours(-1),
            ExitTime = DateTime.Now,
            EntryPrice = 2000,
            ExitPrice = 2050,
            Quantity = 1,
            GrossPnL = 5000, // $50 * 100 oz
            Commission = 10
        });

        var metrics = tracker.GetMetrics();

        Assert.Equal(1, metrics.TotalTrades);
        Assert.Equal(4990, metrics.TotalNetPnL);
    }

    [Fact]
    public void PerformanceTracker_TracksMultipleTrades()
    {
        using var tracker = new PerformanceTracker();

        tracker.RecordTrade(new CompletedTrade
        {
            Strategy = "Aggressive",
            GrossPnL = 500,
            Commission = 5
        });

        tracker.RecordTrade(new CompletedTrade
        {
            Strategy = "Conservative",
            GrossPnL = -200,
            Commission = 5
        });

        tracker.RecordTrade(new CompletedTrade
        {
            Strategy = "Aggressive",
            GrossPnL = 300,
            Commission = 5
        });

        var metrics = tracker.GetMetrics();

        Assert.Equal(3, metrics.TotalTrades);
        Assert.Equal(2, metrics.WinningTrades);
        Assert.Equal(1, metrics.LosingTrades);
    }

    [Fact]
    public void PerformanceTracker_CalculatesWinRate()
    {
        using var tracker = new PerformanceTracker();

        // 3 wins, 2 losses = 60% win rate
        tracker.RecordTrade(new CompletedTrade { GrossPnL = 100, Commission = 0 });
        tracker.RecordTrade(new CompletedTrade { GrossPnL = 100, Commission = 0 });
        tracker.RecordTrade(new CompletedTrade { GrossPnL = 100, Commission = 0 });
        tracker.RecordTrade(new CompletedTrade { GrossPnL = -50, Commission = 0 });
        tracker.RecordTrade(new CompletedTrade { GrossPnL = -50, Commission = 0 });

        var metrics = tracker.GetMetrics();

        Assert.Equal(60.0, metrics.WinRate);
    }

    [Fact]
    public void PerformanceTracker_CalculatesProfitFactor()
    {
        using var tracker = new PerformanceTracker();

        // $300 in wins, $100 in losses = 3.0 profit factor
        tracker.RecordTrade(new CompletedTrade { GrossPnL = 150, Commission = 0 });
        tracker.RecordTrade(new CompletedTrade { GrossPnL = 150, Commission = 0 });
        tracker.RecordTrade(new CompletedTrade { GrossPnL = -100, Commission = 0 });

        var metrics = tracker.GetMetrics();

        Assert.Equal(3.0, metrics.ProfitFactor);
    }

    [Fact]
    public void PerformanceTracker_CalculatesAverages()
    {
        using var tracker = new PerformanceTracker();

        tracker.RecordTrade(new CompletedTrade { GrossPnL = 100, Commission = 0 });
        tracker.RecordTrade(new CompletedTrade { GrossPnL = 200, Commission = 0 });
        tracker.RecordTrade(new CompletedTrade { GrossPnL = -50, Commission = 0 });

        var metrics = tracker.GetMetrics();

        Assert.Equal(150, metrics.AverageWin);
        Assert.Equal(-50, metrics.AverageLoss);
    }

    [Fact]
    public void PerformanceTracker_TracksStreaks()
    {
        using var tracker = new PerformanceTracker();

        // Win streak of 3
        tracker.RecordTrade(new CompletedTrade { GrossPnL = 100, Commission = 0 });
        tracker.RecordTrade(new CompletedTrade { GrossPnL = 100, Commission = 0 });
        tracker.RecordTrade(new CompletedTrade { GrossPnL = 100, Commission = 0 });

        var metrics = tracker.GetMetrics();

        Assert.Equal(3, metrics.CurrentStreak);
        Assert.Equal(3, metrics.MaxWinStreak);
    }

    [Fact]
    public void PerformanceTracker_TracksLossStreaks()
    {
        using var tracker = new PerformanceTracker();

        tracker.RecordTrade(new CompletedTrade { GrossPnL = 100, Commission = 0 });
        tracker.RecordTrade(new CompletedTrade { GrossPnL = -50, Commission = 0 });
        tracker.RecordTrade(new CompletedTrade { GrossPnL = -50, Commission = 0 });

        var metrics = tracker.GetMetrics();

        Assert.Equal(-2, metrics.CurrentStreak);
        Assert.Equal(2, metrics.MaxLoseStreak);
    }

    [Fact]
    public void PerformanceTracker_GetMetricsByStrategy()
    {
        using var tracker = new PerformanceTracker();

        tracker.RecordTrade(new CompletedTrade { Strategy = "Aggressive", GrossPnL = 100, Commission = 0 });
        tracker.RecordTrade(new CompletedTrade { Strategy = "Aggressive", GrossPnL = 100, Commission = 0 });
        tracker.RecordTrade(new CompletedTrade { Strategy = "Conservative", GrossPnL = 50, Commission = 0 });

        var aggMetrics = tracker.GetMetrics("Aggressive");
        var consMetrics = tracker.GetMetrics("Conservative");

        Assert.Equal(2, aggMetrics.TotalTrades);
        Assert.Equal(200, aggMetrics.TotalNetPnL);
        Assert.Equal(1, consMetrics.TotalTrades);
        Assert.Equal(50, consMetrics.TotalNetPnL);
    }

    [Fact]
    public void PerformanceTracker_GetRecentTrades()
    {
        using var tracker = new PerformanceTracker();

        for (int i = 0; i < 15; i++)
        {
            tracker.RecordTrade(new CompletedTrade
            {
                Strategy = "Test",
                ExitTime = DateTime.Now.AddMinutes(i),
                GrossPnL = i * 10,
                Commission = 0
            });
        }

        var recent = tracker.GetRecentTrades(5);

        Assert.Equal(5, recent.Count);
        // Most recent should be first
        Assert.True(recent[0].ExitTime > recent[4].ExitTime);
    }

    [Fact]
    public void PerformanceTracker_FiresTradeCompletedEvent()
    {
        using var tracker = new PerformanceTracker();

        CompletedTrade? completedTrade = null;
        tracker.OnTradeCompleted += trade => completedTrade = trade;

        tracker.RecordTrade(new CompletedTrade { Strategy = "Test", GrossPnL = 100, Commission = 0 });

        Assert.NotNull(completedTrade);
        Assert.Equal("Test", completedTrade!.Strategy);
    }

    [Fact]
    public void PerformanceTracker_FiresMetricsUpdatedEvent()
    {
        using var tracker = new PerformanceTracker();

        PerformanceMetrics? updatedMetrics = null;
        tracker.OnMetricsUpdated += metrics => updatedMetrics = metrics;

        tracker.RecordTrade(new CompletedTrade { GrossPnL = 100, Commission = 0 });

        Assert.NotNull(updatedMetrics);
        Assert.Equal(1, updatedMetrics!.TotalTrades);
    }

    [Fact]
    public void PerformanceTracker_UpdatesEquity()
    {
        using var tracker = new PerformanceTracker(null, 10000);

        tracker.UpdateEquity(11000);
        var curve = tracker.GetEquityCurve();

        Assert.Single(curve);
        Assert.Equal(11000, curve[0].Equity);
    }

    [Fact]
    public void PerformanceTracker_TracksDrawdown()
    {
        using var tracker = new PerformanceTracker(null, 10000);

        tracker.RecordTrade(new CompletedTrade
        {
            ExitTime = DateTime.Now,
            GrossPnL = 1000,
            Commission = 0
        });

        tracker.RecordTrade(new CompletedTrade
        {
            ExitTime = DateTime.Now.AddMinutes(1),
            GrossPnL = -500,
            Commission = 0
        });

        var curve = tracker.GetEquityCurve();
        var lastPoint = curve.Last();

        // Peak was 11000, now 10500
        Assert.Equal(500, lastPoint.Drawdown);
    }

    [Fact]
    public void PerformanceTracker_Clear_ResetsAll()
    {
        using var tracker = new PerformanceTracker();

        tracker.RecordTrade(new CompletedTrade { GrossPnL = 100, Commission = 0 });
        tracker.RecordTrade(new CompletedTrade { GrossPnL = 100, Commission = 0 });

        tracker.Clear();

        var metrics = tracker.GetMetrics();

        Assert.Equal(0, metrics.TotalTrades);
        Assert.Equal(0, tracker.GetTrades().Count);
    }

    [Fact]
    public void PerformanceTracker_GetSummary_ReturnsFormattedString()
    {
        using var tracker = new PerformanceTracker();

        tracker.RecordTrade(new CompletedTrade
        {
            Strategy = "Test",
            ExitTime = DateTime.Today,
            GrossPnL = 100,
            Commission = 0
        });

        var summary = tracker.GetSummary();

        Assert.Contains("PERFORMANCE SUMMARY", summary);
        Assert.Contains("Trades: 1", summary);
        Assert.Contains("Win Rate: 100.0%", summary);
    }
}

/// <summary>
/// Tests for PerformanceSettings
/// </summary>
public class PerformanceSettingsTests
{
    [Fact]
    public void PerformanceSettings_DefaultValues()
    {
        var settings = new PerformanceSettings();

        Assert.Equal(0.05, settings.RiskFreeRate);
        Assert.Equal(252, settings.SharpeAnnualizationFactor);
    }
}

/// <summary>
/// Tests for AlertSettings
/// </summary>
public class AlertSettingsTests
{
    [Fact]
    public void AlertSettings_DefaultValues()
    {
        var settings = new AlertSettings();

        Assert.False(settings.EmailEnabled);
        Assert.Equal("smtp.gmail.com", settings.SmtpHost);
        Assert.Equal(587, settings.SmtpPort);
        Assert.True(settings.SmtpUseSsl);
        Assert.True(settings.AlertOnTrade);
        Assert.True(settings.AlertOnStrategyPause);
        Assert.True(settings.AlertOnRiskLimit);
        Assert.True(settings.DailySummaryEnabled);
    }
}

/// <summary>
/// Tests for AlertRecord
/// </summary>
public class AlertRecordTests
{
    [Fact]
    public void AlertRecord_DefaultValues()
    {
        var alert = new AlertRecord();

        Assert.Equal("", alert.Title);
        Assert.Equal("", alert.Message);
        Assert.False(alert.EmailSent);
    }

    [Fact]
    public void AlertRecord_CanBePopulated()
    {
        var alert = new AlertRecord
        {
            Time = DateTime.Now,
            Type = AlertType.Trade,
            Title = "Trade Executed",
            Message = "BUY 1 @ 2000.00",
            EmailSent = true
        };

        Assert.Equal(AlertType.Trade, alert.Type);
        Assert.Equal("Trade Executed", alert.Title);
        Assert.True(alert.EmailSent);
    }
}

/// <summary>
/// Tests for AlertManager
/// </summary>
public class AlertManagerTests
{
    [Fact]
    public void AlertManager_StartsWithNoAlerts()
    {
        using var manager = new AlertManager();

        var history = manager.GetAlertHistory();

        Assert.Empty(history);
    }

    [Fact]
    public void AlertManager_RecordsTradeAlert()
    {
        using var manager = new AlertManager();

        manager.AlertTrade("Aggressive", "BUY", 1, 2000.00);

        var history = manager.GetAlertHistory();

        Assert.Single(history);
        Assert.Equal(AlertType.Trade, history[0].Type);
        Assert.Contains("Aggressive", history[0].Title);
    }

    [Fact]
    public void AlertManager_RecordsRiskLimitAlert()
    {
        using var manager = new AlertManager();

        manager.AlertRiskLimit("Daily Loss", "Loss limit hit: $500");

        var history = manager.GetAlertHistory();

        Assert.Single(history);
        Assert.Equal(AlertType.RiskLimit, history[0].Type);
    }

    [Fact]
    public void AlertManager_RecordsWarningAlert()
    {
        using var manager = new AlertManager();

        manager.AlertWarning("70% of daily loss limit used");

        var history = manager.GetAlertHistory();

        Assert.Single(history);
        Assert.Equal(AlertType.Warning, history[0].Type);
    }

    [Fact]
    public void AlertManager_RecordsConnectionAlert()
    {
        using var manager = new AlertManager();

        manager.AlertConnection("Connected", "Connected to TWS");

        var history = manager.GetAlertHistory();

        Assert.Single(history);
        Assert.Equal(AlertType.Connection, history[0].Type);
    }

    [Fact]
    public void AlertManager_FiresOnAlertEvent()
    {
        using var manager = new AlertManager();

        AlertRecord? receivedAlert = null;
        manager.OnAlert += alert => receivedAlert = alert;

        manager.AlertTrade("Test", "BUY", 1, 2000.00);

        Assert.NotNull(receivedAlert);
        Assert.Equal(AlertType.Trade, receivedAlert!.Type);
    }

    [Fact]
    public void AlertManager_GetAlertsByType()
    {
        using var manager = new AlertManager();

        manager.AlertTrade("Aggressive", "BUY", 1, 2000.00);
        manager.AlertWarning("Test warning");
        manager.AlertTrade("Conservative", "SELL", 1, 2050.00);

        var tradeAlerts = manager.GetAlertsByType(AlertType.Trade);

        Assert.Equal(2, tradeAlerts.Count);
    }

    [Fact]
    public void AlertManager_GetTodayAlerts()
    {
        using var manager = new AlertManager();

        manager.AlertTrade("Test", "BUY", 1, 2000.00);

        var todayAlerts = manager.GetTodayAlerts();

        Assert.Single(todayAlerts);
        Assert.Equal(DateTime.Today, todayAlerts[0].Time.Date);
    }

    [Fact]
    public void AlertManager_ClearHistory()
    {
        using var manager = new AlertManager();

        manager.AlertTrade("Test", "BUY", 1, 2000.00);
        manager.AlertTrade("Test", "SELL", 1, 2050.00);

        manager.ClearHistory();

        Assert.Empty(manager.GetAlertHistory());
    }

    [Fact]
    public void AlertManager_RespectsAlertSettings_Trade()
    {
        var settings = new AlertSettings { AlertOnTrade = false };
        using var manager = new AlertManager(settings);

        manager.AlertTrade("Test", "BUY", 1, 2000.00);

        Assert.Empty(manager.GetAlertHistory());
    }

    [Fact]
    public void AlertManager_RespectsAlertSettings_RiskLimit()
    {
        var settings = new AlertSettings { AlertOnRiskLimit = false };
        using var manager = new AlertManager(settings);

        manager.AlertRiskLimit("Daily Loss", "Test");

        Assert.Empty(manager.GetAlertHistory());
    }

    [Fact]
    public void AlertManager_LimitsHistorySize()
    {
        using var manager = new AlertManager();

        // Add more than 1000 alerts
        for (int i = 0; i < 1100; i++)
        {
            manager.AlertWarning($"Warning {i}");
        }

        var history = manager.GetAlertHistory(2000); // Request all

        // Should be trimmed to 1000
        Assert.True(history.Count <= 1000);
    }

    [Fact]
    public void AlertManager_StartStop()
    {
        using var manager = new AlertManager();

        manager.Start();
        manager.Stop();

        // Should not throw
    }
}

/// <summary>
/// Tests for Logger (basic functionality)
/// </summary>
public class LoggerTests
{
    [Fact]
    public void Logger_GetLogDirectory_ReturnsPath()
    {
        var dir = Logger.GetLogDirectory();

        Assert.False(string.IsNullOrEmpty(dir));
        Assert.Contains("logs", dir);
    }

    [Fact]
    public void Logger_GetLogFilePath_ReturnsPath()
    {
        var path = Logger.GetLogFilePath();

        Assert.False(string.IsNullOrEmpty(path));
        Assert.Contains(".log", path);
    }

    [Fact]
    public void Logger_GetStrategyLogPath_ReturnsPath()
    {
        var path = Logger.GetStrategyLogPath("TestStrategy");

        Assert.Contains("TestStrategy", path);
        Assert.Contains("strategies", path);
    }

    [Fact]
    public void Logger_SetRetentionDays_AcceptsPositiveValue()
    {
        Logger.SetRetentionDays(14);

        // Should not throw - no way to verify without reading private field
    }

    [Fact]
    public void Logger_SetRetentionDays_MinimumIsOne()
    {
        Logger.SetRetentionDays(0);

        // Should set to 1 (minimum), should not throw
    }
}

/// <summary>
/// Tests for EquityPoint
/// </summary>
public class EquityPointTests
{
    [Fact]
    public void EquityPoint_DefaultValues()
    {
        var point = new EquityPoint();

        Assert.Equal(0, point.Equity);
        Assert.Equal(0, point.Drawdown);
        Assert.Equal(0, point.DrawdownPct);
    }

    [Fact]
    public void EquityPoint_CanBePopulated()
    {
        var point = new EquityPoint
        {
            Time = DateTime.Now,
            Equity = 10000,
            Drawdown = 500,
            DrawdownPct = 5.0
        };

        Assert.Equal(10000, point.Equity);
        Assert.Equal(500, point.Drawdown);
        Assert.Equal(5.0, point.DrawdownPct);
    }
}

/// <summary>
/// Integration tests for Phase 3 components
/// </summary>
public class Phase3IntegrationTests
{
    [Fact]
    public void PerformanceTracker_IntegrationWithAlertManager()
    {
        using var tracker = new PerformanceTracker();
        using var alertManager = new AlertManager();

        CompletedTrade? completedTrade = null;
        tracker.OnTradeCompleted += trade =>
        {
            completedTrade = trade;
            // Simulate what MainForm does
            alertManager.AlertTrade(trade.Strategy,
                trade.IsWinner ? "CLOSED (WIN)" : "CLOSED (LOSS)",
                trade.Quantity, trade.ExitPrice, trade.NetPnL);
        };

        tracker.RecordTrade(new CompletedTrade
        {
            Strategy = "Aggressive",
            EntryPrice = 2000,
            ExitPrice = 2050,
            Quantity = 1,
            GrossPnL = 5000,
            Commission = 10
        });

        var alerts = alertManager.GetAlertHistory();

        Assert.Single(alerts);
        Assert.Equal(AlertType.Trade, alerts[0].Type);
        Assert.Contains("Aggressive", alerts[0].Title);
        // Message contains the action which includes WIN
        Assert.Contains("WIN", alerts[0].Message);
    }

    [Fact]
    public void MultipleStrategies_TrackedSeparately()
    {
        using var tracker = new PerformanceTracker();

        // Aggressive strategy - 2 wins
        tracker.RecordTrade(new CompletedTrade { Strategy = "Aggressive", GrossPnL = 200, Commission = 0 });
        tracker.RecordTrade(new CompletedTrade { Strategy = "Aggressive", GrossPnL = 150, Commission = 0 });

        // Conservative strategy - 1 win, 1 loss
        tracker.RecordTrade(new CompletedTrade { Strategy = "Conservative", GrossPnL = 100, Commission = 0 });
        tracker.RecordTrade(new CompletedTrade { Strategy = "Conservative", GrossPnL = -50, Commission = 0 });

        var aggMetrics = tracker.GetMetrics("Aggressive");
        var consMetrics = tracker.GetMetrics("Conservative");
        var totalMetrics = tracker.GetMetrics();

        // Verify strategy-specific metrics
        Assert.Equal(2, aggMetrics.TotalTrades);
        Assert.Equal(100.0, aggMetrics.WinRate);
        Assert.Equal(350, aggMetrics.TotalNetPnL);

        Assert.Equal(2, consMetrics.TotalTrades);
        Assert.Equal(50.0, consMetrics.WinRate);
        Assert.Equal(50, consMetrics.TotalNetPnL);

        // Verify total
        Assert.Equal(4, totalMetrics.TotalTrades);
        Assert.Equal(400, totalMetrics.TotalNetPnL);
    }

    [Fact]
    public void DailySummary_IncludesAllMetrics()
    {
        using var tracker = new PerformanceTracker();
        using var alertManager = new AlertManager();

        // Record some trades
        tracker.RecordTrade(new CompletedTrade
        {
            Strategy = "Test",
            ExitTime = DateTime.Today,
            GrossPnL = 100,
            Commission = 0
        });

        var metrics = tracker.GetMetrics();
        alertManager.SendDailySummary(metrics, null);

        var summaryAlerts = alertManager.GetAlertsByType(AlertType.DailySummary);

        Assert.Single(summaryAlerts);
        Assert.Contains("Daily Trading Summary", summaryAlerts[0].Title);
        Assert.Contains("TODAY'S PERFORMANCE", summaryAlerts[0].Message);
        Assert.Contains("OVERALL PERFORMANCE", summaryAlerts[0].Message);
    }
}
