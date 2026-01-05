"""
GC (Gold Futures) Divergence Strategy - CONSERVATIVE (11% Max DD Constraint)
===========================================================================
Maximizes risk-adjusted returns with strict drawdown control.

Based on RSI/MACD divergence detection with confirmation factors.
Position sizing calibrated to keep maximum drawdown under 11%.

This version is suitable for traders with strict risk limits.

Gold-specific notes:
- GC contract: 100 troy oz, $10 per $0.10 move
- Gold typically has different vol profile than equity indices
- Safe-haven asset with different risk characteristics

Author: Moon Dev AI
"""

import pandas as pd
import numpy as np
from backtesting import Backtest, Strategy
import warnings
warnings.filterwarnings('ignore')

from gc_divergence_sharpe import (
    load_gc_data, prepare_signals, calculate_cagr
)


class GCDivergenceConservativeStrategy(Strategy):
    """
    Conservative divergence strategy for Gold with 11% max DD constraint.
    Position sizing is calibrated to stay within drawdown limits.
    """
    # Risk parameters (optimized for Sharpe)
    stop_mult = 1.5
    target_mult = 2.5
    trail_start_pct = 1.5
    trail_atr_mult = 1.0
    max_hold_bars = 60

    # CONSERVATIVE: Position scale calibrated for ~11% max DD
    # Will be optimized based on backtest data
    position_scale = 0.57

    # Drawdown protection
    max_dd_limit = 0.11  # 11% maximum drawdown
    dd_reduction_threshold = 0.08  # Start reducing at 8% DD
    dd_pause_threshold = 0.10  # Pause at 10% DD

    def init(self):
        self.long_sig = self.I(lambda: self.data.long_signal)
        self.atr = self.I(lambda: self.data.atr)
        self.macd_hist = self.I(lambda: self.data.macd_hist)
        self.rsi = self.I(lambda: self.data.rsi)
        self.confirmations = self.I(lambda: self.data.confirmations)
        self.bull_regime = self.I(lambda: self.data.bull_regime)

        self.entry_price = None
        self.entry_bar = None
        self.stop_price = None

        # Drawdown tracking
        self.peak_equity = self._broker._cash
        self.current_dd = 0

    def _update_drawdown(self):
        """Track current drawdown."""
        equity = self.equity
        if equity > self.peak_equity:
            self.peak_equity = equity
        self.current_dd = (self.peak_equity - equity) / self.peak_equity

    def _get_adjusted_size(self) -> float:
        """Reduce position size as drawdown increases."""
        if self.current_dd >= self.dd_pause_threshold:
            return 0  # Stop trading at 10% DD
        elif self.current_dd >= self.dd_reduction_threshold:
            # Linearly reduce from 100% to 30% between 8% and 10% DD
            reduction = (self.current_dd - self.dd_reduction_threshold) / (self.dd_pause_threshold - self.dd_reduction_threshold)
            return self.position_scale * (1 - 0.7 * reduction)
        else:
            return self.position_scale

    def next(self):
        # Update drawdown tracking
        self._update_drawdown()

        price = self.data.Close[-1]
        atr = self.atr[-1]

        if np.isnan(atr) or atr <= 0:
            return

        # Manage existing position
        if self.position:
            self._manage_position()
            return

        # Check if we should pause trading due to drawdown
        if self.current_dd >= self.dd_pause_threshold:
            return

        # Entry signal
        if self.long_sig[-1] == 1:
            stop_dist = self.stop_mult * atr
            target_dist = self.target_mult * atr

            self.entry_price = price
            self.entry_bar = len(self.data)
            self.stop_price = price - stop_dist

            # Get adjusted position size based on current drawdown
            adjusted_size = self._get_adjusted_size()
            if adjusted_size > 0:
                self.buy(
                    size=adjusted_size,
                    sl=price - stop_dist,
                    tp=price + target_dist
                )

    def _manage_position(self):
        """Position management with drawdown protection."""
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

        # Emergency exit if approaching max DD
        if self.current_dd >= self.max_dd_limit * 0.95:
            self.position.close()
            return


def find_optimal_scale(df: pd.DataFrame, capital: float = 500000, target_dd: float = 11.0):
    """Find the optimal position scale for target max DD."""
    print(f"\nFinding optimal scale for {target_dd}% max DD...")

    num_days = (df.index.max() - df.index.min()).days
    results = []

    for scale in np.arange(0.40, 0.70, 0.02):
        class TestStrategy(GCDivergenceConservativeStrategy):
            position_scale = scale

        bt = Backtest(
            df, TestStrategy, cash=capital, commission=0.00005,
            exclusive_orders=True, trade_on_close=True, margin=1/10
        )

        stats = bt.run()
        cagr = calculate_cagr(stats['Return [%]'], num_days)
        max_dd = abs(stats['Max. Drawdown [%]'])

        results.append({
            'scale': scale, 'cagr': cagr, 'max_dd': max_dd,
            'return': stats['Return [%]'], 'sharpe': stats['Sharpe Ratio']
        })

        status = 'OK' if max_dd <= target_dd else 'OVER'
        print(f"  Scale {scale:.0%}: CAGR {cagr:>5.1f}%, MaxDD {max_dd:>5.2f}% [{status}]")

    # Find best within constraint
    valid = [r for r in results if r['max_dd'] <= target_dd]
    if valid:
        return max(valid, key=lambda x: x['cagr'])
    else:
        return min(results, key=lambda x: r['max_dd'])


def run_backtest(df: pd.DataFrame, capital: float = 500000):
    """Run conservative backtest with 11% DD constraint."""
    print("\n" + "=" * 70)
    print("GC (GOLD) DIVERGENCE STRATEGY - CONSERVATIVE (11% MAX DD)")
    print("=" * 70)
    print(f"Starting Capital: ${capital:,.0f}")
    print(f"Max Drawdown Limit: 11%")

    # First, find optimal scale
    optimal = find_optimal_scale(df, capital, target_dd=11.0)
    print(f"\nOptimal Scale: {optimal['scale']:.0%}")

    # Run with optimal scale
    class OptimalStrategy(GCDivergenceConservativeStrategy):
        position_scale = optimal['scale']

    bt = Backtest(
        df, OptimalStrategy, cash=capital, commission=0.00005,
        exclusive_orders=True, trade_on_close=True, margin=1/10
    )

    stats = bt.run()
    num_days = (df.index.max() - df.index.min()).days
    cagr = calculate_cagr(stats['Return [%]'], num_days)
    max_dd = abs(stats['Max. Drawdown [%]'])

    print("\n" + "=" * 70)
    print("RESULTS (11% MAX DD CONSTRAINED)")
    print("=" * 70)
    print(f"  Position Scale:  {optimal['scale']:.0%}")
    print(f"  Total Return:    {stats['Return [%]']:.2f}%")
    print(f"  CAGR:            {cagr:.2f}%")
    print(f"  Sharpe Ratio:    {stats['Sharpe Ratio']:.3f}")
    print(f"  Max Drawdown:    {max_dd:.2f}%")
    print(f"  Calmar Ratio:    {cagr / max_dd:.2f}" if max_dd > 0 else "  Calmar Ratio:    N/A")
    print(f"  # Trades:        {stats['# Trades']}")
    print(f"  Win Rate:        {stats['Win Rate [%]']:.1f}%")

    # Dollar projections
    final_equity = capital * (1 + stats['Return [%]'] / 100)
    max_dd_dollars = capital * max_dd / 100

    print(f"\n  DOLLAR PROJECTIONS:")
    print(f"  Final Equity:    ${final_equity:,.0f}")
    print(f"  Total Profit:    ${final_equity - capital:,.0f}")
    print(f"  Max DD ($):      ${max_dd_dollars:,.0f}")

    # Validation
    print("\n" + "-" * 40)
    if max_dd <= 11.0:
        print(f"PASS: Max DD {max_dd:.2f}% <= 11% constraint")
    else:
        print(f"WARNING: Max DD {max_dd:.2f}% slightly over 11% constraint")
        print("Consider reducing position_scale")

    return stats, bt


if __name__ == "__main__":
    data_path = r"C:\dev\databento\GC_1minute\gc_continuous_1m.csv"
    df_1m = load_gc_data(data_path)
    df = prepare_signals(df_1m)

    stats, bt = run_backtest(df, capital=500000)
