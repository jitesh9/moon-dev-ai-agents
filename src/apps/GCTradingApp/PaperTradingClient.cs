/*
 * Paper Trading Client for GC Trading Application
 * Simulates order execution without real broker connection for safe testing
 */

namespace GCTradingApp;

/// <summary>
/// Configuration for paper trading simulation
/// </summary>
public class PaperTradingConfig
{
    /// <summary>Slippage in basis points (1 bp = 0.01%)</summary>
    public double SlippageBps { get; set; } = 1.0;

    /// <summary>Simulated fill delay in milliseconds</summary>
    public int FillDelayMs { get; set; } = 100;

    /// <summary>Initial account balance for paper trading</summary>
    public double InitialBalance { get; set; } = 100000;

    /// <summary>GC contract multiplier (100 oz per contract)</summary>
    public double ContractMultiplier { get; set; } = 100;
}

/// <summary>
/// State of a paper trading order
/// </summary>
public enum PaperOrderState
{
    Pending,
    Submitted,
    Filled,
    Cancelled
}

/// <summary>
/// Represents an order in paper trading
/// </summary>
public class PaperOrder
{
    public int OrderId { get; set; }
    public string Action { get; set; } = "";  // "BUY" or "SELL"
    public decimal Quantity { get; set; }
    public string OrderType { get; set; } = "";  // "MKT", "STP"
    public double StopPrice { get; set; }
    public string OrderRef { get; set; } = "";  // Strategy name
    public DateTime SubmitTime { get; set; }
    public PaperOrderState State { get; set; }
    public decimal FilledQuantity { get; set; }
    public double AvgFillPrice { get; set; }
}

/// <summary>
/// Paper trading client that simulates order execution
/// Implements IOrderClient to be swappable with real IBKRClient
/// </summary>
public class PaperTradingClient : IOrderClient
{
    private readonly PaperTradingConfig _config;
    private readonly Dictionary<int, PaperOrder> _stopOrders = new();
    private readonly List<PaperOrder> _filledOrders = new();
    private readonly object _lock = new();
    private readonly Random _random = new();
    private readonly SynchronizationContext? _syncContext;

    private int _nextOrderId = 1000;
    private BarData? _currentBar;

    // Position tracking
    private decimal _position = 0;
    private double _avgCost = 0;
    private double _realizedPnL = 0;
    private double _unrealizedPnL = 0;
    private double _balance;

    // Events (from IOrderClient)
    public event Action<OrderStatusData>? OnOrderStatus;
    public event Action<ExecutionData>? OnExecution;
    public event Action<PositionData>? OnPosition;

    // Additional events for paper trading
    public event Action<string>? OnLog;
    public event Action<PaperTradingState>? OnStateChanged;

    /// <summary>
    /// Gets the next available order ID
    /// </summary>
    public int NextOrderId => Interlocked.Increment(ref _nextOrderId);

    /// <summary>
    /// Gets current paper trading state
    /// </summary>
    public PaperTradingState GetState() => new PaperTradingState
    {
        Position = _position,
        AvgCost = _avgCost,
        RealizedPnL = _realizedPnL,
        UnrealizedPnL = _unrealizedPnL,
        Balance = _balance,
        Trades = _filledOrders.Select(o => new PaperTradeRecord
        {
            Time = o.SubmitTime,
            Action = o.Action,
            Quantity = o.FilledQuantity,
            Price = o.AvgFillPrice,
            Strategy = o.OrderRef
        }).ToList()
    };

    public PaperTradingClient(PaperTradingConfig? config = null)
    {
        _config = config ?? new PaperTradingConfig();
        _balance = _config.InitialBalance;
        _syncContext = SynchronizationContext.Current;
        Log($"Paper trading initialized with balance: ${_balance:N0}, slippage: {_config.SlippageBps} bps");
    }

    /// <summary>
    /// Restore state from saved paper trading state
    /// </summary>
    public void RestoreState(PaperTradingState state)
    {
        lock (_lock)
        {
            _position = state.Position;
            _avgCost = state.AvgCost;
            _realizedPnL = state.RealizedPnL;
            _balance = state.Balance;
            Log($"Restored paper state: Position={_position}, AvgCost={_avgCost:F2}, RealizedPnL=${_realizedPnL:F2}");
        }
    }

    /// <summary>
    /// Reset paper trading account to initial state
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _position = 0;
            _avgCost = 0;
            _realizedPnL = 0;
            _unrealizedPnL = 0;
            _balance = _config.InitialBalance;
            _stopOrders.Clear();
            _filledOrders.Clear();
            Log($"Paper trading reset. Balance: ${_balance:N0}");
            NotifyStateChanged();
        }
    }

    /// <summary>
    /// Process a new bar - checks for stop order triggers
    /// </summary>
    public void ProcessBar(BarData bar)
    {
        lock (_lock)
        {
            _currentBar = bar;
            UpdateUnrealizedPnL(bar.Close);

            // Check each stop order for trigger
            var triggeredOrders = new List<int>();

            foreach (var kvp in _stopOrders)
            {
                var order = kvp.Value;
                bool triggered = false;

                if (order.Action == "SELL")
                {
                    // Sell stop triggers when price falls to or below stop
                    triggered = bar.Low <= order.StopPrice;
                }
                else // BUY stop
                {
                    // Buy stop triggers when price rises to or above stop
                    triggered = bar.High >= order.StopPrice;
                }

                if (triggered)
                {
                    triggeredOrders.Add(kvp.Key);
                    // Fill at stop price with slippage (worse execution on stop)
                    var fillPrice = CalculateSlippage(order.StopPrice, order.Action);
                    Log($"Stop order {order.OrderId} triggered at {bar.Close:F2}, filling at {fillPrice:F2}");
                    SimulateFill(order, fillPrice);
                }
            }

            // Remove triggered orders
            foreach (var orderId in triggeredOrders)
            {
                _stopOrders.Remove(orderId);
            }
        }
    }

    /// <summary>
    /// Place a market order - fills immediately at current price with slippage
    /// </summary>
    public void PlaceMarketOrder(string action, decimal quantity, string orderRef = "")
    {
        var orderId = NextOrderId;
        var order = new PaperOrder
        {
            OrderId = orderId,
            Action = action,
            Quantity = quantity,
            OrderType = "MKT",
            OrderRef = orderRef,
            SubmitTime = DateTime.Now,
            State = PaperOrderState.Pending
        };

        Log($"Market order placed: {action} {quantity} @ MKT (OrderRef: {orderRef})");

        // Simulate async order flow
        Task.Run(async () =>
        {
            // Brief delay for "submission"
            await Task.Delay(10);

            // Fire submitted status
            PostToUI(() => OnOrderStatus?.Invoke(new OrderStatusData
            {
                OrderId = orderId,
                Status = "Submitted",
                Filled = 0,
                Remaining = quantity
            }));

            // Wait for configured fill delay
            await Task.Delay(_config.FillDelayMs);

            // Fill at current price with slippage
            lock (_lock)
            {
                if (_currentBar == null)
                {
                    Log($"Warning: No current bar data, using dummy price for fill");
                    SimulateFill(order, 2000.0); // Fallback
                }
                else
                {
                    var fillPrice = CalculateSlippage(_currentBar.Close, action);
                    SimulateFill(order, fillPrice);
                }
            }
        });
    }

    /// <summary>
    /// Place a stop order - waits for price trigger
    /// </summary>
    public void PlaceStopOrder(string action, decimal quantity, double stopPrice, string orderRef = "")
    {
        var orderId = NextOrderId;
        var order = new PaperOrder
        {
            OrderId = orderId,
            Action = action,
            Quantity = quantity,
            OrderType = "STP",
            StopPrice = stopPrice,
            OrderRef = orderRef,
            SubmitTime = DateTime.Now,
            State = PaperOrderState.Submitted
        };

        lock (_lock)
        {
            _stopOrders[orderId] = order;
        }

        Log($"Stop order placed: {action} {quantity} @ {stopPrice:F2} (OrderRef: {orderRef})");

        // Fire submitted status
        PostToUI(() => OnOrderStatus?.Invoke(new OrderStatusData
        {
            OrderId = orderId,
            Status = "Submitted",
            Filled = 0,
            Remaining = quantity
        }));
    }

    /// <summary>
    /// Update an existing stop order price
    /// </summary>
    public void UpdateStop(int orderId, double newStopPrice, decimal quantity, string action = "SELL")
    {
        lock (_lock)
        {
            if (_stopOrders.TryGetValue(orderId, out var order))
            {
                var oldPrice = order.StopPrice;
                order.StopPrice = newStopPrice;
                order.Quantity = quantity;
                order.Action = action;
                Log($"Stop order {orderId} updated: {oldPrice:F2} -> {newStopPrice:F2}");
            }
            else
            {
                // Create new stop order if doesn't exist
                var newOrder = new PaperOrder
                {
                    OrderId = orderId,
                    Action = action,
                    Quantity = quantity,
                    OrderType = "STP",
                    StopPrice = newStopPrice,
                    OrderRef = "",
                    SubmitTime = DateTime.Now,
                    State = PaperOrderState.Submitted
                };
                _stopOrders[orderId] = newOrder;
                Log($"Stop order {orderId} created at {newStopPrice:F2}");
            }
        }
    }

    /// <summary>
    /// Cancel an order
    /// </summary>
    public void CancelOrder(int orderId)
    {
        lock (_lock)
        {
            if (_stopOrders.TryGetValue(orderId, out var order))
            {
                _stopOrders.Remove(orderId);
                order.State = PaperOrderState.Cancelled;
                Log($"Order {orderId} cancelled");

                PostToUI(() => OnOrderStatus?.Invoke(new OrderStatusData
                {
                    OrderId = orderId,
                    Status = "Cancelled",
                    Filled = order.FilledQuantity,
                    Remaining = order.Quantity - order.FilledQuantity
                }));
            }
        }
    }

    /// <summary>
    /// Calculate fill price with slippage
    /// </summary>
    private double CalculateSlippage(double price, string action)
    {
        // Base slippage from config
        var slippageAmount = price * (_config.SlippageBps / 10000.0);

        // Add random component (+/- 20% of base slippage)
        slippageAmount *= (0.8 + _random.NextDouble() * 0.4);

        // Apply direction: buys get worse (higher), sells get worse (lower)
        return action == "BUY"
            ? price + slippageAmount
            : price - slippageAmount;
    }

    /// <summary>
    /// Simulate order fill and update position
    /// </summary>
    private void SimulateFill(PaperOrder order, double fillPrice)
    {
        order.State = PaperOrderState.Filled;
        order.FilledQuantity = order.Quantity;
        order.AvgFillPrice = fillPrice;

        // Update position
        if (order.Action == "BUY")
        {
            // Opening or adding to long position
            if (_position >= 0)
            {
                // Adding to long or opening new long
                var totalCost = (_avgCost * (double)_position) + (fillPrice * (double)order.Quantity);
                _position += order.Quantity;
                _avgCost = _position > 0 ? totalCost / (double)_position : 0;
            }
            else
            {
                // Covering short position
                var coveredQty = Math.Min(order.Quantity, Math.Abs(_position));
                var pnl = (_avgCost - fillPrice) * (double)coveredQty * _config.ContractMultiplier;
                _realizedPnL += pnl;
                _balance += pnl;

                _position += order.Quantity;
                if (_position > 0)
                {
                    // Now long, set new avg cost
                    _avgCost = fillPrice;
                }
                else if (_position == 0)
                {
                    _avgCost = 0;
                }
            }
        }
        else // SELL
        {
            // Closing or opening short position
            if (_position > 0)
            {
                // Closing long position
                var closedQty = Math.Min(order.Quantity, _position);
                var pnl = (fillPrice - _avgCost) * (double)closedQty * _config.ContractMultiplier;
                _realizedPnL += pnl;
                _balance += pnl;

                _position -= order.Quantity;
                if (_position < 0)
                {
                    // Now short, set new avg cost
                    _avgCost = fillPrice;
                }
                else if (_position == 0)
                {
                    _avgCost = 0;
                }
            }
            else
            {
                // Adding to short or opening new short
                var totalCost = (_avgCost * Math.Abs((double)_position)) + (fillPrice * (double)order.Quantity);
                _position -= order.Quantity;
                _avgCost = _position < 0 ? totalCost / Math.Abs((double)_position) : 0;
            }
        }

        _filledOrders.Add(order);
        Log($"FILLED: {order.Action} {order.FilledQuantity} @ {fillPrice:F2}, Position: {_position}, AvgCost: {_avgCost:F2}, RealizedPnL: ${_realizedPnL:F2}");

        // Fire order status
        PostToUI(() => OnOrderStatus?.Invoke(new OrderStatusData
        {
            OrderId = order.OrderId,
            Status = "Filled",
            Filled = order.FilledQuantity,
            Remaining = 0,
            AvgFillPrice = fillPrice
        }));

        // Fire execution
        PostToUI(() => OnExecution?.Invoke(new ExecutionData
        {
            ExecId = Guid.NewGuid().ToString(),
            Symbol = "GC",
            Side = order.Action == "BUY" ? "BOT" : "SLD",
            Shares = order.FilledQuantity,
            Price = fillPrice,
            OrderRef = order.OrderRef
        }));

        // Fire position update
        PostToUI(() => OnPosition?.Invoke(new PositionData
        {
            Account = "Paper",
            Symbol = "GC",
            Position = _position,
            AvgCost = _avgCost,
            UnrealizedPnL = _unrealizedPnL,
            RealizedPnL = _realizedPnL
        }));

        NotifyStateChanged();
    }

    /// <summary>
    /// Update unrealized P&L based on current price
    /// </summary>
    private void UpdateUnrealizedPnL(double currentPrice)
    {
        if (_position != 0)
        {
            if (_position > 0)
            {
                _unrealizedPnL = (currentPrice - _avgCost) * (double)_position * _config.ContractMultiplier;
            }
            else
            {
                _unrealizedPnL = (_avgCost - currentPrice) * Math.Abs((double)_position) * _config.ContractMultiplier;
            }
        }
        else
        {
            _unrealizedPnL = 0;
        }
    }

    private void NotifyStateChanged()
    {
        PostToUI(() => OnStateChanged?.Invoke(GetState()));
    }

    private void Log(string message)
    {
        Logger.Info($"[PaperTrading] {message}");
        PostToUI(() => OnLog?.Invoke(message));
    }

    private void PostToUI(Action action)
    {
        if (_syncContext != null)
            _syncContext.Post(_ => action(), null);
        else
            action();
    }
}

/// <summary>
/// State of paper trading account for persistence
/// </summary>
public class PaperTradingState
{
    public decimal Position { get; set; }
    public double AvgCost { get; set; }
    public double RealizedPnL { get; set; }
    public double UnrealizedPnL { get; set; }
    public double Balance { get; set; }
    public List<PaperTradeRecord> Trades { get; set; } = new();
}

/// <summary>
/// Record of a paper trade
/// </summary>
public class PaperTradeRecord
{
    public DateTime Time { get; set; }
    public string Action { get; set; } = "";
    public decimal Quantity { get; set; }
    public double Price { get; set; }
    public double PnL { get; set; }
    public string Strategy { get; set; } = "";
}
