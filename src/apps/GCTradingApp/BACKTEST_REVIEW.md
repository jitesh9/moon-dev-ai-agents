# GC Trading Strategy Backtest Review

**Review Date**: 2026-01-07
**Reviewer**: Claude Code
**Strategy**: GC (Gold Futures) RSI/MACD Divergence Trading

---

## Executive Summary

The GC Trading Strategy implements RSI/MACD divergence detection with multi-confirmation entry for Gold Futures (NYMEX). The backtest framework is well-structured with good event-driven architecture, but contains several implementation bugs that affect result accuracy.

**Key Metrics (Claimed)**:

| Mode | Target CAGR | Max Drawdown | Win Rate | Position Scale |
|------|-------------|--------------|----------|----------------|
| Aggressive | 187% | 48% | 68.5% | 1.0x |
| Conservative | 11.18% | 10.41% | 61.2% | 0.62x |

---

## Issues Found

### Issue #1: StepBackward Corrupts Bar Data (CRITICAL)

**Location**: `SimulationEngine.cs:202-228`

**Problem**: When stepping backward through historical data, the simulation still calls `ProcessBar()` which adds the bar to the strategy's internal `_bars` list. Walking forward→backward→forward causes duplicate bars, corrupting all indicator calculations.

```csharp
public bool StepBackward()
{
    // ...
    ProcessBar(_currentBar);  // BUG: Adds bar to _bars list again!
}
```

**Impact**: Backtest results are unreliable when using step-backward functionality.

**Fix**: Create evaluation-only mode that doesn't modify strategy state.

---

### Issue #2: Divergence Detection Window Too Narrow (HIGH)

**Location**: `GCStrategyEngine.cs:344-355`

**Problem**: The divergence detection loop can identify two "lows" that are only 1 bar apart, which doesn't represent meaningful price structure divergence.

```csharp
for (int i = 5; i < 15; i++)
{
    if (priceLow1 < 0) priceLow1 = i;
    else priceLow2 = i;  // Could be just 1 bar apart!
}
```

**Impact**: False divergence signals, potentially inflating win rate.

**Fix**: Require minimum 5-bar separation between detected lows.

---

### Issue #3: SuperTrend Implementation Incorrect (HIGH)

**Location**: `GCStrategyEngine.cs:664-676`

**Problem**: Standard SuperTrend maintains band state across bars and flips on breakouts. This implementation recalculates from scratch each bar without tracking trend direction history.

```csharp
private double CalculateSuperTrend(...)
{
    // Simplified: return lower band for bullish
    return closes.Last() > hl2 ? lowerBand : upperBand;  // Wrong!
}
```

**Impact**: SuperTrend confirmation signals differ from standard implementation. Backtest results won't match live trading behavior.

**Fix**: Implement proper SuperTrend with trend direction state tracking.

---

### Issue #4: CalculateBarATR Naming Misleading (MEDIUM)

**Location**: `GCStrategyEngine.cs:595-598`

**Problem**: Function named "ATR" but only calculates bar range (High - Low), not True Range.

```csharp
private double CalculateBarATR(BarData bar)
{
    return bar.High - bar.Low;  // This is Range, not ATR
}
```

**Impact**: Volatility confirmation may pass/fail incorrectly.

**Fix**: Rename to `CalculateBarRange()` or implement true ATR calculation.

---

### Issue #5: MACD History Corruption on Backward Steps (MEDIUM)

**Location**: `GCStrategyEngine.cs:628-631`

**Problem**: `_macdHistory` list grows during `CalculateIndicators()`. Stepping backward adds old values, corrupting the signal line EMA.

**Impact**: MACD histogram values incorrect after backward navigation.

**Fix**: Reset MACD history when simulation resets, or use index-based calculation.

---

### Issue #6: No Commission/Slippage in Backtest (MEDIUM)

**Problem**: Backtest evaluates signals but doesn't account for:
- Trading costs (~$2.25/contract/side for GC)
- Market order slippage
- Bid-ask spread

**Impact**: Profitability significantly overstated, especially with high trade frequency.

**Fix**: Add configurable commission and slippage to simulation.

---

### Issue #7: Entry Bar Count Fragile (LOW)

**Location**: `GCStrategyEngine.cs:533-536, 439-442`

**Problem**: `_entryBarCount` stores bar count, but `_bars` list is trimmed at `_lookback` (100 bars). Long positions could have incorrect `barsHeld` calculation.

```csharp
_entryBarCount = _bars.Count;  // Stores count
// Later...
barsHeld = _bars.Count - _entryBarCount;  // Breaks if list trimmed
```

**Impact**: Time exit (60 bars) may trigger incorrectly for long positions.

**Fix**: Store absolute bar index or timestamp instead of list count.

---

## Risk Management Assessment

### Strengths
- Drawdown protection with linear position reduction (8-10% DD range)
- Emergency exit at 95% of max drawdown
- Trading hours restriction (8 AM - 5 PM)
- State persistence for crash recovery
- Separate Aggressive/Conservative modes

### Weaknesses
- No maximum position limit across strategies
- No correlation with account equity for dynamic sizing
- Fixed contracts mode bypasses all risk scaling

---

## Code Quality Summary

| Aspect | Rating | Notes |
|--------|--------|-------|
| Thread Safety | Good | Proper `_barsLock` usage throughout |
| State Persistence | Good | JSON with atomic writes via temp file |
| Event Architecture | Good | Clean event-driven callbacks |
| Indicator Calculations | Fair | RSI/MACD correct, SuperTrend flawed |
| Error Handling | Fair | Re-throws for circuit breaker pattern |
| Testability | Fair | Simulation mode exists but has bugs |

---

## To-Do List

### Priority 1 (Critical)
- [x] **Fix StepBackward bar corruption** - Add evaluation-only mode to SimulationEngine (FIXED 2026-01-07)

### Priority 2 (High)
- [x] **Fix divergence detection** - Require minimum 5-bar separation between lows (FIXED 2026-01-07)
- [x] **Fix SuperTrend implementation** - Track trend direction state properly (FIXED 2026-01-07)

### Priority 3 (Medium)
- [x] **Rename CalculateBarATR** - Change to CalculateBarRange for clarity (FIXED 2026-01-07)
- [x] **Fix MACD history on rewind** - Made MACD calculation stateless (FIXED 2026-01-07)
- [x] **Add commission/slippage** - Full trade tracking with costs (FIXED 2026-01-07)

### Priority 4 (Low)
- [x] **Fix entry bar count** - Use absolute index instead of list count (FIXED 2026-01-07)

---

## Recommendations

1. **Before trusting backtest results**: Fix issues #1, #2, and #3 as they directly affect signal generation
2. **For production readiness**: Add commission/slippage modeling
3. **For validation**: Implement walk-forward testing with out-of-sample periods
4. **For maintenance**: Add unit tests for indicator calculations
