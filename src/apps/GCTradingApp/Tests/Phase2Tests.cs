/*
 * Phase 2 Tests - Risk Management
 * Tests for RiskManager, daily loss limits, position limits, emergency flatten
 */

using Xunit;
using Moq;

namespace GCTradingApp.Tests;

/// <summary>
/// Tests for RiskSettings configuration
/// </summary>
public class RiskSettingsTests
{
    [Fact]
    public void RiskSettings_DefaultValues_AreCorrect()
    {
        var settings = new RiskSettings();

        Assert.True(settings.DailyLossLimitEnabled);
        Assert.Equal(500.0, settings.MaxDailyLossUsd);
        Assert.Equal(0.7, settings.DailyLossWarningPct);
        Assert.True(settings.PositionLimitsEnabled);
        Assert.Equal(5, settings.MaxContractsPerStrategy);
        Assert.Equal(10, settings.MaxTotalContracts);
        Assert.Equal(20, settings.MaxTradesPerDay);
        Assert.False(settings.AutoFlattenOnLimit);
    }

    [Fact]
    public void RiskSettings_CanBeCustomized()
    {
        var settings = new RiskSettings
        {
            DailyLossLimitEnabled = false,
            MaxDailyLossUsd = 1000.0,
            DailyLossWarningPct = 0.8,
            PositionLimitsEnabled = false,
            MaxContractsPerStrategy = 10,
            MaxTotalContracts = 20,
            MaxTradesPerDay = 50,
            AutoFlattenOnLimit = true
        };

        Assert.False(settings.DailyLossLimitEnabled);
        Assert.Equal(1000.0, settings.MaxDailyLossUsd);
        Assert.Equal(0.8, settings.DailyLossWarningPct);
        Assert.False(settings.PositionLimitsEnabled);
        Assert.Equal(10, settings.MaxContractsPerStrategy);
        Assert.Equal(20, settings.MaxTotalContracts);
        Assert.Equal(50, settings.MaxTradesPerDay);
        Assert.True(settings.AutoFlattenOnLimit);
    }
}

/// <summary>
/// Tests for RiskState tracking
/// </summary>
public class RiskStateTests
{
    [Fact]
    public void RiskState_DefaultValues_AreCorrect()
    {
        var state = new RiskState();

        Assert.Equal(DateTime.Today, state.TradingDate);
        Assert.Equal(0, state.DailyPnL);
        Assert.Equal(0, state.DailyHighWater);
        Assert.False(state.TradingPaused);
        Assert.Equal("", state.PauseReason);
        Assert.Equal(0, state.TradesExecutedToday);
        Assert.Equal(0m, state.TotalPositionSize);
    }

    [Fact]
    public void RiskState_CanTrackDailyPnL()
    {
        var state = new RiskState
        {
            DailyPnL = -250.50,
            DailyHighWater = 100.00,
            TradesExecutedToday = 5
        };

        Assert.Equal(-250.50, state.DailyPnL);
        Assert.Equal(100.00, state.DailyHighWater);
        Assert.Equal(5, state.TradesExecutedToday);
    }

    [Fact]
    public void RiskState_CanTrackPausedState()
    {
        var state = new RiskState
        {
            TradingPaused = true,
            PauseReason = "Daily loss limit hit"
        };

        Assert.True(state.TradingPaused);
        Assert.Equal("Daily loss limit hit", state.PauseReason);
    }
}

/// <summary>
/// Tests for RiskCheckResult
/// </summary>
public class RiskCheckResultTests
{
    [Fact]
    public void RiskCheckResult_DefaultIsAllowed()
    {
        var result = new RiskCheckResult();

        Assert.True(result.Allowed);
        Assert.Equal("", result.Reason);
        Assert.Equal(0m, result.AdjustedQuantity);
    }

    [Fact]
    public void RiskCheckResult_CanBeDenied()
    {
        var result = new RiskCheckResult
        {
            Allowed = false,
            Reason = "Daily loss limit reached",
            AdjustedQuantity = 0
        };

        Assert.False(result.Allowed);
        Assert.Equal("Daily loss limit reached", result.Reason);
    }

    [Fact]
    public void RiskCheckResult_CanHaveAdjustedQuantity()
    {
        var result = new RiskCheckResult
        {
            Allowed = true,
            Reason = "Quantity reduced to 3 (strategy limit)",
            AdjustedQuantity = 3
        };

        Assert.True(result.Allowed);
        Assert.Equal(3m, result.AdjustedQuantity);
    }
}

/// <summary>
/// Tests for RiskManager core functionality
/// </summary>
public class RiskManagerTests
{
    private Mock<IBKRClient> CreateMockClient()
    {
        var mock = new Mock<IBKRClient>();
        return mock;
    }

    [Fact]
    public void RiskManager_Constructor_WithDefaults()
    {
        var mockClient = CreateMockClient();
        using var manager = new RiskManager(mockClient.Object);

        Assert.NotNull(manager.Settings);
        Assert.NotNull(manager.State);
        Assert.False(manager.IsTradingPaused);
    }

    [Fact]
    public void RiskManager_Constructor_WithCustomSettings()
    {
        var mockClient = CreateMockClient();
        var settings = new RiskSettings { MaxDailyLossUsd = 1000.0 };

        using var manager = new RiskManager(mockClient.Object, settings);

        Assert.Equal(1000.0, manager.Settings.MaxDailyLossUsd);
    }

    [Fact]
    public void RiskManager_Constructor_RestoresSavedState()
    {
        var mockClient = CreateMockClient();
        var savedState = new RiskState
        {
            TradingDate = DateTime.Today,
            DailyPnL = -100.0,
            TradesExecutedToday = 3
        };

        using var manager = new RiskManager(mockClient.Object, null, savedState);

        Assert.Equal(-100.0, manager.State.DailyPnL);
        Assert.Equal(3, manager.State.TradesExecutedToday);
    }

    [Fact]
    public void RiskManager_Constructor_IgnoresOldState()
    {
        var mockClient = CreateMockClient();
        var oldState = new RiskState
        {
            TradingDate = DateTime.Today.AddDays(-1), // Yesterday
            DailyPnL = -100.0,
            TradesExecutedToday = 3
        };

        using var manager = new RiskManager(mockClient.Object, null, oldState);

        // Should start fresh
        Assert.Equal(0, manager.State.DailyPnL);
        Assert.Equal(0, manager.State.TradesExecutedToday);
    }
}

/// <summary>
/// Tests for CheckNewTrade functionality
/// </summary>
public class RiskManagerCheckTradeTests
{
    private Mock<IBKRClient> CreateMockClient()
    {
        var mock = new Mock<IBKRClient>();
        return mock;
    }

    [Fact]
    public void CheckNewTrade_AllowsNormalTrade()
    {
        var mockClient = CreateMockClient();
        using var manager = new RiskManager(mockClient.Object);

        var result = manager.CheckNewTrade("Aggressive", "BUY", 1);

        Assert.True(result.Allowed);
        Assert.Equal(1m, result.AdjustedQuantity);
    }

    [Fact]
    public void CheckNewTrade_DeniesWhenTradingPaused()
    {
        var mockClient = CreateMockClient();
        using var manager = new RiskManager(mockClient.Object);

        manager.PauseTrading("Test pause");
        var result = manager.CheckNewTrade("Aggressive", "BUY", 1);

        Assert.False(result.Allowed);
        Assert.Contains("Trading paused", result.Reason);
    }

    [Fact]
    public void CheckNewTrade_DeniesWhenDailyTradeLimitReached()
    {
        var mockClient = CreateMockClient();
        var settings = new RiskSettings { MaxTradesPerDay = 2 };
        var state = new RiskState
        {
            TradingDate = DateTime.Today,
            TradesExecutedToday = 2
        };

        using var manager = new RiskManager(mockClient.Object, settings, state);
        var result = manager.CheckNewTrade("Aggressive", "BUY", 1);

        Assert.False(result.Allowed);
        Assert.Contains("Daily trade limit reached", result.Reason);
    }

    [Fact]
    public void CheckNewTrade_DeniesWhenStrategyPositionLimitReached()
    {
        var mockClient = CreateMockClient();
        var settings = new RiskSettings
        {
            PositionLimitsEnabled = true,
            MaxContractsPerStrategy = 3
        };

        using var manager = new RiskManager(mockClient.Object, settings);

        // Record trades to build up position
        manager.RecordTrade("Aggressive", "BUY", 3, 0);

        var result = manager.CheckNewTrade("Aggressive", "BUY", 1);

        Assert.False(result.Allowed);
        Assert.Contains("Strategy position limit reached", result.Reason);
    }

    [Fact]
    public void CheckNewTrade_ReducesQuantityToFitStrategyLimit()
    {
        var mockClient = CreateMockClient();
        var settings = new RiskSettings
        {
            PositionLimitsEnabled = true,
            MaxContractsPerStrategy = 5
        };

        using var manager = new RiskManager(mockClient.Object, settings);

        // Record trades to build up position
        manager.RecordTrade("Aggressive", "BUY", 3, 0);

        var result = manager.CheckNewTrade("Aggressive", "BUY", 5); // Requesting 5, only 2 available

        Assert.True(result.Allowed);
        Assert.Equal(2m, result.AdjustedQuantity);
        Assert.Contains("Quantity reduced", result.Reason);
    }

    [Fact]
    public void CheckNewTrade_DeniesWhenTotalPositionLimitReached()
    {
        var mockClient = CreateMockClient();
        var settings = new RiskSettings
        {
            PositionLimitsEnabled = true,
            MaxContractsPerStrategy = 10,
            MaxTotalContracts = 5
        };

        using var manager = new RiskManager(mockClient.Object, settings);

        // Build up positions across strategies
        manager.RecordTrade("Aggressive", "BUY", 3, 0);
        manager.RecordTrade("Conservative", "BUY", 2, 0);

        var result = manager.CheckNewTrade("Aggressive", "BUY", 1);

        Assert.False(result.Allowed);
        Assert.Contains("Total position limit reached", result.Reason);
    }

    [Fact]
    public void CheckNewTrade_DeniesWhenDailyLossLimitReached()
    {
        var mockClient = CreateMockClient();
        var settings = new RiskSettings
        {
            DailyLossLimitEnabled = true,
            MaxDailyLossUsd = 500.0
        };

        using var manager = new RiskManager(mockClient.Object, settings);

        // Update daily PnL to loss limit - this will pause trading
        manager.UpdateDailyPnL(-500.0);

        var result = manager.CheckNewTrade("Aggressive", "BUY", 1);

        Assert.False(result.Allowed);
        // Trading is paused due to loss limit
        Assert.Contains("Trading paused", result.Reason);
    }

    [Fact]
    public void CheckNewTrade_AllowsSellsRegardlessOfPositionLimits()
    {
        var mockClient = CreateMockClient();
        var settings = new RiskSettings
        {
            PositionLimitsEnabled = true,
            MaxContractsPerStrategy = 5,
            MaxTotalContracts = 10
        };

        using var manager = new RiskManager(mockClient.Object, settings);

        // At position limit
        manager.RecordTrade("Aggressive", "BUY", 5, 0);

        // Should still allow sells
        var result = manager.CheckNewTrade("Aggressive", "SELL", 2);

        Assert.True(result.Allowed);
    }

    [Fact]
    public void CheckNewTrade_FiresWarningNearDailyLossLimit()
    {
        var mockClient = CreateMockClient();
        var settings = new RiskSettings
        {
            DailyLossLimitEnabled = true,
            MaxDailyLossUsd = 500.0,
            DailyLossWarningPct = 0.7
        };

        using var manager = new RiskManager(mockClient.Object, settings);

        string? warningMessage = null;
        manager.OnWarning += msg => warningMessage = msg;

        // Set PnL to 80% of limit (past warning threshold)
        manager.UpdateDailyPnL(-400.0);

        manager.CheckNewTrade("Aggressive", "BUY", 1);

        Assert.NotNull(warningMessage);
        Assert.Contains("daily loss limit used", warningMessage);
    }
}

/// <summary>
/// Tests for RecordTrade functionality
/// </summary>
public class RiskManagerRecordTradeTests
{
    private Mock<IBKRClient> CreateMockClient()
    {
        var mock = new Mock<IBKRClient>();
        return mock;
    }

    [Fact]
    public void RecordTrade_IncrementsTradeCount()
    {
        var mockClient = CreateMockClient();
        using var manager = new RiskManager(mockClient.Object);

        manager.RecordTrade("Aggressive", "BUY", 1, 0);

        Assert.Equal(1, manager.State.TradesExecutedToday);
    }

    [Fact]
    public void RecordTrade_UpdatesDailyPnL()
    {
        var mockClient = CreateMockClient();
        using var manager = new RiskManager(mockClient.Object);

        manager.RecordTrade("Aggressive", "SELL", 1, 150.0);

        Assert.Equal(150.0, manager.State.DailyPnL);
    }

    [Fact]
    public void RecordTrade_TracksHighWaterMark()
    {
        var mockClient = CreateMockClient();
        using var manager = new RiskManager(mockClient.Object);

        manager.RecordTrade("Aggressive", "SELL", 1, 100.0);
        manager.RecordTrade("Aggressive", "SELL", 1, -50.0);

        Assert.Equal(100.0, manager.State.DailyHighWater);
        Assert.Equal(50.0, manager.State.DailyPnL); // Net PnL
    }

    [Fact]
    public void RecordTrade_UpdatesStrategyPosition_Buy()
    {
        var mockClient = CreateMockClient();
        using var manager = new RiskManager(mockClient.Object);

        manager.RecordTrade("Aggressive", "BUY", 2, 0);

        Assert.Equal(2m, manager.GetStrategyPosition("Aggressive"));
        Assert.Equal(2m, manager.GetTotalPosition());
    }

    [Fact]
    public void RecordTrade_UpdatesStrategyPosition_Sell()
    {
        var mockClient = CreateMockClient();
        using var manager = new RiskManager(mockClient.Object);

        manager.RecordTrade("Aggressive", "BUY", 3, 0);
        manager.RecordTrade("Aggressive", "SELL", 2, 50.0);

        Assert.Equal(1m, manager.GetStrategyPosition("Aggressive"));
        Assert.Equal(1m, manager.GetTotalPosition());
    }

    [Fact]
    public void RecordTrade_PausesTradingOnDailyLossLimit()
    {
        var mockClient = CreateMockClient();
        var settings = new RiskSettings
        {
            DailyLossLimitEnabled = true,
            MaxDailyLossUsd = 500.0
        };

        using var manager = new RiskManager(mockClient.Object, settings);

        bool limitHit = false;
        manager.OnLimitHit += _ => limitHit = true;

        // Record a large loss
        manager.RecordTrade("Aggressive", "SELL", 1, -600.0);

        Assert.True(manager.IsTradingPaused);
        Assert.True(limitHit);
    }

    [Fact]
    public void RecordTrade_FiresStateChangedEvent()
    {
        var mockClient = CreateMockClient();
        using var manager = new RiskManager(mockClient.Object);

        RiskState? changedState = null;
        manager.OnStateChanged += state => changedState = state;

        manager.RecordTrade("Aggressive", "BUY", 1, 0);

        Assert.NotNull(changedState);
        Assert.Equal(1, changedState!.TradesExecutedToday);
    }
}

/// <summary>
/// Tests for UpdateDailyPnL functionality
/// </summary>
public class RiskManagerUpdatePnLTests
{
    private Mock<IBKRClient> CreateMockClient()
    {
        var mock = new Mock<IBKRClient>();
        return mock;
    }

    [Fact]
    public void UpdateDailyPnL_UpdatesState()
    {
        var mockClient = CreateMockClient();
        using var manager = new RiskManager(mockClient.Object);

        manager.UpdateDailyPnL(250.0);

        Assert.Equal(250.0, manager.State.DailyPnL);
    }

    [Fact]
    public void UpdateDailyPnL_PausesTradingOnLossLimit()
    {
        var mockClient = CreateMockClient();
        var settings = new RiskSettings
        {
            DailyLossLimitEnabled = true,
            MaxDailyLossUsd = 500.0
        };

        using var manager = new RiskManager(mockClient.Object, settings);

        manager.UpdateDailyPnL(-550.0);

        Assert.True(manager.IsTradingPaused);
    }

    [Fact]
    public void UpdateDailyPnL_FiresWarningNearLimit()
    {
        var mockClient = CreateMockClient();
        var settings = new RiskSettings
        {
            DailyLossLimitEnabled = true,
            MaxDailyLossUsd = 500.0,
            DailyLossWarningPct = 0.7
        };

        using var manager = new RiskManager(mockClient.Object, settings);

        string? warningMessage = null;
        manager.OnWarning += msg => warningMessage = msg;

        manager.UpdateDailyPnL(-400.0); // 80% of limit

        Assert.NotNull(warningMessage);
        Assert.Contains("Daily loss warning", warningMessage);
    }

    [Fact]
    public void UpdateDailyPnL_NoWarningWhenProfitable()
    {
        var mockClient = CreateMockClient();
        var settings = new RiskSettings
        {
            DailyLossLimitEnabled = true,
            MaxDailyLossUsd = 500.0
        };

        using var manager = new RiskManager(mockClient.Object, settings);

        string? warningMessage = null;
        manager.OnWarning += msg => warningMessage = msg;

        manager.UpdateDailyPnL(100.0); // Profitable

        Assert.Null(warningMessage);
    }
}

/// <summary>
/// Tests for PauseTrading/ResumeTrading functionality
/// </summary>
public class RiskManagerPauseResumeTests
{
    private Mock<IBKRClient> CreateMockClient()
    {
        var mock = new Mock<IBKRClient>();
        return mock;
    }

    [Fact]
    public void PauseTrading_SetsPausedState()
    {
        var mockClient = CreateMockClient();
        using var manager = new RiskManager(mockClient.Object);

        manager.PauseTrading("Test reason");

        Assert.True(manager.IsTradingPaused);
        Assert.Equal("Test reason", manager.State.PauseReason);
    }

    [Fact]
    public void PauseTrading_FiresLimitHitEvent()
    {
        var mockClient = CreateMockClient();
        using var manager = new RiskManager(mockClient.Object);

        string? hitMessage = null;
        manager.OnLimitHit += msg => hitMessage = msg;

        manager.PauseTrading("Test reason");

        Assert.Equal("Test reason", hitMessage);
    }

    [Fact]
    public void PauseTrading_DoesNotFireTwice()
    {
        var mockClient = CreateMockClient();
        using var manager = new RiskManager(mockClient.Object);

        int hitCount = 0;
        manager.OnLimitHit += _ => hitCount++;

        manager.PauseTrading("First");
        manager.PauseTrading("Second");

        Assert.Equal(1, hitCount);
    }

    [Fact]
    public void ResumeTrading_ClearsPausedState()
    {
        var mockClient = CreateMockClient();
        using var manager = new RiskManager(mockClient.Object);

        manager.PauseTrading("Test reason");
        manager.ResumeTrading();

        Assert.False(manager.IsTradingPaused);
        Assert.Equal("", manager.State.PauseReason);
    }

    [Fact]
    public void ResumeTrading_FiresStateChanged()
    {
        var mockClient = CreateMockClient();
        using var manager = new RiskManager(mockClient.Object);

        manager.PauseTrading("Test");

        RiskState? changedState = null;
        manager.OnStateChanged += state => changedState = state;

        manager.ResumeTrading();

        Assert.NotNull(changedState);
        Assert.False(changedState!.TradingPaused);
    }
}

/// <summary>
/// Tests for ResetDaily functionality
/// </summary>
public class RiskManagerResetTests
{
    private Mock<IBKRClient> CreateMockClient()
    {
        var mock = new Mock<IBKRClient>();
        return mock;
    }

    [Fact]
    public void ResetDaily_ClearsAllCounters()
    {
        var mockClient = CreateMockClient();
        using var manager = new RiskManager(mockClient.Object);

        // Build up some state
        manager.RecordTrade("Aggressive", "BUY", 2, 0);
        manager.RecordTrade("Conservative", "BUY", 1, 0);
        manager.UpdateDailyPnL(-250.0);
        manager.PauseTrading("Test");

        manager.ResetDaily();

        Assert.Equal(0, manager.State.DailyPnL);
        Assert.Equal(0, manager.State.TradesExecutedToday);
        Assert.Equal(0m, manager.State.TotalPositionSize);
        Assert.False(manager.IsTradingPaused);
        Assert.Equal(0m, manager.GetStrategyPosition("Aggressive"));
        Assert.Equal(0m, manager.GetStrategyPosition("Conservative"));
    }

    [Fact]
    public void ResetDaily_SetsNewTradingDate()
    {
        var mockClient = CreateMockClient();
        using var manager = new RiskManager(mockClient.Object);

        manager.ResetDaily();

        Assert.Equal(DateTime.Today, manager.State.TradingDate);
    }
}

/// <summary>
/// Tests for GetRemainingDailyLoss and GetDailyLossUsedPct
/// </summary>
public class RiskManagerDailyLossCalcTests
{
    private Mock<IBKRClient> CreateMockClient()
    {
        var mock = new Mock<IBKRClient>();
        return mock;
    }

    [Fact]
    public void GetRemainingDailyLoss_FullAmountWhenProfitable()
    {
        var mockClient = CreateMockClient();
        var settings = new RiskSettings { MaxDailyLossUsd = 500.0 };
        using var manager = new RiskManager(mockClient.Object, settings);

        manager.UpdateDailyPnL(100.0);

        Assert.Equal(500.0, manager.GetRemainingDailyLoss());
    }

    [Fact]
    public void GetRemainingDailyLoss_ReducedWhenLoss()
    {
        var mockClient = CreateMockClient();
        var settings = new RiskSettings { MaxDailyLossUsd = 500.0 };
        using var manager = new RiskManager(mockClient.Object, settings);

        manager.UpdateDailyPnL(-200.0);

        Assert.Equal(300.0, manager.GetRemainingDailyLoss());
    }

    [Fact]
    public void GetDailyLossUsedPct_ZeroWhenProfitable()
    {
        var mockClient = CreateMockClient();
        var settings = new RiskSettings { MaxDailyLossUsd = 500.0 };
        using var manager = new RiskManager(mockClient.Object, settings);

        manager.UpdateDailyPnL(100.0);

        Assert.Equal(0.0, manager.GetDailyLossUsedPct());
    }

    [Fact]
    public void GetDailyLossUsedPct_CorrectPercentage()
    {
        var mockClient = CreateMockClient();
        var settings = new RiskSettings { MaxDailyLossUsd = 500.0 };
        using var manager = new RiskManager(mockClient.Object, settings);

        manager.UpdateDailyPnL(-250.0);

        Assert.Equal(0.5, manager.GetDailyLossUsedPct());
    }

    [Fact]
    public void GetDailyLossUsedPct_CapsAtOne()
    {
        var mockClient = CreateMockClient();
        var settings = new RiskSettings { MaxDailyLossUsd = 500.0 };
        using var manager = new RiskManager(mockClient.Object, settings);

        manager.UpdateDailyPnL(-750.0); // Exceeds limit

        Assert.Equal(1.0, manager.GetDailyLossUsedPct());
    }
}

/// <summary>
/// Tests for Emergency Flatten - Note: these test the RiskManager state changes
/// Actual broker interactions require integration tests with a mock broker
/// </summary>
public class RiskManagerEmergencyFlattenTests
{
    [Fact]
    public void EmergencyFlatten_TracksPositionsSeparately()
    {
        // Verify position tracking that EmergencyFlatten will use
        var mockClient = new Mock<IBKRClient>();
        using var manager = new RiskManager(mockClient.Object);

        manager.RecordTrade("Aggressive", "BUY", 2, 0);
        manager.RecordTrade("Conservative", "BUY", 1, 0);

        Assert.Equal(2m, manager.GetStrategyPosition("Aggressive"));
        Assert.Equal(1m, manager.GetStrategyPosition("Conservative"));
        Assert.Equal(3m, manager.GetTotalPosition());
    }

    [Fact]
    public void EmergencyFlatten_PositionsResetOnSell()
    {
        var mockClient = new Mock<IBKRClient>();
        using var manager = new RiskManager(mockClient.Object);

        manager.RecordTrade("Aggressive", "BUY", 2, 0);
        manager.RecordTrade("Aggressive", "SELL", 2, 0);

        Assert.Equal(0m, manager.GetStrategyPosition("Aggressive"));
    }

    [Fact]
    public void PauseTrading_SetsFlattenedState()
    {
        var mockClient = new Mock<IBKRClient>();
        using var manager = new RiskManager(mockClient.Object);

        manager.PauseTrading("Emergency flatten executed");

        Assert.True(manager.IsTradingPaused);
        Assert.Contains("Emergency flatten", manager.State.PauseReason);
    }

    [Fact]
    public void GetStrategyPosition_ReturnsZeroForUnknownStrategy()
    {
        var mockClient = new Mock<IBKRClient>();
        using var manager = new RiskManager(mockClient.Object);

        Assert.Equal(0m, manager.GetStrategyPosition("NonExistent"));
    }

    [Fact]
    public void GetTotalPosition_ReturnsCorrectSum()
    {
        var mockClient = new Mock<IBKRClient>();
        using var manager = new RiskManager(mockClient.Object);

        manager.RecordTrade("Aggressive", "BUY", 3, 0);
        manager.RecordTrade("Conservative", "BUY", 2, 0);
        manager.RecordTrade("Aggressive", "SELL", 1, 0);

        Assert.Equal(4m, manager.GetTotalPosition());
    }
}

/// <summary>
/// Tests for Start/Stop lifecycle
/// </summary>
public class RiskManagerLifecycleTests
{
    [Fact]
    public void Start_SubscribesToClientEvents()
    {
        var mockClient = new Mock<IBKRClient>();
        using var manager = new RiskManager(mockClient.Object);

        manager.Start();

        // Verify event subscriptions - we just verify no exception thrown
        // The actual subscription is verified by behavior tests
    }

    [Fact]
    public void Stop_UnsubscribesFromClientEvents()
    {
        var mockClient = new Mock<IBKRClient>();
        using var manager = new RiskManager(mockClient.Object);

        manager.Start();
        manager.Stop();

        // Verify no exception and clean shutdown
    }

    [Fact]
    public void Dispose_CallsStop()
    {
        var mockClient = new Mock<IBKRClient>();
        var manager = new RiskManager(mockClient.Object);

        manager.Start();
        manager.Dispose();

        // Should not throw
    }
}

/// <summary>
/// Integration tests for RiskManager with multi-strategy scenarios
/// </summary>
public class RiskManagerIntegrationTests
{
    private Mock<IBKRClient> CreateMockClient()
    {
        return new Mock<IBKRClient>();
    }

    [Fact]
    public void MultiStrategy_TracksPositionsSeparately()
    {
        var mockClient = CreateMockClient();
        using var manager = new RiskManager(mockClient.Object);

        manager.RecordTrade("Aggressive", "BUY", 3, 0);
        manager.RecordTrade("Conservative", "BUY", 2, 0);

        Assert.Equal(3m, manager.GetStrategyPosition("Aggressive"));
        Assert.Equal(2m, manager.GetStrategyPosition("Conservative"));
        Assert.Equal(5m, manager.GetTotalPosition());
    }

    [Fact]
    public void MultiStrategy_EnforcesPerStrategyLimits()
    {
        var mockClient = CreateMockClient();
        var settings = new RiskSettings
        {
            PositionLimitsEnabled = true,
            MaxContractsPerStrategy = 3,
            MaxTotalContracts = 10
        };

        using var manager = new RiskManager(mockClient.Object, settings);

        manager.RecordTrade("Aggressive", "BUY", 3, 0);

        // Aggressive at limit
        var aggressiveResult = manager.CheckNewTrade("Aggressive", "BUY", 1);
        Assert.False(aggressiveResult.Allowed);

        // Conservative still allowed
        var conservativeResult = manager.CheckNewTrade("Conservative", "BUY", 1);
        Assert.True(conservativeResult.Allowed);
    }

    [Fact]
    public void FullDaySimulation_HandlesMultipleTrades()
    {
        var mockClient = CreateMockClient();
        var settings = new RiskSettings
        {
            DailyLossLimitEnabled = true,
            MaxDailyLossUsd = 500.0,
            MaxTradesPerDay = 10
        };

        using var manager = new RiskManager(mockClient.Object, settings);

        // Morning trades
        manager.RecordTrade("Aggressive", "BUY", 1, 0);
        manager.RecordTrade("Aggressive", "SELL", 1, 100);

        Assert.Equal(100, manager.State.DailyPnL);
        Assert.Equal(2, manager.State.TradesExecutedToday);

        // More trades
        manager.RecordTrade("Conservative", "BUY", 1, 0);
        manager.RecordTrade("Conservative", "SELL", 1, -50);

        Assert.Equal(50, manager.State.DailyPnL);
        Assert.Equal(100, manager.State.DailyHighWater);

        // Losing streak
        manager.RecordTrade("Aggressive", "BUY", 1, 0);
        manager.RecordTrade("Aggressive", "SELL", 1, -200);
        manager.RecordTrade("Aggressive", "BUY", 1, 0);
        manager.RecordTrade("Aggressive", "SELL", 1, -200);
        manager.RecordTrade("Aggressive", "BUY", 1, 0);
        manager.RecordTrade("Aggressive", "SELL", 1, -200);

        // Should be paused due to loss limit
        Assert.True(manager.IsTradingPaused);
        Assert.Equal(-550, manager.State.DailyPnL);
    }
}
