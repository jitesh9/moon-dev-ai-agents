/*
 * Risk-Aware Order Client Wrapper
 * Wraps IOrderClient to enforce risk checks before order placement
 */

namespace GCTradingApp;

/// <summary>
/// Wraps an IOrderClient to enforce risk management checks before placing orders
/// </summary>
public class RiskAwareOrderClient : IOrderClient
{
    private readonly IOrderClient _innerClient;
    private readonly RiskManager? _riskManager;
    private readonly string _strategyName;

    public int NextOrderId => _innerClient.NextOrderId;

    public event Action<OrderStatusData>? OnOrderStatus
    {
        add => _innerClient.OnOrderStatus += value;
        remove => _innerClient.OnOrderStatus -= value;
    }

    public event Action<ExecutionData>? OnExecution
    {
        add => _innerClient.OnExecution += value;
        remove => _innerClient.OnExecution -= value;
    }

    public event Action<PositionData>? OnPosition
    {
        add => _innerClient.OnPosition += value;
        remove => _innerClient.OnPosition -= value;
    }

    public RiskAwareOrderClient(IOrderClient innerClient, RiskManager? riskManager, string strategyName)
    {
        _innerClient = innerClient ?? throw new ArgumentNullException(nameof(innerClient));
        _riskManager = riskManager;
        _strategyName = strategyName ?? throw new ArgumentNullException(nameof(strategyName));
    }

    public void PlaceMarketOrder(string action, decimal quantity, string orderRef = "")
    {
        // Check risk before placing order
        if (_riskManager != null)
        {
            var riskCheck = _riskManager.CheckNewTrade(_strategyName, action, quantity);
            
            if (!riskCheck.Allowed)
            {
                Logger.Warn($"[RiskAwareOrderClient] Order REJECTED by risk manager: {riskCheck.Reason}");
                Logger.Warn($"[RiskAwareOrderClient] Strategy: {_strategyName}, Action: {action}, Requested Qty: {quantity}");
                return; // Reject the order
            }

            // Use adjusted quantity if risk manager modified it
            if (riskCheck.AdjustedQuantity != quantity && !string.IsNullOrEmpty(riskCheck.Reason))
            {
                Logger.Info($"[RiskAwareOrderClient] Order quantity adjusted: {quantity} -> {riskCheck.AdjustedQuantity}. Reason: {riskCheck.Reason}");
                quantity = riskCheck.AdjustedQuantity;
            }
        }

        // Place order through inner client
        _innerClient.PlaceMarketOrder(action, quantity, orderRef);
    }

    public void PlaceStopOrder(string action, decimal quantity, double stopPrice, string orderRef = "")
    {
        // For stop orders (exits), we still check risk but may be more lenient
        // Exit orders are generally lower risk than entries
        if (_riskManager != null)
        {
            // Check if trading is paused (but allow exits even if paused)
            if (_riskManager.IsTradingPaused)
            {
                // Allow exit orders even when paused (to close positions)
                // But check position limits
                var riskCheck = _riskManager.CheckNewTrade(_strategyName, action, quantity);
                if (!riskCheck.Allowed && !riskCheck.Reason.Contains("paused"))
                {
                    Logger.Warn($"[RiskAwareOrderClient] Stop order REJECTED: {riskCheck.Reason}");
                    return;
                }
            }
            else
            {
                // Normal risk check for stop orders
                var riskCheck = _riskManager.CheckNewTrade(_strategyName, action, quantity);
                if (!riskCheck.Allowed)
                {
                    Logger.Warn($"[RiskAwareOrderClient] Stop order REJECTED: {riskCheck.Reason}");
                    return;
                }
                quantity = riskCheck.AdjustedQuantity;
            }
        }

        _innerClient.PlaceStopOrder(action, quantity, stopPrice, orderRef);
    }

    public void UpdateStop(int orderId, double newStopPrice, decimal quantity, string action = "SELL")
    {
        // Stop updates are generally safe, but check if trading is paused
        if (_riskManager != null && _riskManager.IsTradingPaused)
        {
            // Allow stop updates even when paused (to protect positions)
            // This is intentional - we want to update stops even during pauses
        }

        _innerClient.UpdateStop(orderId, newStopPrice, quantity, action);
    }

    public void CancelOrder(int orderId)
    {
        // Cancellations are always allowed
        _innerClient.CancelOrder(orderId);
    }
}

