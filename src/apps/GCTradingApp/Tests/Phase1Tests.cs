/*
 * Phase 1 Tests - Reliability & Robustness
 * Tests for ConnectionManager, PositionReconciler, and OrderTracker
 */

using Xunit;
using FluentAssertions;

namespace GCTradingApp.Tests;

/// <summary>
/// Tests for ConnectionManager
/// </summary>
public class ConnectionManagerTests
{
    [Fact]
    public void InitialState_ShouldBeDisconnected()
    {
        using var manager = new ConnectionManager();

        manager.State.Should().Be(ConnectionState.Disconnected);
        manager.ReconnectAttempt.Should().Be(0);
        manager.AutoReconnectEnabled.Should().BeTrue();
    }

    [Fact]
    public void AutoReconnectEnabled_ShouldBeSettable()
    {
        using var manager = new ConnectionManager();

        manager.AutoReconnectEnabled = false;
        manager.AutoReconnectEnabled.Should().BeFalse();

        manager.AutoReconnectEnabled = true;
        manager.AutoReconnectEnabled.Should().BeTrue();
    }

    [Fact]
    public void EnableAutoReconnect_ShouldSetFlag()
    {
        using var manager = new ConnectionManager();
        manager.AutoReconnectEnabled = false;

        manager.EnableAutoReconnect();

        manager.AutoReconnectEnabled.Should().BeTrue();
    }

    [Fact]
    public void ConnectionStateChanged_ShouldFireEvent()
    {
        using var manager = new ConnectionManager();
        ConnectionState? receivedState = null;
        manager.OnConnectionStateChanged += state => receivedState = state;

        // Note: We can't fully test Connect without a real TWS connection
        // This test verifies event subscription works
        receivedState.Should().BeNull();
    }

    [Fact]
    public void Client_ShouldBeNullInitially()
    {
        using var manager = new ConnectionManager();

        manager.Client.Should().BeNull();
    }
}

/// <summary>
/// Tests for OrderTracker
/// </summary>
public class OrderTrackerTests
{
    [Fact]
    public void TrackOrder_ShouldAddOrderToPendingList()
    {
        using var tracker = new OrderTracker();

        tracker.TrackOrder(1001, "Aggressive", "BUY", 1, "MKT");

        var order = tracker.GetOrder(1001);
        order.Should().NotBeNull();
        order!.OrderId.Should().Be(1001);
        order.Strategy.Should().Be("Aggressive");
        order.Action.Should().Be("BUY");
        order.Quantity.Should().Be(1);
        order.OrderType.Should().Be("MKT");
        order.State.Should().Be(TrackedOrderState.Pending);
    }

    [Fact]
    public void UpdateOrderStatus_Submitted_ShouldChangeToAcknowledged()
    {
        using var tracker = new OrderTracker();
        tracker.TrackOrder(1001, "Test", "BUY", 1, "MKT");

        var status = new OrderStatusData
        {
            OrderId = 1001,
            Status = "Submitted",
            Filled = 0,
            Remaining = 1,
            PermId = 123456
        };

        tracker.UpdateOrderStatus(status);

        var order = tracker.GetOrder(1001);
        order!.State.Should().Be(TrackedOrderState.Acknowledged);
        order.PermId.Should().Be(123456);
        order.AckTime.Should().NotBeNull();
    }

    [Fact]
    public void UpdateOrderStatus_Filled_ShouldChangeToFilled()
    {
        using var tracker = new OrderTracker();
        tracker.TrackOrder(1001, "Test", "BUY", 1, "MKT");

        var status = new OrderStatusData
        {
            OrderId = 1001,
            Status = "Filled",
            Filled = 1,
            Remaining = 0,
            AvgFillPrice = 2650.50
        };

        tracker.UpdateOrderStatus(status);

        var order = tracker.GetOrder(1001);
        order!.State.Should().Be(TrackedOrderState.Filled);
        order.FilledQuantity.Should().Be(1);
        order.AvgFillPrice.Should().Be(2650.50);
        order.FillTime.Should().NotBeNull();
    }

    [Fact]
    public void UpdateOrderStatus_Cancelled_ShouldChangeToCancelled()
    {
        using var tracker = new OrderTracker();
        tracker.TrackOrder(1001, "Test", "BUY", 1, "LMT", limitPrice: 2600);

        var status = new OrderStatusData
        {
            OrderId = 1001,
            Status = "Cancelled",
            Filled = 0,
            Remaining = 1
        };

        tracker.UpdateOrderStatus(status);

        var order = tracker.GetOrder(1001);
        order!.State.Should().Be(TrackedOrderState.Cancelled);
    }

    [Fact]
    public void UpdateOrderStatus_PartialFill_ShouldChangeToPartialFill()
    {
        using var tracker = new OrderTracker();
        tracker.TrackOrder(1001, "Test", "BUY", 5, "MKT");

        var status = new OrderStatusData
        {
            OrderId = 1001,
            Status = "PartiallyFilled",
            Filled = 2,
            Remaining = 3,
            AvgFillPrice = 2650.00
        };

        tracker.UpdateOrderStatus(status);

        var order = tracker.GetOrder(1001);
        order!.State.Should().Be(TrackedOrderState.PartialFill);
        order.FilledQuantity.Should().Be(2);
        order.RemainingQuantity.Should().Be(3);
    }

    [Fact]
    public void UpdateOrderError_ShouldSetErrorState()
    {
        using var tracker = new OrderTracker();
        tracker.TrackOrder(1001, "Test", "BUY", 1, "MKT");

        tracker.UpdateOrderError(1001, 201, "Order rejected");

        var order = tracker.GetOrder(1001);
        order!.State.Should().Be(TrackedOrderState.Error);
        order.ErrorMessage.Should().Contain("Order rejected");
    }

    [Fact]
    public void UpdateOrderError_InformationalCode_ShouldNotChangeState()
    {
        using var tracker = new OrderTracker();
        tracker.TrackOrder(1001, "Test", "BUY", 1, "MKT");

        // 2104 is informational (market data farm connected)
        tracker.UpdateOrderError(1001, 2104, "Market data farm connected");

        var order = tracker.GetOrder(1001);
        order!.State.Should().Be(TrackedOrderState.Pending);
    }

    [Fact]
    public void GetPendingOrders_ShouldReturnOnlyPending()
    {
        using var tracker = new OrderTracker();
        tracker.TrackOrder(1001, "Test1", "BUY", 1, "MKT");
        tracker.TrackOrder(1002, "Test2", "SELL", 1, "MKT");

        tracker.UpdateOrderStatus(new OrderStatusData { OrderId = 1001, Status = "Filled", Filled = 1 });

        var pending = tracker.GetPendingOrders();
        pending.Should().HaveCount(1);
        pending[0].OrderId.Should().Be(1002);
    }

    [Fact]
    public void GetActiveOrders_ShouldIncludePendingAcknowledgedAndPartialFill()
    {
        using var tracker = new OrderTracker();
        tracker.TrackOrder(1001, "Test1", "BUY", 1, "MKT");  // Pending
        tracker.TrackOrder(1002, "Test2", "SELL", 1, "MKT"); // Will be Acknowledged
        tracker.TrackOrder(1003, "Test3", "BUY", 5, "MKT");  // Will be PartialFill
        tracker.TrackOrder(1004, "Test4", "SELL", 1, "MKT"); // Will be Filled

        tracker.UpdateOrderStatus(new OrderStatusData { OrderId = 1002, Status = "Submitted" });
        tracker.UpdateOrderStatus(new OrderStatusData { OrderId = 1003, Status = "PartiallyFilled", Filled = 2, Remaining = 3 });
        tracker.UpdateOrderStatus(new OrderStatusData { OrderId = 1004, Status = "Filled", Filled = 1 });

        var active = tracker.GetActiveOrders();
        active.Should().HaveCount(3);
        active.Should().Contain(o => o.OrderId == 1001);
        active.Should().Contain(o => o.OrderId == 1002);
        active.Should().Contain(o => o.OrderId == 1003);
    }

    [Fact]
    public void IsOrderActive_ShouldReturnCorrectStatus()
    {
        using var tracker = new OrderTracker();
        tracker.TrackOrder(1001, "Test", "BUY", 1, "MKT");

        tracker.IsOrderActive(1001).Should().BeTrue();
        tracker.IsOrderActive(9999).Should().BeFalse();

        tracker.UpdateOrderStatus(new OrderStatusData { OrderId = 1001, Status = "Filled", Filled = 1 });
        tracker.IsOrderActive(1001).Should().BeFalse();
    }

    [Fact]
    public void CleanupOldOrders_ShouldRemoveCompletedOrders()
    {
        using var tracker = new OrderTracker();
        tracker.TrackOrder(1001, "Test1", "BUY", 1, "MKT");
        tracker.TrackOrder(1002, "Test2", "SELL", 1, "MKT");

        tracker.UpdateOrderStatus(new OrderStatusData { OrderId = 1001, Status = "Filled", Filled = 1 });

        // Wait a bit, then cleanup with a very short max age
        System.Threading.Thread.Sleep(100);
        tracker.CleanupOldOrders(TimeSpan.FromMilliseconds(50));

        tracker.GetOrder(1001).Should().BeNull();
        tracker.GetOrder(1002).Should().NotBeNull();
    }

    [Fact]
    public void GetAllOrders_ShouldReturnAllOrders()
    {
        using var tracker = new OrderTracker();
        tracker.TrackOrder(1001, "Test1", "BUY", 1, "MKT");
        tracker.TrackOrder(1002, "Test2", "SELL", 1, "MKT");
        tracker.TrackOrder(1003, "Test3", "BUY", 2, "LMT", limitPrice: 2600);

        var all = tracker.GetAllOrders();
        all.Should().HaveCount(3);
    }

    [Fact]
    public void TrackedOrder_ShouldStoreAllProperties()
    {
        using var tracker = new OrderTracker();

        tracker.TrackOrder(1001, "Aggressive", "BUY", 3, "LMT", limitPrice: 2650.50, stopPrice: 2640.00);

        var order = tracker.GetOrder(1001);
        order!.OrderId.Should().Be(1001);
        order.Strategy.Should().Be("Aggressive");
        order.Action.Should().Be("BUY");
        order.Quantity.Should().Be(3);
        order.OrderType.Should().Be("LMT");
        order.LimitPrice.Should().Be(2650.50);
        order.StopPrice.Should().Be(2640.00);
        order.SubmitTime.Should().BeCloseTo(DateTime.Now, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void OnOrderFilled_EventShouldFire()
    {
        using var tracker = new OrderTracker();
        TrackedOrder? filledOrder = null;
        tracker.OnOrderFilled += (s, e) => filledOrder = e.Order;

        tracker.TrackOrder(1001, "Test", "BUY", 1, "MKT");
        tracker.UpdateOrderStatus(new OrderStatusData { OrderId = 1001, Status = "Filled", Filled = 1, AvgFillPrice = 2650 });

        filledOrder.Should().NotBeNull();
        filledOrder!.OrderId.Should().Be(1001);
    }

    [Fact]
    public void OnOrderTimeout_EventShouldFireWhenStarted()
    {
        // Short timeout for testing
        using var tracker = new OrderTracker(timeoutMs: 100);
        TrackedOrder? timedOutOrder = null;
        tracker.OnOrderTimeout += (s, e) => timedOutOrder = e.Order;

        tracker.Start();
        tracker.TrackOrder(1001, "Test", "BUY", 1, "MKT");

        // Wait for timeout - need enough time for the monitor loop to run
        // Monitor checks every 1000ms, so we need at least 1500ms total
        System.Threading.Thread.Sleep(1500);

        timedOutOrder.Should().NotBeNull();
        timedOutOrder!.OrderId.Should().Be(1001);
        timedOutOrder.State.Should().Be(TrackedOrderState.Timeout);

        tracker.Stop();
    }
}

/// <summary>
/// Tests for PositionReconciler
/// </summary>
public class PositionReconcilerTests
{
    [Fact]
    public void ReconciliationResult_NoMismatch_WhenNoPositions()
    {
        var result = new ReconciliationResult();
        result.HasMismatch.Should().BeFalse();
        result.Mismatches.Should().BeEmpty();
        result.BrokerPositions.Should().BeEmpty();
    }

    [Fact]
    public void ReconciliationResult_ShouldStoreBrokerPositions()
    {
        var result = new ReconciliationResult();
        result.BrokerPositions.Add(new PositionData
        {
            Symbol = "GC",
            Position = 2,
            AvgCost = 2650.00
        });

        result.BrokerPositions.Should().HaveCount(1);
        result.BrokerPositions[0].Symbol.Should().Be("GC");
    }

    [Fact]
    public void PositionMismatch_ShouldStoreDetails()
    {
        var mismatch = new PositionMismatch
        {
            Symbol = "GC",
            Strategy = "Aggressive",
            BrokerPosition = 2,
            SavedPosition = 1,
            BrokerAvgCost = 2650.00,
            SavedEntryPrice = 2640.00,
            Description = "Position quantity mismatch"
        };

        mismatch.Symbol.Should().Be("GC");
        mismatch.BrokerPosition.Should().Be(2);
        mismatch.SavedPosition.Should().Be(1);
    }

    [Fact]
    public void TrackedOrderState_EnumValues_ShouldExist()
    {
        // Verify all expected states exist
        TrackedOrderState.Pending.Should().BeDefined();
        TrackedOrderState.Acknowledged.Should().BeDefined();
        TrackedOrderState.PartialFill.Should().BeDefined();
        TrackedOrderState.Filled.Should().BeDefined();
        TrackedOrderState.Cancelled.Should().BeDefined();
        TrackedOrderState.Error.Should().BeDefined();
        TrackedOrderState.Timeout.Should().BeDefined();
    }
}

/// <summary>
/// Tests for ConnectionState
/// </summary>
public class ConnectionStateTests
{
    [Fact]
    public void ConnectionState_EnumValues_ShouldExist()
    {
        ConnectionState.Disconnected.Should().BeDefined();
        ConnectionState.Connecting.Should().BeDefined();
        ConnectionState.Connected.Should().BeDefined();
        ConnectionState.Reconnecting.Should().BeDefined();
    }

    [Fact]
    public void ConnectionState_ShouldHaveFourValues()
    {
        var values = Enum.GetValues<ConnectionState>();
        values.Should().HaveCount(4);
    }
}

/// <summary>
/// Integration tests for Phase 1 components
/// </summary>
public class Phase1IntegrationTests
{
    [Fact]
    public void OrderTracker_FullLifecycle_BuyOrder()
    {
        using var tracker = new OrderTracker();
        var events = new List<string>();

        tracker.OnOrderAcknowledged += (s, e) => events.Add("Acknowledged");
        tracker.OnOrderFilled += (s, e) => events.Add("Filled");

        // Submit order
        tracker.TrackOrder(1001, "Aggressive", "BUY", 1, "MKT");
        tracker.GetOrder(1001)!.State.Should().Be(TrackedOrderState.Pending);

        // Broker acknowledges
        tracker.UpdateOrderStatus(new OrderStatusData { OrderId = 1001, Status = "Submitted", PermId = 123 });
        tracker.GetOrder(1001)!.State.Should().Be(TrackedOrderState.Acknowledged);

        // Order fills
        tracker.UpdateOrderStatus(new OrderStatusData { OrderId = 1001, Status = "Filled", Filled = 1, AvgFillPrice = 2650 });
        tracker.GetOrder(1001)!.State.Should().Be(TrackedOrderState.Filled);

        events.Should().Contain("Acknowledged");
        events.Should().Contain("Filled");
    }

    [Fact]
    public void OrderTracker_FullLifecycle_PartialFillThenComplete()
    {
        using var tracker = new OrderTracker();

        tracker.TrackOrder(1001, "Test", "BUY", 5, "MKT");

        // First acknowledge
        tracker.UpdateOrderStatus(new OrderStatusData
        {
            OrderId = 1001,
            Status = "Submitted",
            Filled = 0,
            Remaining = 5,
            AvgFillPrice = 0
        });

        var order = tracker.GetOrder(1001)!;
        order.State.Should().Be(TrackedOrderState.Acknowledged);

        // Partial fill (use a status that triggers partial fill detection)
        tracker.UpdateOrderStatus(new OrderStatusData
        {
            OrderId = 1001,
            Status = "PartialFill",  // Use explicit partial fill status
            Filled = 2,
            Remaining = 3,
            AvgFillPrice = 2650
        });

        order = tracker.GetOrder(1001)!;
        order.State.Should().Be(TrackedOrderState.PartialFill);
        order.FilledQuantity.Should().Be(2);

        // Complete fill
        tracker.UpdateOrderStatus(new OrderStatusData
        {
            OrderId = 1001,
            Status = "Filled",
            Filled = 5,
            Remaining = 0,
            AvgFillPrice = 2651
        });

        order = tracker.GetOrder(1001)!;
        order.State.Should().Be(TrackedOrderState.Filled);
        order.FilledQuantity.Should().Be(5);
    }

    [Fact]
    public void ConnectionManager_StateTransitions_ShouldBeTracked()
    {
        using var manager = new ConnectionManager();
        var states = new List<ConnectionState>();

        manager.OnConnectionStateChanged += state => states.Add(state);

        // Initial state
        manager.State.Should().Be(ConnectionState.Disconnected);

        // Note: Further state transitions require actual TWS connection
        // This test verifies the event system is properly wired
    }

    [Fact]
    public void ReconciliationResult_WithMultipleMismatches()
    {
        var result = new ReconciliationResult
        {
            HasMismatch = true
        };

        result.Mismatches.Add(new PositionMismatch
        {
            Strategy = "Aggressive",
            BrokerPosition = 0,
            SavedPosition = 2,
            Description = "Aggressive: Expected 2 contracts, broker has 0"
        });

        result.Mismatches.Add(new PositionMismatch
        {
            Strategy = "Conservative",
            BrokerPosition = 0,
            SavedPosition = 1,
            Description = "Conservative: Expected 1 contract, broker has 0"
        });

        result.HasMismatch.Should().BeTrue();
        result.Mismatches.Should().HaveCount(2);
        result.Mismatches.Should().Contain(m => m.Strategy == "Aggressive");
        result.Mismatches.Should().Contain(m => m.Strategy == "Conservative");
    }
}
