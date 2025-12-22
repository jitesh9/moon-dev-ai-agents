/*
 * IOrderClient Interface for GC Trading Application
 * Abstracts order operations to allow swapping between real IBKR and paper trading
 */

namespace GCTradingApp;

/// <summary>
/// Interface for order placement and management.
/// Implemented by IBKRClient (real trading) and PaperTradingClient (simulated).
/// </summary>
public interface IOrderClient
{
    /// <summary>
    /// Gets the next available order ID
    /// </summary>
    int NextOrderId { get; }

    /// <summary>
    /// Place a market order
    /// </summary>
    /// <param name="action">"BUY" or "SELL"</param>
    /// <param name="quantity">Number of contracts</param>
    /// <param name="orderRef">Strategy name for tracking</param>
    void PlaceMarketOrder(string action, decimal quantity, string orderRef = "");

    /// <summary>
    /// Place a stop order
    /// </summary>
    /// <param name="action">"BUY" or "SELL"</param>
    /// <param name="quantity">Number of contracts</param>
    /// <param name="stopPrice">Stop trigger price</param>
    /// <param name="orderRef">Strategy name for tracking</param>
    void PlaceStopOrder(string action, decimal quantity, double stopPrice, string orderRef = "");

    /// <summary>
    /// Update an existing stop order price
    /// </summary>
    /// <param name="orderId">Order ID to update</param>
    /// <param name="newStopPrice">New stop price</param>
    /// <param name="quantity">Order quantity</param>
    /// <param name="action">"BUY" or "SELL"</param>
    void UpdateStop(int orderId, double newStopPrice, decimal quantity, string action = "SELL");

    /// <summary>
    /// Cancel an order
    /// </summary>
    /// <param name="orderId">Order ID to cancel</param>
    void CancelOrder(int orderId);

    /// <summary>
    /// Fired when order status changes (Submitted, Filled, Cancelled, etc.)
    /// </summary>
    event Action<OrderStatusData>? OnOrderStatus;

    /// <summary>
    /// Fired when an execution (fill) occurs
    /// </summary>
    event Action<ExecutionData>? OnExecution;

    /// <summary>
    /// Fired when position changes
    /// </summary>
    event Action<PositionData>? OnPosition;
}
