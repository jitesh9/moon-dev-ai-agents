"""
ES Intraday Swing Strategy - Parameter Optimization
====================================================
Optimizing the best configuration: Pullback Entry + RTH Filter

Parameters to optimize:
- stop_mult: Stop loss multiplier (ATR)
- target_mult: Take profit multiplier (R multiple)
- SuperTrend period and multiplier
- EMA periods

Author: Moon Dev AI
"""

import pandas as pd
import numpy as np
from backtesting import Backtest, Strategy
import warnings
warnings.filterwarnings('ignore')


def load_data(filepath: str) -> pd.DataFrame:
    df = pd.read_csv(filepath, parse_dates=['datetime'])
    df.set_index('datetime', inplace=True)
    df.columns = [c.capitalize() for c in df.columns]
    return df


def calculate_supertrend(high, low, close, period=10, multiplier=3.0):
    tr1 = high - low
    tr2 = abs(high - close.shift(1))
    tr3 = abs(low - close.shift(1))
    tr = pd.concat([tr1, tr2, tr3], axis=1).max(axis=1)
    atr = tr.rolling(window=period).mean()

    hl2 = (high + low) / 2
    upper_band = hl2 + (multiplier * atr)
    lower_band = hl2 - (multiplier * atr)

    supertrend = pd.Series(index=close.index, dtype=float)
    direction = pd.Series(index=close.index, dtype=float)

    supertrend.iloc[period] = upper_band.iloc[period]
    direction.iloc[period] = -1

    for i in range(period + 1, len(close)):
        curr_upper = upper_band.iloc[i]
        curr_lower = lower_band.iloc[i]
        prev_st = supertrend.iloc[i-1]
        curr_close = close.iloc[i]

        if prev_st == upper_band.iloc[i-1]:
            if curr_close > curr_upper:
                supertrend.iloc[i] = curr_lower
                direction.iloc[i] = 1
            else:
                supertrend.iloc[i] = min(curr_upper, prev_st)
                direction.iloc[i] = -1
        else:
            if curr_close < curr_lower:
                supertrend.iloc[i] = curr_upper
                direction.iloc[i] = -1
            else:
                supertrend.iloc[i] = max(curr_lower, prev_st)
                direction.iloc[i] = 1

    return supertrend, direction


def prepare_data(hourly_path: str, daily_path: str) -> pd.DataFrame:
    """Prepare data with RTH filter (best performer)."""
    df_1h = load_data(hourly_path)
    df_daily = load_data(daily_path)

    # Daily 50 SMA regime filter
    df_daily['sma_50'] = df_daily['Close'].rolling(window=50).mean()
    df_daily['bull_regime'] = (df_daily['Close'] > df_daily['sma_50']).astype(int)

    df_daily['date'] = df_daily.index.date
    daily_regime = df_daily[['date', 'sma_50', 'bull_regime']].copy()
    daily_regime.columns = ['date', 'daily_sma_50', 'daily_bull_regime']

    df_1h['date'] = df_1h.index.date
    df = df_1h.merge(daily_regime, on='date', how='left')
    df.set_index(df_1h.index, inplace=True)

    df['daily_sma_50'] = df['daily_sma_50'].ffill()
    df['daily_bull_regime'] = df['daily_bull_regime'].ffill().fillna(0)

    # EMAs
    df['ema_34'] = df['Close'].ewm(span=34, adjust=False).mean()
    df['ema_55'] = df['Close'].ewm(span=55, adjust=False).mean()
    df['ema_bull'] = (df['ema_34'] > df['ema_55']).astype(int)

    # SuperTrend
    st, st_dir = calculate_supertrend(df['High'], df['Low'], df['Close'], period=10, multiplier=3.0)
    df['supertrend'] = st
    df['st_direction'] = st_dir

    # ATR
    tr = pd.concat([
        df['High'] - df['Low'],
        abs(df['High'] - df['Close'].shift(1)),
        abs(df['Low'] - df['Close'].shift(1))
    ], axis=1).max(axis=1)
    df['atr_14'] = tr.rolling(window=14).mean()

    # RTH Filter (9 AM - 4 PM)
    df['hour'] = df.index.hour
    df['rth_ok'] = ((df['hour'] >= 9) & (df['hour'] < 16)).astype(int)

    # Pullback entry
    ema_tolerance = 0.003
    df['pullback_long'] = (
        (df['Low'] <= df['ema_34'] * (1 + ema_tolerance)) &
        (df['Close'] > df['ema_34']) &
        (df['Close'] > df['Open']) &
        (df['Close'].shift(1) > df['ema_34'].shift(1))
    ).astype(int)

    # Final signal with RTH filter
    df['long_signal'] = (
        (df['daily_bull_regime'] == 1) &
        (df['st_direction'] == 1) &
        (df['ema_bull'] == 1) &
        (df['pullback_long'] == 1) &
        (df['rth_ok'] == 1)
    ).astype(int)

    essential_cols = ['Open', 'High', 'Low', 'Close', 'Volume', 'atr_14', 'ema_34', 'ema_55']
    df = df.dropna(subset=essential_cols)
    df = df.drop(columns=['date', 'hour'], errors='ignore')

    return df


class ESSwingOptimized(Strategy):
    """ES Swing Strategy with optimizable parameters."""

    # Parameters to optimize
    stop_mult = 1.5
    target_mult = 3.0

    def init(self):
        self.long_signal = self.I(lambda: self.data.long_signal)
        self.atr = self.I(lambda: self.data.atr_14)

    def next(self):
        if self.position:
            return

        price = self.data.Close[-1]
        atr = self.atr[-1]

        if np.isnan(atr) or atr <= 0:
            return

        stop_dist = self.stop_mult * atr
        target_dist = self.target_mult * stop_dist

        if self.long_signal[-1] == 1:
            self.buy(sl=price - stop_dist, tp=price + target_dist)


def run_optimization(hourly_path: str, daily_path: str):
    """Run parameter optimization."""
    print("=" * 70)
    print("ES INTRADAY SWING - PARAMETER OPTIMIZATION")
    print("Pullback Entry + RTH Filter")
    print("=" * 70)

    df = prepare_data(hourly_path, daily_path)
    print(f"Dataset: {len(df):,} bars\n")

    bt = Backtest(
        df,
        ESSwingOptimized,
        cash=50000,
        commission=0.00005,
        exclusive_orders=True,
        trade_on_close=True
    )

    # Run baseline first
    print("Running baseline (stop=1.5, target=3.0)...")
    baseline = bt.run()
    print(f"Baseline Return: {baseline['Return [%]']:.2f}%")
    print(f"Baseline Sharpe: {baseline['Sharpe Ratio']:.3f}\n")

    # Optimize stop and target multipliers
    print("Optimizing stop_mult and target_mult...")
    print("Testing: stop_mult=[1.0, 1.25, 1.5, 1.75, 2.0, 2.5]")
    print("         target_mult=[1.5, 2.0, 2.5, 3.0, 3.5, 4.0, 5.0]\n")

    # Optimize for Sharpe Ratio (risk-adjusted returns)
    stats_sharpe = bt.optimize(
        stop_mult=[1.0, 1.25, 1.5, 1.75, 2.0, 2.5],
        target_mult=[1.5, 2.0, 2.5, 3.0, 3.5, 4.0, 5.0],
        maximize='Sharpe Ratio',
        constraint=lambda p: p.target_mult >= p.stop_mult,
        return_heatmap=False
    )

    print("\n" + "=" * 70)
    print("OPTIMIZATION RESULTS - MAXIMIZE SHARPE RATIO")
    print("=" * 70)
    print(f"Best Stop Mult:    {stats_sharpe._strategy.stop_mult}")
    print(f"Best Target Mult:  {stats_sharpe._strategy.target_mult}")
    print(f"Risk:Reward Ratio: 1:{stats_sharpe._strategy.target_mult:.1f}")
    print("-" * 70)
    print(f"Return:            {stats_sharpe['Return [%]']:.2f}%")
    print(f"# Trades:          {stats_sharpe['# Trades']}")
    print(f"Win Rate:          {stats_sharpe['Win Rate [%]']:.2f}%")
    print(f"Profit Factor:     {stats_sharpe['Profit Factor']:.3f}")
    print(f"Max Drawdown:      {stats_sharpe['Max. Drawdown [%]']:.2f}%")
    print(f"Sharpe Ratio:      {stats_sharpe['Sharpe Ratio']:.3f}")
    print(f"Avg Trade:         {stats_sharpe['Avg. Trade [%]']:.3f}%")

    # Also optimize for Return
    print("\n" + "-" * 70)
    print("OPTIMIZATION RESULTS - MAXIMIZE RETURN")
    print("-" * 70)

    stats_return = bt.optimize(
        stop_mult=[1.0, 1.25, 1.5, 1.75, 2.0, 2.5],
        target_mult=[1.5, 2.0, 2.5, 3.0, 3.5, 4.0, 5.0],
        maximize='Return [%]',
        constraint=lambda p: p.target_mult >= p.stop_mult,
        return_heatmap=False
    )

    print(f"Best Stop Mult:    {stats_return._strategy.stop_mult}")
    print(f"Best Target Mult:  {stats_return._strategy.target_mult}")
    print(f"Risk:Reward Ratio: 1:{stats_return._strategy.target_mult:.1f}")
    print("-" * 70)
    print(f"Return:            {stats_return['Return [%]']:.2f}%")
    print(f"# Trades:          {stats_return['# Trades']}")
    print(f"Win Rate:          {stats_return['Win Rate [%]']:.2f}%")
    print(f"Profit Factor:     {stats_return['Profit Factor']:.3f}")
    print(f"Max Drawdown:      {stats_return['Max. Drawdown [%]']:.2f}%")
    print(f"Sharpe Ratio:      {stats_return['Sharpe Ratio']:.3f}")

    # Optimize for Profit Factor
    print("\n" + "-" * 70)
    print("OPTIMIZATION RESULTS - MAXIMIZE PROFIT FACTOR")
    print("-" * 70)

    stats_pf = bt.optimize(
        stop_mult=[1.0, 1.25, 1.5, 1.75, 2.0, 2.5],
        target_mult=[1.5, 2.0, 2.5, 3.0, 3.5, 4.0, 5.0],
        maximize='Profit Factor',
        constraint=lambda p: p.target_mult >= p.stop_mult,
        return_heatmap=False
    )

    print(f"Best Stop Mult:    {stats_pf._strategy.stop_mult}")
    print(f"Best Target Mult:  {stats_pf._strategy.target_mult}")
    print(f"Risk:Reward Ratio: 1:{stats_pf._strategy.target_mult:.1f}")
    print("-" * 70)
    print(f"Return:            {stats_pf['Return [%]']:.2f}%")
    print(f"# Trades:          {stats_pf['# Trades']}")
    print(f"Win Rate:          {stats_pf['Win Rate [%]']:.2f}%")
    print(f"Profit Factor:     {stats_pf['Profit Factor']:.3f}")
    print(f"Max Drawdown:      {stats_pf['Max. Drawdown [%]']:.2f}%")
    print(f"Sharpe Ratio:      {stats_pf['Sharpe Ratio']:.3f}")

    # Summary comparison
    print("\n" + "=" * 70)
    print("SUMMARY COMPARISON")
    print("=" * 70)
    print(f"{'Metric':<20} {'Baseline':>12} {'Max Sharpe':>12} {'Max Return':>12} {'Max PF':>12}")
    print("-" * 70)

    metrics = [
        ('Return [%]', 'Return'),
        ('# Trades', 'Trades'),
        ('Win Rate [%]', 'Win Rate'),
        ('Profit Factor', 'PF'),
        ('Max. Drawdown [%]', 'Max DD'),
        ('Sharpe Ratio', 'Sharpe'),
    ]

    for key, label in metrics:
        b = baseline.get(key, 0) or 0
        s = stats_sharpe.get(key, 0) or 0
        r = stats_return.get(key, 0) or 0
        p = stats_pf.get(key, 0) or 0

        if isinstance(b, float):
            print(f"{label:<20} {b:>12.2f} {s:>12.2f} {r:>12.2f} {p:>12.2f}")
        else:
            print(f"{label:<20} {b:>12} {s:>12} {r:>12} {p:>12}")

    print("-" * 70)
    print(f"{'Stop Mult':<20} {'1.50':>12} {stats_sharpe._strategy.stop_mult:>12} {stats_return._strategy.stop_mult:>12} {stats_pf._strategy.stop_mult:>12}")
    print(f"{'Target Mult':<20} {'3.00':>12} {stats_sharpe._strategy.target_mult:>12} {stats_return._strategy.target_mult:>12} {stats_pf._strategy.target_mult:>12}")

    # Save best plot
    try:
        ESSwingOptimized.stop_mult = stats_sharpe._strategy.stop_mult
        ESSwingOptimized.target_mult = stats_sharpe._strategy.target_mult
        bt_final = Backtest(df, ESSwingOptimized, cash=50000, commission=0.00005,
                           exclusive_orders=True, trade_on_close=True)
        bt_final.run()
        bt_final.plot(filename='src/data/rbi/es_swing_optimized.html', open_browser=False)
        print("\nChart saved to: src/data/rbi/es_swing_optimized.html")
    except Exception as e:
        print(f"\nCould not save chart: {e}")

    return stats_sharpe, stats_return, stats_pf


if __name__ == "__main__":
    import os

    script_dir = os.path.dirname(os.path.abspath(__file__))
    project_root = os.path.dirname(os.path.dirname(script_dir))

    hourly_path = os.path.join(project_root, 'src', 'data', 'rbi', 'ES-1H.csv')
    daily_path = os.path.join(project_root, 'src', 'data', 'rbi', 'ES-1D.csv')

    stats_sharpe, stats_return, stats_pf = run_optimization(hourly_path, daily_path)
