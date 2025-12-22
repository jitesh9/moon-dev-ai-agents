/*
 * Order Tracker for IBKR TWS
 * Tracks order lifecycle with timeout detection and partial fill handling
 */

namespace GCTradingApp;

/// <summary>
/// Tracked order state
/// </summary>
public enum TrackedOrderState
{
    Pending,        // Order submitted, waiting for acknowledgment
    Acknowledged,   // Order acknowledged by broker
    PartialFill,    // Partially filled
    Filled,         // Completely filled
    Cancelled,      // Cancelled
    Error,          // Order error
    Timeout         // No response within timeout period
}

/// <summary>
/// Tracked order information
/// </summary>
public class TrackedOrder
{
    public int OrderId { get; set; }
    public long PermId { get; set; }
    public string Strategy { get; set; } = "";
    public string Action { get; set; } = "";
    public decimal Quantity { get; set; }
    public string OrderType { get; set; } = "";
    public double LimitPrice { get; set; }
    public double StopPrice { get; set; }
    public TrackedOrderState State { get; set; } = TrackedOrderState.Pending;
    public DateTime SubmitTime { get; set; }
    public DateTime? AckTime { get; set; }
    public DateTime? FillTime { get; set; }
    public decimal FilledQuantity { get; set; }
    public decimal RemainingQuantity { get; set; }
    public double AvgFillPrice { get; set; }
    public string LastStatus { get; set; } = "";
    public string ErrorMessage { get; set; } = "";
}

/// <summary>
/// Order tracker event args
/// </summary>
public class OrderTrackerEventArgs : EventArgs
{
    public TrackedOrder Order { get; set; } = null!;
    public string Message { get; set; } = "";
}

/// <summary>
/// Tracks orders with confirmation, timeout detection, and partial fill handling
/// </summary>
public class OrderTracker : IDisposable
{
    private readonly Dictionary<int, TrackedOrder> _orders = new();
    private readonly object _lock = new();
    private readonly int _timeoutMs;
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;

    // Events
    public event Action<string>? OnLog;
    public event EventHandler<OrderTrackerEventArgs>? OnOrderAcknowledged;
    public event EventHandler<OrderTrackerEventArgs>? OnOrderFilled;
    public event EventHandler<OrderTrackerEventArgs>? OnOrderPartialFill;
    public event EventHandler<OrderTrackerEventArgs>? OnOrderCancelled;
    public event EventHandler<OrderTrackerEventArgs>? OnOrderError;
    public event EventHandler<OrderTrackerEventArgs>? OnOrderTimeout;

    /// <summary>
    /// Default timeout is 30 seconds
    /// </summary>
    public OrderTracker(int timeoutMs = 30000)
    {
        _timeoutMs = timeoutMs;
    }

    /// <summary>
    /// Start the order monitoring task
    /// </summary>
    public void Start()
    {
        _monitorCts = new CancellationTokenSource();
        _monitorTask = Task.Run(() => MonitorOrdersAsync(_monitorCts.Token));
        Log("Order tracker started");
    }

    /// <summary>
    /// Stop the order monitoring task
    /// </summary>
    public void Stop()
    {
        _monitorCts?.Cancel();
        _monitorCts?.Dispose();
        _monitorCts = null;
        Log("Order tracker stopped");
    }

    /// <summary>
    /// Track a new order
    /// </summary>
    public void TrackOrder(int orderId, string strategy, string action, decimal quantity,
        string orderType, double limitPrice = 0, double stopPrice = 0)
    {
        var order = new TrackedOrder
        {
            OrderId = orderId,
            Strategy = strategy,
            Action = action,
            Quantity = quantity,
            RemainingQuantity = quantity,
            OrderType = orderType,
            LimitPrice = limitPrice,
            StopPrice = stopPrice,
            State = TrackedOrderState.Pending,
            SubmitTime = DateTime.Now
        };

        lock (_lock)
        {
            _orders[orderId] = order;
        }

        Log($"Tracking order {orderId}: {action} {quantity} {orderType}");
    }

    /// <summary>
    /// Update order from IBKR order status callback
    /// </summary>
    public void UpdateOrderStatus(OrderStatusData status)
    {
        TrackedOrder? order;
        lock (_lock)
        {
            if (!_orders.TryGetValue(status.OrderId, out order))
            {
                // Order not tracked - might be from previous session or manual order
                return;
            }
        }

        var previousState = order.State;
        order.PermId = status.PermId;
        order.FilledQuantity = status.Filled;
        order.RemainingQuantity = status.Remaining;
        order.AvgFillPrice = status.AvgFillPrice;
        order.LastStatus = status.Status;

        switch (status.Status.ToUpperInvariant())
        {
            case "PRESUBMITTED":
            case "SUBMITTED":
                if (order.State == TrackedOrderState.Pending)
                {
                    order.State = TrackedOrderState.Acknowledged;
                    order.AckTime = DateTime.Now;
                    OnOrderAcknowledged?.Invoke(this, new OrderTrackerEventArgs
                    {
                        Order = order,
                        Message = $"Order {order.OrderId} acknowledged by broker"
                    });
                    Log($"Order {order.OrderId} acknowledged (PermId: {status.PermId})");
                }
                break;

            case "FILLED":
                order.State = TrackedOrderState.Filled;
                order.FillTime = DateTime.Now;
                OnOrderFilled?.Invoke(this, new OrderTrackerEventArgs
                {
                    Order = order,
                    Message = $"Order {order.OrderId} filled: {status.Filled} @ {status.AvgFillPrice:F2}"
                });
                Log($"Order {order.OrderId} FILLED: {status.Filled} @ {status.AvgFillPrice:F2}");
                break;

            case "CANCELLED":
            case "CANCELED":
                order.State = TrackedOrderState.Cancelled;
                OnOrderCancelled?.Invoke(this, new OrderTrackerEventArgs
                {
                    Order = order,
                    Message = $"Order {order.OrderId} cancelled"
                });
                Log($"Order {order.OrderId} cancelled");
                break;

            case "INACTIVE":
                // Check if it's a partial fill scenario
                if (status.Filled > 0)
                {
                    order.State = TrackedOrderState.PartialFill;
                    OnOrderPartialFill?.Invoke(this, new OrderTrackerEventArgs
                    {
                        Order = order,
                        Message = $"Order {order.OrderId} partial fill: {status.Filled}/{order.Quantity}"
                    });
                    Log($"Order {order.OrderId} partial fill: {status.Filled}/{order.Quantity}");
                }
                break;

            case "PENDINGSUBMIT":
            case "PENDINGCANCEL":
                // Transitional states - no action needed
                break;

            default:
                // Check for partial fills
                if (status.Filled > 0 && status.Remaining > 0 && previousState != TrackedOrderState.PartialFill)
                {
                    order.State = TrackedOrderState.PartialFill;
                    OnOrderPartialFill?.Invoke(this, new OrderTrackerEventArgs
                    {
                        Order = order,
                        Message = $"Order {order.OrderId} partial fill: {status.Filled}/{order.Quantity}"
                    });
                    Log($"Order {order.OrderId} partial fill: {status.Filled}/{order.Quantity}");
                }
                break;
        }
    }

    /// <summary>
    /// Update order from IBKR error callback
    /// </summary>
    public void UpdateOrderError(int orderId, int errorCode, string errorMsg)
    {
        TrackedOrder? order;
        lock (_lock)
        {
            if (!_orders.TryGetValue(orderId, out order))
            {
                return;
            }
        }

        // Some error codes are informational, not actual errors
        // 399: Order warning
        // 2100-2199: Informational
        if (errorCode == 399 || (errorCode >= 2100 && errorCode <= 2199))
        {
            Log($"Order {orderId} info: [{errorCode}] {errorMsg}");
            return;
        }

        // Order rejection error codes
        // 201: Order rejected
        // 202: Order cancelled
        // 203: Contract not found
        // 10147: Duplicate order
        if (errorCode == 201 || errorCode == 202 || errorCode == 203 || errorCode == 10147)
        {
            order.State = TrackedOrderState.Error;
            order.ErrorMessage = $"[{errorCode}] {errorMsg}";
            OnOrderError?.Invoke(this, new OrderTrackerEventArgs
            {
                Order = order,
                Message = $"Order {orderId} error: [{errorCode}] {errorMsg}"
            });
            Log($"Order {orderId} ERROR: [{errorCode}] {errorMsg}");
        }
    }

    /// <summary>
    /// Get tracked order by ID
    /// </summary>
    public TrackedOrder? GetOrder(int orderId)
    {
        lock (_lock)
        {
            return _orders.TryGetValue(orderId, out var order) ? order : null;
        }
    }

    /// <summary>
    /// Get all tracked orders
    /// </summary>
    public List<TrackedOrder> GetAllOrders()
    {
        lock (_lock)
        {
            return _orders.Values.ToList();
        }
    }

    /// <summary>
    /// Get pending orders (not yet acknowledged)
    /// </summary>
    public List<TrackedOrder> GetPendingOrders()
    {
        lock (_lock)
        {
            return _orders.Values.Where(o => o.State == TrackedOrderState.Pending).ToList();
        }
    }

    /// <summary>
    /// Get active orders (pending, acknowledged, or partial fill)
    /// </summary>
    public List<TrackedOrder> GetActiveOrders()
    {
        lock (_lock)
        {
            return _orders.Values.Where(o =>
                o.State == TrackedOrderState.Pending ||
                o.State == TrackedOrderState.Acknowledged ||
                o.State == TrackedOrderState.PartialFill).ToList();
        }
    }

    /// <summary>
    /// Check if an order is still active
    /// </summary>
    public bool IsOrderActive(int orderId)
    {
        lock (_lock)
        {
            if (!_orders.TryGetValue(orderId, out var order))
                return false;

            return order.State == TrackedOrderState.Pending ||
                   order.State == TrackedOrderState.Acknowledged ||
                   order.State == TrackedOrderState.PartialFill;
        }
    }

    /// <summary>
    /// Remove completed/cancelled orders older than specified age
    /// </summary>
    public void CleanupOldOrders(TimeSpan maxAge)
    {
        var cutoff = DateTime.Now - maxAge;
        lock (_lock)
        {
            var toRemove = _orders.Where(kvp =>
                (kvp.Value.State == TrackedOrderState.Filled ||
                 kvp.Value.State == TrackedOrderState.Cancelled ||
                 kvp.Value.State == TrackedOrderState.Error ||
                 kvp.Value.State == TrackedOrderState.Timeout) &&
                kvp.Value.SubmitTime < cutoff)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var id in toRemove)
            {
                _orders.Remove(id);
            }

            if (toRemove.Count > 0)
            {
                Log($"Cleaned up {toRemove.Count} old orders");
            }
        }
    }

    private async Task MonitorOrdersAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, token);  // Check every second

                List<TrackedOrder> timedOutOrders;
                lock (_lock)
                {
                    var now = DateTime.Now;
                    timedOutOrders = _orders.Values
                        .Where(o => o.State == TrackedOrderState.Pending &&
                                   (now - o.SubmitTime).TotalMilliseconds > _timeoutMs)
                        .ToList();
                }

                foreach (var order in timedOutOrders)
                {
                    order.State = TrackedOrderState.Timeout;
                    order.ErrorMessage = $"No response within {_timeoutMs / 1000} seconds";
                    OnOrderTimeout?.Invoke(this, new OrderTrackerEventArgs
                    {
                        Order = order,
                        Message = $"Order {order.OrderId} TIMEOUT: No broker response after {_timeoutMs / 1000}s"
                    });
                    Log($"Order {order.OrderId} TIMEOUT: No broker response after {_timeoutMs / 1000}s");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"Monitor error: {ex.Message}");
            }
        }
    }

    private void Log(string message)
    {
        Logger.Info($"[OrderTracker] {message}");
        OnLog?.Invoke(message);
    }

    public void Dispose()
    {
        Stop();
    }
}
