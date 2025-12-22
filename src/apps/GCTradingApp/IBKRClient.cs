/*
 * IBKR TWS API Client Wrapper
 * Handles connection, market data, and order execution for GC futures
 */

using IBApi;

namespace GCTradingApp;

/// <summary>
/// IBKR TWS API Client wrapper with event-based callbacks
/// </summary>
public class IBKRClient : EWrapper, IOrderClient
{
    private EClientSocket _clientSocket;
    private EReaderSignal _readerSignal;
    private EReader? _reader;
    private int _nextOrderId;
    private bool _isConnected;
    private readonly SynchronizationContext? _syncContext;

    // GC Contract definition
    private Contract? _gcContract;
    private int _gcReqId = 1001;

    // Events
    public event Action? OnConnected;
    public event Action? OnDisconnected;
    public event Action<int, int, string>? OnError;
    public event Action<OrderStatusData>? OnOrderStatus;
    public event Action<OpenOrderData>? OnOpenOrder;
    public event Action<ExecutionData>? OnExecution;
    public event Action<PositionData>? OnPosition;
    public event Action<string, string, string>? OnAccountUpdate;
    public event Action<BarData>? OnRealtimeBar;
    public event Action<BarData>? OnHistoricalBar;

    /// <summary>
    /// Thread-safe order ID generator
    /// </summary>
    public int NextOrderId => Interlocked.Increment(ref _nextOrderId);

    public IBKRClient()
    {
        try
        {
            Logger.Info("IBKRClient constructor starting...");

            _readerSignal = new EReaderMonitorSignal();
            Logger.Debug("EReaderMonitorSignal created");

            _clientSocket = new EClientSocket(this, _readerSignal);
            Logger.Debug("EClientSocket created");

            _syncContext = SynchronizationContext.Current;
            Logger.Debug($"SynchronizationContext captured: {_syncContext != null}");

            // Define GC futures contract
            var expiry = GetNextGCExpiry();
            Logger.Info($"GC contract expiry calculated: {expiry}");

            _gcContract = new Contract
            {
                Symbol = "GC",
                SecType = "FUT",
                Exchange = "COMEX",
                Currency = "USD",
                LastTradeDateOrContractMonth = expiry
            };

            Logger.Info("IBKRClient constructor completed successfully");
        }
        catch (Exception ex)
        {
            Logger.Error("IBKRClient constructor failed", ex);
            throw;
        }
    }

    private string GetNextGCExpiry()
    {
        // GC futures expire on third-to-last business day of the contract month
        // Contract months: Feb (G), Apr (J), Jun (M), Aug (Q), Oct (V), Dec (Z)
        var now = DateTime.Now;
        var year = now.Year;
        var month = now.Month;
        var day = now.Day;

        int[] contractMonths = { 2, 4, 6, 8, 10, 12 };

        // Check if current month is a contract month and contract hasn't expired yet
        // GC expires around the 3rd-to-last business day, roughly day 25-27
        // Use day 25 as a safe cutoff - after that, use next contract
        bool isContractMonth = contractMonths.Contains(month);
        bool currentMonthStillValid = isContractMonth && day <= 25;

        int contractMonth;
        if (currentMonthStillValid)
        {
            // Use current month's contract
            contractMonth = month;
        }
        else
        {
            // Find next contract month after current month
            contractMonth = contractMonths.FirstOrDefault(m => m > month);
            if (contractMonth == 0)
            {
                // No more contract months this year, use February next year
                contractMonth = 2;
                year++;
            }
        }

        Logger.Debug($"GetNextGCExpiry: now={now:yyyy-MM-dd}, contractMonth={contractMonth}, year={year}");
        return $"{year}{contractMonth:D2}";
    }

    public void Connect(string host, int port, int clientId)
    {
        try
        {
            Logger.Info($"IBKRClient.Connect() called: host={host}, port={port}, clientId={clientId}");

            Logger.Debug("Calling eConnect...");
            _clientSocket.eConnect(host, port, clientId);
            Logger.Info($"eConnect completed. IsConnected: {_clientSocket.IsConnected()}");

            // Start the reader thread
            Logger.Debug("Creating EReader...");
            _reader = new EReader(_clientSocket, _readerSignal);
            Logger.Debug("Starting EReader...");
            _reader.Start();
            Logger.Info("EReader started successfully");

            // Process messages in background
            Logger.Debug("Starting message processing thread...");
            new Thread(() =>
            {
                Logger.Debug("Message processing thread started");
                try
                {
                    while (_clientSocket.IsConnected())
                    {
                        _readerSignal.waitForSignal();
                        _reader.processMsgs();
                    }
                    Logger.Info("Message processing thread exiting - socket disconnected");
                }
                catch (Exception ex)
                {
                    Logger.Error("Message processing thread error", ex);
                }
            })
            { IsBackground = true, Name = "IBKRMessageProcessor" }.Start();

            Logger.Info("Connect() method completed");
        }
        catch (Exception ex)
        {
            Logger.Error("IBKRClient.Connect() failed", ex);
            throw;
        }
    }

    public void Disconnect()
    {
        _clientSocket.eDisconnect();
        _isConnected = false;
    }

    public void RequestAccountUpdates()
    {
        _clientSocket.reqAccountUpdates(true, "");
        _clientSocket.reqPositions();
    }

    public void SubscribeToGCData()
    {
        if (_gcContract == null) return;

        // Request 5-second realtime bars for GC
        _clientSocket.reqRealTimeBars(_gcReqId, _gcContract, 5, "TRADES", false, new List<TagValue>());
    }

    public void RequestHistoricalData(int reqId, string duration, string barSize)
    {
        if (_gcContract == null) return;

        _clientSocket.reqHistoricalData(
            reqId,
            _gcContract,
            "",  // End date/time (empty = now)
            duration,  // e.g., "1 D", "1 W"
            barSize,   // e.g., "1 hour", "5 mins"
            "TRADES",
            1,  // Use RTH
            1,  // Format date as string
            false,
            new List<TagValue>()
        );
    }

    public void PlaceOrder(string action, decimal quantity, string orderType, double limitPrice = 0, double stopPrice = 0, string orderRef = "")
    {
        if (_gcContract == null) return;

        var order = new Order
        {
            Action = action,
            TotalQuantity = quantity,
            OrderType = orderType,
            LmtPrice = limitPrice,
            AuxPrice = stopPrice,
            OrderRef = orderRef,
            Tif = "GTC",
            Transmit = true
        };

        _clientSocket.placeOrder(NextOrderId, _gcContract, order);
    }

    public void PlaceBracketOrder(string action, decimal quantity, double entryPrice, double stopLoss, double takeProfit, string orderRef = "")
    {
        if (_gcContract == null) return;

        var parentOrderId = NextOrderId;

        // Parent order (entry)
        var parentOrder = new Order
        {
            Action = action,
            TotalQuantity = quantity,
            OrderType = "LMT",
            LmtPrice = entryPrice,
            OrderRef = orderRef,
            Tif = "GTC",
            Transmit = false
        };

        // Stop loss
        var stopOrder = new Order
        {
            Action = action == "BUY" ? "SELL" : "BUY",
            TotalQuantity = quantity,
            OrderType = "STP",
            AuxPrice = stopLoss,
            ParentId = parentOrderId,
            OrderRef = orderRef + "_SL",
            Tif = "GTC",
            Transmit = false
        };

        // Take profit
        var tpOrder = new Order
        {
            Action = action == "BUY" ? "SELL" : "BUY",
            TotalQuantity = quantity,
            OrderType = "LMT",
            LmtPrice = takeProfit,
            ParentId = parentOrderId,
            OrderRef = orderRef + "_TP",
            Tif = "GTC",
            Transmit = true  // Transmit all when last order is placed
        };

        _clientSocket.placeOrder(parentOrderId, _gcContract, parentOrder);
        _clientSocket.placeOrder(NextOrderId, _gcContract, stopOrder);
        _clientSocket.placeOrder(NextOrderId, _gcContract, tpOrder);
    }

    public void PlaceMarketOrder(string action, decimal quantity, string orderRef = "")
    {
        PlaceOrder(action, quantity, "MKT", orderRef: orderRef);
    }

    public void PlaceStopOrder(string action, decimal quantity, double stopPrice, string orderRef = "")
    {
        PlaceOrder(action, quantity, "STP", stopPrice: stopPrice, orderRef: orderRef);
    }

    public void CancelOrder(int orderId)
    {
        _clientSocket.cancelOrder(orderId, new OrderCancel());
    }

    public void CancelAllOrders()
    {
        _clientSocket.reqGlobalCancel(new OrderCancel());
    }

    /// <summary>
    /// Updates an existing stop order with a new stop price
    /// </summary>
    /// <param name="orderId">The order ID to update</param>
    /// <param name="newStopPrice">The new stop price</param>
    /// <param name="quantity">The quantity</param>
    /// <param name="action">The action: SELL for long positions, BUY for short positions</param>
    public void UpdateStop(int orderId, double newStopPrice, decimal quantity, string action = "SELL")
    {
        if (_gcContract == null) return;

        var order = new Order
        {
            OrderId = orderId,
            Action = action,  // SELL for long positions, BUY for short positions
            TotalQuantity = quantity,
            OrderType = "STP",
            AuxPrice = newStopPrice,
            Tif = "GTC",
            Transmit = true
        };

        _clientSocket.placeOrder(orderId, _gcContract, order);
    }

    // EWrapper implementations

    public void nextValidId(int orderId)
    {
        // Set to orderId - 1 because NextOrderId uses Interlocked.Increment (pre-increment)
        Interlocked.Exchange(ref _nextOrderId, orderId - 1);
        _isConnected = true;
        PostToUI(() => OnConnected?.Invoke());
    }

    public void connectionClosed()
    {
        _isConnected = false;
        PostToUI(() => OnDisconnected?.Invoke());
    }

    public void error(int id, long errorTime, int errorCode, string errorMsg, string advancedOrderRejectJson)
    {
        PostToUI(() => OnError?.Invoke(id, errorCode, errorMsg));
    }

    public void error(Exception e)
    {
        PostToUI(() => OnError?.Invoke(-1, -1, e.Message));
    }

    public void error(string str)
    {
        PostToUI(() => OnError?.Invoke(-1, -1, str));
    }

    public void orderStatus(int orderId, string status, decimal filled, decimal remaining, double avgFillPrice, long permId, int parentId, double lastFillPrice, int clientId, string whyHeld, double mktCapPrice)
    {
        PostToUI(() => OnOrderStatus?.Invoke(new OrderStatusData
        {
            OrderId = orderId,
            Status = status,
            Filled = filled,
            Remaining = remaining,
            AvgFillPrice = avgFillPrice,
            PermId = permId,
            ParentId = parentId,
            LastFillPrice = lastFillPrice,
            ClientId = clientId,
            WhyHeld = whyHeld
        }));
    }

    public void openOrder(int orderId, Contract contract, Order order, OrderState orderState)
    {
        PostToUI(() => OnOpenOrder?.Invoke(new OpenOrderData
        {
            OrderId = orderId,
            Symbol = contract.Symbol,
            Action = order.Action,
            Quantity = order.TotalQuantity,
            OrderType = order.OrderType,
            LimitPrice = order.LmtPrice,
            StopPrice = order.AuxPrice,
            Status = orderState.Status,
            OrderRef = order.OrderRef
        }));
    }

    public void execDetails(int reqId, Contract contract, Execution execution)
    {
        PostToUI(() => OnExecution?.Invoke(new ExecutionData
        {
            ExecId = execution.ExecId,
            Symbol = contract.Symbol,
            Side = execution.Side,
            Shares = execution.Shares,
            Price = execution.Price,
            OrderRef = execution.OrderRef
        }));
    }

    public void commissionAndFeesReport(CommissionAndFeesReport report)
    {
        // Commission info - can be used to update fills
    }

    public void position(string account, Contract contract, decimal pos, double avgCost)
    {
        PostToUI(() => OnPosition?.Invoke(new PositionData
        {
            Account = account,
            Symbol = contract.Symbol,
            Position = pos,
            AvgCost = avgCost
        }));
    }

    public void updateAccountValue(string key, string value, string currency, string accountName)
    {
        PostToUI(() => OnAccountUpdate?.Invoke(key, value, currency));
    }

    public void realtimeBar(int reqId, long time, double open, double high, double low, double close, decimal volume, decimal wap, int count)
    {
        var bar = new BarData
        {
            Time = DateTimeOffset.FromUnixTimeSeconds(time).LocalDateTime,
            Open = open,
            High = high,
            Low = low,
            Close = close,
            Volume = volume,
            WAP = wap,
            Count = count
        };
        PostToUI(() => OnRealtimeBar?.Invoke(bar));
    }

    public void historicalData(int reqId, Bar bar)
    {
        var barData = new BarData
        {
            Time = DateTime.Parse(bar.Time),
            Open = bar.Open,
            High = bar.High,
            Low = bar.Low,
            Close = bar.Close,
            Volume = bar.Volume,
            WAP = bar.WAP,
            Count = bar.Count
        };
        PostToUI(() => OnHistoricalBar?.Invoke(barData));
    }

    private void PostToUI(Action action)
    {
        if (_syncContext != null)
            _syncContext.Post(_ => action(), null);
        else
            action();
    }

    // Required EWrapper implementations (no-op for unused callbacks)
    public void connectAck() { if (_clientSocket.AsyncEConnect) _clientSocket.startApi(); }
    public void currentTime(long time) { }
    public void tickPrice(int tickerId, int field, double price, TickAttrib attribs) { }
    public void tickSize(int tickerId, int field, decimal size) { }
    public void tickString(int tickerId, int tickType, string value) { }
    public void tickGeneric(int tickerId, int field, double value) { }
    public void tickEFP(int tickerId, int tickType, double basisPoints, string formattedBasisPoints, double impliedFuture, int holdDays, string futureLastTradeDate, double dividendImpact, double dividendsToLastTradeDate) { }
    public void tickSnapshotEnd(int tickerId) { }
    public void deltaNeutralValidation(int reqId, DeltaNeutralContract deltaNeutralContract) { }
    public void managedAccounts(string accountsList) { }
    public void tickOptionComputation(int tickerId, int field, int tickAttrib, double impliedVolatility, double delta, double optPrice, double pvDividend, double gamma, double vega, double theta, double undPrice) { }
    public void accountSummary(int reqId, string account, string tag, string value, string currency) { }
    public void accountSummaryEnd(int reqId) { }
    public void updatePortfolio(Contract contract, decimal position, double marketPrice, double marketValue, double averageCost, double unrealizedPNL, double realizedPNL, string accountName) { }
    public void updateAccountTime(string timestamp) { }
    public void accountDownloadEnd(string account) { }
    public void openOrderEnd() { }
    public void contractDetails(int reqId, ContractDetails contractDetails) { }
    public void contractDetailsEnd(int reqId) { }
    public void execDetailsEnd(int reqId) { }
    public void fundamentalData(int reqId, string data) { }
    public void historicalDataEnd(int reqId, string start, string end) { }
    public void marketDataType(int reqId, int marketDataType) { }
    public void updateMktDepth(int tickerId, int position, int operation, int side, double price, decimal size) { }
    public void updateMktDepthL2(int tickerId, int position, string marketMaker, int operation, int side, double price, decimal size, bool isSmartDepth) { }
    public void updateNewsBulletin(int msgId, int msgType, string message, string origExchange) { }
    public void positionEnd() { }
    public void scannerParameters(string xml) { }
    public void scannerData(int reqId, int rank, ContractDetails contractDetails, string distance, string benchmark, string projection, string legsStr) { }
    public void scannerDataEnd(int reqId) { }
    public void receiveFA(int faDataType, string faXmlData) { }
    public void bondContractDetails(int reqId, ContractDetails contract) { }
    public void verifyMessageAPI(string apiData) { }
    public void verifyCompleted(bool isSuccessful, string errorText) { }
    public void verifyAndAuthMessageAPI(string apiData, string xyzChallenge) { }
    public void verifyAndAuthCompleted(bool isSuccessful, string errorText) { }
    public void displayGroupList(int reqId, string groups) { }
    public void displayGroupUpdated(int reqId, string contractInfo) { }
    public void positionMulti(int reqId, string account, string modelCode, Contract contract, decimal pos, double avgCost) { }
    public void positionMultiEnd(int reqId) { }
    public void accountUpdateMulti(int reqId, string account, string modelCode, string key, string value, string currency) { }
    public void accountUpdateMultiEnd(int reqId) { }
    public void securityDefinitionOptionParameter(int reqId, string exchange, int underlyingConId, string tradingClass, string multiplier, HashSet<string> expirations, HashSet<double> strikes) { }
    public void securityDefinitionOptionParameterEnd(int reqId) { }
    public void softDollarTiers(int reqId, SoftDollarTier[] tiers) { }
    public void familyCodes(FamilyCode[] familyCodes) { }
    public void symbolSamples(int reqId, ContractDescription[] contractDescriptions) { }
    public void mktDepthExchanges(DepthMktDataDescription[] depthMktDataDescriptions) { }
    public void tickNews(int tickerId, long timeStamp, string providerCode, string articleId, string headline, string extraData) { }
    public void smartComponents(int reqId, Dictionary<int, KeyValuePair<string, char>> theMap) { }
    public void tickReqParams(int tickerId, double minTick, string bboExchange, int snapshotPermissions) { }
    public void newsProviders(NewsProvider[] newsProviders) { }
    public void newsArticle(int requestId, int articleType, string articleText) { }
    public void historicalNews(int requestId, string time, string providerCode, string articleId, string headline) { }
    public void historicalNewsEnd(int requestId, bool hasMore) { }
    public void headTimestamp(int reqId, string headTimestamp) { }
    public void histogramData(int reqId, HistogramEntry[] data) { }
    public void historicalDataUpdate(int reqId, Bar bar) { }
    public void rerouteMktDataReq(int reqId, int conId, string exchange) { }
    public void rerouteMktDepthReq(int reqId, int conId, string exchange) { }
    public void marketRule(int marketRuleId, PriceIncrement[] priceIncrements) { }
    public void pnl(int reqId, double dailyPnL, double unrealizedPnL, double realizedPnL) { }
    public void pnlSingle(int reqId, decimal pos, double dailyPnL, double unrealizedPnL, double realizedPnL, double value) { }
    public void historicalTicks(int reqId, HistoricalTick[] ticks, bool done) { }
    public void historicalTicksBidAsk(int reqId, HistoricalTickBidAsk[] ticks, bool done) { }
    public void historicalTicksLast(int reqId, HistoricalTickLast[] ticks, bool done) { }
    public void tickByTickAllLast(int reqId, int tickType, long time, double price, decimal size, TickAttribLast tickAttribLast, string exchange, string specialConditions) { }
    public void tickByTickBidAsk(int reqId, long time, double bidPrice, double askPrice, decimal bidSize, decimal askSize, TickAttribBidAsk tickAttribBidAsk) { }
    public void tickByTickMidPoint(int reqId, long time, double midPoint) { }
    public void orderBound(long permId, int clientId, int orderId) { }
    public void completedOrder(Contract contract, Order order, OrderState orderState) { }
    public void completedOrdersEnd() { }
    public void replaceFAEnd(int reqId, string text) { }
    public void wshMetaData(int reqId, string dataJson) { }
    public void wshEventData(int reqId, string dataJson) { }
    public void historicalSchedule(int reqId, string startDateTime, string endDateTime, string timeZone, HistoricalSession[] sessions) { }
    public void userInfo(int reqId, string whiteBrandingId) { }
    public void currentTimeInMillis(long timeInMillis) { }
    public void orderStatusProtoBuf(IBApi.protobuf.OrderStatus orderStatusProto) { }
    public void openOrderProtoBuf(IBApi.protobuf.OpenOrder openOrderProto) { }
    public void openOrdersEndProtoBuf(IBApi.protobuf.OpenOrdersEnd openOrdersEndProto) { }
    public void errorProtoBuf(IBApi.protobuf.ErrorMessage errorMessageProto) { }
    public void execDetailsProtoBuf(IBApi.protobuf.ExecutionDetails executionDetailsProto) { }
    public void execDetailsEndProtoBuf(IBApi.protobuf.ExecutionDetailsEnd executionDetailsEndProto) { }
}
