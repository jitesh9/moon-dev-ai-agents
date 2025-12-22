/*
 * Paper Trading Tests
 * Tests for PaperTradingClient simulation of order execution
 */

using Xunit;

namespace GCTradingApp.Tests;

/// <summary>
/// Tests for PaperTradingConfig
/// </summary>
public class PaperTradingConfigTests
{
    [Fact]
    public void PaperTradingConfig_DefaultValues()
    {
        var config = new PaperTradingConfig();

        Assert.Equal(1.0, config.SlippageBps);
        Assert.Equal(100, config.FillDelayMs);
        Assert.Equal(100000, config.InitialBalance);
        Assert.Equal(100, config.ContractMultiplier);
    }

    [Fact]
    public void PaperTradingConfig_CanBeCustomized()
    {
        var config = new PaperTradingConfig
        {
            SlippageBps = 2.5,
            FillDelayMs = 200,
            InitialBalance = 50000,
            ContractMultiplier = 100
        };

        Assert.Equal(2.5, config.SlippageBps);
        Assert.Equal(200, config.FillDelayMs);
        Assert.Equal(50000, config.InitialBalance);
    }
}

/// <summary>
/// Tests for PaperOrder
/// </summary>
public class PaperOrderTests
{
    [Fact]
    public void PaperOrder_DefaultValues()
    {
        var order = new PaperOrder();

        Assert.Equal(0, order.OrderId);
        Assert.Equal("", order.Action);
        Assert.Equal(0, order.Quantity);
        Assert.Equal("", order.OrderType);
        Assert.Equal(0, order.StopPrice);
        Assert.Equal("", order.OrderRef);
        Assert.Equal(PaperOrderState.Pending, order.State);
    }

    [Fact]
    public void PaperOrder_CanBePopulated()
    {
        var order = new PaperOrder
        {
            OrderId = 1001,
            Action = "BUY",
            Quantity = 2,
            OrderType = "MKT",
            OrderRef = "Aggressive",
            SubmitTime = DateTime.Now,
            State = PaperOrderState.Submitted
        };

        Assert.Equal(1001, order.OrderId);
        Assert.Equal("BUY", order.Action);
        Assert.Equal(2, order.Quantity);
        Assert.Equal("MKT", order.OrderType);
        Assert.Equal("Aggressive", order.OrderRef);
        Assert.Equal(PaperOrderState.Submitted, order.State);
    }
}

/// <summary>
/// Tests for PaperTradingState
/// </summary>
public class PaperTradingStateTests
{
    [Fact]
    public void PaperTradingState_DefaultValues()
    {
        var state = new PaperTradingState();

        Assert.Equal(0, state.Position);
        Assert.Equal(0, state.AvgCost);
        Assert.Equal(0, state.RealizedPnL);
        Assert.Equal(0, state.UnrealizedPnL);
        Assert.Equal(0, state.Balance);
        Assert.Empty(state.Trades);
    }

    [Fact]
    public void PaperTradingState_CanBePopulated()
    {
        var state = new PaperTradingState
        {
            Position = 2,
            AvgCost = 2650.50,
            RealizedPnL = 1500.0,
            UnrealizedPnL = 250.0,
            Balance = 101500.0,
            Trades = new List<PaperTradeRecord>
            {
                new PaperTradeRecord { Action = "BUY", Quantity = 2, Price = 2650.50 }
            }
        };

        Assert.Equal(2, state.Position);
        Assert.Equal(2650.50, state.AvgCost);
        Assert.Equal(1500.0, state.RealizedPnL);
        Assert.Single(state.Trades);
    }
}

/// <summary>
/// Tests for PaperTradeRecord
/// </summary>
public class PaperTradeRecordTests
{
    [Fact]
    public void PaperTradeRecord_DefaultValues()
    {
        var record = new PaperTradeRecord();

        Assert.Equal("", record.Action);
        Assert.Equal(0, record.Quantity);
        Assert.Equal(0, record.Price);
        Assert.Equal(0, record.PnL);
        Assert.Equal("", record.Strategy);
    }

    [Fact]
    public void PaperTradeRecord_CanBePopulated()
    {
        var record = new PaperTradeRecord
        {
            Time = DateTime.Now,
            Action = "SELL",
            Quantity = 1,
            Price = 2700.00,
            PnL = 500.0,
            Strategy = "Conservative"
        };

        Assert.Equal("SELL", record.Action);
        Assert.Equal(1, record.Quantity);
        Assert.Equal(2700.00, record.Price);
        Assert.Equal(500.0, record.PnL);
        Assert.Equal("Conservative", record.Strategy);
    }
}

/// <summary>
/// Tests for PaperTradingClient
/// </summary>
public class PaperTradingClientTests
{
    [Fact]
    public void PaperTradingClient_InitializesWithDefaultConfig()
    {
        var client = new PaperTradingClient();
        var state = client.GetState();

        Assert.Equal(0, state.Position);
        Assert.Equal(100000, state.Balance);
        Assert.Equal(0, state.RealizedPnL);
    }

    [Fact]
    public void PaperTradingClient_InitializesWithCustomConfig()
    {
        var config = new PaperTradingConfig { InitialBalance = 50000 };
        var client = new PaperTradingClient(config);
        var state = client.GetState();

        Assert.Equal(50000, state.Balance);
    }

    [Fact]
    public void PaperTradingClient_NextOrderId_Increments()
    {
        var client = new PaperTradingClient();

        var id1 = client.NextOrderId;
        var id2 = client.NextOrderId;
        var id3 = client.NextOrderId;

        Assert.Equal(id1 + 1, id2);
        Assert.Equal(id2 + 1, id3);
    }

    [Fact]
    public void PaperTradingClient_Reset_ClearsState()
    {
        var client = new PaperTradingClient();

        // Process a bar and create some orders to simulate activity
        var bar = new BarData { Close = 2650.0, High = 2660.0, Low = 2640.0 };
        client.ProcessBar(bar);

        client.Reset();
        var state = client.GetState();

        Assert.Equal(0, state.Position);
        Assert.Equal(100000, state.Balance);
        Assert.Equal(0, state.RealizedPnL);
        Assert.Empty(state.Trades);
    }

    [Fact]
    public void PaperTradingClient_RestoreState_RestoresPosition()
    {
        var client = new PaperTradingClient();

        var savedState = new PaperTradingState
        {
            Position = 2,
            AvgCost = 2600.0,
            RealizedPnL = 500.0,
            Balance = 100500.0
        };

        client.RestoreState(savedState);
        var state = client.GetState();

        Assert.Equal(2, state.Position);
        Assert.Equal(2600.0, state.AvgCost);
        Assert.Equal(500.0, state.RealizedPnL);
        Assert.Equal(100500.0, state.Balance);
    }

    [Fact]
    public void PaperTradingClient_ProcessBar_UpdatesUnrealizedPnL()
    {
        var client = new PaperTradingClient();

        // First establish a position by restoring state
        var savedState = new PaperTradingState
        {
            Position = 1,
            AvgCost = 2600.0,
            Balance = 100000.0
        };
        client.RestoreState(savedState);

        // Process a bar with higher price
        var bar = new BarData { Close = 2650.0, High = 2660.0, Low = 2640.0 };
        client.ProcessBar(bar);

        var state = client.GetState();

        // Unrealized PnL = (2650 - 2600) * 1 * 100 = 5000
        Assert.Equal(5000.0, state.UnrealizedPnL);
    }
}

/// <summary>
/// Tests for stop order triggering
/// </summary>
public class PaperTradingStopOrderTests
{
    [Fact]
    public void StopOrder_SellStop_TriggersOnLowBreak()
    {
        var client = new PaperTradingClient();

        // Establish a long position
        var savedState = new PaperTradingState
        {
            Position = 1,
            AvgCost = 2650.0,
            Balance = 100000.0
        };
        client.RestoreState(savedState);

        OrderStatusData? lastStatus = null;
        client.OnOrderStatus += status => lastStatus = status;

        // Place a sell stop at 2620
        client.PlaceStopOrder("SELL", 1, 2620.0, "Aggressive");

        // Wait for submitted status
        Thread.Sleep(100);

        // Bar doesn't trigger stop (low is above stop)
        var bar1 = new BarData { Close = 2640.0, High = 2650.0, Low = 2630.0 };
        client.ProcessBar(bar1);

        // Wait for any processing
        Thread.Sleep(100);

        // Check that stop was not triggered (no fill status yet)
        Assert.True(lastStatus == null || lastStatus.Status != "Filled");

        // Bar triggers stop (low breaks through stop price)
        var bar2 = new BarData { Close = 2610.0, High = 2630.0, Low = 2605.0 };
        client.ProcessBar(bar2);

        // Wait for async fill
        Thread.Sleep(100);

        Assert.NotNull(lastStatus);
        Assert.Equal("Filled", lastStatus!.Status);
    }

    [Fact]
    public void StopOrder_BuyStop_TriggersOnHighBreak()
    {
        var client = new PaperTradingClient();

        OrderStatusData? lastStatus = null;
        client.OnOrderStatus += status => lastStatus = status;

        // Place a buy stop at 2680
        client.PlaceStopOrder("BUY", 1, 2680.0, "Aggressive");

        // Wait for submitted status
        Thread.Sleep(100);

        // Bar doesn't trigger stop (high is below stop)
        var bar1 = new BarData { Close = 2660.0, High = 2670.0, Low = 2650.0 };
        client.ProcessBar(bar1);

        // Wait for any processing
        Thread.Sleep(100);

        // Status should be submitted, not filled
        Assert.True(lastStatus == null || lastStatus.Status == "Submitted");

        // Bar triggers stop (high breaks through stop price)
        var bar2 = new BarData { Close = 2685.0, High = 2690.0, Low = 2665.0 };
        client.ProcessBar(bar2);

        // Wait for async fill
        Thread.Sleep(100);

        Assert.NotNull(lastStatus);
        Assert.Equal("Filled", lastStatus!.Status);
    }

    [Fact]
    public void StopOrder_UpdateStop_ChangesPrice()
    {
        var client = new PaperTradingClient();

        var savedState = new PaperTradingState
        {
            Position = 1,
            AvgCost = 2650.0,
            Balance = 100000.0
        };
        client.RestoreState(savedState);

        OrderStatusData? lastStatus = null;
        client.OnOrderStatus += status => lastStatus = status;

        // Place initial stop
        client.PlaceStopOrder("SELL", 1, 2620.0, "Aggressive");

        // Wait for submitted status
        Thread.Sleep(100);

        // Get the order ID from the submitted status
        var orderId = lastStatus?.OrderId ?? 0;

        // Update stop to tighter level
        client.UpdateStop(orderId, 2635.0, 1, "SELL");

        // Bar that would have missed old stop triggers new stop
        var bar = new BarData { Close = 2630.0, High = 2645.0, Low = 2625.0 };
        client.ProcessBar(bar);

        // Wait for fill
        Thread.Sleep(100);

        Assert.NotNull(lastStatus);
        Assert.Equal("Filled", lastStatus!.Status);
    }

    [Fact]
    public void StopOrder_CancelOrder_RemovesStop()
    {
        var client = new PaperTradingClient();

        OrderStatusData? lastStatus = null;
        client.OnOrderStatus += status => lastStatus = status;

        // Place a stop
        client.PlaceStopOrder("SELL", 1, 2620.0, "Aggressive");

        // Wait for submitted status
        Thread.Sleep(100);
        var orderId = lastStatus?.OrderId ?? 0;

        // Cancel the stop
        client.CancelOrder(orderId);

        // Wait for cancelled status
        Thread.Sleep(100);

        Assert.NotNull(lastStatus);
        Assert.Equal("Cancelled", lastStatus!.Status);

        // Bar that would trigger the stop shouldn't produce a fill
        var bar = new BarData { Close = 2610.0, High = 2630.0, Low = 2600.0 };
        client.ProcessBar(bar);

        // Wait for any async processing
        Thread.Sleep(100);

        // Status should still be cancelled, not filled
        Assert.Equal("Cancelled", lastStatus.Status);
    }
}

/// <summary>
/// Tests for position tracking and P&L calculation
/// </summary>
public class PaperTradingPositionTests
{
    [Fact]
    public void Position_OpenLong_TracksAvgCost()
    {
        var client = new PaperTradingClient(new PaperTradingConfig { FillDelayMs = 0 });

        ExecutionData? lastExec = null;
        client.OnExecution += exec => lastExec = exec;

        // Set current bar for fill
        var bar = new BarData { Close = 2650.0, High = 2655.0, Low = 2645.0 };
        client.ProcessBar(bar);

        client.PlaceMarketOrder("BUY", 1, "Aggressive");

        // Wait for async fill (Task.Run needs time to complete)
        Thread.Sleep(500);

        var state = client.GetState();

        Assert.Equal(1, state.Position);
        Assert.True(state.AvgCost > 0);
    }

    [Fact]
    public void Position_CloseLong_CalculatesPnL()
    {
        var config = new PaperTradingConfig
        {
            FillDelayMs = 0,
            SlippageBps = 0 // No slippage for precise calculation
        };
        var client = new PaperTradingClient(config);

        // Establish a long position
        var savedState = new PaperTradingState
        {
            Position = 1,
            AvgCost = 2600.0,
            Balance = 100000.0
        };
        client.RestoreState(savedState);

        ExecutionData? lastExec = null;
        client.OnExecution += exec => lastExec = exec;

        // Set current bar for fill at higher price
        var bar = new BarData { Close = 2650.0, High = 2655.0, Low = 2645.0 };
        client.ProcessBar(bar);

        client.PlaceMarketOrder("SELL", 1, "Aggressive");

        // Wait for async fill (Task.Run needs time)
        Thread.Sleep(500);

        var state = client.GetState();

        Assert.Equal(0, state.Position);
        // PnL = (2650 - 2600) * 1 * 100 = 5000 (before slippage)
        Assert.True(state.RealizedPnL > 4900); // Allow for small slippage
    }

    [Fact]
    public void Position_ShortPosition_TracksCorrectly()
    {
        var config = new PaperTradingConfig { FillDelayMs = 0, SlippageBps = 0 };
        var client = new PaperTradingClient(config);

        // Establish a short position by selling first
        var savedState = new PaperTradingState
        {
            Position = -1,
            AvgCost = 2700.0,
            Balance = 100000.0
        };
        client.RestoreState(savedState);

        // Process bar for unrealized calculation (price dropped = profit on short)
        var bar = new BarData { Close = 2650.0, High = 2660.0, Low = 2640.0 };
        client.ProcessBar(bar);

        var state = client.GetState();

        Assert.Equal(-1, state.Position);
        // Unrealized PnL for short = (avgCost - currentPrice) * abs(position) * multiplier
        // = (2700 - 2650) * 1 * 100 = 5000
        Assert.Equal(5000.0, state.UnrealizedPnL);
    }

    [Fact]
    public void Position_CoverShort_CalculatesPnL()
    {
        var config = new PaperTradingConfig { FillDelayMs = 0, SlippageBps = 0 };
        var client = new PaperTradingClient(config);

        // Establish a short position
        var savedState = new PaperTradingState
        {
            Position = -1,
            AvgCost = 2700.0,
            Balance = 100000.0
        };
        client.RestoreState(savedState);

        // Set bar for fill
        var bar = new BarData { Close = 2650.0, High = 2660.0, Low = 2640.0 };
        client.ProcessBar(bar);

        client.PlaceMarketOrder("BUY", 1, "Aggressive");

        // Wait for async fill (Task.Run needs time)
        Thread.Sleep(500);

        var state = client.GetState();

        Assert.Equal(0, state.Position);
        // Profit on covering = (2700 - 2650) * 1 * 100 = 5000
        Assert.Equal(5000.0, state.RealizedPnL);
    }
}

/// <summary>
/// Tests for slippage calculation
/// </summary>
public class PaperTradingSlippageTests
{
    [Fact]
    public void Slippage_BuyOrder_PriceIncreases()
    {
        var config = new PaperTradingConfig { FillDelayMs = 0, SlippageBps = 10.0 }; // 0.1%
        var client = new PaperTradingClient(config);

        ExecutionData? lastExec = null;
        client.OnExecution += exec => lastExec = exec;

        var bar = new BarData { Close = 2000.0, High = 2010.0, Low = 1990.0 };
        client.ProcessBar(bar);

        client.PlaceMarketOrder("BUY", 1, "Test");

        Thread.Sleep(500);

        Assert.NotNull(lastExec);
        // Fill price should be higher than bar close due to slippage
        // With 10bps slippage, price should be around 2000 * 1.001 = 2002
        Assert.True(lastExec!.Price > 2000.0);
        Assert.True(lastExec.Price < 2010.0); // But not unreasonably high
    }

    [Fact]
    public void Slippage_SellOrder_PriceDecreases()
    {
        var config = new PaperTradingConfig { FillDelayMs = 0, SlippageBps = 10.0 }; // 0.1%
        var client = new PaperTradingClient(config);

        // Establish position first
        var savedState = new PaperTradingState { Position = 1, AvgCost = 2000.0, Balance = 100000.0 };
        client.RestoreState(savedState);

        ExecutionData? lastExec = null;
        client.OnExecution += exec => lastExec = exec;

        var bar = new BarData { Close = 2000.0, High = 2010.0, Low = 1990.0 };
        client.ProcessBar(bar);

        client.PlaceMarketOrder("SELL", 1, "Test");

        Thread.Sleep(500);

        Assert.NotNull(lastExec);
        // Fill price should be lower than bar close due to slippage
        Assert.True(lastExec!.Price < 2000.0);
        Assert.True(lastExec.Price > 1990.0); // But not unreasonably low
    }

    [Fact]
    public void Slippage_ZeroBps_NoSlippage()
    {
        var config = new PaperTradingConfig { FillDelayMs = 0, SlippageBps = 0 };
        var client = new PaperTradingClient(config);

        ExecutionData? lastExec = null;
        client.OnExecution += exec => lastExec = exec;

        var bar = new BarData { Close = 2000.0, High = 2010.0, Low = 1990.0 };
        client.ProcessBar(bar);

        client.PlaceMarketOrder("BUY", 1, "Test");

        Thread.Sleep(500);

        Assert.NotNull(lastExec);
        Assert.Equal(2000.0, lastExec!.Price);
    }
}

/// <summary>
/// Tests for event firing
/// </summary>
public class PaperTradingEventTests
{
    [Fact]
    public void MarketOrder_FiresOrderStatusSubmitted()
    {
        var config = new PaperTradingConfig { FillDelayMs = 200 };
        var client = new PaperTradingClient(config);

        var statuses = new List<OrderStatusData>();
        client.OnOrderStatus += status => statuses.Add(status);

        var bar = new BarData { Close = 2650.0, High = 2660.0, Low = 2640.0 };
        client.ProcessBar(bar);

        client.PlaceMarketOrder("BUY", 1, "Test");

        // Wait for submitted status
        Thread.Sleep(500);

        Assert.True(statuses.Any(s => s.Status == "Submitted"));
    }

    [Fact]
    public void MarketOrder_FiresOrderStatusFilled()
    {
        var config = new PaperTradingConfig { FillDelayMs = 50 };
        var client = new PaperTradingClient(config);

        var statuses = new List<OrderStatusData>();
        client.OnOrderStatus += status => statuses.Add(status);

        var bar = new BarData { Close = 2650.0, High = 2660.0, Low = 2640.0 };
        client.ProcessBar(bar);

        client.PlaceMarketOrder("BUY", 1, "Test");

        // Wait for fill (includes 50ms FillDelay plus async overhead)
        Thread.Sleep(500);

        Assert.True(statuses.Any(s => s.Status == "Filled"));
    }

    [Fact]
    public void MarketOrder_FiresExecution()
    {
        var config = new PaperTradingConfig { FillDelayMs = 0 };
        var client = new PaperTradingClient(config);

        ExecutionData? receivedExec = null;
        client.OnExecution += exec => receivedExec = exec;

        var bar = new BarData { Close = 2650.0, High = 2660.0, Low = 2640.0 };
        client.ProcessBar(bar);

        client.PlaceMarketOrder("BUY", 1, "TestStrategy");

        Thread.Sleep(500);

        Assert.NotNull(receivedExec);
        Assert.Equal("GC", receivedExec!.Symbol);
        Assert.Equal("BOT", receivedExec.Side);
        Assert.Equal(1, receivedExec.Shares);
        Assert.Equal("TestStrategy", receivedExec.OrderRef);
    }

    [Fact]
    public void MarketOrder_FiresPositionUpdate()
    {
        var config = new PaperTradingConfig { FillDelayMs = 0 };
        var client = new PaperTradingClient(config);

        PositionData? receivedPos = null;
        client.OnPosition += pos => receivedPos = pos;

        var bar = new BarData { Close = 2650.0, High = 2660.0, Low = 2640.0 };
        client.ProcessBar(bar);

        client.PlaceMarketOrder("BUY", 1, "Test");

        Thread.Sleep(500);

        Assert.NotNull(receivedPos);
        Assert.Equal("Paper", receivedPos!.Account);
        Assert.Equal("GC", receivedPos.Symbol);
        Assert.Equal(1, receivedPos.Position);
    }

    [Fact]
    public void StopOrder_FiresOrderStatusSubmitted()
    {
        var client = new PaperTradingClient();

        OrderStatusData? receivedStatus = null;
        client.OnOrderStatus += status => receivedStatus = status;

        client.PlaceStopOrder("SELL", 1, 2620.0, "Test");

        // Wait for async sync context post
        Thread.Sleep(100);

        Assert.NotNull(receivedStatus);
        Assert.Equal("Submitted", receivedStatus!.Status);
        Assert.Equal(1, receivedStatus.Remaining);
    }

    [Fact]
    public void OnStateChanged_FiresOnFill()
    {
        var config = new PaperTradingConfig { FillDelayMs = 0 };
        var client = new PaperTradingClient(config);

        PaperTradingState? receivedState = null;
        client.OnStateChanged += state => receivedState = state;

        var bar = new BarData { Close = 2650.0, High = 2660.0, Low = 2640.0 };
        client.ProcessBar(bar);

        client.PlaceMarketOrder("BUY", 1, "Test");

        Thread.Sleep(500);

        Assert.NotNull(receivedState);
        Assert.Equal(1, receivedState!.Position);
    }

    [Fact]
    public void OnLog_FiresLogMessages()
    {
        var client = new PaperTradingClient();

        var logs = new List<string>();
        client.OnLog += msg => logs.Add(msg);

        client.PlaceStopOrder("SELL", 1, 2620.0, "Test");

        // Wait for async sync context post
        Thread.Sleep(100);

        Assert.True(logs.Count > 0);
        Assert.True(logs.Any(log => log.Contains("Stop order placed")));
    }
}

/// <summary>
/// Tests for IOrderClient interface compliance
/// </summary>
public class PaperTradingInterfaceTests
{
    [Fact]
    public void PaperTradingClient_ImplementsIOrderClient()
    {
        IOrderClient client = new PaperTradingClient();

        Assert.NotNull(client);
        Assert.True(client.NextOrderId > 0);
    }

    [Fact]
    public void PaperTradingClient_SwappableWithIBKRClient()
    {
        // This tests that PaperTradingClient can be used where IOrderClient is expected
        IOrderClient orderClient = new PaperTradingClient();

        // Should be able to call all interface methods
        orderClient.PlaceMarketOrder("BUY", 1, "Test");
        orderClient.PlaceStopOrder("SELL", 1, 2620.0, "Test");

        // Get an order ID to use for update/cancel
        var id = orderClient.NextOrderId;
        orderClient.UpdateStop(id, 2630.0, 1, "SELL");
        orderClient.CancelOrder(id);

        // Events should be subscribable
        orderClient.OnOrderStatus += _ => { };
        orderClient.OnExecution += _ => { };
        orderClient.OnPosition += _ => { };
    }
}

/// <summary>
/// Integration tests for paper trading with strategies
/// </summary>
public class PaperTradingIntegrationTests
{
    [Fact]
    public void PaperTrading_RoundTrip_TracksCorrectPnL()
    {
        var config = new PaperTradingConfig
        {
            FillDelayMs = 0,
            SlippageBps = 0,
            InitialBalance = 100000,
            ContractMultiplier = 100
        };
        var client = new PaperTradingClient(config);

        var fills = new List<ExecutionData>();
        client.OnExecution += exec => fills.Add(exec);

        // Entry bar
        var entryBar = new BarData { Close = 2600.0, High = 2610.0, Low = 2590.0 };
        client.ProcessBar(entryBar);

        // Buy 1 contract at 2600
        client.PlaceMarketOrder("BUY", 1, "RoundTrip");
        Thread.Sleep(500);

        // Verify BUY was filled before proceeding
        var stateAfterBuy = client.GetState();
        Assert.Equal(1, stateAfterBuy.Position);

        // Exit bar at higher price
        var exitBar = new BarData { Close = 2650.0, High = 2660.0, Low = 2640.0 };
        client.ProcessBar(exitBar);

        // Sell 1 contract at 2650
        client.PlaceMarketOrder("SELL", 1, "RoundTrip");
        Thread.Sleep(500);

        var state = client.GetState();

        Assert.Equal(0, state.Position);
        // Profit = (2650 - 2600) * 1 * 100 = 5000
        Assert.Equal(5000.0, state.RealizedPnL);
        Assert.Equal(105000.0, state.Balance);
        Assert.Equal(2, fills.Count);
    }

    [Fact]
    public void PaperTrading_MultipleContracts_ScalesCorrectly()
    {
        var config = new PaperTradingConfig
        {
            FillDelayMs = 0,
            SlippageBps = 0,
            ContractMultiplier = 100
        };
        var client = new PaperTradingClient(config);

        var bar = new BarData { Close = 2600.0, High = 2610.0, Low = 2590.0 };
        client.ProcessBar(bar);

        // Buy 3 contracts
        client.PlaceMarketOrder("BUY", 3, "Scale");
        Thread.Sleep(500);

        var state = client.GetState();

        Assert.Equal(3, state.Position);
        Assert.Equal(2600.0, state.AvgCost);
    }

    [Fact]
    public void PaperTrading_StopLoss_TriggersAndExits()
    {
        var config = new PaperTradingConfig
        {
            FillDelayMs = 0,
            SlippageBps = 0,
            ContractMultiplier = 100
        };
        var client = new PaperTradingClient(config);

        // Establish position
        var savedState = new PaperTradingState
        {
            Position = 1,
            AvgCost = 2650.0,
            Balance = 100000.0
        };
        client.RestoreState(savedState);

        // Place stop loss
        client.PlaceStopOrder("SELL", 1, 2620.0, "StopLoss");

        // Bar that triggers the stop
        var stopBar = new BarData { Close = 2610.0, High = 2640.0, Low = 2605.0 };
        client.ProcessBar(stopBar);

        Thread.Sleep(500);

        var state = client.GetState();

        Assert.Equal(0, state.Position);
        // Loss = (2620 - 2650) * 1 * 100 = -3000 (filled at stop price, no slippage)
        Assert.Equal(-3000.0, state.RealizedPnL);
    }

    [Fact]
    public void PaperTrading_StatePersistedAcrossResets()
    {
        var client = new PaperTradingClient(new PaperTradingConfig { FillDelayMs = 0, SlippageBps = 0 });

        // Create a position
        var bar = new BarData { Close = 2650.0, High = 2660.0, Low = 2640.0 };
        client.ProcessBar(bar);
        client.PlaceMarketOrder("BUY", 2, "Test");
        Thread.Sleep(500);

        // Get state before reset
        var stateBeforeReset = client.GetState();
        Assert.Equal(2, stateBeforeReset.Position);

        // Create new client and restore state
        var newClient = new PaperTradingClient();
        newClient.RestoreState(stateBeforeReset);

        var restoredState = newClient.GetState();

        Assert.Equal(stateBeforeReset.Position, restoredState.Position);
        Assert.Equal(stateBeforeReset.AvgCost, restoredState.AvgCost);
        Assert.Equal(stateBeforeReset.Balance, restoredState.Balance);
    }
}

/// <summary>
/// Tests for edge cases
/// </summary>
public class PaperTradingEdgeCaseTests
{
    [Fact]
    public void NoCurrentBar_MarketOrderUsesDefaultPrice()
    {
        var config = new PaperTradingConfig { FillDelayMs = 0 };
        var client = new PaperTradingClient(config);

        ExecutionData? lastExec = null;
        client.OnExecution += exec => lastExec = exec;

        // Don't process any bar, place order immediately
        client.PlaceMarketOrder("BUY", 1, "Test");

        Thread.Sleep(500);

        // Should still fill with fallback price
        Assert.NotNull(lastExec);
        Assert.True(lastExec!.Price > 0);
    }

    [Fact]
    public void UpdateStop_NonexistentOrder_CreatesNew()
    {
        var client = new PaperTradingClient();

        OrderStatusData? lastStatus = null;
        client.OnOrderStatus += status => lastStatus = status;

        // Update a stop that doesn't exist - should create it
        client.UpdateStop(9999, 2620.0, 1, "SELL");

        // Process bar that triggers the stop
        var bar = new BarData { Close = 2610.0, High = 2625.0, Low = 2600.0 };
        client.ProcessBar(bar);

        // Stop should have been created and triggered
        Assert.NotNull(lastStatus);
        Assert.Equal("Filled", lastStatus!.Status);
    }

    [Fact]
    public void CancelOrder_NonexistentOrder_NoError()
    {
        var client = new PaperTradingClient();

        // Should not throw
        client.CancelOrder(9999);
    }

    [Fact]
    public void ZeroQuantity_Order_StillProcesses()
    {
        var client = new PaperTradingClient(new PaperTradingConfig { FillDelayMs = 0 });

        var bar = new BarData { Close = 2650.0, High = 2660.0, Low = 2640.0 };
        client.ProcessBar(bar);

        ExecutionData? lastExec = null;
        client.OnExecution += exec => lastExec = exec;

        client.PlaceMarketOrder("BUY", 0, "Test");

        Thread.Sleep(500);

        // Should still fire execution event
        Assert.NotNull(lastExec);
        Assert.Equal(0, lastExec!.Shares);
    }

    [Fact]
    public void MultipleStopOrders_AllProcess()
    {
        var client = new PaperTradingClient();

        // Establish position
        var savedState = new PaperTradingState
        {
            Position = 3,
            AvgCost = 2650.0,
            Balance = 100000.0
        };
        client.RestoreState(savedState);

        var fills = new List<OrderStatusData>();
        client.OnOrderStatus += status =>
        {
            if (status.Status == "Filled") fills.Add(status);
        };

        // Place multiple stops at different levels
        client.PlaceStopOrder("SELL", 1, 2640.0, "Stop1");
        client.PlaceStopOrder("SELL", 1, 2630.0, "Stop2");
        client.PlaceStopOrder("SELL", 1, 2620.0, "Stop3");

        // Bar that triggers all stops
        var bar = new BarData { Close = 2610.0, High = 2650.0, Low = 2605.0 };
        client.ProcessBar(bar);

        // All 3 stops should have triggered
        Assert.Equal(3, fills.Count);
    }
}
