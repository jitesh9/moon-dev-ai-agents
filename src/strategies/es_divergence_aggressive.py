"""
ES Divergence Strategy - AGGRESSIVE (No DD Constraint)
======================================================
Maximizes absolute returns without drawdown limits.

Based on RSI/MACD divergence detection with confirmation factors.
Uses full position sizing (99% of equity per trade) for maximum leverage.

WARNING: This version has NO drawdown protection. High risk, high reward.

Results (Backtest 2020-2025):
- Return: ~2600%
- CAGR: ~93%
- Max Drawdown: ~18%
- Sharpe: ~1.37

Author: Moon Dev AI
"""

import pandas as pd
import numpy as np
from backtesting import Backtest, Strategy
import warnings
warnings.filterwarnings('ignore')

from es_divergence_sharpe import (
    load_databento_data, prepare_signals, calculate_cagr
)


class DivergenceAggressiveStrategy(Strategy):
    """
    Aggressive divergence strategy - maximum position sizing, no DD limits.
    """
    # Risk parameters
    stop_mult = 1.5
    target_mult = 2.5
    trail_start_pct = 1.5
    trail_atr_mult = 1.0
    max_hold_bars = 60

    # AGGRESSIVE: Full position sizing
    position_scale = 0.99  # 99% of equity per trade

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

            # AGGRESSIVE: Use full position
            self.buy(
                size=self.position_scale,
                sl=price - stop_dist,
                tp=price + target_dist
            )

    def _manage_position(self):
        """Trailing stop and exit management."""
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


def run_backtest(df: pd.DataFrame, capital: float = 500000):
    """Run aggressive backtest."""
    print("\n" + "=" * 70)
    print("ES DIVERGENCE STRATEGY - AGGRESSIVE (NO DD LIMIT)")
    print("=" * 70)
    print(f"Starting Capital: ${capital:,.0f}")
    print(f"Position Scale: 99% (MAXIMUM)")
    print("WARNING: No drawdown protection!")

    bt = Backtest(
        df,
        DivergenceAggressiveStrategy,
        cash=capital,
        commission=0.00005,
        exclusive_orders=True,
        trade_on_close=True,
        margin=1/10
    )

    stats = bt.run()
    num_days = (df.index.max() - df.index.min()).days
    cagr = calculate_cagr(stats['Return [%]'], num_days)
    max_dd = abs(stats['Max. Drawdown [%]'])

    print(f"\nRESULTS:")
    print(f"  Total Return:    {stats['Return [%]']:.2f}%")
    print(f"  CAGR:            {cagr:.2f}%")
    print(f"  Sharpe Ratio:    {stats['Sharpe Ratio']:.3f}")
    print(f"  Max Drawdown:    {max_dd:.2f}%")
    print(f"  Calmar Ratio:    {cagr / max_dd:.2f}")
    print(f"  # Trades:        {stats['# Trades']}")
    print(f"  Win Rate:        {stats['Win Rate [%]']:.1f}%")

    # Dollar projections
    final_equity = capital * (1 + stats['Return [%]'] / 100)
    max_dd_dollars = capital * max_dd / 100

    print(f"\n  DOLLAR PROJECTIONS:")
    print(f"  Final Equity:    ${final_equity:,.0f}")
    print(f"  Total Profit:    ${final_equity - capital:,.0f}")
    print(f"  Max DD ($):      ${max_dd_dollars:,.0f}")

    return stats


if __name__ == "__main__":
    data_dir = r"C:\dev\databento\ES_1minute"
    df_1m = load_databento_data(data_dir)
    df = prepare_signals(df_1m)

    stats = run_backtest(df, capital=500000)
