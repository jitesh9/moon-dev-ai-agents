# GC Gold Futures Trading Application

A standalone C# WinForms application (.NET 8.0) for trading Gold Futures (GC) via IBKR TWS API using RSI/MACD divergence strategies.

## Features

- **Two Strategy Modes**:
  - **Aggressive**: 99% position sizing, no drawdown protection, ~187% CAGR backtest, ~48% max DD
  - **Conservative**: 62% position sizing, 11% max DD limit, ~11% CAGR backtest, ~10% max DD

- **Position Sizing Options**:
  - Fixed number of contracts
  - Capital allocation (auto-calculates contracts)

- **Real-time Monitoring**:
  - Open orders tracking
  - Fill/execution history
  - Position management
  - Account equity and drawdown

- **State Persistence**:
  - Saves all orders, fills, positions to JSON
  - Restores state on application restart
  - Tracks peak equity for drawdown calculations

## Requirements

- Windows 10/11
- .NET 8.0 Runtime
- IBKR TWS or IB Gateway installed and running
- TWS API enabled in TWS settings

## TWS Configuration

1. Open TWS → File → Global Configuration → API → Settings
2. Enable "Enable ActiveX and Socket Clients"
3. Set Socket Port: 7497 (paper) or 7496 (live)
4. Add trusted IP: 127.0.0.1 (or your machine's IP)
5. Disable "Read-Only API" if you want to place orders

## Building

```bash
cd src/apps/GCTradingApp
dotnet build
```

## Running

```bash
dotnet run
```

Or execute the built binary:
```
bin\Debug\net8.0-windows\GCTradingApp.exe
```

## Usage

### 1. Connect to TWS
- Enter Host (default: 127.0.0.1)
- Enter Port (default: 7497 for paper trading)
- Enter Client ID (any unique number)
- Click "Connect"

### 2. Configure Strategy
- Select **Aggressive** and/or **Conservative** strategy
- Choose sizing mode:
  - **Contracts**: Enter fixed number of contracts per strategy
  - **Capital ($)**: Enter dollar amount to allocate per strategy

### 3. Start Trading
- Click "Start Strategy"
- Monitor activity in the Log tab
- View open orders, fills, and positions in their respective tabs

### 4. Stop Trading
- Click "Stop Strategy" to halt new signal processing
- Existing positions will remain until manually closed

## Strategy Logic

### Entry Conditions
1. **Bullish Divergence**: Price makes lower low while RSI or MACD makes higher low
2. **Minimum 4 Confirmations** from:
   - Bull regime (price > 50 SMA)
   - EMA bullish (EMA13 > EMA34)
   - SuperTrend bullish
   - RSI in valid range (30-70)
   - MACD improving
   - Volatility OK (ATR > 80% of average)
3. **Trading Hours**: 8 AM - 5 PM

### Exit Conditions
- **Stop Loss**: 1.5x ATR below entry
- **Take Profit**: 2.5x ATR above entry
- **Trailing Stop**: Activates at 1.5% profit, trails by 1.0x ATR
- **Time Exit**: Maximum 60 bars holding period
- **Momentum Exit**: Close if MACD declining while profitable
- **Emergency Exit** (Conservative only): Close at 95% of max DD limit

### Conservative Drawdown Protection
| Drawdown | Position Size | Action |
|----------|---------------|--------|
| 0-8% | 62% | Normal trading |
| 8-10% | 62% → 19% | Linear reduction |
| 10%+ | 0% | Pause trading |
| 10.45%+ | - | Emergency exit |

## File Structure

```
GCTradingApp/
├── GCTradingApp.csproj    # Project file
├── Program.cs             # Entry point with global exception handlers
├── MainForm.cs            # UI and main logic
├── IBKRClient.cs          # TWS API wrapper
├── GCStrategyEngine.cs    # Divergence strategy implementation
├── DataModels.cs          # Data classes and state persistence
├── Logger.cs              # File logging utility
└── README.md              # This file
```

## Logging

The application includes comprehensive file logging for debugging and monitoring:

- **Log Location**: `logs/gc_trading_YYYY-MM-DD.log` in the application directory
- **Daily Rotation**: New log file created each day
- **Log Levels**: Debug, Info, Warn, Error
- **Exception Capture**: Global handlers catch unhandled exceptions

Log files track:
- Application startup/shutdown
- TWS connection attempts and status
- Order placements and fills
- Strategy signals and position management
- Errors with full stack traces

To view logs:
```
bin\Debug\net8.0-windows\logs\
```

## State File

The application saves state to `gc_trading_state.json` in the application directory:
- Connection settings
- Strategy configuration
- Order history
- Fill history
- Position tracking
- Peak equity for drawdown calculation

## Risk Warning

- **This is experimental software** - use at your own risk
- **Paper trade first** - test thoroughly before live trading
- **No guarantees** - past backtest results don't guarantee future performance
- **Significant risk** - Aggressive strategy has ~48% max drawdown potential
- **Monitor actively** - Don't leave unattended without stop losses

## Backtest Results (Reference)

### Aggressive Strategy
- Total Return: 19,253.69%
- CAGR: 187.02%
- Sharpe Ratio: 1.048
- Max Drawdown: 48.58%
- Win Rate: 68.5%

### Conservative Strategy
- Total Return: 69.74%
- CAGR: 11.18%
- Sharpe Ratio: 0.851
- Max Drawdown: 10.41%
- Win Rate: 61.2%

## Author

Moon Dev AI - November 2025
