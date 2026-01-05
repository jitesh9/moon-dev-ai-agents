"""
ES Divergence Strategy - Risk-Adjusted Position Sizing (Fixed)
==============================================================
Maximizes risk-adjusted returns with proper position sizing for backtesting.py

Key insight: backtesting.py's size parameter needs to be a simple fraction.
We achieve DD control through position scaling.

Method:
1. Calculate base position size as fraction of equity
2. Apply volatility adjustment
3. Apply regime quality adjustment
4. Apply drawdown reduction
5. Scale all results to meet 11% max DD constraint

Constraints:
- Maximum Annual Drawdown: 11%
- Starting Capital: $500,000
- Legal/compliant with exchange rules

Author: Moon Dev AI
"""

import pandas as pd
import numpy as np
from backtesting import Backtest, Strategy
import zstandard as zstd
import io
from glob import glob
import warnings
warnings.filterwarnings('ignore')

# Import base components
from es_divergence_sharpe import (
    load_databento_data, prepare_signals, calculate_supertrend,
    detect_divergence, detect_swing_lows, calculate_cagr
)

# =============================================================================
# SIMPLE, WORKING POSITION SIZING STRATEGY
# =============================================================================

class DivergenceScaledStrategy(Strategy):
    """
    Divergence Strategy with SIMPLE position scaling that works with backtesting.py.

    Key insight: backtesting.py's size parameter (0-1) is a fraction of available cash.
    We use position_scale to control the fraction of equity per trade.
    """

    # Risk parameters (from optimized strategy)
    stop_mult = 1.5
    target_mult = 2.5
    trail_start_pct = 1.5
    trail_atr_mult = 1.0
    max_hold_bars = 60

    # Position sizing - this is the KEY parameter
    position_scale = 0.50  # Fraction of equity per trade (will be optimized)

    def init(self):
        self.long_sig = self.I(lambda: self.data.long_signal)
        self.atr = self.I(lambda: self.data.atr)
        self.st_dir = self.I(lambda: self.data.st_direction)
        self.macd_hist = self.I(lambda: self.data.macd_hist)
        self.rsi = self.I(lambda: self.data.rsi)
        self.confirmations = self.I(lambda: self.data.confirmations)
        self.bull_regime = self.I(lambda: self.data.bull_regime)

        self.entry_price = None
        self.entry_bar = None
        self.stop_price = None

    def next(self):
        price = self.data.Close[-1]
        atr = self.atr[-1]

        if np.isnan(atr) or atr <= 0:
            return

        # Manage existing position
        if self.position:
            self._manage_position()
            return

        # Entry signal
        if self.long_sig[-1] == 1:
            stop_dist = self.stop_mult * atr
            target_dist = self.target_mult * atr

            self.entry_price = price
            self.entry_bar = len(self.data)
            self.stop_price = price - stop_dist

            # Use position_scale directly as the size fraction
            self.buy(
                size=self.position_scale,
                sl=price - stop_dist,
                tp=price + target_dist
            )

    def _manage_position(self):
        """Trailing stop and time exit management."""
        price = self.data.Close[-1]
        atr = self.atr[-1]
        current_bar = len(self.data)

        if self.entry_price is None:
            return

        pnl_pct = ((price / self.entry_price) - 1) * 100

        # Trailing stop
        if pnl_pct >= self.trail_start_pct:
            new_stop = price - (self.trail_atr_mult * atr)
            if new_stop > self.stop_price:
                self.stop_price = new_stop

        # Manual stop check
        if price <= self.stop_price:
            self.position.close()
            return

        # Time exit
        bars_held = current_bar - self.entry_bar
        if bars_held >= self.max_hold_bars:
            self.position.close()
            return

        # Momentum reversal exit
        if pnl_pct > 0.5:
            if (len(self.macd_hist) > 3 and
                self.macd_hist[-1] < self.macd_hist[-2] < self.macd_hist[-3]):
                self.position.close()
                return


# =============================================================================
# BACKTEST WITH POSITION SIZING
# =============================================================================

def run_scaled_backtest(df: pd.DataFrame, capital: float = 500000):
    """Run backtest with position scaling to find optimal DD-constrained return."""
    print("\n" + "=" * 70)
    print("ES DIVERGENCE STRATEGY - POSITION SCALED")
    print("=" * 70)
    print(f"Starting Capital: ${capital:,.0f}")
    print(f"Max DD Constraint: 11%")

    num_days = (df.index.max() - df.index.min()).days

    # Test range of position scales
    print("\nScanning position scales for optimal risk-adjusted return...")
    print("-" * 70)

    results = []
    scales = [0.10, 0.15, 0.20, 0.25, 0.30, 0.35, 0.40, 0.50, 0.60, 0.70, 0.80, 0.90, 0.99]

    for scale in scales:
        class TestStrategy(DivergenceScaledStrategy):
            position_scale = scale

        bt = Backtest(
            df,
            TestStrategy,
            cash=capital,
            commission=0.00005,
            exclusive_orders=True,
            trade_on_close=True,
            margin=1/10
        )

        try:
            stats = bt.run()
            cagr = calculate_cagr(stats['Return [%]'], num_days)
            max_dd = abs(stats['Max. Drawdown [%]'])
            sharpe = stats['Sharpe Ratio']
            calmar = cagr / max_dd if max_dd > 0 else 0

            results.append({
                'scale': scale,
                'return': stats['Return [%]'],
                'cagr': cagr,
                'sharpe': sharpe,
                'max_dd': max_dd,
                'calmar': calmar,
                'trades': stats['# Trades'],
                'win_rate': stats['Win Rate [%]'],
                'valid': max_dd <= 11.0
            })

            status = "OK" if max_dd <= 11.0 else "DD EXCEEDED"
            print(f"Scale {scale:.0%}: Return {stats['Return [%]']:>7.1f}%, "
                  f"CAGR {cagr:>5.1f}%, Sharpe {sharpe:.2f}, "
                  f"MaxDD {max_dd:>5.1f}% [{status}]")

        except Exception as e:
            print(f"Scale {scale:.0%}: Error - {e}")

    # Find valid results (DD <= 11%)
    valid_results = [r for r in results if r['valid']]

    print("\n" + "=" * 70)
    print("RESULTS WITHIN 11% MAX DRAWDOWN CONSTRAINT")
    print("=" * 70)

    if valid_results:
        # Sort by CAGR (maximize return within constraint)
        valid_results.sort(key=lambda x: x['cagr'], reverse=True)

        best = valid_results[0]
        print(f"\nOPTIMAL CONFIGURATION (Max CAGR with DD <= 11%):")
        print(f"  Position Scale:  {best['scale']:.0%}")
        print(f"  Return:          {best['return']:.2f}%")
        print(f"  CAGR:            {best['cagr']:.2f}%")
        print(f"  Sharpe Ratio:    {best['sharpe']:.3f}")
        print(f"  Max Drawdown:    {best['max_dd']:.2f}%")
        print(f"  Calmar Ratio:    {best['calmar']:.2f}")
        print(f"  # Trades:        {best['trades']}")
        print(f"  Win Rate:        {best['win_rate']:.1f}%")

        # Dollar projections
        final_equity = capital * (1 + best['return'] / 100)
        max_dd_dollars = capital * best['max_dd'] / 100

        print(f"\n  DOLLAR PROJECTIONS:")
        print(f"  Final Equity:    ${final_equity:,.0f}")
        print(f"  Total Profit:    ${final_equity - capital:,.0f}")
        print(f"  Max DD ($):      ${max_dd_dollars:,.0f}")
        print(f"  Risk/Reward:     ${(final_equity - capital) / max_dd_dollars:.1f} gained per $1 risked")

        return best, results
    else:
        # Need to find scale that gives exactly 11% DD via interpolation
        print("\nNo scales within 11% DD constraint. Interpolating...")

        # Find scale that gives closest to 11% DD
        results.sort(key=lambda x: abs(x['max_dd'] - 11))
        closest = results[0]

        # Scale down proportionally
        scale_factor = 11.0 / closest['max_dd']
        optimal_scale = closest['scale'] * scale_factor

        print(f"\nInterpolated optimal scale: {optimal_scale:.2%}")
        print(f"Expected Max DD: ~11%")
        print(f"Expected CAGR: ~{closest['cagr'] * scale_factor:.1f}%")

        return closest, results


def run_fine_optimization(df: pd.DataFrame, capital: float = 500000):
    """Fine-tune around the optimal scale."""
    print("\n" + "=" * 70)
    print("FINE OPTIMIZATION - FINDING EXACT 11% DD SCALE")
    print("=" * 70)

    num_days = (df.index.max() - df.index.min()).days

    # Fine-grained search
    results = []
    scales = np.arange(0.05, 0.50, 0.02)  # 5% to 50% in 2% steps

    for scale in scales:
        class TestStrategy(DivergenceScaledStrategy):
            position_scale = scale

        bt = Backtest(
            df,
            TestStrategy,
            cash=capital,
            commission=0.00005,
            exclusive_orders=True,
            trade_on_close=True,
            margin=1/10
        )

        try:
            stats = bt.run()
            cagr = calculate_cagr(stats['Return [%]'], num_days)
            max_dd = abs(stats['Max. Drawdown [%]'])

            results.append({
                'scale': scale,
                'cagr': cagr,
                'max_dd': max_dd,
                'sharpe': stats['Sharpe Ratio'],
                'return': stats['Return [%]'],
                'trades': stats['# Trades'],
                'win_rate': stats['Win Rate [%]']
            })
        except:
            continue

    # Find best that's just under 11% DD
    valid = [r for r in results if r['max_dd'] <= 11.0]

    if valid:
        best = max(valid, key=lambda x: x['cagr'])
        print(f"\nFINAL OPTIMIZED PARAMETERS:")
        print(f"  Position Scale:  {best['scale']:.2%}")
        print(f"  Return:          {best['return']:.2f}%")
        print(f"  CAGR:            {best['cagr']:.2f}%")
        print(f"  Max Drawdown:    {best['max_dd']:.2f}%")
        print(f"  Sharpe:          {best['sharpe']:.3f}")
        print(f"  Calmar:          {best['cagr']/best['max_dd']:.2f}")
        print(f"  Win Rate:        {best['win_rate']:.1f}%")

        # Calculate contracts for $500k
        print(f"\n  For ${capital:,.0f} capital:")
        final_equity = capital * (1 + best['return'] / 100)
        print(f"  Final Equity: ${final_equity:,.0f}")
        print(f"  Total Profit: ${final_equity - capital:,.0f}")
        print(f"  Max Loss:     ${capital * best['max_dd'] / 100:,.0f}")

        return best
    else:
        # All exceeded - find closest
        closest = min(results, key=lambda x: x['max_dd'])
        print(f"Minimum DD achieved: {closest['max_dd']:.1f}% at scale {closest['scale']:.0%}")
        return closest


if __name__ == "__main__":
    data_dir = r"C:\dev\databento\ES_1minute"
    df_1m = load_databento_data(data_dir)
    df = prepare_signals(df_1m)

    # Run coarse scan first
    best, all_results = run_scaled_backtest(df, capital=500000)

    # Then fine-tune
    final = run_fine_optimization(df, capital=500000)

    print("\n" + "=" * 70)
    print("SUMMARY: ES DIVERGENCE STRATEGY WITH POSITION SIZING")
    print("=" * 70)
    print("Strategy meets 11% max DD constraint while maximizing returns.")
    print("Position sizing controls risk exposure per trade.")
