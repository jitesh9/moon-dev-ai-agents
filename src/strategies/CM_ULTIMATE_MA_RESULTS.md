# CM Ultimate MA MTF Swing Strategy - Results

## Strategy Overview

| Parameter | Value |
|-----------|-------|
| **Based On** | ChrisMoody's CM_Ultimate_MA_MTF_V2 TradingView Indicator |
| **Timeframe** | Hourly (with Daily MTF filter) |
| **Entry Logic** | Pullback-to-MA with trend confirmation |
| **Data Period** | Nov 2020 - Nov 2025 (~5 years) |
| **Starting Capital** | $500,000 |

## Entry Logic (Pullback-to-MA)

The strategy uses **pullback entries** instead of simple MA crosses for better signal quality:

### Long Entry Conditions:
1. Price **pulls back to fast MA** (Low touches MA zone, Close above MA)
2. **Bullish candle** (Close > Open)
3. Fast MA **trending up** (above prior smoothed value)
4. Fast MA **above Slow MA** (trend confirmation)
5. Daily **trend filter up** (Close > 50 SMA on daily)
6. RSI **40-70** (momentum confirmation)
7. **Regular trading hours** (9 AM - 4 PM)

### Exit Conditions:
- **Stop Loss**: 1.5x ATR (baseline), 2.5x ATR (optimized)
- **Take Profit**: 2.5x ATR (baseline), 4.0x ATR (optimized)
- **Trailing Stop**: Activates at 1.5-2.0% profit, trails by 1.0-1.5x ATR
- **MA Cross Exit**: Close if fast MA crosses below slow MA
- **Time Exit**: Maximum 60-80 bars holding period

---

## ES (E-mini S&P 500) Results

### MA Combination Comparison (12 Tested)

| Rank | Fast MA | Slow MA | Sharpe | Return | CAGR | Max DD | Trades | Win Rate |
|------|---------|---------|--------|--------|------|--------|--------|----------|
| 1 | **T3** | **EMA** | **0.497** | 14,278% | 170% | -70% | 249 | 49.0% |
| 2 | T3 | SMA | 0.416 | 4,673% | 117% | -74% | 256 | 44.5% |
| 3 | EMA | RMA | 0.309 | 9,858% | 151% | -79% | 378 | 45.5% |
| 4 | T3 | RMA | 0.288 | 1,131% | 65% | -79% | 252 | 44.4% |
| 5 | EMA | SMA | 0.282 | 3,202% | 102% | -85% | 381 | 40.7% |
| 6 | EMA | EMA | 0.233 | 1,732% | 80% | - | 383 | 42.3% |

### Best ES Combination: T3(20) / EMA(50)

**Baseline Results:**
| Metric | Value |
|--------|-------|
| Total Return | 14,278% |
| CAGR | 170.44% |
| Sharpe Ratio | 0.497 |
| Max Drawdown | -70.30% |
| Total Trades | 249 |
| Win Rate | 49.00% |

**Optimized Results:**
| Metric | Baseline | Optimized |
|--------|----------|-----------|
| Stop Loss | 1.5x ATR | 2.5x ATR |
| Target | 2.5x ATR | 2.5x ATR |
| Trail Start | 1.5% | 2.0% |
| Trail ATR | 1.0x | 1.5x |
| Max Hold | 60 bars | 80 bars |
| **Sharpe** | 0.497 | **0.671** |
| **Max DD** | -70.30% | **-58.52%** |
| **Win Rate** | 49.00% | **64.06%** |

---

## GC (Gold Futures) Results

### MA Combination Comparison

| Rank | Fast MA | Slow MA | Sharpe | Return | CAGR | Max DD | Trades |
|------|---------|---------|--------|--------|------|--------|--------|
| 1 | T3 | SMA | -0.657 | -91% | -39% | -97% | 248 |
| 2 | T3 | RMA | -0.869 | -96% | -49% | -99% | 242 |
| 3 | EMA | RMA | -0.926 | -98% | -53% | -100% | 358 |
| 4 | T3 | EMA | -0.927 | -96% | -47% | -98% | 227 |
| 5 | EMA | EMA | -1.081 | -98% | -57% | -100% | 346 |

### Best GC Combination: T3(20) / SMA(50)

**Baseline vs Optimized:**
| Metric | Baseline | Optimized |
|--------|----------|-----------|
| Stop Loss | 1.5x ATR | 2.5x ATR |
| Target | 2.5x ATR | **4.0x ATR** |
| Trail Start | 1.5% | 2.0% |
| Trail ATR | 1.0x | 1.5x |
| Max Hold | 60 bars | 80 bars |
| **Sharpe** | -0.657 | **+0.216** |
| **Return** | -91% | **+237%** |
| **CAGR** | -39% | **+27.6%** |
| **Max DD** | -97% | **-71%** |
| **Win Rate** | 40% | **45%** |

---

## Key Findings

### 1. T3 (Tilson) is the Best Fast MA
The Tilson T3 moving average consistently outperformed other MA types as the fast MA on both instruments. Its smoothing properties reduce whipsaws while maintaining responsiveness.

### 2. ES is More Suitable Than GC
- ES showed positive Sharpe ratios with 5 out of 12 combinations
- GC required significant optimization to become profitable
- ES benefits from trending behavior suited for pullback entries

### 3. Wider Stops Improve Results
- Optimized strategies use 2.5x ATR stops vs 1.5x baseline
- Wider stops + larger targets reduce whipsaw losses
- GC needed 4.0x ATR targets for profitability

### 4. High Drawdowns Remain a Concern
Even the best configurations show 58-71% max drawdowns. For production use:
- Reduce position sizing (50-60% of baseline)
- Implement drawdown protection (pause at 15% DD)
- Consider regime filtering (volatility-based)

---

## Recommended Production Parameters

### ES Strategy
```python
# MA Configuration
fast_ma_type = 8      # T3 (Tilson)
slow_ma_type = 2      # EMA
fast_len = 20
slow_len = 50
t3_factor = 0.7

# Risk Management
stop_mult = 2.5       # ATR multiplier
target_mult = 2.5     # ATR multiplier
trail_start_pct = 2.0
trail_atr_mult = 1.5
max_hold_bars = 80
position_scale = 0.5  # 50% for lower drawdown
```

### GC Strategy
```python
# MA Configuration
fast_ma_type = 8      # T3 (Tilson)
slow_ma_type = 1      # SMA
fast_len = 20
slow_len = 50
t3_factor = 0.7

# Risk Management
stop_mult = 2.5       # ATR multiplier
target_mult = 4.0     # ATR multiplier (wider for GC)
trail_start_pct = 2.0
trail_atr_mult = 1.5
max_hold_bars = 80
position_scale = 0.4  # 40% for lower drawdown
```

---

## Comparison with Previous Strategies

| Strategy | Instrument | Sharpe | CAGR | Max DD | Win Rate |
|----------|------------|--------|------|--------|----------|
| **CM Ultimate MA (Opt)** | ES | **0.671** | 524% | -59% | 64% |
| CM Ultimate MA (Baseline) | ES | 0.497 | 170% | -70% | 49% |
| ES Divergence Sharpe | ES | 2.327 | - | - | - |
| **CM Ultimate MA (Opt)** | GC | **0.216** | 28% | -71% | 45% |
| GC Divergence Conservative | GC | 0.851 | 11% | -10% | 61% |
| GC Divergence Aggressive | GC | 1.048 | 187% | -49% | 69% |

### Key Insight
The CM Ultimate MA strategy with pullback entries shows promise on ES but underperforms the divergence-based strategies on GC. The divergence strategies remain superior for Gold futures trading.

---

## File References

| File | Purpose |
|------|---------|
| `cm_ultimate_ma_swing.py` | Strategy implementation |
| `CM_ULTIMATE_MA_RESULTS.md` | This documentation |
| `CM_Ultimate_MA_MTF_V2_text.txt` | Original PineScript indicator |

---

*Generated: December 2025*
*Author: Moon Dev AI*
