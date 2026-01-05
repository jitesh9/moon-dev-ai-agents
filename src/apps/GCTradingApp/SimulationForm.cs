/*
 * Simulation Form
 * UI for simulation mode - allows manual price control and historical data navigation
 */

using System.Windows.Forms;

namespace GCTradingApp;

/// <summary>
/// Form for simulation mode with price controls and strategy condition display
/// </summary>
public class SimulationForm : UserControl
{
    private SimulationEngine? _simulationEngine;
    
    // Price Control Panel
    private TextBox txtPrice = null!;
    private TrackBar trkPrice = null!;
    private Label lblPrice = null!;
    private Button btnStepDown01 = null!;
    private Button btnStepUp01 = null!;
    private Button btnStepDown05 = null!;
    private Button btnStepUp05 = null!;
    private Button btnStepDown1 = null!;
    private Button btnStepUp1 = null!;
    private Button btnStepDown5 = null!;
    private Button btnStepUp5 = null!;
    
    // Historical Data Navigation
    private Button btnLoadFile = null!;
    private Button btnFirst = null!;
    private Button btnPrev = null!;
    private Button btnNext = null!;
    private Button btnLast = null!;
    private Label lblBarInfo = null!;
    private TextBox txtJumpTo = null!;
    private Button btnJumpTo = null!;
    
    // Strategy Condition Displays
    private TabControl tabStrategies = null!;
    private Dictionary<string, DataGridView> _entryGrids = new();
    private Dictionary<string, DataGridView> _exitGrids = new();
    private Dictionary<string, Label> _entryLabels = new();
    private Dictionary<string, Label> _exitLabels = new();
    
    // Event Log
    private RichTextBox txtEventLog = null!;
    
    // Current price for slider
    private double _currentPrice = 2050.0;
    private double _sliderMin = 2000.0;
    private double _sliderMax = 2100.0;

    public SimulationForm()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        this.Dock = DockStyle.Fill;
        this.Padding = new Padding(10);

        var mainPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(5)
        };
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 120)); // Price controls
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));  // Historical navigation
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // Strategy conditions
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 150)); // Event log

        // Price Control Panel
        var grpPrice = CreatePriceControlPanel();
        mainPanel.Controls.Add(grpPrice, 0, 0);

        // Historical Navigation Panel
        var grpHistory = CreateHistoricalNavigationPanel();
        mainPanel.Controls.Add(grpHistory, 0, 1);

        // Strategy Conditions Panel
        var grpStrategies = CreateStrategyConditionsPanel();
        mainPanel.Controls.Add(grpStrategies, 0, 2);

        // Event Log Panel
        var grpLog = CreateEventLogPanel();
        mainPanel.Controls.Add(grpLog, 0, 3);

        this.Controls.Add(mainPanel);
    }

    private Panel CreatePriceControlPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill };

        var grp = new GroupBox
        {
            Text = "Price Control",
            Dock = DockStyle.Fill
        };

        // Manual input
        var lblPriceInput = new Label { Text = "Price:", Location = new Point(10, 25), AutoSize = true };
        txtPrice = new TextBox
        {
            Location = new Point(60, 22),
            Width = 100,
            Text = _currentPrice.ToString("F2")
        };
        txtPrice.KeyDown += TxtPrice_KeyDown;
        txtPrice.Leave += TxtPrice_Leave;

        // Slider
        var lblSlider = new Label { Text = "Slider:", Location = new Point(180, 25), AutoSize = true };
        trkPrice = new TrackBar
        {
            Location = new Point(230, 20),
            Width = 300,
            Minimum = 0,
            Maximum = 1000,
            Value = 500,
            TickFrequency = 100
        };
        trkPrice.ValueChanged += TrkPrice_ValueChanged;

        lblPrice = new Label
        {
            Text = $"${_currentPrice:F2}",
            Location = new Point(540, 25),
            AutoSize = true,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };

        // Step buttons
        btnStepDown01 = new Button { Text = "-$0.10", Location = new Point(10, 55), Size = new Size(60, 25) };
        btnStepUp01 = new Button { Text = "+$0.10", Location = new Point(75, 55), Size = new Size(60, 25) };
        btnStepDown05 = new Button { Text = "-$0.50", Location = new Point(140, 55), Size = new Size(60, 25) };
        btnStepUp05 = new Button { Text = "+$0.50", Location = new Point(205, 55), Size = new Size(60, 25) };
        btnStepDown1 = new Button { Text = "-$1.00", Location = new Point(270, 55), Size = new Size(60, 25) };
        btnStepUp1 = new Button { Text = "+$1.00", Location = new Point(335, 55), Size = new Size(60, 25) };
        btnStepDown5 = new Button { Text = "-$5.00", Location = new Point(400, 55), Size = new Size(60, 25) };
        btnStepUp5 = new Button { Text = "+$5.00", Location = new Point(465, 55), Size = new Size(60, 25) };

        btnStepDown01.Click += (s, e) => StepPrice(-0.10);
        btnStepUp01.Click += (s, e) => StepPrice(0.10);
        btnStepDown05.Click += (s, e) => StepPrice(-0.50);
        btnStepUp05.Click += (s, e) => StepPrice(0.50);
        btnStepDown1.Click += (s, e) => StepPrice(-1.00);
        btnStepUp1.Click += (s, e) => StepPrice(1.00);
        btnStepDown5.Click += (s, e) => StepPrice(-5.00);
        btnStepUp5.Click += (s, e) => StepPrice(5.00);

        grp.Controls.AddRange(new Control[]
        {
            lblPriceInput, txtPrice, lblSlider, trkPrice, lblPrice,
            btnStepDown01, btnStepUp01, btnStepDown05, btnStepUp05,
            btnStepDown1, btnStepUp1, btnStepDown5, btnStepUp5
        });

        panel.Controls.Add(grp);
        return panel;
    }

    private Panel CreateHistoricalNavigationPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill };

        var grp = new GroupBox
        {
            Text = "Historical Data Navigation",
            Dock = DockStyle.Fill
        };

        btnLoadFile = new Button { Text = "Load File...", Location = new Point(10, 25), Size = new Size(90, 30) };
        btnLoadFile.Click += BtnLoadFile_Click;

        btnFirst = new Button { Text = "◄◄", Location = new Point(110, 25), Size = new Size(40, 30) };
        btnPrev = new Button { Text = "◄", Location = new Point(155, 25), Size = new Size(40, 30) };
        btnNext = new Button { Text = "►", Location = new Point(200, 25), Size = new Size(40, 30) };
        btnLast = new Button { Text = "►►", Location = new Point(245, 25), Size = new Size(40, 30) };

        btnFirst.Click += (s, e) => _simulationEngine?.JumpToFirst();
        btnPrev.Click += (s, e) => _simulationEngine?.StepBackward();
        btnNext.Click += (s, e) => _simulationEngine?.StepForward();
        btnLast.Click += (s, e) => _simulationEngine?.JumpToLast();

        lblBarInfo = new Label
        {
            Text = "No data loaded",
            Location = new Point(300, 30),
            AutoSize = true
        };

        var lblJump = new Label { Text = "Jump to bar:", Location = new Point(10, 65), AutoSize = true };
        txtJumpTo = new TextBox { Location = new Point(90, 62), Width = 60 };
        btnJumpTo = new Button { Text = "Go", Location = new Point(155, 60), Size = new Size(50, 25) };
        btnJumpTo.Click += BtnJumpTo_Click;

        grp.Controls.AddRange(new Control[]
        {
            btnLoadFile, btnFirst, btnPrev, btnNext, btnLast, lblBarInfo,
            lblJump, txtJumpTo, btnJumpTo
        });

        panel.Controls.Add(grp);
        return panel;
    }

    private Panel CreateStrategyConditionsPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill };

        tabStrategies = new TabControl { Dock = DockStyle.Fill };

        panel.Controls.Add(tabStrategies);
        return panel;
    }

    private Panel CreateEventLogPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill };

        var grp = new GroupBox
        {
            Text = "Simulation Event Log",
            Dock = DockStyle.Fill
        };

        txtEventLog = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            Font = new Font("Consolas", 9)
        };

        grp.Controls.Add(txtEventLog);
        panel.Controls.Add(grp);
        return panel;
    }

    /// <summary>
    /// Set the simulation engine and subscribe to events
    /// </summary>
    public void SetSimulationEngine(SimulationEngine engine)
    {
        if (_simulationEngine != null)
        {
            // Unsubscribe from old engine
            _simulationEngine.OnBarChanged -= SimulationEngine_OnBarChanged;
            _simulationEngine.OnEntryConditionsUpdated -= SimulationEngine_OnEntryConditionsUpdated;
            _simulationEngine.OnExitConditionsUpdated -= SimulationEngine_OnExitConditionsUpdated;
            _simulationEngine.OnSimulationEvent -= SimulationEngine_OnSimulationEvent;
            _simulationEngine.OnLog -= SimulationEngine_OnLog;
        }

        _simulationEngine = engine;

        // Subscribe to new engine
        _simulationEngine.OnBarChanged += SimulationEngine_OnBarChanged;
        _simulationEngine.OnEntryConditionsUpdated += SimulationEngine_OnEntryConditionsUpdated;
        _simulationEngine.OnExitConditionsUpdated += SimulationEngine_OnExitConditionsUpdated;
        _simulationEngine.OnSimulationEvent += SimulationEngine_OnSimulationEvent;
        _simulationEngine.OnLog += SimulationEngine_OnLog;

        UpdateBarInfo();
    }

    /// <summary>
    /// Register a strategy for display
    /// </summary>
    public void RegisterStrategy(string strategyName)
    {
        if (_entryGrids.ContainsKey(strategyName))
            return;

        var tab = new TabPage(strategyName);
        
        var splitContainer = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal
        };

        // Entry conditions
        var grpEntry = new GroupBox
        {
            Text = "Entry Conditions",
            Dock = DockStyle.Fill
        };

        var lblEntry = new Label
        {
            Text = "Status: Not Running",
            Location = new Point(10, 25),
            AutoSize = true,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = Color.Gray
        };
        _entryLabels[strategyName] = lblEntry;

        var dgvEntry = new DataGridView
        {
            Location = new Point(10, 55),
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            ReadOnly = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            RowHeadersVisible = false
        };
        dgvEntry.Columns.Add("Condition", "Condition");
        dgvEntry.Columns.Add("Status", "Status");
        dgvEntry.Columns.Add("Description", "Description");
        dgvEntry.Columns.Add("Value", "Value");
        dgvEntry.Columns["Status"].Width = 80;
        dgvEntry.Columns["Value"].Width = 120;
        _entryGrids[strategyName] = dgvEntry;

        grpEntry.Controls.Add(lblEntry);
        grpEntry.Controls.Add(dgvEntry);

        // Exit conditions
        var grpExit = new GroupBox
        {
            Text = "Exit Conditions",
            Dock = DockStyle.Fill
        };

        var lblExit = new Label
        {
            Text = "Status: Not In Position",
            Location = new Point(10, 25),
            AutoSize = true,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = Color.Gray
        };
        _exitLabels[strategyName] = lblExit;

        var dgvExit = new DataGridView
        {
            Location = new Point(10, 55),
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            ReadOnly = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            RowHeadersVisible = false
        };
        dgvExit.Columns.Add("Condition", "Condition");
        dgvExit.Columns.Add("Status", "Status");
        dgvExit.Columns.Add("Description", "Description");
        dgvExit.Columns.Add("Value", "Value");
        dgvExit.Columns["Status"].Width = 80;
        dgvExit.Columns["Value"].Width = 120;
        _exitGrids[strategyName] = dgvExit;

        grpExit.Controls.Add(lblExit);
        grpExit.Controls.Add(dgvExit);

        splitContainer.Panel1.Controls.Add(grpEntry);
        splitContainer.Panel2.Controls.Add(grpExit);
        splitContainer.SplitterDistance = splitContainer.Height / 2;

        tab.Controls.Add(splitContainer);
        tabStrategies.TabPages.Add(tab);
    }

    private void TxtPrice_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            ApplyPriceFromTextBox();
        }
    }

    private void TxtPrice_Leave(object? sender, EventArgs e)
    {
        ApplyPriceFromTextBox();
    }

    private void ApplyPriceFromTextBox()
    {
        if (double.TryParse(txtPrice.Text, out var price) && price > 0)
        {
            _currentPrice = price;
            UpdateSliderFromPrice();
            _simulationEngine?.SetPrice(price);
        }
        else
        {
            txtPrice.Text = _currentPrice.ToString("F2");
        }
    }

    private void TrkPrice_ValueChanged(object? sender, EventArgs e)
    {
        var sliderValue = trkPrice.Value;
        var price = _sliderMin + (sliderValue / 1000.0) * (_sliderMax - _sliderMin);
        _currentPrice = price;
        txtPrice.Text = price.ToString("F2");
        lblPrice.Text = $"${price:F2}";
        _simulationEngine?.SetPrice(price);
    }

    private void StepPrice(double increment)
    {
        _currentPrice += increment;
        if (_currentPrice < 0) _currentPrice = 0;
        
        txtPrice.Text = _currentPrice.ToString("F2");
        UpdateSliderFromPrice();
        _simulationEngine?.SetPrice(_currentPrice);
    }

    private void UpdateSliderFromPrice()
    {
        if (_currentPrice < _sliderMin) _sliderMin = _currentPrice - 50;
        if (_currentPrice > _sliderMax) _sliderMax = _currentPrice + 50;

        var sliderValue = (int)(((_currentPrice - _sliderMin) / (_sliderMax - _sliderMin)) * 1000);
        sliderValue = Math.Max(0, Math.Min(1000, sliderValue));
        trkPrice.Value = sliderValue;
        lblPrice.Text = $"${_currentPrice:F2}";
    }

    private void BtnLoadFile_Click(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv|JSON files (*.json)|*.json|All files (*.*)|*.*",
            Title = "Load Historical Data"
        };

        if (dialog.ShowDialog() == DialogResult.OK)
        {
            try
            {
                _simulationEngine?.LoadHistoricalData(dialog.FileName);
                UpdateBarInfo();
                
                // Set price from first bar
                if (_simulationEngine?.CurrentBar != null)
                {
                    _currentPrice = _simulationEngine.CurrentBar.Close;
                    txtPrice.Text = _currentPrice.ToString("F2");
                    UpdateSliderFromPrice();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading file: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private void BtnJumpTo_Click(object? sender, EventArgs e)
    {
        if (int.TryParse(txtJumpTo.Text, out var index))
        {
            _simulationEngine?.JumpToBar(index - 1); // Convert to 0-based
            UpdateBarInfo();
        }
        else
        {
            MessageBox.Show("Please enter a valid bar number", "Invalid Input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void SimulationEngine_OnBarChanged(BarData bar)
    {
        if (InvokeRequired)
        {
            Invoke(new Action(() => SimulationEngine_OnBarChanged(bar)));
            return;
        }

        _currentPrice = bar.Close;
        txtPrice.Text = _currentPrice.ToString("F2");
        UpdateSliderFromPrice();
        UpdateBarInfo();
    }

    private void SimulationEngine_OnEntryConditionsUpdated(Dictionary<string, EntryConditionsResult> results)
    {
        if (InvokeRequired)
        {
            Invoke(new Action(() => SimulationEngine_OnEntryConditionsUpdated(results)));
            return;
        }

        foreach (var kvp in results)
        {
            if (_entryGrids.TryGetValue(kvp.Key, out var dgv) && _entryLabels.TryGetValue(kvp.Key, out var lbl))
            {
                UpdateEntryConditionsGrid(dgv, lbl, kvp.Value);
            }
        }
    }

    private void SimulationEngine_OnExitConditionsUpdated(Dictionary<string, ExitConditionsResult> results)
    {
        if (InvokeRequired)
        {
            Invoke(new Action(() => SimulationEngine_OnExitConditionsUpdated(results)));
            return;
        }

        foreach (var kvp in results)
        {
            if (_exitGrids.TryGetValue(kvp.Key, out var dgv) && _exitLabels.TryGetValue(kvp.Key, out var lbl))
            {
                UpdateExitConditionsGrid(dgv, lbl, kvp.Value);
            }
        }
    }

    private void SimulationEngine_OnSimulationEvent(string strategy, string message)
    {
        if (InvokeRequired)
        {
            Invoke(new Action(() => SimulationEngine_OnSimulationEvent(strategy, message)));
            return;
        }

        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        txtEventLog.AppendText($"[{timestamp}] [{strategy}] {message}\n");
        txtEventLog.ScrollToCaret();
    }

    private void SimulationEngine_OnLog(string message)
    {
        if (InvokeRequired)
        {
            Invoke(new Action(() => SimulationEngine_OnLog(message)));
            return;
        }

        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        txtEventLog.AppendText($"[{timestamp}] {message}\n");
        txtEventLog.ScrollToCaret();
    }

    private void UpdateEntryConditionsGrid(DataGridView dgv, Label lbl, EntryConditionsResult result)
    {
        dgv.Rows.Clear();

        foreach (var condition in result.Conditions)
        {
            var row = dgv.Rows.Add(
                condition.ConditionName,
                condition.IsTrue ? "✓ TRUE" : "✗ FALSE",
                condition.Description,
                condition.Value ?? ""
            );

            var statusCell = dgv.Rows[row].Cells["Status"];
            if (condition.IsTrue)
            {
                statusCell.Style.ForeColor = Color.Green;
                statusCell.Style.Font = new Font(dgv.Font, FontStyle.Bold);
            }
            else
            {
                statusCell.Style.ForeColor = Color.Red;
            }
        }

        // Update label
        if (result.CanEnter)
        {
            lbl.Text = "✓ READY TO ENTER";
            lbl.ForeColor = Color.Green;
        }
        else if (result.BlockingReason != null)
        {
            lbl.Text = $"✗ BLOCKED: {result.BlockingReason}";
            lbl.ForeColor = Color.Red;
        }
        else
        {
            lbl.Text = "⏳ Waiting for conditions...";
            lbl.ForeColor = Color.Orange;
        }
    }

    private void UpdateExitConditionsGrid(DataGridView dgv, Label lbl, ExitConditionsResult result)
    {
        dgv.Rows.Clear();

        if (!result.InPosition)
        {
            lbl.Text = "Status: Not In Position";
            lbl.ForeColor = Color.Gray;
            return;
        }

        foreach (var condition in result.Conditions)
        {
            var row = dgv.Rows.Add(
                condition.ConditionName,
                condition.IsTrue ? "✓ TRUE" : "✗ FALSE",
                condition.Description,
                condition.Value ?? ""
            );

            var statusCell = dgv.Rows[row].Cells["Status"];
            if (condition.IsTrue)
            {
                statusCell.Style.ForeColor = Color.Green;
                statusCell.Style.Font = new Font(dgv.Font, FontStyle.Bold);
            }
            else
            {
                statusCell.Style.ForeColor = Color.Red;
            }
        }

        // Update label
        if (result.ShouldExit)
        {
            lbl.Text = $"⚠ EXIT SIGNAL: {result.ExitReason}";
            lbl.ForeColor = Color.Red;
        }
        else
        {
            lbl.Text = $"In Position - Entry: {result.EntryPrice:F2}, Current: {result.CurrentPrice:F2}, PnL: ${result.UnrealizedPnL:F2} ({result.UnrealizedPnLPct:F2}%)";
            lbl.ForeColor = result.UnrealizedPnL >= 0 ? Color.Green : Color.Red;
        }
    }

    private void UpdateBarInfo()
    {
        if (_simulationEngine == null)
        {
            lblBarInfo.Text = "No simulation engine";
            return;
        }

        if (!_simulationEngine.HasHistoricalData)
        {
            lblBarInfo.Text = "No data loaded";
            return;
        }

        var current = _simulationEngine.CurrentBarIndex + 1;
        var total = _simulationEngine.TotalBars;
        var bar = _simulationEngine.CurrentBar;
        
        if (bar != null)
        {
            lblBarInfo.Text = $"Bar {current}/{total} - {bar.Time:yyyy-MM-dd HH:mm:ss} - O:{bar.Open:F2} H:{bar.High:F2} L:{bar.Low:F2} C:{bar.Close:F2}";
        }
        else
        {
            lblBarInfo.Text = $"Bar {current}/{total}";
        }
    }
}

