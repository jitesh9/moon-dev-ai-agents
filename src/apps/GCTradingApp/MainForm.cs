/*
 * GC Trading Application - Main Form
 * Implements Aggressive and Conservative GC divergence strategies
 */

using System.ComponentModel;
using IBApi;
using Newtonsoft.Json;

namespace GCTradingApp;

public partial class MainForm : Form
{
    // IBKR Client and Connection Management
    private IBKRClient? _ibClient;
    private ConnectionManager? _connectionManager;
    private PositionReconciler? _positionReconciler;
    private OrderTracker? _orderTracker;
    private RiskManager? _riskManager;
    private PerformanceTracker? _performanceTracker;
    private AlertManager? _alertManager;
    private bool _isConnected = false;

    // State (protected by _stateLock for thread-safe access)
    private AppState _state;
    private readonly string _stateFilePath;
    private readonly object _stateLock = new();

    // UI Controls
    private TextBox txtHost = null!;
    private NumericUpDown numPort = null!;
    private NumericUpDown numClientId = null!;
    private Button btnConnect = null!;
    private Label lblStatus = null!;
    private Label lblReconnect = null!;
    private CheckBox chkAutoReconnect = null!;

    private CheckBox chkAggressive = null!;
    private CheckBox chkConservative = null!;
    private RadioButton rdoContracts = null!;
    private RadioButton rdoCapital = null!;
    private NumericUpDown numAggressiveSize = null!;
    private NumericUpDown numConservativeSize = null!;
    private Button btnStartStrategy = null!;
    private Button btnStopStrategy = null!;
    private Label lblStrategyStatus = null!;

    // Risk Management UI
    private Button btnEmergencyFlatten = null!;
    private Label lblDailyPnL = null!;
    private Label lblRiskStatus = null!;
    private ProgressBar prgDailyLoss = null!;
    private NumericUpDown numMaxDailyLoss = null!;
    private NumericUpDown numMaxContracts = null!;
    private CheckBox chkAutoFlatten = null!;

    private DataGridView dgvOrders = null!;
    private DataGridView dgvFills = null!;
    private DataGridView dgvPositions = null!;
    private RichTextBox txtLog = null!;

    private Label lblEquity = null!;
    private Label lblPnL = null!;
    private Label lblDrawdown = null!;

    // Performance Dashboard UI
    private Label lblWinRate = null!;
    private Label lblProfitFactor = null!;
    private Label lblSharpeRatio = null!;
    private Label lblTotalTrades = null!;
    private Label lblTotalPnL = null!;
    private Label lblMaxDrawdown = null!;
    private Label lblAvgWin = null!;
    private Label lblAvgLoss = null!;
    private Label lblCurrentStreak = null!;
    private Label lblTodayTrades = null!;
    private Label lblTodayPnL = null!;
    private DataGridView dgvRecentTrades = null!;

    // Strategy engines
    private GCStrategyEngine? _aggressiveEngine;
    private GCStrategyEngine? _conservativeEngine;
    private MTFStrategyEngine? _mtfEngine;

    // Circuit breakers for each strategy
    private CircuitBreaker? _aggressiveCircuitBreaker;
    private CircuitBreaker? _conservativeCircuitBreaker;
    private CircuitBreaker? _mtfCircuitBreaker;

    // MTF UI Controls
    private CheckBox chkMTF_5m15m1H = null!;
    private CheckBox chkMTF_1m5m15m = null!;
    private CheckBox chkMTF_15m1H4H = null!;
    private CheckBox chkMTF_5m1HDaily = null!;
    private NumericUpDown numMTFSize = null!;
    private CheckBox chkMTFAllowShorts = null!;
    private Label lblMTFStatus = null!;
    private Label lblTF1Status = null!;
    private Label lblTF2Status = null!;
    private Label lblTF3Status = null!;

    // Paper Trading
    private PaperTradingClient? _paperClient;
    private RadioButton rbLiveTrading = null!;
    private RadioButton rbPaperTrading = null!;
    private GroupBox grpPaperSettings = null!;
    private NumericUpDown numPaperSlippage = null!;
    private NumericUpDown numPaperDelay = null!;
    private NumericUpDown numPaperBalance = null!;
    private Label lblPaperPnL = null!;
    private Label lblPaperPosition = null!;
    private Button btnResetPaper = null!;

    public MainForm()
    {
        Logger.Info("MainForm constructor starting...");

        try
        {
            _stateFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gc_trading_state.json");
            Logger.Debug($"State file path: {_stateFilePath}");

            _state = LoadState();
            Logger.Debug("State loaded successfully");

            InitializeComponent();
            Logger.Debug("UI components initialized");

            ApplyStateToUI();
            Logger.Debug("State applied to UI");

            this.FormClosing += MainForm_FormClosing;

            Logger.Info("MainForm constructor completed successfully");
        }
        catch (Exception ex)
        {
            Logger.Error("MainForm constructor failed", ex);
            throw;
        }
    }

    private void InitializeComponent()
    {
        this.Text = "GC Gold Futures Trading - Divergence Strategy";
        this.Size = new Size(1400, 900);
        this.StartPosition = FormStartPosition.CenterScreen;
        this.MinimumSize = new Size(1200, 700);

        // Create main layout
        var mainPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(10)
        };
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 260));  // Increased for risk controls
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // Top panel - Connection and Strategy Settings
        var topPanel = CreateTopPanel();
        mainPanel.Controls.Add(topPanel, 0, 0);

        // Bottom panel - Orders, Fills, Positions, Log
        var bottomPanel = CreateBottomPanel();
        mainPanel.Controls.Add(bottomPanel, 0, 1);

        this.Controls.Add(mainPanel);
    }

    private Panel CreateTopPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill };

        // Connection Group
        var grpConnection = new GroupBox
        {
            Text = "TWS Connection",
            Location = new Point(0, 0),
            Size = new Size(300, 100)
        };

        var lblHost = new Label { Text = "Host:", Location = new Point(10, 25), AutoSize = true };
        txtHost = new TextBox { Location = new Point(80, 22), Width = 100, Text = _state.Host };

        var lblPort = new Label { Text = "Port:", Location = new Point(10, 55), AutoSize = true };
        numPort = new NumericUpDown { Location = new Point(100, 52), Width = 70, Minimum = 1, Maximum = 65535, Value = _state.Port };

        var lblClientId = new Label { Text = "ID:", Location = new Point(165, 55), AutoSize = true };
        numClientId = new NumericUpDown { Location = new Point(185, 52), Width = 60, Minimum = 0, Maximum = 999, Value = _state.ClientId };

        chkAutoReconnect = new CheckBox { Text = "Auto", Location = new Point(230, 54), AutoSize = true, Checked = true };

        btnConnect = new Button { Text = "Connect", Location = new Point(190, 20), Size = new Size(90, 28) };
        btnConnect.Click += BtnConnect_Click;

        grpConnection.Controls.AddRange(new Control[] { lblHost, txtHost, lblPort, numPort, lblClientId, numClientId, chkAutoReconnect, btnConnect });

        // Status Group
        var grpStatus = new GroupBox
        {
            Text = "Status",
            Location = new Point(310, 0),
            Size = new Size(300, 100)
        };

        lblStatus = new Label { Text = "Disconnected", Location = new Point(10, 25), AutoSize = true, ForeColor = Color.Red };
        lblReconnect = new Label { Text = "", Location = new Point(120, 25), AutoSize = true, ForeColor = Color.Orange };
        lblEquity = new Label { Text = "Equity: --", Location = new Point(10, 45), AutoSize = true };
        lblPnL = new Label { Text = "Daily PnL: --", Location = new Point(10, 65), AutoSize = true };
        lblDrawdown = new Label { Text = "Drawdown: --", Location = new Point(150, 45), AutoSize = true };

        grpStatus.Controls.AddRange(new Control[] { lblStatus, lblReconnect, lblEquity, lblPnL, lblDrawdown });

        // Strategy Settings Group
        var grpStrategy = new GroupBox
        {
            Text = "Strategy Settings",
            Location = new Point(620, 0),
            Size = new Size(550, 100)
        };

        chkAggressive = new CheckBox { Text = "Aggressive (99%,NoDDLimit)", Location = new Point(10, 22), AutoSize = true, Checked = _state.AggressiveEnabled };
        chkConservative = new CheckBox { Text = "Conservative (11% MaxDD)", Location = new Point(10, 48), AutoSize = true, Checked = _state.ConservativeEnabled };

        rdoContracts = new RadioButton { Text = "Contracts", Location = new Point(280, 22), AutoSize = true, Checked = _state.SizingMode == "Contracts" };
        rdoCapital = new RadioButton { Text = "Capital ($)", Location = new Point(280, 48), AutoSize = true, Checked = _state.SizingMode == "Capital" };

        var lblAggSize = new Label { Text = "Agg:", Location = new Point(390, 25), AutoSize = true };
        numAggressiveSize = new NumericUpDown { Location = new Point(450, 22), Width = 80, Minimum = 1, Maximum = 1000000, Value = (decimal)_state.AggressiveSize, DecimalPlaces = 0 };

        var lblConsSize = new Label { Text = "Cons:", Location = new Point(390, 51), AutoSize = true };
        numConservativeSize = new NumericUpDown { Location = new Point(450, 48), Width = 80, Minimum = 1, Maximum = 1000000, Value = (decimal)_state.ConservativeSize, DecimalPlaces = 0 };

        grpStrategy.Controls.AddRange(new Control[] { chkAggressive, chkConservative, rdoContracts, rdoCapital, lblAggSize, numAggressiveSize, lblConsSize, numConservativeSize });

        // Control Buttons Group
        var grpControls = new GroupBox
        {
            Text = "Controls",
            Location = new Point(1180, 0),
            Size = new Size(200, 100)
        };

        btnStartStrategy = new Button { Text = "Start Strategy", Location = new Point(10, 25), Size = new Size(90, 30), Enabled = false };
        btnStartStrategy.Click += BtnStartStrategy_Click;

        btnStopStrategy = new Button { Text = "Stop Strategy", Location = new Point(105, 25), Size = new Size(85, 30), Enabled = false };
        btnStopStrategy.Click += BtnStopStrategy_Click;

        lblStrategyStatus = new Label { Text = "Strategy: Stopped", Location = new Point(10, 65), AutoSize = true, ForeColor = Color.Gray };

        grpControls.Controls.AddRange(new Control[] { btnStartStrategy, btnStopStrategy, lblStrategyStatus });

        // Risk Management Group
        var grpRisk = new GroupBox
        {
            Text = "Risk Management",
            Location = new Point(0, 105),
            Size = new Size(620, 50)
        };

        var lblMaxLoss = new Label { Text = "Max Daily Loss $:", Location = new Point(10, 20), AutoSize = true };
        numMaxDailyLoss = new NumericUpDown { Location = new Point(160, 17), Width = 70, Minimum = 100, Maximum = 10000, Value = (decimal)_state.RiskSettings.MaxDailyLossUsd, DecimalPlaces = 0 };

        var lblMaxPos = new Label { Text = "Max Contracts:", Location = new Point(215, 20), AutoSize = true };
        numMaxContracts = new NumericUpDown { Location = new Point(350, 17), Width = 50, Minimum = 1, Maximum = 50, Value = _state.RiskSettings.MaxTotalContracts };

        chkAutoFlatten = new CheckBox { Text = "Auto-Flatten", Location = new Point(440, 18), AutoSize = true, Checked = _state.RiskSettings.AutoFlattenOnLimit };

        lblRiskStatus = new Label { Text = "", Location = new Point(460, 20), AutoSize = true, ForeColor = Color.Gray };

        grpRisk.Controls.AddRange(new Control[] { lblMaxLoss, numMaxDailyLoss, lblMaxPos, numMaxContracts, chkAutoFlatten, lblRiskStatus });

        // Daily PnL and Emergency Flatten Group
        var grpEmergency = new GroupBox
        {
            Text = "Daily PnL / Emergency",
            Location = new Point(630, 105),
            Size = new Size(350, 50)
        };

        lblDailyPnL = new Label { Text = "Daily PnL: $0.00", Location = new Point(10, 20), AutoSize = true };
        prgDailyLoss = new ProgressBar { Location = new Point(130, 17), Size = new Size(100, 20), Maximum = 100, Value = 0 };

        btnEmergencyFlatten = new Button
        {
            Text = "EMERGENCY FLATTEN",
            Location = new Point(240, 14),
            Size = new Size(100, 28),
            BackColor = Color.Red,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Enabled = false
        };
        btnEmergencyFlatten.Click += BtnEmergencyFlatten_Click;

        grpEmergency.Controls.AddRange(new Control[] { lblDailyPnL, prgDailyLoss, btnEmergencyFlatten });

        // MTF Strategy Settings Group
        var grpMTF = new GroupBox
        {
            Text = "MTF Strategies (SuperTrend Alignment - All 3 TFs must agree)",
            Location = new Point(0, 160),
            Size = new Size(980, 90)
        };

        chkMTF_5m15m1H = new CheckBox { Text = "5m/15m/1H", Location = new Point(10, 22), AutoSize = true, Checked = _state.MTF_5m15m1H_Enabled };
        chkMTF_1m5m15m = new CheckBox { Text = "1m/5m/15m (Fast)", Location = new Point(120, 22), AutoSize = true, Checked = _state.MTF_1m5m15m_Enabled };
        chkMTF_15m1H4H = new CheckBox { Text = "15m/1H/4H (Swing)", Location = new Point(260, 22), AutoSize = true, Checked = _state.MTF_15m1H4H_Enabled };
        chkMTF_5m1HDaily = new CheckBox { Text = "5m/1H/Daily", Location = new Point(410, 22), AutoSize = true, Checked = _state.MTF_5m1HDaily_Enabled };

        var lblMTFSizeLabel = new Label { Text = "Contracts:", Location = new Point(540, 25), AutoSize = true };
        numMTFSize = new NumericUpDown { Location = new Point(610, 22), Width = 50, Minimum = 1, Maximum = 50, Value = _state.MTFSize };

        chkMTFAllowShorts = new CheckBox { Text = "Allow Shorts", Location = new Point(680, 22), AutoSize = true, Checked = _state.MTFAllowShorts };

        lblMTFStatus = new Label { Text = "MTF: Waiting...", Location = new Point(10, 55), AutoSize = true, ForeColor = Color.Gray };
        lblTF1Status = new Label { Text = "TF1: --", Location = new Point(130, 55), AutoSize = true, ForeColor = Color.Gray };
        lblTF2Status = new Label { Text = "TF2: --", Location = new Point(230, 55), AutoSize = true, ForeColor = Color.Gray };
        lblTF3Status = new Label { Text = "TF3: --", Location = new Point(330, 55), AutoSize = true, ForeColor = Color.Gray };

        var lblMTFInfo = new Label
        {
            Text = "Entry: Divergence + 4 Confirmations when all TFs aligned  |  Long when all bullish, Short when all bearish",
            Location = new Point(440, 55),
            AutoSize = true,
            ForeColor = Color.DarkBlue
        };

        grpMTF.Controls.AddRange(new Control[] { chkMTF_5m15m1H, chkMTF_1m5m15m, chkMTF_15m1H4H, chkMTF_5m1HDaily, lblMTFSizeLabel, numMTFSize, chkMTFAllowShorts, lblMTFStatus, lblTF1Status, lblTF2Status, lblTF3Status, lblMTFInfo });

        // GC Contract Info
        var grpContract = new GroupBox
        {
            Text = "GC Contract",
            Location = new Point(990, 160),
            Size = new Size(200, 90)
        };

        var lblContractInfo = new Label
        {
            Text = "Symbol: GC\nExchange: COMEX\nMultiplier: 100 oz",
            Location = new Point(10, 22),
            AutoSize = true
        };

        grpContract.Controls.AddRange(new Control[] { lblContractInfo });

        // Paper Trading Mode
        var grpPaperMode = new GroupBox
        {
            Text = "Trading Mode",
            Location = new Point(1200, 160),
            Size = new Size(180, 90)
        };

        rbLiveTrading = new RadioButton { Text = "Live Trading", Location = new Point(10, 20), AutoSize = true, Checked = !_state.PaperTradingEnabled };
        rbPaperTrading = new RadioButton { Text = "Paper Trading", Location = new Point(10, 42), AutoSize = true, Checked = _state.PaperTradingEnabled };
        rbPaperTrading.CheckedChanged += RbPaperTrading_CheckedChanged;

        lblPaperPnL = new Label { Text = "Paper PnL: --", Location = new Point(10, 65), AutoSize = true, ForeColor = Color.Blue };

        grpPaperMode.Controls.AddRange(new Control[] { rbLiveTrading, rbPaperTrading, lblPaperPnL });

        // Paper Trading Settings (initially hidden unless paper mode)
        grpPaperSettings = new GroupBox
        {
            Text = "Paper Settings",
            Location = new Point(990, 0),
            Size = new Size(390, 50),
            Visible = _state.PaperTradingEnabled
        };

        var lblSlippage = new Label { Text = "Slippage (bps):", Location = new Point(10, 20), AutoSize = true };
        numPaperSlippage = new NumericUpDown { Location = new Point(95, 17), Width = 50, Minimum = 0, Maximum = 100, DecimalPlaces = 1, Value = (decimal)_state.PaperSlippageBps };

        var lblDelay = new Label { Text = "Delay (ms):", Location = new Point(155, 20), AutoSize = true };
        numPaperDelay = new NumericUpDown { Location = new Point(225, 17), Width = 60, Minimum = 0, Maximum = 5000, Value = _state.PaperFillDelayMs };

        btnResetPaper = new Button { Text = "Reset", Location = new Point(300, 14), Size = new Size(70, 28) };
        btnResetPaper.Click += BtnResetPaper_Click;

        grpPaperSettings.Controls.AddRange(new Control[] { lblSlippage, numPaperSlippage, lblDelay, numPaperDelay, btnResetPaper });

        panel.Controls.AddRange(new Control[] { grpConnection, grpStatus, grpStrategy, grpControls, grpRisk, grpEmergency, grpMTF, grpContract, grpPaperMode, grpPaperSettings });

        return panel;
    }

    private Panel CreateBottomPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill };

        var tabControl = new TabControl { Dock = DockStyle.Fill };

        // Orders Tab
        var tabOrders = new TabPage("Open Orders");
        dgvOrders = CreateDataGridView(new[] { "OrderId", "Time", "Strategy", "Action", "Qty", "Type", "Limit", "Stop", "Status", "Filled", "Remaining" });
        tabOrders.Controls.Add(dgvOrders);

        // Fills Tab
        var tabFills = new TabPage("Fills / Executions");
        dgvFills = CreateDataGridView(new[] { "ExecId", "Time", "Strategy", "Action", "Qty", "Price", "Commission", "RealizedPnL" });
        tabFills.Controls.Add(dgvFills);

        // Positions Tab
        var tabPositions = new TabPage("Positions");
        dgvPositions = CreateDataGridView(new[] { "Strategy", "Symbol", "Position", "AvgCost", "MarketPrice", "MarketValue", "UnrealizedPnL", "RealizedPnL" });
        tabPositions.Controls.Add(dgvPositions);

        // Performance Tab
        var tabPerformance = new TabPage("Performance Dashboard");
        tabPerformance.Controls.Add(CreatePerformancePanel());

        // Log Tab
        var tabLog = new TabPage("Activity Log");
        txtLog = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, Font = new Font("Consolas", 9) };
        tabLog.Controls.Add(txtLog);

        tabControl.TabPages.AddRange(new[] { tabOrders, tabFills, tabPositions, tabPerformance, tabLog });

        panel.Controls.Add(tabControl);
        return panel;
    }

    private DataGridView CreateDataGridView(string[] columns)
    {
        var dgv = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            ReadOnly = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        };

        foreach (var col in columns)
        {
            dgv.Columns.Add(col, col);
        }

        return dgv;
    }

    private Panel CreatePerformancePanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill };

        // Today's Performance Group
        var grpToday = new GroupBox
        {
            Text = "Today's Performance",
            Location = new Point(10, 10),
            Size = new Size(250, 100)
        };

        lblTodayTrades = new Label { Text = "Trades: 0", Location = new Point(15, 25), AutoSize = true, Font = new Font("Segoe UI", 10) };
        lblTodayPnL = new Label { Text = "PnL: $0.00", Location = new Point(15, 50), AutoSize = true, Font = new Font("Segoe UI", 12, FontStyle.Bold) };
        lblCurrentStreak = new Label { Text = "Streak: 0", Location = new Point(15, 75), AutoSize = true };

        grpToday.Controls.AddRange(new Control[] { lblTodayTrades, lblTodayPnL, lblCurrentStreak });

        // Overall Metrics Group
        var grpMetrics = new GroupBox
        {
            Text = "Overall Metrics",
            Location = new Point(270, 10),
            Size = new Size(350, 100)
        };

        lblTotalTrades = new Label { Text = "Total Trades: 0", Location = new Point(15, 25), AutoSize = true };
        lblWinRate = new Label { Text = "Win Rate: 0.0%", Location = new Point(15, 50), AutoSize = true, Font = new Font("Segoe UI", 10, FontStyle.Bold) };
        lblTotalPnL = new Label { Text = "Total PnL: $0.00", Location = new Point(15, 75), AutoSize = true };

        lblProfitFactor = new Label { Text = "Profit Factor: 0.00", Location = new Point(180, 25), AutoSize = true };
        lblSharpeRatio = new Label { Text = "Sharpe Ratio: 0.00", Location = new Point(180, 50), AutoSize = true };
        lblMaxDrawdown = new Label { Text = "Max DD: $0.00", Location = new Point(180, 75), AutoSize = true };

        grpMetrics.Controls.AddRange(new Control[] { lblTotalTrades, lblWinRate, lblTotalPnL, lblProfitFactor, lblSharpeRatio, lblMaxDrawdown });

        // Win/Loss Details Group
        var grpWinLoss = new GroupBox
        {
            Text = "Win/Loss Details",
            Location = new Point(630, 10),
            Size = new Size(200, 100)
        };

        lblAvgWin = new Label { Text = "Avg Win: $0.00", Location = new Point(15, 25), AutoSize = true, ForeColor = Color.Green };
        lblAvgLoss = new Label { Text = "Avg Loss: $0.00", Location = new Point(15, 50), AutoSize = true, ForeColor = Color.Red };

        grpWinLoss.Controls.AddRange(new Control[] { lblAvgWin, lblAvgLoss });

        // Recent Trades Grid
        var grpRecentTrades = new GroupBox
        {
            Text = "Recent Trades",
            Location = new Point(10, 120),
            Size = new Size(820, 250),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
        };

        dgvRecentTrades = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            ReadOnly = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        };

        dgvRecentTrades.Columns.Add("Strategy", "Strategy");
        dgvRecentTrades.Columns.Add("EntryTime", "Entry Time");
        dgvRecentTrades.Columns.Add("ExitTime", "Exit Time");
        dgvRecentTrades.Columns.Add("EntryPrice", "Entry");
        dgvRecentTrades.Columns.Add("ExitPrice", "Exit");
        dgvRecentTrades.Columns.Add("Qty", "Qty");
        dgvRecentTrades.Columns.Add("PnL", "PnL");
        dgvRecentTrades.Columns.Add("Duration", "Duration");

        grpRecentTrades.Controls.Add(dgvRecentTrades);

        panel.Controls.AddRange(new Control[] { grpToday, grpMetrics, grpWinLoss, grpRecentTrades });

        return panel;
    }

    private void BtnConnect_Click(object? sender, EventArgs e)
    {
        Logger.Info($"Connect button clicked. IsConnected: {_isConnected}");

        if (!_isConnected)
        {
            try
            {
                // Initialize connection manager if not exists
                if (_connectionManager == null)
                {
                    _connectionManager = new ConnectionManager();
                    _connectionManager.OnConnectionStateChanged += ConnectionManager_OnStateChanged;
                    _connectionManager.OnReconnectAttempt += ConnectionManager_OnReconnectAttempt;
                    _connectionManager.OnLog += msg => Log($"[CONN] {msg}");
                }

                // Get the underlying client and subscribe to events
                _connectionManager.AutoReconnectEnabled = chkAutoReconnect.Checked;

                var host = txtHost.Text;
                var port = (int)numPort.Value;
                var clientId = (int)numClientId.Value;

                Logger.Info($"Attempting connection to TWS at {host}:{port} with clientId {clientId}...");
                Log($"Connecting to TWS at {host}:{port}...");

                _connectionManager.Connect(host, port, clientId);

                // Get the client and subscribe to its events
                _ibClient = _connectionManager.Client;
                if (_ibClient != null)
                {
                    SubscribeToClientEvents();
                }

                // Initialize order tracker
                if (_orderTracker == null)
                {
                    _orderTracker = new OrderTracker();
                    _orderTracker.OnLog += msg => Log($"[ORDER] {msg}");
                    _orderTracker.OnOrderTimeout += OrderTracker_OnTimeout;
                    _orderTracker.OnOrderError += OrderTracker_OnError;
                    _orderTracker.Start();
                }

                Logger.Info("Connect() call completed - waiting for callback...");
            }
            catch (Exception ex)
            {
                LogError("Connection error", ex);
                MessageBox.Show($"Failed to connect: {ex.Message}\n\nCheck log file at:\n{Logger.GetLogFilePath()}",
                    "Connection Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        else
        {
            Logger.Info("Disconnecting from TWS...");
            _connectionManager?.Disconnect();
        }
    }

    private void SubscribeToClientEvents()
    {
        if (_ibClient == null) return;

        _ibClient.OnConnected += IbClient_OnConnected;
        _ibClient.OnDisconnected += IbClient_OnDisconnected;
        _ibClient.OnError += IbClient_OnError;
        _ibClient.OnOrderStatus += IbClient_OnOrderStatus;
        _ibClient.OnOpenOrder += IbClient_OnOpenOrder;
        _ibClient.OnExecution += IbClient_OnExecution;
        _ibClient.OnPosition += IbClient_OnPosition;
        _ibClient.OnAccountUpdate += IbClient_OnAccountUpdate;
        _ibClient.OnRealtimeBar += IbClient_OnRealtimeBar;
    }

    private void ConnectionManager_OnStateChanged(ConnectionState state)
    {
        if (InvokeRequired)
        {
            Invoke(new Action(() => ConnectionManager_OnStateChanged(state)));
            return;
        }

        Logger.Info($"Connection state changed: {state}");

        switch (state)
        {
            case ConnectionState.Connecting:
                lblStatus.Text = "Connecting...";
                lblStatus.ForeColor = Color.Orange;
                lblReconnect.Text = "";
                break;

            case ConnectionState.Connected:
                lblReconnect.Text = "";
                // Re-subscribe to client events on reconnection
                _ibClient = _connectionManager?.Client;
                if (_ibClient != null)
                {
                    SubscribeToClientEvents();
                }
                break;

            case ConnectionState.Reconnecting:
                lblStatus.Text = "Reconnecting";
                lblStatus.ForeColor = Color.Orange;
                break;

            case ConnectionState.Disconnected:
                if (!chkAutoReconnect.Checked)
                {
                    lblReconnect.Text = "";
                }
                break;
        }
    }

    private void ConnectionManager_OnReconnectAttempt(string message)
    {
        if (InvokeRequired)
        {
            Invoke(new Action(() => ConnectionManager_OnReconnectAttempt(message)));
            return;
        }

        lblReconnect.Text = $"Attempt {_connectionManager?.ReconnectAttempt ?? 0}";
        Log($"[RECONNECT] {message}");
    }

    private void OrderTracker_OnTimeout(object? sender, OrderTrackerEventArgs e)
    {
        if (InvokeRequired)
        {
            Invoke(new Action(() => OrderTracker_OnTimeout(sender, e)));
            return;
        }

        Log($"[TIMEOUT] Order {e.Order.OrderId}: No broker response", LogLevel.Warn);
        MessageBox.Show($"Order {e.Order.OrderId} timed out - no response from broker.\n\nPlease check TWS for order status.",
            "Order Timeout", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void OrderTracker_OnError(object? sender, OrderTrackerEventArgs e)
    {
        if (InvokeRequired)
        {
            Invoke(new Action(() => OrderTracker_OnError(sender, e)));
            return;
        }

        Log($"[ORDER ERROR] {e.Message}", LogLevel.Error);
    }

    private async void BtnEmergencyFlatten_Click(object? sender, EventArgs e)
    {
        var result = MessageBox.Show(
            "Are you sure you want to EMERGENCY FLATTEN all positions?\n\n" +
            "This will:\n" +
            "- Cancel all pending orders\n" +
            "- Close all open positions at market\n" +
            "- Pause trading\n\n" +
            "This action cannot be undone!",
            "Confirm Emergency Flatten",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (result == DialogResult.Yes)
        {
            btnEmergencyFlatten.Enabled = false;
            btnEmergencyFlatten.Text = "FLATTENING...";

            Log("[EMERGENCY] Initiating emergency flatten!", LogLevel.Warn);

            if (_riskManager != null)
            {
                await _riskManager.EmergencyFlattenAsync();
            }
            else if (_ibClient != null)
            {
                // Fallback if risk manager not initialized
                _ibClient.CancelAllOrders();
                await Task.Delay(500);

                // Close any tracked positions
                _aggressiveEngine?.Stop();
                _conservativeEngine?.Stop();
            }

            btnEmergencyFlatten.Text = "EMERGENCY FLATTEN";
            btnEmergencyFlatten.Enabled = _isConnected;

            Log("[EMERGENCY] Flatten complete - trading paused");
            MessageBox.Show("Emergency flatten complete. Trading is now paused.", "Flatten Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void RbPaperTrading_CheckedChanged(object? sender, EventArgs e)
    {
        bool isPaperMode = rbPaperTrading.Checked;
        grpPaperSettings.Visible = isPaperMode;
        _state.PaperTradingEnabled = isPaperMode;
        SaveState();

        if (isPaperMode)
        {
            Log("[PAPER] Paper trading mode enabled");
            // Create paper trading client if not exists
            if (_paperClient == null)
            {
                var config = new PaperTradingConfig
                {
                    SlippageBps = (double)numPaperSlippage.Value,
                    FillDelayMs = (int)numPaperDelay.Value,
                    InitialBalance = _state.PaperInitialBalance
                };
                _paperClient = new PaperTradingClient(config);
                _paperClient.OnStateChanged += PaperClient_OnStateChanged;
                _paperClient.OnLog += msg => Log($"[PAPER] {msg}");

                // Restore saved paper state if available
                if (_state.PaperState != null)
                {
                    _paperClient.RestoreState(_state.PaperState);
                }
            }
        }
        else
        {
            Log("[PAPER] Live trading mode enabled");
        }
    }

    private void PaperClient_OnStateChanged(PaperTradingState state)
    {
        if (InvokeRequired)
        {
            Invoke(new Action<PaperTradingState>(PaperClient_OnStateChanged), state);
            return;
        }

        lblPaperPnL.Text = $"Paper PnL: ${state.RealizedPnL:N2}";
        lblPaperPnL.ForeColor = state.RealizedPnL >= 0 ? Color.Green : Color.Red;

        // Save paper state
        _state.PaperState = state;
        SaveState();
    }

    private void BtnResetPaper_Click(object? sender, EventArgs e)
    {
        var result = MessageBox.Show(
            "Are you sure you want to reset the paper trading account?\n\n" +
            "This will clear all paper trading history and reset the balance.",
            "Reset Paper Account",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (result == DialogResult.Yes)
        {
            _paperClient?.Reset();
            _state.PaperState = null;
            SaveState();
            Log("[PAPER] Paper trading account reset");
        }
    }

    /// <summary>
    /// Gets the order client to use based on current trading mode
    /// Wraps with RiskAwareOrderClient to enforce risk checks
    /// </summary>
    private IOrderClient GetOrderClient(string strategyName)
    {
        IOrderClient baseClient;
        if (rbPaperTrading.Checked && _paperClient != null)
        {
            baseClient = _paperClient;
        }
        else
        {
            baseClient = _ibClient!;
        }

        // Wrap with risk-aware client if risk manager is available
        if (_riskManager != null)
        {
            return new RiskAwareOrderClient(baseClient, _riskManager, strategyName);
        }

        return baseClient;
    }

    private void IbClient_OnConnected()
    {
        Logger.Info("IbClient_OnConnected callback received");

        if (InvokeRequired)
        {
            Invoke(new Action(IbClient_OnConnected));
            return;
        }

        _isConnected = true;
        btnConnect.Text = "Disconnect";
        lblStatus.Text = "Connected";
        lblStatus.ForeColor = Color.Green;
        btnStartStrategy.Enabled = true;
        btnEmergencyFlatten.Enabled = true;
        Log("Connected to TWS successfully");

        // Initialize risk manager
        if (_ibClient != null && _riskManager == null)
        {
            RiskSettings riskSettings;
            RiskState? riskState;
            lock (_stateLock)
            {
                riskSettings = _state.RiskSettings;
                riskState = _state.RiskState;
            }

            _riskManager = new RiskManager(_ibClient, riskSettings, riskState);
            _riskManager.OnLog += msg => Log($"[RISK] {msg}");
            _riskManager.OnWarning += RiskManager_OnWarning;
            _riskManager.OnLimitHit += RiskManager_OnLimitHit;
            _riskManager.OnStateChanged += RiskManager_OnStateChanged;
            _riskManager.Start();
            Log("Risk manager initialized");
        }

        // Initialize performance tracker
        if (_performanceTracker == null)
        {
            PerformanceSettings perfSettings;
            List<CompletedTrade> savedTrades;
            lock (_stateLock)
            {
                perfSettings = _state.PerformanceSettings;
                savedTrades = _state.CompletedTrades;
            }

            _performanceTracker = new PerformanceTracker(perfSettings, _state.CurrentEquity);
            _performanceTracker.OnLog += msg => Log($"[PERF] {msg}");
            _performanceTracker.OnMetricsUpdated += PerformanceTracker_OnMetricsUpdated;
            _performanceTracker.OnTradeCompleted += PerformanceTracker_OnTradeCompleted;

            // Restore saved trades
            foreach (var trade in savedTrades)
            {
                _performanceTracker.RecordTrade(trade);
            }

            Log("Performance tracker initialized");
        }

        // Initialize alert manager
        if (_alertManager == null)
        {
            AlertSettings alertSettings;
            lock (_stateLock)
            {
                alertSettings = _state.AlertSettings;
            }

            _alertManager = new AlertManager(alertSettings);
            _alertManager.OnLog += msg => Log($"[ALERT] {msg}");
            _alertManager.OnAlert += AlertManager_OnAlert;
            _alertManager.Start();
            Log("Alert manager initialized");

            // Send connection alert
            _alertManager.AlertConnection("Connected", $"Connected to TWS at {txtHost.Text}:{numPort.Value}");
        }

        // Request account updates
        Logger.Debug("Requesting account updates...");
        _ibClient?.RequestAccountUpdates();

        // Subscribe to GC market data
        Logger.Debug("Subscribing to GC market data...");
        _ibClient?.SubscribeToGCData();

        // Perform position reconciliation if we have saved state
        _ = PerformPositionReconciliationAsync();

        Logger.Info("Connection setup completed");
    }

    private async Task PerformPositionReconciliationAsync()
    {
        if (_ibClient == null) return;

        StrategyState? aggState, consState;
        lock (_stateLock)
        {
            aggState = _state.AggressiveState;
            consState = _state.ConservativeState;
        }

        // Only reconcile if we have saved positions
        if ((aggState?.InPosition != true) && (consState?.InPosition != true))
        {
            Log("[RECONCILE] No saved positions to reconcile");
            return;
        }

        Log("[RECONCILE] Starting position reconciliation...");

        _positionReconciler = new PositionReconciler(_ibClient);
        _positionReconciler.OnLog += msg => Log($"[RECONCILE] {msg}");

        try
        {
            var result = await _positionReconciler.ReconcileAsync(aggState, consState);

            if (result.HasMismatch)
            {
                // Show mismatch dialog
                var message = "Position mismatch detected:\n\n";
                foreach (var mismatch in result.Mismatches)
                {
                    message += $"- {mismatch.Description}\n";
                }
                message += "\nWould you like to adopt the broker's position?";

                var dialogResult = MessageBox.Show(message, "Position Mismatch",
                    MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);

                if (dialogResult == DialogResult.Yes)
                {
                    // Adopt broker positions
                    AdoptBrokerPositions(result);
                    Log("[RECONCILE] Adopted broker positions");
                }
                else if (dialogResult == DialogResult.No)
                {
                    // Clear saved state
                    ClearSavedPositions();
                    Log("[RECONCILE] Cleared saved positions");
                }
                // Cancel = keep current saved state (user will handle manually)
            }
        }
        catch (Exception ex)
        {
            Log($"[RECONCILE] Error: {ex.Message}");
            Logger.Error("Position reconciliation failed", ex);
        }
    }

    private void AdoptBrokerPositions(ReconciliationResult result)
    {
        var gcPosition = result.BrokerPositions.FirstOrDefault(p => p.Symbol == "GC");
        if (gcPosition == null || gcPosition.Position == 0)
        {
            ClearSavedPositions();
            return;
        }

        // For simplicity, assign to aggressive strategy
        var newState = _positionReconciler!.AdoptBrokerPosition(gcPosition, "Aggressive");

        lock (_stateLock)
        {
            _state.AggressiveState = newState;
            _state.ConservativeState = new StrategyState(); // Clear conservative
        }

        SaveState();
    }

    private void ClearSavedPositions()
    {
        lock (_stateLock)
        {
            _state.AggressiveState = new StrategyState();
            _state.ConservativeState = new StrategyState();
        }
        SaveState();
    }

    private void RiskManager_OnWarning(string message)
    {
        if (InvokeRequired)
        {
            Invoke(new Action(() => RiskManager_OnWarning(message)));
            return;
        }

        Log($"[RISK WARNING] {message}", LogLevel.Warn);
        lblRiskStatus.Text = "Warning";
        lblRiskStatus.ForeColor = Color.Orange;
    }

    private void RiskManager_OnLimitHit(string message)
    {
        if (InvokeRequired)
        {
            Invoke(new Action(() => RiskManager_OnLimitHit(message)));
            return;
        }

        Log($"[RISK LIMIT] {message}", LogLevel.Error);
        lblRiskStatus.Text = "LIMIT HIT";
        lblRiskStatus.ForeColor = Color.Red;

        MessageBox.Show($"Risk limit triggered:\n\n{message}\n\nTrading is now paused.",
            "Risk Limit Hit", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void RiskManager_OnStateChanged(RiskState state)
    {
        if (InvokeRequired)
        {
            Invoke(new Action(() => RiskManager_OnStateChanged(state)));
            return;
        }

        // Update UI
        lblDailyPnL.Text = $"Daily PnL: ${state.DailyPnL:F2}";
        lblDailyPnL.ForeColor = state.DailyPnL >= 0 ? Color.Green : Color.Red;

        // Update progress bar (loss percentage)
        var lossUsedPct = _riskManager?.GetDailyLossUsedPct() ?? 0;
        prgDailyLoss.Value = Math.Min(100, (int)(lossUsedPct * 100));

        if (state.TradingPaused)
        {
            lblRiskStatus.Text = "PAUSED";
            lblRiskStatus.ForeColor = Color.Red;
        }
        else if (lossUsedPct >= 0.7)
        {
            lblRiskStatus.Text = "Warning";
            lblRiskStatus.ForeColor = Color.Orange;
        }
        else
        {
            lblRiskStatus.Text = "OK";
            lblRiskStatus.ForeColor = Color.Green;
        }

        // Save state
        lock (_stateLock)
        {
            _state.RiskState = state;
        }
        SaveState();
    }

    private void PerformanceTracker_OnMetricsUpdated(PerformanceMetrics metrics)
    {
        if (InvokeRequired)
        {
            Invoke(new Action(() => PerformanceTracker_OnMetricsUpdated(metrics)));
            return;
        }

        UpdatePerformanceDashboard(metrics);

        // Log performance periodically
        Logger.LogPerformance(metrics);
    }

    private void PerformanceTracker_OnTradeCompleted(CompletedTrade trade)
    {
        if (InvokeRequired)
        {
            Invoke(new Action(() => PerformanceTracker_OnTradeCompleted(trade)));
            return;
        }

        // Save completed trade
        lock (_stateLock)
        {
            _state.CompletedTrades.Add(trade);
        }
        SaveState();

        // Log the trade
        Logger.LogTrade(trade.Strategy, trade.IsWinner ? "WIN" : "LOSS", trade.Quantity, trade.ExitPrice, trade.NetPnL);

        // Send trade alert
        _alertManager?.AlertTrade(trade.Strategy, trade.IsWinner ? "CLOSED (WIN)" : "CLOSED (LOSS)",
            trade.Quantity, trade.ExitPrice, trade.NetPnL);

        // Update recent trades grid
        UpdateRecentTradesGrid();
    }

    private void AlertManager_OnAlert(AlertRecord alert)
    {
        if (InvokeRequired)
        {
            Invoke(new Action(() => AlertManager_OnAlert(alert)));
            return;
        }

        // Log the alert
        var level = alert.Type == AlertType.Error ? LogLevel.Error :
                   alert.Type == AlertType.Warning ? LogLevel.Warn : LogLevel.Info;
        Log($"[{alert.Type}] {alert.Title}", level);
    }

    private void UpdatePerformanceDashboard(PerformanceMetrics metrics)
    {
        // Today's performance
        lblTodayTrades.Text = $"Trades: {metrics.TodayTrades}";
        lblTodayPnL.Text = $"PnL: ${metrics.TodayPnL:F2}";
        lblTodayPnL.ForeColor = metrics.TodayPnL >= 0 ? Color.Green : Color.Red;
        lblCurrentStreak.Text = $"Streak: {(metrics.CurrentStreak >= 0 ? "+" : "")}{metrics.CurrentStreak}";
        lblCurrentStreak.ForeColor = metrics.CurrentStreak >= 0 ? Color.Green : Color.Red;

        // Overall metrics
        lblTotalTrades.Text = $"Total Trades: {metrics.TotalTrades}";
        lblWinRate.Text = $"Win Rate: {metrics.WinRate:F1}%";
        lblWinRate.ForeColor = metrics.WinRate >= 50 ? Color.Green : Color.Red;
        lblTotalPnL.Text = $"Total PnL: ${metrics.TotalNetPnL:F2}";
        lblTotalPnL.ForeColor = metrics.TotalNetPnL >= 0 ? Color.Green : Color.Red;

        lblProfitFactor.Text = $"Profit Factor: {metrics.ProfitFactor:F2}";
        lblProfitFactor.ForeColor = metrics.ProfitFactor >= 1.5 ? Color.Green : (metrics.ProfitFactor >= 1 ? Color.Orange : Color.Red);
        lblSharpeRatio.Text = $"Sharpe Ratio: {metrics.SharpeRatio:F2}";
        lblSharpeRatio.ForeColor = metrics.SharpeRatio >= 1 ? Color.Green : (metrics.SharpeRatio >= 0 ? Color.Orange : Color.Red);
        lblMaxDrawdown.Text = $"Max DD: ${metrics.MaxDrawdown:F2} ({metrics.MaxDrawdownPct:F1}%)";

        // Win/Loss details
        lblAvgWin.Text = $"Avg Win: ${metrics.AverageWin:F2}";
        lblAvgLoss.Text = $"Avg Loss: ${metrics.AverageLoss:F2}";
    }

    private void UpdateRecentTradesGrid()
    {
        if (_performanceTracker == null) return;

        var recentTrades = _performanceTracker.GetRecentTrades(20);

        dgvRecentTrades.Rows.Clear();
        foreach (var trade in recentTrades)
        {
            var row = dgvRecentTrades.Rows.Add(
                trade.Strategy,
                trade.EntryTime.ToString("MM/dd HH:mm"),
                trade.ExitTime.ToString("MM/dd HH:mm"),
                trade.EntryPrice.ToString("F2"),
                trade.ExitPrice.ToString("F2"),
                trade.Quantity,
                $"${trade.NetPnL:F2}",
                trade.Duration.ToString(@"hh\:mm")
            );

            // Color the PnL cell
            dgvRecentTrades.Rows[row].Cells["PnL"].Style.ForeColor = trade.IsWinner ? Color.Green : Color.Red;
        }
    }

    private void IbClient_OnDisconnected()
    {
        Logger.Info("IbClient_OnDisconnected callback received");

        if (InvokeRequired)
        {
            Invoke(new Action(IbClient_OnDisconnected));
            return;
        }

        _isConnected = false;
        btnConnect.Text = "Connect";
        lblStatus.Text = "Disconnected";
        lblStatus.ForeColor = Color.Red;
        btnStartStrategy.Enabled = false;
        btnStopStrategy.Enabled = false;
        btnEmergencyFlatten.Enabled = false;
        Log("Disconnected from TWS");

        // Stop strategies if running
        StopStrategies();
    }

    private void IbClient_OnError(int id, int errorCode, string errorMsg)
    {
        // IBKR error code classification:
        // 2100-2199: Informational messages (not actual errors)
        // 1100-1102: Connectivity warnings
        // Other: Actual errors

        bool isInfo = errorCode >= 2100 && errorCode <= 2199;
        bool isWarning = errorCode >= 1100 && errorCode <= 1102;

        var level = isInfo ? LogLevel.Info : (isWarning ? LogLevel.Warn : LogLevel.Error);
        Logger.Log($"IBKR Message - Id: {id}, Code: {errorCode}, Message: {errorMsg}", level);

        // Update order tracker if this is an order-related error
        if (id > 0)
        {
            _orderTracker?.UpdateOrderError(id, errorCode, errorMsg);
        }

        if (InvokeRequired)
        {
            Invoke(new Action(() => IbClient_OnError(id, errorCode, errorMsg)));
            return;
        }

        if (isInfo)
        {
            Log($"[INFO {errorCode}] {errorMsg}");
        }
        else if (isWarning)
        {
            Log($"[WARN {errorCode}] {errorMsg}", LogLevel.Warn);
        }
        else
        {
            Log($"[ERROR {errorCode}] {errorMsg}", LogLevel.Error);
        }
    }

    private void IbClient_OnOrderStatus(OrderStatusData status)
    {
        if (InvokeRequired)
        {
            Invoke(new Action(() => IbClient_OnOrderStatus(status)));
            return;
        }

        // Update order tracker
        _orderTracker?.UpdateOrderStatus(status);

        // Update orders grid
        UpdateOrderGrid(status);

        // Save state
        SaveState();

        Log($"Order {status.OrderId}: {status.Status} - Filled: {status.Filled}, Remaining: {status.Remaining}");
    }

    private void IbClient_OnOpenOrder(OpenOrderData order)
    {
        if (InvokeRequired)
        {
            Invoke(new Action(() => IbClient_OnOpenOrder(order)));
            return;
        }

        // Add to state
        var orderRecord = new OrderRecord
        {
            OrderId = order.OrderId,
            Time = DateTime.Now,
            Strategy = order.OrderRef ?? "Manual",
            Action = order.Action,
            Quantity = order.Quantity,
            OrderType = order.OrderType,
            LimitPrice = order.LimitPrice,
            StopPrice = order.StopPrice,
            Status = order.Status
        };

        lock (_stateLock)
        {
            _state.Orders[order.OrderId] = orderRecord;
        }
        UpdateOrdersGrid();
        SaveState();
    }

    private void IbClient_OnExecution(ExecutionData exec)
    {
        if (InvokeRequired)
        {
            Invoke(new Action(() => IbClient_OnExecution(exec)));
            return;
        }

        // Add fill to state
        var fill = new FillRecord
        {
            ExecId = exec.ExecId,
            Time = DateTime.Now,
            Strategy = exec.OrderRef ?? "Manual",
            Action = exec.Side,
            Quantity = exec.Shares,
            Price = exec.Price,
            Commission = exec.Commission,
            RealizedPnL = exec.RealizedPnL
        };

        lock (_stateLock)
        {
            _state.Fills.Add(fill);
        }
        UpdateFillsGrid();
        SaveState();

        Log($"FILL: {exec.Side} {exec.Shares} @ {exec.Price:F2}");
    }

    private void IbClient_OnPosition(PositionData pos)
    {
        if (InvokeRequired)
        {
            Invoke(new Action(() => IbClient_OnPosition(pos)));
            return;
        }

        // Update positions
        var key = $"{pos.Symbol}_{pos.Account}";
        lock (_stateLock)
        {
            _state.Positions[key] = new PositionRecord
            {
                Symbol = pos.Symbol,
                Position = pos.Position,
                AvgCost = pos.AvgCost,
                MarketPrice = pos.MarketPrice,
                MarketValue = pos.MarketValue,
                UnrealizedPnL = pos.UnrealizedPnL,
                RealizedPnL = pos.RealizedPnL
            };
        }

        UpdatePositionsGrid();
        SaveState();
    }

    private void IbClient_OnAccountUpdate(string key, string value, string currency)
    {
        if (InvokeRequired)
        {
            Invoke(new Action(() => IbClient_OnAccountUpdate(key, value, currency)));
            return;
        }

        if (key == "NetLiquidation" && double.TryParse(value, out var equity))
        {
            lblEquity.Text = $"Equity: ${equity:N2}";
            _state.CurrentEquity = equity;

            // Update drawdown
            if (equity > _state.PeakEquity) _state.PeakEquity = equity;
            var dd = (_state.PeakEquity - equity) / _state.PeakEquity * 100;
            lblDrawdown.Text = $"Drawdown: {dd:F2}%";
            _state.CurrentDrawdown = dd;
        }
        else if (key == "DailyPnL" && double.TryParse(value, out var pnl))
        {
            lblPnL.Text = $"Daily PnL: ${pnl:N2}";
            lblPnL.ForeColor = pnl >= 0 ? Color.Green : Color.Red;
        }
    }

    private void IbClient_OnRealtimeBar(BarData bar)
    {
        // Forward bar to paper trading client for stop order processing
        try
        {
            _paperClient?.ProcessBar(bar);
        }
        catch (Exception ex)
        {
            Logger.Error("Error processing bar in paper trading client", ex);
            LogError("Paper trading client error", ex);
        }

        // Forward to strategy engines with circuit breaker protection
        if (_aggressiveEngine != null && _aggressiveCircuitBreaker != null)
        {
            try
            {
                var executed = _aggressiveCircuitBreaker.Execute(() =>
                {
                    _aggressiveEngine.ProcessBar(bar);
                });

                if (!executed)
                {
                    // Circuit breaker blocked the operation
                    Log($"[CIRCUIT BREAKER] Aggressive strategy blocked - circuit is OPEN", LogLevel.Warn);
                }
            }
            catch (Exception ex)
            {
                // Exception was logged by circuit breaker, but we still log here for visibility
                Logger.Error("Exception in aggressive strategy (circuit breaker handled)", ex);
                LogError("Aggressive strategy exception", ex);
            }
        }
        else if (_aggressiveEngine != null)
        {
            // Fallback if circuit breaker not initialized
            try
            {
                _aggressiveEngine.ProcessBar(bar);
            }
            catch (Exception ex)
            {
                Logger.Error("Error processing bar in aggressive strategy", ex);
                LogError("Aggressive strategy error", ex);
            }
        }

        if (_conservativeEngine != null && _conservativeCircuitBreaker != null)
        {
            try
            {
                var executed = _conservativeCircuitBreaker.Execute(() =>
                {
                    _conservativeEngine.ProcessBar(bar);
                });

                if (!executed)
                {
                    Log($"[CIRCUIT BREAKER] Conservative strategy blocked - circuit is OPEN", LogLevel.Warn);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Exception in conservative strategy (circuit breaker handled)", ex);
                LogError("Conservative strategy exception", ex);
            }
        }
        else if (_conservativeEngine != null)
        {
            try
            {
                _conservativeEngine.ProcessBar(bar);
            }
            catch (Exception ex)
            {
                Logger.Error("Error processing bar in conservative strategy", ex);
                LogError("Conservative strategy error", ex);
            }
        }

        if (_mtfEngine != null && _mtfCircuitBreaker != null)
        {
            try
            {
                var executed = _mtfCircuitBreaker.Execute(() =>
                {
                    _mtfEngine.ProcessBar(bar);
                });

                if (!executed)
                {
                    Log($"[CIRCUIT BREAKER] MTF strategy blocked - circuit is OPEN", LogLevel.Warn);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Exception in MTF strategy (circuit breaker handled)", ex);
                LogError("MTF strategy exception", ex);
            }
        }
        else if (_mtfEngine != null)
        {
            try
            {
                _mtfEngine.ProcessBar(bar);
            }
            catch (Exception ex)
            {
                Logger.Error("Error processing bar in MTF strategy", ex);
                LogError("MTF strategy error", ex);
            }
        }
    }

    private void BtnStartStrategy_Click(object? sender, EventArgs e)
    {
        bool anyMTFChecked = chkMTF_5m15m1H.Checked || chkMTF_1m5m15m.Checked || chkMTF_15m1H4H.Checked || chkMTF_5m1HDaily.Checked;
        if (!chkAggressive.Checked && !chkConservative.Checked && !anyMTFChecked)
        {
            MessageBox.Show("Please select at least one strategy to run.", "No Strategy Selected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        bool useContracts = rdoContracts.Checked;

        if (chkAggressive.Checked)
        {
            // Restore saved state if available
            StrategyState? savedState = _state.AggressiveState?.InPosition == true ? _state.AggressiveState : null;

            // Create circuit breaker for aggressive strategy
            _aggressiveCircuitBreaker = new CircuitBreaker("Aggressive", new CircuitBreakerConfig
            {
                FailureThreshold = 5,
                TimeWindowSeconds = 60,
                TimeoutSeconds = 30,
                SuccessThreshold = 2
            });
            _aggressiveCircuitBreaker.OnStateChanged += (name, state) =>
            {
                Log($"[CIRCUIT BREAKER] {name} state changed to {state}", 
                    state == CircuitState.Open ? LogLevel.Error : LogLevel.Warn);
            };
            _aggressiveCircuitBreaker.OnLog += msg => Log($"[CIRCUIT] {msg}");

            _aggressiveEngine = new GCStrategyEngine(
                _ibClient!,
                GetOrderClient("Aggressive"),  // Use paper or real based on mode, wrapped with risk checks
                "Aggressive",
                0.99,  // 99% position scale
                false, // No DD protection
                useContracts ? (int)numAggressiveSize.Value : 0,
                useContracts ? 0 : (double)numAggressiveSize.Value,
                savedState: savedState
            );
            _aggressiveEngine.OnLog += msg => Log($"[AGG] {msg}");
            _aggressiveEngine.OnStateChanged += state => OnAggressiveStateChanged(state);
            _aggressiveEngine.Start();
            Log("Aggressive strategy started" + (savedState != null ? " (restored position)" : ""));
        }

        if (chkConservative.Checked)
        {
            // Restore saved state if available
            StrategyState? savedState = _state.ConservativeState?.InPosition == true ? _state.ConservativeState : null;

            // Create circuit breaker for conservative strategy
            _conservativeCircuitBreaker = new CircuitBreaker("Conservative", new CircuitBreakerConfig
            {
                FailureThreshold = 5,
                TimeWindowSeconds = 60,
                TimeoutSeconds = 30,
                SuccessThreshold = 2
            });
            _conservativeCircuitBreaker.OnStateChanged += (name, state) =>
            {
                Log($"[CIRCUIT BREAKER] {name} state changed to {state}",
                    state == CircuitState.Open ? LogLevel.Error : LogLevel.Warn);
            };
            _conservativeCircuitBreaker.OnLog += msg => Log($"[CIRCUIT] {msg}");

            _conservativeEngine = new GCStrategyEngine(
                _ibClient!,
                GetOrderClient("Conservative"),  // Use paper or real based on mode, wrapped with risk checks
                "Conservative",
                0.62,  // 62% position scale
                true,  // DD protection enabled
                useContracts ? (int)numConservativeSize.Value : 0,
                useContracts ? 0 : (double)numConservativeSize.Value,
                maxDrawdown: 0.11,  // 11% max DD
                savedState: savedState
            );
            _conservativeEngine.OnLog += msg => Log($"[CONS] {msg}");
            _conservativeEngine.OnStateChanged += state => OnConservativeStateChanged(state);
            _conservativeEngine.Start();
            Log("Conservative strategy started" + (savedState != null ? " (restored position)" : ""));
        }

        // Start MTF strategy if any preset is selected
        StartMTFStrategy();

        btnStartStrategy.Enabled = false;
        btnStopStrategy.Enabled = true;
        lblStrategyStatus.Text = "Strategy: Running";
        lblStrategyStatus.ForeColor = Color.Green;

        _state.StrategyRunning = true;
        SaveState();
    }

    private void OnAggressiveStateChanged(StrategyState state)
    {
        lock (_stateLock)
        {
            _state.AggressiveState = state;
        }
        SaveState();
        Log($"[AGG] State saved - InPosition: {state.InPosition}, Entry: {state.EntryPrice:F2}");
    }

    private void OnConservativeStateChanged(StrategyState state)
    {
        lock (_stateLock)
        {
            _state.ConservativeState = state;
        }
        SaveState();
        Log($"[CONS] State saved - InPosition: {state.InPosition}, Entry: {state.EntryPrice:F2}");
    }

    private void StartMTFStrategy()
    {
        // Determine which preset is selected (only one at a time)
        TimeframePreset? selectedPreset = null;
        string presetName = "";

        if (chkMTF_5m15m1H.Checked)
        {
            selectedPreset = TimeframePreset.Preset_5m_15m_1H;
            presetName = "MTF_5m15m1H";
        }
        else if (chkMTF_1m5m15m.Checked)
        {
            selectedPreset = TimeframePreset.Preset_1m_5m_15m;
            presetName = "MTF_1m5m15m";
        }
        else if (chkMTF_15m1H4H.Checked)
        {
            selectedPreset = TimeframePreset.Preset_15m_1H_4H;
            presetName = "MTF_15m1H4H";
        }
        else if (chkMTF_5m1HDaily.Checked)
        {
            selectedPreset = TimeframePreset.Preset_5m_1H_Daily;
            presetName = "MTF_5m1HDaily";
        }

        if (selectedPreset == null)
            return;

        // Restore saved state if available
        MTFStrategyState? savedState = _state.MTFState?.InPosition == true ? _state.MTFState : null;

        var config = new MTFStrategyConfig
        {
            Name = presetName,
            TimeframePreset = selectedPreset.Value,
            PositionScale = 0.80,
            DrawdownProtection = true,
            MaxDrawdown = 0.11,
            FixedContracts = (int)numMTFSize.Value,
            AllowShorts = chkMTFAllowShorts.Checked
        };

        // Create circuit breaker for MTF strategy
        _mtfCircuitBreaker = new CircuitBreaker($"MTF_{presetName}", new CircuitBreakerConfig
        {
            FailureThreshold = 5,
            TimeWindowSeconds = 60,
            TimeoutSeconds = 30,
            SuccessThreshold = 2
        });
        _mtfCircuitBreaker.OnStateChanged += (name, state) =>
        {
            Log($"[CIRCUIT BREAKER] {name} state changed to {state}",
                state == CircuitState.Open ? LogLevel.Error : LogLevel.Warn);
        };
        _mtfCircuitBreaker.OnLog += msg => Log($"[CIRCUIT] {msg}");

        _mtfEngine = new MTFStrategyEngine(_ibClient!, GetOrderClient(config.Name), config, savedState);
        _mtfEngine.OnLog += msg => Log(msg);
        _mtfEngine.OnStateChanged += OnMTFStateChanged;
        _mtfEngine.OnAlignmentUpdated += OnMTFAlignmentUpdated;
        _mtfEngine.Start();

        Log($"MTF strategy started: {presetName}" + (savedState != null ? " (restored position)" : ""));
    }

    private void OnMTFStateChanged(MTFStrategyState state)
    {
        lock (_stateLock)
        {
            _state.MTFState = state;
        }
        SaveState();
        var direction = state.PositionDirection == 1 ? "LONG" : state.PositionDirection == -1 ? "SHORT" : "FLAT";
        Log($"[MTF] State saved - {direction}, Entry: {state.EntryPrice:F2}");
    }

    private void OnMTFAlignmentUpdated(MTFAlignmentResult alignment)
    {
        if (InvokeRequired)
        {
            Invoke(() => OnMTFAlignmentUpdated(alignment));
            return;
        }

        // Update MTF status labels
        if (alignment.AllBullish)
        {
            lblMTFStatus.Text = "MTF: ALL BULLISH";
            lblMTFStatus.ForeColor = Color.Green;
        }
        else if (alignment.AllBearish)
        {
            lblMTFStatus.Text = "MTF: ALL BEARISH";
            lblMTFStatus.ForeColor = Color.Red;
        }
        else
        {
            lblMTFStatus.Text = "MTF: Not Aligned";
            lblMTFStatus.ForeColor = Color.Gray;
        }

        // Update individual timeframe labels
        var tfNames = alignment.DirectionByTimeframe.Keys.ToList();
        if (tfNames.Count >= 1)
        {
            var dir1 = alignment.DirectionByTimeframe[tfNames[0]];
            lblTF1Status.Text = $"{tfNames[0]}: {(dir1 == 1 ? "BULL" : dir1 == -1 ? "BEAR" : "?")}";
            lblTF1Status.ForeColor = dir1 == 1 ? Color.Green : dir1 == -1 ? Color.Red : Color.Gray;
        }
        if (tfNames.Count >= 2)
        {
            var dir2 = alignment.DirectionByTimeframe[tfNames[1]];
            lblTF2Status.Text = $"{tfNames[1]}: {(dir2 == 1 ? "BULL" : dir2 == -1 ? "BEAR" : "?")}";
            lblTF2Status.ForeColor = dir2 == 1 ? Color.Green : dir2 == -1 ? Color.Red : Color.Gray;
        }
        if (tfNames.Count >= 3)
        {
            var dir3 = alignment.DirectionByTimeframe[tfNames[2]];
            lblTF3Status.Text = $"{tfNames[2]}: {(dir3 == 1 ? "BULL" : dir3 == -1 ? "BEAR" : "?")}";
            lblTF3Status.ForeColor = dir3 == 1 ? Color.Green : dir3 == -1 ? Color.Red : Color.Gray;
        }
    }

    private void BtnStopStrategy_Click(object? sender, EventArgs e)
    {
        StopStrategies();
    }

    private void StopStrategies()
    {
        // Save final state before stopping
        if (_aggressiveEngine != null)
        {
            lock (_stateLock)
            {
                _state.AggressiveState = _aggressiveEngine.GetState();
            }
        }
        if (_conservativeEngine != null)
        {
            lock (_stateLock)
            {
                _state.ConservativeState = _conservativeEngine.GetState();
            }
        }
        if (_mtfEngine != null)
        {
            lock (_stateLock)
            {
                _state.MTFState = _mtfEngine.GetState();
            }
        }

        _aggressiveEngine?.Stop();
        _conservativeEngine?.Stop();
        _mtfEngine?.Stop();
        _aggressiveEngine = null;
        _conservativeEngine = null;
        _mtfEngine = null;
        
        // Reset circuit breakers
        _aggressiveCircuitBreaker?.Reset();
        _conservativeCircuitBreaker?.Reset();
        _mtfCircuitBreaker?.Reset();
        _aggressiveCircuitBreaker = null;
        _conservativeCircuitBreaker = null;
        _mtfCircuitBreaker = null;

        if (InvokeRequired)
        {
            Invoke(new Action(() =>
            {
                btnStartStrategy.Enabled = _isConnected;
                btnStopStrategy.Enabled = false;
                lblStrategyStatus.Text = "Strategy: Stopped";
                lblStrategyStatus.ForeColor = Color.Gray;
            }));
        }
        else
        {
            btnStartStrategy.Enabled = _isConnected;
            btnStopStrategy.Enabled = false;
            lblStrategyStatus.Text = "Strategy: Stopped";
            lblStrategyStatus.ForeColor = Color.Gray;
        }

        _state.StrategyRunning = false;
        SaveState();

        Log("Strategies stopped");
    }

    private void UpdateOrderGrid(OrderStatusData status)
    {
        lock (_stateLock)
        {
            if (_state.Orders.TryGetValue(status.OrderId, out var order))
            {
                order.Status = status.Status;
                order.Filled = status.Filled;
                order.Remaining = status.Remaining;
            }
        }
        UpdateOrdersGrid();
    }

    private void UpdateOrdersGrid()
    {
        List<OrderRecord> ordersCopy;
        lock (_stateLock)
        {
            ordersCopy = _state.Orders.Values.ToList();
        }

        dgvOrders.Rows.Clear();
        foreach (var order in ordersCopy.OrderByDescending(o => o.Time))
        {
            dgvOrders.Rows.Add(
                order.OrderId,
                order.Time.ToString("HH:mm:ss"),
                order.Strategy,
                order.Action,
                order.Quantity,
                order.OrderType,
                order.LimitPrice > 0 ? order.LimitPrice.ToString("F2") : "-",
                order.StopPrice > 0 ? order.StopPrice.ToString("F2") : "-",
                order.Status,
                order.Filled,
                order.Remaining
            );
        }
    }

    private void UpdateFillsGrid()
    {
        List<FillRecord> fillsCopy;
        lock (_stateLock)
        {
            fillsCopy = _state.Fills.ToList();
        }

        dgvFills.Rows.Clear();
        foreach (var fill in fillsCopy.OrderByDescending(f => f.Time))
        {
            dgvFills.Rows.Add(
                fill.ExecId,
                fill.Time.ToString("HH:mm:ss"),
                fill.Strategy,
                fill.Action,
                fill.Quantity,
                fill.Price.ToString("F2"),
                fill.Commission.ToString("F2"),
                fill.RealizedPnL.ToString("F2")
            );
        }
    }

    private void UpdatePositionsGrid()
    {
        List<PositionRecord> positionsCopy;
        lock (_stateLock)
        {
            positionsCopy = _state.Positions.Values.ToList();
        }

        dgvPositions.Rows.Clear();
        foreach (var pos in positionsCopy)
        {
            dgvPositions.Rows.Add(
                pos.Strategy ?? "Manual",
                pos.Symbol,
                pos.Position,
                pos.AvgCost.ToString("F2"),
                pos.MarketPrice.ToString("F2"),
                pos.MarketValue.ToString("F2"),
                pos.UnrealizedPnL.ToString("F2"),
                pos.RealizedPnL.ToString("F2")
            );
        }
    }

    private void Log(string message, LogLevel level = LogLevel.Info)
    {
        // Always write to file first (thread-safe)
        Logger.Log(message, level);

        // Don't try to update UI if form is disposed or disposing
        if (IsDisposed || !IsHandleCreated) return;

        if (InvokeRequired)
        {
            try
            {
                Invoke(new Action(() => Log(message, level)));
            }
            catch (ObjectDisposedException)
            {
                // Form was disposed while invoking - ignore
            }
            return;
        }

        if (txtLog == null || txtLog.IsDisposed) return;

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var prefix = level == LogLevel.Error ? "[ERROR] " : level == LogLevel.Warn ? "[WARN] " : "";
        txtLog.AppendText($"[{timestamp}] {prefix}{message}\n");
        txtLog.ScrollToCaret();
    }

    private void LogError(string message, Exception ex)
    {
        Logger.Error(message, ex);

        // Don't try to update UI if form is disposed or disposing
        if (IsDisposed || !IsHandleCreated) return;

        if (InvokeRequired)
        {
            try
            {
                Invoke(new Action(() => LogError(message, ex)));
            }
            catch (ObjectDisposedException)
            {
                // Form was disposed while invoking - ignore
            }
            return;
        }

        if (txtLog == null || txtLog.IsDisposed) return;

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        txtLog.SelectionColor = Color.Red;
        txtLog.AppendText($"[{timestamp}] [ERROR] {message}: {ex.Message}\n");
        txtLog.SelectionColor = txtLog.ForeColor;
        txtLog.ScrollToCaret();
    }

    private AppState LoadState()
    {
        try
        {
            if (File.Exists(_stateFilePath))
            {
                var json = File.ReadAllText(_stateFilePath);
                return JsonConvert.DeserializeObject<AppState>(json) ?? new AppState();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load state: {ex.Message}");
        }
        return new AppState();
    }

    private void SaveState()
    {
        try
        {
            string json;
            lock (_stateLock)
            {
                // Update state from UI (safe - we're on UI thread)
                _state.Host = txtHost.Text;
                _state.Port = (int)numPort.Value;
                _state.ClientId = (int)numClientId.Value;
                _state.AggressiveEnabled = chkAggressive.Checked;
                _state.ConservativeEnabled = chkConservative.Checked;
                _state.SizingMode = rdoContracts.Checked ? "Contracts" : "Capital";
                _state.AggressiveSize = (double)numAggressiveSize.Value;
                _state.ConservativeSize = (double)numConservativeSize.Value;

                // Update risk settings from UI
                _state.RiskSettings.MaxDailyLossUsd = (double)numMaxDailyLoss.Value;
                _state.RiskSettings.MaxTotalContracts = (int)numMaxContracts.Value;
                _state.RiskSettings.AutoFlattenOnLimit = chkAutoFlatten.Checked;

                // Update MTF settings from UI
                _state.MTF_5m15m1H_Enabled = chkMTF_5m15m1H.Checked;
                _state.MTF_1m5m15m_Enabled = chkMTF_1m5m15m.Checked;
                _state.MTF_15m1H4H_Enabled = chkMTF_15m1H4H.Checked;
                _state.MTF_5m1HDaily_Enabled = chkMTF_5m1HDaily.Checked;
                _state.MTFSize = (int)numMTFSize.Value;
                _state.MTFAllowShorts = chkMTFAllowShorts.Checked;

                // Update risk manager settings if active
                if (_riskManager != null)
                {
                    _riskManager.Settings = _state.RiskSettings;
                }

                // Serialize under lock to prevent concurrent modification
                json = JsonConvert.SerializeObject(_state, Formatting.Indented);
            }
            File.WriteAllText(_stateFilePath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save state: {ex.Message}");
        }
    }

    private void ApplyStateToUI()
    {
        // Restore orders/fills/positions from saved state
        UpdateOrdersGrid();
        UpdateFillsGrid();
        UpdatePositionsGrid();
    }

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        StopStrategies();

        // Send daily summary before closing
        if (_performanceTracker != null && _alertManager != null)
        {
            var metrics = _performanceTracker.GetMetrics();
            _alertManager.SendDailySummary(metrics, _riskManager?.State);
        }

        // Stop alert manager
        _alertManager?.Stop();
        _alertManager?.Dispose();

        // Stop performance tracker
        _performanceTracker?.Dispose();

        // Stop risk manager
        _riskManager?.Stop();
        _riskManager?.Dispose();

        // Stop order tracker
        _orderTracker?.Stop();
        _orderTracker?.Dispose();

        // Disconnect and dispose connection manager
        _connectionManager?.Disconnect();
        _connectionManager?.Dispose();

        SaveState();
    }
}
