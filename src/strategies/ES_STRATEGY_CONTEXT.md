# ES Divergence Strategy - Session Context

## Overview
High Sharpe ratio ES futures strategy development using Databento 1-minute data.

**Goal:** Create strategy with 2.0+ Sharpe ratio
**Result:** Achieved **2.327 Sharpe** (optimized), **1.868 baseline**

---

## Data Source
- **Location:** `C:\dev\databento\ES_1minute`
- **Format:** Zstd-compressed CSV files (*.ohlcv-1m.csv.zst)
- **Period:** 2020-11-23 to 2025-11-21 (~5 years)
- **Bars:** 1,769,794 1-minute bars → 29,540 hourly bars

---

## Strategy Files Created

### 1. Main Strategy: `es_divergence_sharpe.py`
**Key Pattern:** RSI/MACD Divergence Detection
- Bullish divergence: Price makes lower low, but RSI/MACD makes higher low
- Combined with 6 confirmation factors
- Hourly timeframe with daily regime filter

**Best Parameters (Optimized):**
```python
stop_mult = 1.5       # ATR multiplier for stop
target_mult = 2.5     # ATR multiplier for target
trail_start_pct = 1.5 # Start trailing at 1.5% profit
trail_atr_mult = 1.0  # Trail by 1.0 ATR
max_hold_bars = 60    # Max hold time
```

**Performance:**
| Metric | Baseline | Optimized |
|--------|----------|-----------|
| Sharpe | 1.868 | **2.327** |
| Return | 40.63% | - |
| CAGR | 7.07% | - |
| Trades | 107 | 100+ |
| Win Rate | 71.96% | - |
| Max DD | -2.00% | -1.74% |

### 2. Robustness Tests: `es_divergence_robustness.py`
Comprehensive testing suite with 5 tests:
1. Walk-forward analysis
2. Parameter sensitivity
3. Monte Carlo simulation
4. Market regime analysis
5. Year-by-year analysis

**Verdict: ROBUST (5/5 tests passed)**

### 3. First Attempt: `es_high_sharpe.py`
Confirmation-based strategy (achieved 1.043 Sharpe - not used)

### 4. Position Sized Strategies (NEW)

#### AGGRESSIVE: `es_divergence_aggressive.py`
**No drawdown limit - maximum returns**
- Position Scale: 99% of equity per trade
- Suitable for high risk tolerance traders

| Metric | Value |
|--------|-------|
| Total Return | **2605%** |
| CAGR | **93.55%** |
| Sharpe | 1.365 |
| Max DD | 18.12% |
| Calmar | 5.16 |
| Win Rate | 72.0% |

**$500,000 → $13,525,647** over 5 years (Max DD: $90,622)

#### CONSERVATIVE: `es_divergence_conservative.py`
**11% max drawdown constraint - risk-adjusted returns**
- Position Scale: 53% of equity per trade
- Drawdown protection with position reduction

| Metric | Value |
|--------|-------|
| Total Return | **481%** |
| CAGR | **42.25%** |
| Sharpe | 1.589 |
| Max DD | 10.24% |
| Calmar | 4.13 |
| Win Rate | 72.0% |

**$500,000 → $2,906,075** over 5 years (Max DD: $51,196)

---

## Robustness Test Results

### Walk-Forward (Out-of-Sample)
- **Avg Sharpe:** 1.828 ± 0.581
- **Consistency:** 100% positive
- **Range:** 0.886 to 2.393

### Parameter Sensitivity
- **Score:** 2.9% (EXCELLENT - < 15% threshold)
- Strategy stable with ±20% parameter changes

### Year-by-Year Performance
| Year | Sharpe | Return | Win Rate |
|------|--------|--------|----------|
| 2021 | 2.187 | 9.13% | 72.0% |
| 2022 | 1.077 | 5.25% | 55.0% |
| 2023 | 2.848 | 8.20% | 81.8% |
| 2024 | 2.319 | 5.42% | 73.9% |
| 2025 | 1.824 | 6.80% | 80.0% |

### Market Regimes
| Regime | Sharpe | Notes |
|--------|--------|-------|
| Bull | 1.881 | Good |
| Bear | 1.411 | Profitable |
| Sideways | 2.300 | Best |

---

## Key Technical Components

### Divergence Detection Logic
```python
# BULLISH DIVERGENCE:
# Price makes LOWER low, but RSI or MACD makes HIGHER low
price_lower = price_low < prev_price * 0.998  # 0.2% lower
rsi_higher = rsi_low > prev_rsi * 1.02        # RSI 2% higher
macd_higher = macd_low > prev_macd            # MACD higher

if price_lower and (rsi_higher or macd_higher):
    divergence_signal = 1
```

### Confirmation Factors (need 4+)
1. Daily bull regime (Close > 50 SMA)
2. EMA bullish (13 > 34)
3. SuperTrend bullish
4. RSI in range (30-70)
5. MACD improving
6. Volatility OK (ATR > 0.8 * ATR_SMA)

### Entry Conditions
- Divergence signal detected
- 4+ confirmations met
- RTH hours (9 AM - 4 PM)

---

## Files Reference
- **Base Strategy:** `src/strategies/es_divergence_sharpe.py` (2.327 Sharpe)
- **Aggressive (99%):** `src/strategies/es_divergence_aggressive.py` (93.55% CAGR, 18% DD)
- **Conservative (53%):** `src/strategies/es_divergence_conservative.py` (42.25% CAGR, 10% DD)
- **Position Sizing:** `src/strategies/es_divergence_sized.py` (optimization tool)
- **Robustness:** `src/strategies/es_divergence_robustness.py`
- **Data:** `C:\dev\databento\ES_1minute\*.ohlcv-1m.csv.zst`

---

## Position Sizing Summary

| Strategy | Scale | CAGR | Max DD | Sharpe | $500k → |
|----------|-------|------|--------|--------|---------|
| Aggressive | 99% | 93.55% | 18.12% | 1.37 | $13.5M |
| Conservative | 53% | 42.25% | 10.24% | 1.59 | $2.9M |

**Key Insight:** Position scale directly controls risk/reward tradeoff.
- Lower scale = Lower DD, Lower returns, Higher Sharpe
- Higher scale = Higher DD, Higher returns, Lower Sharpe

---

## Next Steps (Future Sessions)
1. Consider adding short signals (bearish divergence)
2. Test on other instruments (NQ, CL, GC)
3. Implement live trading connector
4. Add volatility-adjusted position sizing
5. Walk-forward validate position sizing parameters

---

## Commands to Run

```bash
# Run aggressive strategy (high risk)
cd /c/dev/moondev-ai-agents/src/strategies
python es_divergence_aggressive.py

# Run conservative strategy (11% max DD)
python es_divergence_conservative.py

# Run base strategy with optimization
python es_divergence_sharpe.py

# Run robustness tests
python es_divergence_robustness.py
```

---

*Last Updated: 2025-11-23*
*Session Result: SUCCESS*
- Base: 2.327 Sharpe, ROBUST (5/5 tests passed)
- Aggressive: 93.55% CAGR, $500k → $13.5M
- Conservative: 42.25% CAGR, $500k → $2.9M (within 11% DD)
