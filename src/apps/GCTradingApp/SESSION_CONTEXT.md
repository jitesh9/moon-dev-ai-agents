# GC Trading App - Session Context

## Session Date: November 23, 2025

## What Was Built

A standalone C# WinForms application (.NET 8.0) that connects to IBKR TWS API to trade Gold Futures (GC) using RSI/MACD divergence strategies.

## Project Location
`C:\dev\moondev-ai-agents\src\apps\GCTradingApp\`

## Files Created

1. **GCTradingApp.csproj** - .NET 8.0 WinForms project file
   - References: CSharpAPI.dll (TWS API), Newtonsoft.Json
   - Target: net8.0-windows

2. **Program.cs** - Entry point

3. **MainForm.cs** - Main UI containing:
   - Connection panel (host, port, client ID)
   - Strategy settings (Aggressive/Conservative checkboxes)
   - Position sizing (contracts or capital)
   - DataGridViews for orders, fills, positions
   - Activity log
   - Account equity and drawdown display

4. **IBKRClient.cs** - TWS API wrapper implementing EWrapper interface
   - Connection management
   - GC futures contract definition (auto-selects next expiry)
   - Order placement (market, stop, bracket)
   - Real-time bar subscription
   - Account updates
   - Event-based callbacks for UI updates

5. **GCStrategyEngine.cs** - Strategy implementation
   - RSI/MACD divergence detection
   - 6 confirmation factors (regime, EMA, SuperTrend, RSI, MACD, volatility)
   - Position management with trailing stops
   - Drawdown protection (Conservative mode)
   - Technical indicators: ATR, RSI, MACD, EMA, SMA, SuperTrend

6. **DataModels.cs** - Data classes
   - AppState (persisted to JSON)
   - OrderRecord, FillRecord, PositionRecord
   - StrategyState
   - Event data classes for IBKR callbacks

7. **README.md** - Usage documentation

## Strategy Parameters (from Python backtests)

### Entry Conditions
- Bullish divergence (price lower low + RSI/MACD higher low)
- Minimum 4 confirmations
- Trading hours: 8 AM - 5 PM

### Exit Conditions
- Stop Loss: 1.5x ATR
- Take Profit: 2.5x ATR
- Trailing Stop: 1.0x ATR @ 1.5% profit
- Time Exit: 60 bars max
- Momentum Exit: MACD declining while profitable

### Strategy Variants

| Variant | Position Scale | Max DD | DD Protection |
|---------|----------------|--------|---------------|
| Aggressive | 99% | None | No |
| Conservative | 62% | 11% | Yes |

## Backtest Results (Reference)

### Aggressive
- CAGR: 187.02%
- Max DD: 48.58%
- Sharpe: 1.048
- Win Rate: 68.5%

### Conservative
- CAGR: 11.18%
- Max DD: 10.41%
- Sharpe: 0.851
- Win Rate: 61.2%

## Build Status
- **Restored**: Success
- **Built**: Success (2 minor warnings about unused fields)
- **Output**: `bin\Debug\net8.0-windows\GCTradingApp.exe`

## Dependencies
- TWS API DLL: `C:\TWS API\source\CSharpClient\client\bin\Release\netstandard2.0\CSharpAPI.dll`
- NuGet: Newtonsoft.Json 13.0.3

## State Persistence
Application saves to `gc_trading_state.json`:
- Connection settings
- Strategy configuration
- Orders, fills, positions
- Peak equity (for drawdown calculation)

## Related Python Strategy Files
Located at `C:\dev\moondev-ai-agents\src\strategies\`:
- `gc_divergence_sharpe.py` - Base strategy
- `gc_divergence_aggressive.py` - Aggressive variant
- `gc_divergence_conservative.py` - Conservative variant
- `gc_divergence_robustness.py` - Robustness testing
- `GC_SHARPE_STRATEGY_RESULTS.md`
- `GC_AGGRESSIVE_STRATEGY_RESULTS.md`
- `GC_CONSERVATIVE_STRATEGY_RESULTS.md`

## To Continue Development
1. Run with TWS paper trading to validate order flow
2. Add historical data request for indicator warm-up
3. Implement hourly bar aggregation from 5-second real-time bars
4. Add position quantity tracking per strategy
5. Enhance divergence detection with more lookback patterns
