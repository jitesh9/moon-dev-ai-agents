/*
 * Feature Tests for Order Flow
 * Tests order lifecycle, position management, and strategy execution
 */

using FluentAssertions;
using Xunit;

namespace GCTradingApp.Tests;

/// <summary>
/// Tests for order flow and position management
/// </summary>
public class OrderFlowTests
{
    #region Order Record Tests

    [Fact]
    public void OrderRecord_NewOrder_HasCorrectDefaults()
    {
        // Arrange & Act
        var order = new OrderRecord();

        // Assert
        order.OrderId.Should().Be(0);
        order.Strategy.Should().BeEmpty();
        order.Action.Should().BeEmpty();
        order.Quantity.Should().Be(0);
        order.OrderType.Should().BeEmpty();
        order.LimitPrice.Should().Be(0);
        order.StopPrice.Should().Be(0);
        order.Status.Should().BeEmpty();
        order.Filled.Should().Be(0);
        order.Remaining.Should().Be(0);
    }

    [Fact]
    public void OrderRecord_MarketOrder_SetsCorrectFields()
    {
        // Arrange & Act
        var order = new OrderRecord
        {
            OrderId = 1,
            Time = DateTime.Now,
            Strategy = "Aggressive",
            Action = "BUY",
            Quantity = 2,
            OrderType = "MKT",
            Status = "Submitted"
        };

        // Assert
        order.OrderType.Should().Be("MKT");
        order.LimitPrice.Should().Be(0); // Not used for market orders
        order.StopPrice.Should().Be(0);
    }

    [Fact]
    public void OrderRecord_LimitOrder_IncludesLimitPrice()
    {
        // Arrange & Act
        var order = new OrderRecord
        {
            OrderId = 2,
            Time = DateTime.Now,
            Strategy = "Conservative",
            Action = "BUY",
            Quantity = 1,
            OrderType = "LMT",
            LimitPrice = 2050.50,
            Status = "Submitted"
        };

        // Assert
        order.OrderType.Should().Be("LMT");
        order.LimitPrice.Should().Be(2050.50);
    }

    [Fact]
    public void OrderRecord_StopOrder_IncludesStopPrice()
    {
        // Arrange & Act
        var order = new OrderRecord
        {
            OrderId = 3,
            Time = DateTime.Now,
            Strategy = "Aggressive",
            Action = "SELL",
            Quantity = 2,
            OrderType = "STP",
            StopPrice = 2030.00,
            Status = "Submitted"
        };

        // Assert
        order.OrderType.Should().Be("STP");
        order.StopPrice.Should().Be(2030.00);
    }

    [Fact]
    public void OrderRecord_PartialFill_UpdatesCorrectly()
    {
        // Arrange
        var order = new OrderRecord
        {
            OrderId = 4,
            Quantity = 5,
            Status = "Submitted",
            Filled = 0,
            Remaining = 5
        };

        // Act - Partial fill
        order.Status = "PartialFill";
        order.Filled = 2;
        order.Remaining = 3;

        // Assert
        order.Status.Should().Be("PartialFill");
        order.Filled.Should().Be(2);
        order.Remaining.Should().Be(3);
    }

    [Fact]
    public void OrderRecord_CompleteFill_UpdatesCorrectly()
    {
        // Arrange
        var order = new OrderRecord
        {
            OrderId = 5,
            Quantity = 3,
            Status = "PartialFill",
            Filled = 1,
            Remaining = 2
        };

        // Act - Complete fill
        order.Status = "Filled";
        order.Filled = 3;
        order.Remaining = 0;

        // Assert
        order.Status.Should().Be("Filled");
        order.Filled.Should().Be(3);
        order.Remaining.Should().Be(0);
    }

    #endregion

    #region Fill Record Tests

    [Fact]
    public void FillRecord_NewFill_HasCorrectDefaults()
    {
        // Arrange & Act
        var fill = new FillRecord();

        // Assert
        fill.ExecId.Should().BeEmpty();
        fill.Strategy.Should().BeEmpty();
        fill.Action.Should().BeEmpty();
        fill.Quantity.Should().Be(0);
        fill.Price.Should().Be(0);
        fill.Commission.Should().Be(0);
        fill.RealizedPnL.Should().Be(0);
    }

    [Fact]
    public void FillRecord_BuyFill_ContainsAllInfo()
    {
        // Arrange & Act
        var fill = new FillRecord
        {
            ExecId = "0000001.20241222.1",
            Time = new DateTime(2024, 12, 22, 10, 30, 0),
            Strategy = "Aggressive",
            Action = "BUY",
            Quantity = 2,
            Price = 2055.25,
            Commission = 4.50,
            RealizedPnL = 0 // Entry fill has no realized PnL
        };

        // Assert
        fill.Action.Should().Be("BUY");
        fill.Price.Should().Be(2055.25);
        fill.Commission.Should().Be(4.50);
        fill.RealizedPnL.Should().Be(0);
    }

    [Fact]
    public void FillRecord_SellFill_IncludesRealizedPnL()
    {
        // Arrange - Assuming 2 contracts bought at 2055.25, sold at 2070.00
        // PnL = (2070 - 2055.25) * 2 * 100 = $2,950 (before commission)
        var fill = new FillRecord
        {
            ExecId = "0000002.20241222.1",
            Time = new DateTime(2024, 12, 22, 12, 0, 0),
            Strategy = "Aggressive",
            Action = "SELL",
            Quantity = 2,
            Price = 2070.00,
            Commission = 4.50,
            RealizedPnL = 2941.00 // After commission
        };

        // Assert
        fill.Action.Should().Be("SELL");
        fill.RealizedPnL.Should().BePositive();
    }

    [Fact]
    public void FillRecord_LosingTrade_HasNegativePnL()
    {
        // Arrange - Entry at 2055.25, exit at 2040.00 (stop loss)
        var fill = new FillRecord
        {
            ExecId = "0000003.20241222.1",
            Time = new DateTime(2024, 12, 22, 14, 0, 0),
            Strategy = "Conservative",
            Action = "SELL",
            Quantity = 1,
            Price = 2040.00,
            Commission = 2.25,
            RealizedPnL = -1527.25 // (2040-2055.25)*100 - 2.25
        };

        // Assert
        fill.RealizedPnL.Should().BeNegative();
    }

    #endregion

    #region Position Record Tests

    [Fact]
    public void PositionRecord_NewPosition_HasCorrectDefaults()
    {
        // Arrange & Act
        var position = new PositionRecord();

        // Assert
        position.Strategy.Should().BeEmpty();
        position.Symbol.Should().BeEmpty();
        position.Position.Should().Be(0);
        position.AvgCost.Should().Be(0);
        position.MarketPrice.Should().Be(0);
        position.MarketValue.Should().Be(0);
        position.UnrealizedPnL.Should().Be(0);
        position.RealizedPnL.Should().Be(0);
    }

    [Fact]
    public void PositionRecord_LongPosition_HasPositiveQuantity()
    {
        // Arrange & Act
        var position = new PositionRecord
        {
            Symbol = "GC",
            Position = 2, // Long 2 contracts
            AvgCost = 2055.25,
            MarketPrice = 2060.00,
            MarketValue = 412000, // 2 * 2060 * 100
            UnrealizedPnL = 950.00 // (2060-2055.25) * 2 * 100
        };

        // Assert
        position.Position.Should().BePositive();
        position.UnrealizedPnL.Should().BePositive();
    }

    [Fact]
    public void PositionRecord_FlatPosition_HasZeroQuantity()
    {
        // Arrange & Act
        var position = new PositionRecord
        {
            Symbol = "GC",
            Position = 0,
            AvgCost = 0,
            MarketPrice = 2060.00,
            MarketValue = 0,
            UnrealizedPnL = 0,
            RealizedPnL = 1500.00 // From previous trades
        };

        // Assert
        position.Position.Should().Be(0);
        position.MarketValue.Should().Be(0);
        position.UnrealizedPnL.Should().Be(0);
    }

    [Fact]
    public void PositionRecord_UnrealizedLoss_IsNegative()
    {
        // Arrange & Act
        var position = new PositionRecord
        {
            Symbol = "GC",
            Position = 1,
            AvgCost = 2070.00, // Bought higher
            MarketPrice = 2055.00, // Market lower
            MarketValue = 205500,
            UnrealizedPnL = -1500.00 // (2055-2070) * 1 * 100
        };

        // Assert
        position.UnrealizedPnL.Should().BeNegative();
    }

    #endregion

    #region Order Status Transition Tests

    [Theory]
    [InlineData("Submitted", "PreSubmitted")]
    [InlineData("PreSubmitted", "Submitted")]
    [InlineData("Submitted", "Filled")]
    [InlineData("Submitted", "Cancelled")]
    [InlineData("Submitted", "PartialFill")]
    [InlineData("PartialFill", "Filled")]
    [InlineData("PartialFill", "Cancelled")]
    public void OrderStatus_ValidTransitions_AreAllowed(string from, string to)
    {
        // Arrange
        var order = new OrderRecord { Status = from };

        // Act
        order.Status = to;

        // Assert
        order.Status.Should().Be(to);
    }

    [Fact]
    public void OrderStatusData_FromBroker_ContainsAllFields()
    {
        // Arrange & Act
        var status = new OrderStatusData
        {
            OrderId = 123,
            Status = "Filled",
            Filled = 2,
            Remaining = 0,
            AvgFillPrice = 2055.25,
            PermId = 987654321,
            ParentId = 0,
            LastFillPrice = 2055.25,
            ClientId = 1,
            WhyHeld = ""
        };

        // Assert
        status.Status.Should().Be("Filled");
        status.AvgFillPrice.Should().Be(2055.25);
        status.Filled.Should().Be(2);
        status.Remaining.Should().Be(0);
    }

    #endregion

    #region Strategy State Transition Tests

    [Fact]
    public void StrategyState_EntrySignal_TransitionsCorrectly()
    {
        // Arrange - Flat position
        var state = new StrategyState
        {
            InPosition = false,
            EntryPrice = 0,
            StopPrice = 0,
            TargetPrice = 0
        };

        // Act - Entry signal triggered, order filled
        state.InPosition = true;
        state.EntryPrice = 2055.50;
        state.EntryTime = DateTime.Now;
        state.EntryBarCount = 42;
        state.StopPrice = 2040.00;
        state.TargetPrice = 2080.00;
        state.PositionQuantity = 2;

        // Assert
        state.InPosition.Should().BeTrue();
        state.EntryPrice.Should().Be(2055.50);
        state.StopPrice.Should().BeLessThan(state.EntryPrice);
        state.TargetPrice.Should().BeGreaterThan(state.EntryPrice);
    }

    [Fact]
    public void StrategyState_ExitSignal_TransitionsCorrectly()
    {
        // Arrange - In position
        var state = new StrategyState
        {
            InPosition = true,
            EntryPrice = 2055.50,
            EntryBarCount = 42,
            StopPrice = 2040.00,
            TargetPrice = 2080.00,
            PositionQuantity = 2
        };

        // Act - Exit (target hit)
        state.InPosition = false;
        state.EntryPrice = 0;
        state.StopPrice = 0;
        state.TargetPrice = 0;
        state.PositionQuantity = 0;

        // Assert
        state.InPosition.Should().BeFalse();
        state.EntryPrice.Should().Be(0);
        state.PositionQuantity.Should().Be(0);
    }

    [Fact]
    public void StrategyState_TrailingStop_UpdatesStopPrice()
    {
        // Arrange - In position, price moved favorably
        var state = new StrategyState
        {
            InPosition = true,
            EntryPrice = 2055.50,
            StopPrice = 2040.00, // Original stop
            TargetPrice = 2080.00
        };

        var originalStop = state.StopPrice;

        // Act - Price moved up, trail stop higher
        state.StopPrice = 2055.00; // Moved from 2040 to 2055

        // Assert
        state.StopPrice.Should().BeGreaterThan(originalStop);
        state.StopPrice.Should().BeLessThan(state.EntryPrice); // But still below entry (was moved close)
    }

    #endregion

    #region Open Order Data Tests

    [Fact]
    public void OpenOrderData_MarketBuy_HasCorrectFields()
    {
        // Arrange & Act
        var order = new OpenOrderData
        {
            OrderId = 1,
            Symbol = "GC",
            Action = "BUY",
            Quantity = 2,
            OrderType = "MKT",
            LimitPrice = 0,
            StopPrice = 0,
            Status = "Submitted",
            OrderRef = "Aggressive"
        };

        // Assert
        order.Action.Should().Be("BUY");
        order.OrderType.Should().Be("MKT");
        order.OrderRef.Should().Be("Aggressive");
    }

    [Fact]
    public void OpenOrderData_StopLoss_HasStopPrice()
    {
        // Arrange & Act
        var order = new OpenOrderData
        {
            OrderId = 2,
            Symbol = "GC",
            Action = "SELL",
            Quantity = 2,
            OrderType = "STP",
            StopPrice = 2040.00,
            Status = "Submitted",
            OrderRef = "Aggressive_StopLoss"
        };

        // Assert
        order.Action.Should().Be("SELL");
        order.OrderType.Should().Be("STP");
        order.StopPrice.Should().Be(2040.00);
    }

    #endregion

    #region Execution Data Tests

    [Fact]
    public void ExecutionData_BuyExecution_ContainsAllInfo()
    {
        // Arrange & Act
        var exec = new ExecutionData
        {
            ExecId = "0000001.20241222.1",
            Symbol = "GC",
            Side = "BOT",
            Shares = 2,
            Price = 2055.25,
            Commission = 4.50,
            RealizedPnL = 0,
            OrderRef = "Aggressive"
        };

        // Assert
        exec.Side.Should().Be("BOT");
        exec.Shares.Should().Be(2);
        exec.Price.Should().Be(2055.25);
    }

    [Fact]
    public void ExecutionData_SellExecution_IncludesPnL()
    {
        // Arrange & Act
        var exec = new ExecutionData
        {
            ExecId = "0000002.20241222.1",
            Symbol = "GC",
            Side = "SLD",
            Shares = 2,
            Price = 2070.00,
            Commission = 4.50,
            RealizedPnL = 2941.00,
            OrderRef = "Aggressive_TakeProfit"
        };

        // Assert
        exec.Side.Should().Be("SLD");
        exec.RealizedPnL.Should().BePositive();
    }

    #endregion

    #region Position Data Tests

    [Fact]
    public void PositionData_FromBroker_ContainsAllFields()
    {
        // Arrange & Act
        var position = new PositionData
        {
            Account = "DU123456",
            Symbol = "GC",
            Position = 2,
            AvgCost = 2055.25,
            MarketPrice = 2060.00,
            MarketValue = 412000,
            UnrealizedPnL = 950.00,
            RealizedPnL = 0
        };

        // Assert
        position.Account.Should().Be("DU123456");
        position.Symbol.Should().Be("GC");
        position.Position.Should().Be(2);
    }

    #endregion

    #region Bar Data Tests

    [Fact]
    public void BarData_ValidBar_HasAllFields()
    {
        // Arrange & Act
        var bar = new BarData
        {
            Time = new DateTime(2024, 12, 22, 10, 30, 0),
            Open = 2050.00,
            High = 2055.50,
            Low = 2048.25,
            Close = 2054.00,
            Volume = 1500,
            WAP = 2052.50m,
            Count = 350
        };

        // Assert
        bar.High.Should().BeGreaterThanOrEqualTo(bar.Open);
        bar.High.Should().BeGreaterThanOrEqualTo(bar.Close);
        bar.Low.Should().BeLessThanOrEqualTo(bar.Open);
        bar.Low.Should().BeLessThanOrEqualTo(bar.Close);
    }

    [Fact]
    public void BarData_BullishBar_CloseAboveOpen()
    {
        // Arrange & Act
        var bar = new BarData
        {
            Open = 2050.00,
            High = 2060.00,
            Low = 2048.00,
            Close = 2058.00
        };

        // Assert
        bar.Close.Should().BeGreaterThan(bar.Open);
    }

    [Fact]
    public void BarData_BearishBar_CloseBelowOpen()
    {
        // Arrange & Act
        var bar = new BarData
        {
            Open = 2058.00,
            High = 2060.00,
            Low = 2048.00,
            Close = 2050.00
        };

        // Assert
        bar.Close.Should().BeLessThan(bar.Open);
    }

    #endregion

    #region Integration-like Tests

    [Fact]
    public void OrderFlow_CompleteTradeLifecycle_UpdatesAllRecords()
    {
        // Arrange
        var state = new AppState();
        var strategyState = new StrategyState();
        var orderId = 1;

        // Act - Step 1: Place entry order
        state.Orders[orderId] = new OrderRecord
        {
            OrderId = orderId,
            Time = DateTime.Now,
            Strategy = "Aggressive",
            Action = "BUY",
            Quantity = 2,
            OrderType = "MKT",
            Status = "Submitted"
        };

        // Act - Step 2: Order filled
        state.Orders[orderId].Status = "Filled";
        state.Orders[orderId].Filled = 2;
        state.Orders[orderId].Remaining = 0;

        state.Fills.Add(new FillRecord
        {
            ExecId = "EXEC001",
            Time = DateTime.Now,
            Strategy = "Aggressive",
            Action = "BUY",
            Quantity = 2,
            Price = 2055.25,
            Commission = 4.50
        });

        strategyState.InPosition = true;
        strategyState.EntryPrice = 2055.25;
        strategyState.StopPrice = 2040.00;
        strategyState.TargetPrice = 2080.00;
        strategyState.PositionQuantity = 2;

        state.Positions["GC_ACC1"] = new PositionRecord
        {
            Symbol = "GC",
            Position = 2,
            AvgCost = 2055.25,
            MarketPrice = 2055.25,
            MarketValue = 411050,
            UnrealizedPnL = 0
        };

        // Act - Step 3: Price moves, update position
        state.Positions["GC_ACC1"].MarketPrice = 2070.00;
        state.Positions["GC_ACC1"].MarketValue = 414000;
        state.Positions["GC_ACC1"].UnrealizedPnL = 2950.00;

        // Act - Step 4: Exit at target
        var exitOrderId = 2;
        state.Orders[exitOrderId] = new OrderRecord
        {
            OrderId = exitOrderId,
            Time = DateTime.Now,
            Strategy = "Aggressive",
            Action = "SELL",
            Quantity = 2,
            OrderType = "MKT",
            Status = "Filled",
            Filled = 2,
            Remaining = 0
        };

        state.Fills.Add(new FillRecord
        {
            ExecId = "EXEC002",
            Time = DateTime.Now,
            Strategy = "Aggressive",
            Action = "SELL",
            Quantity = 2,
            Price = 2080.00,
            Commission = 4.50,
            RealizedPnL = 4941.00 // (2080-2055.25)*2*100 - 9 commission
        });

        strategyState.InPosition = false;
        strategyState.EntryPrice = 0;
        strategyState.StopPrice = 0;
        strategyState.TargetPrice = 0;
        strategyState.PositionQuantity = 0;

        state.Positions["GC_ACC1"].Position = 0;
        state.Positions["GC_ACC1"].UnrealizedPnL = 0;
        state.Positions["GC_ACC1"].RealizedPnL = 4941.00;

        // Assert
        state.Orders.Should().HaveCount(2);
        state.Fills.Should().HaveCount(2);
        strategyState.InPosition.Should().BeFalse();
        state.Positions["GC_ACC1"].Position.Should().Be(0);
        state.Positions["GC_ACC1"].RealizedPnL.Should().BePositive();
    }

    [Fact]
    public void OrderFlow_StopLossHit_UpdatesAllRecords()
    {
        // Arrange - Already in position
        var state = new AppState();
        var strategyState = new StrategyState
        {
            InPosition = true,
            EntryPrice = 2055.25,
            StopPrice = 2040.00,
            TargetPrice = 2080.00,
            PositionQuantity = 1
        };

        state.Positions["GC_ACC1"] = new PositionRecord
        {
            Symbol = "GC",
            Position = 1,
            AvgCost = 2055.25
        };

        // Act - Stop loss triggered
        state.Orders[1] = new OrderRecord
        {
            OrderId = 1,
            Time = DateTime.Now,
            Strategy = "Aggressive_StopLoss",
            Action = "SELL",
            Quantity = 1,
            OrderType = "STP",
            StopPrice = 2040.00,
            Status = "Filled"
        };

        var lossAmount = (2040.00 - 2055.25) * 1 * 100; // -$1,525
        state.Fills.Add(new FillRecord
        {
            ExecId = "EXEC001",
            Time = DateTime.Now,
            Strategy = "Aggressive",
            Action = "SELL",
            Quantity = 1,
            Price = 2040.00,
            Commission = 2.25,
            RealizedPnL = lossAmount - 2.25
        });

        strategyState.InPosition = false;
        strategyState.PositionQuantity = 0;

        state.Positions["GC_ACC1"].Position = 0;
        state.Positions["GC_ACC1"].RealizedPnL = lossAmount - 2.25;

        // Assert
        state.Fills[0].RealizedPnL.Should().BeNegative();
        strategyState.InPosition.Should().BeFalse();
    }

    #endregion
}
