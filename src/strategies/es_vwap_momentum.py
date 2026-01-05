"""
ES VWAP Momentum Reclaim Strategy
==================================
1-Minute timeframe momentum strategy targeting >63% CAGR

Strategy Logic:
- Track VWAP (Volume Weighted Average Price) per session
- Long: Price reclaims VWAP from below with momentum confirmation
- Short: Price loses VWAP from above with momentum confirmation
- Use RSI/momentum filters to avoid chop
- Time-based exits: Close positions before EOD
- ATR-based stops and targets

Author: Moon Dev AI
"""

import pandas as pd
import numpy as np
from backtesting import Backtest, Strategy
from pathlib import Path
import zstandard as zstd
import io
from glob import glob
import warnings
warnings.filterwarnings('ignore')


def load_databento_data(data_dir: str, symbol_filter: str = 'ES') -> pd.DataFrame:
    """
    Load and combine all Databento 1-minute OHLCV files.
    Uses continuous front-month contract (highest volume).
    """
    print(f"Loading data from: {data_dir}")

    files = sorted(glob(f"{data_dir}/*.ohlcv-1m.csv.zst"))
    print(f"Found {len(files)} daily files")

    all_data = []

    for filepath in files:
        try:
            with open(filepath, 'rb') as f:
                dctx = zstd.ZstdDecompressor()
                with dctx.stream_reader(f) as reader:
                    text = io.TextIOWrapper(reader, encoding='utf-8')
                    df = pd.read_csv(text)

                    if len(df) == 0:
                        continue

                    # Filter for ES contracts only (not spreads)
                    df = df[df['symbol'].str.match(r'^ES[A-Z]\d$', na=False)]

                    if len(df) > 0:
                        all_data.append(df)
        except Exception as e:
            continue

    if not all_data:
        raise ValueError("No data loaded!")

    print(f"Loaded {len(all_data)} days of data")
    df = pd.concat(all_data, ignore_index=True)

    # Parse timestamp
    df['datetime'] = pd.to_datetime(df['ts_event'])

    # Group by timestamp and take the contract with highest volume (front month)
    df_agg = df.groupby('datetime').apply(
        lambda x: x.loc[x['volume'].idxmax()]
    ).reset_index(drop=True)

    # Format for backtesting.py
    df_final = pd.DataFrame({
        'datetime': df_agg['datetime'],
        'Open': df_agg['open'],
        'High': df_agg['high'],
        'Low': df_agg['low'],
        'Close': df_agg['close'],
        'Volume': df_agg['volume']
    })

    df_final.set_index('datetime', inplace=True)
    df_final.sort_index(inplace=True)

    # Remove duplicates
    df_final = df_final[~df_final.index.duplicated(keep='first')]

    print(f"Final dataset: {len(df_final):,} 1-minute bars")
    print(f"Date range: {df_final.index.min()} to {df_final.index.max()}")

    return df_final


def prepare_vwap_signals(df: pd.DataFrame) -> pd.DataFrame:
    """
    Prepare VWAP Momentum Reclaim signals.

    Strategy:
    - Calculate session VWAP (reset daily at RTH open)
    - Long when price crosses above VWAP with momentum
    - Short when price crosses below VWAP with momentum
    """
    df = df.copy()

    # Identify RTH hours (9:30 AM - 4:00 PM ET)
    # Data is in UTC, RTH is 14:30-21:00 UTC (EST+5)
    df['hour'] = df.index.hour
    df['minute'] = df.index.minute
    df['time_decimal'] = df['hour'] + df['minute'] / 60

    # RTH: 14:30-21:00 UTC = 9:30-16:00 ET
    df['rth'] = ((df['time_decimal'] >= 14.5) & (df['time_decimal'] < 21.0)).astype(int)

    # Date for session grouping
    df['date'] = df.index.date

    # Session VWAP - reset at RTH open each day
    df['typical_price'] = (df['High'] + df['Low'] + df['Close']) / 3
    df['tp_volume'] = df['typical_price'] * df['Volume']

    # Cumulative values within each RTH session
    df['cum_tp_vol'] = df.groupby('date')['tp_volume'].cumsum()
    df['cum_vol'] = df.groupby('date')['Volume'].cumsum()
    df['vwap'] = df['cum_tp_vol'] / (df['cum_vol'] + 1e-10)

    # ATR for stops (14-period)
    df['tr'] = pd.concat([
        df['High'] - df['Low'],
        abs(df['High'] - df['Close'].shift(1)),
        abs(df['Low'] - df['Close'].shift(1))
    ], axis=1).max(axis=1)
    df['atr'] = df['tr'].rolling(window=14, min_periods=1).mean()

    # RSI for momentum confirmation (14-period)
    delta = df['Close'].diff()
    gain = delta.where(delta > 0, 0).rolling(window=14, min_periods=1).mean()
    loss = (-delta.where(delta < 0, 0)).rolling(window=14, min_periods=1).mean()
    rs = gain / (loss + 1e-10)
    df['rsi'] = 100 - (100 / (1 + rs))

    # EMAs for trend context
    df['ema_9'] = df['Close'].ewm(span=9, adjust=False).mean()
    df['ema_21'] = df['Close'].ewm(span=21, adjust=False).mean()
    df['ema_bull'] = (df['ema_9'] > df['ema_21']).astype(int)

    # Price relative to VWAP
    df['above_vwap'] = (df['Close'] > df['vwap']).astype(int)
    df['prev_above_vwap'] = df['above_vwap'].shift(1)

    # VWAP distance (normalized by ATR)
    df['vwap_dist'] = (df['Close'] - df['vwap']) / (df['atr'] + 1e-10)

    # Trading period: During RTH but not first 15 min or last 30 min
    df['trade_period'] = (
        (df['time_decimal'] >= 14.75) &  # After 9:45 AM ET
        (df['time_decimal'] < 20.5)       # Before 3:30 PM ET
    ).astype(int)

    # VWAP Reclaim Long Signal
    # Price crosses above VWAP + momentum confirmation
    df['vwap_reclaim_long'] = (
        (df['Close'] > df['vwap']) &              # Above VWAP now
        (df['Close'].shift(1) <= df['vwap'].shift(1)) &  # Was below VWAP
        (df['rsi'] > 50) & (df['rsi'] < 70) &     # RSI bullish but not overbought
        (df['ema_bull'] == 1) &                    # Short-term trend up
        (df['trade_period'] == 1) &
        (df['rth'] == 1)
    ).astype(int)

    # VWAP Breakdown Short Signal
    # Price crosses below VWAP + momentum confirmation
    df['vwap_break_short'] = (
        (df['Close'] < df['vwap']) &              # Below VWAP now
        (df['Close'].shift(1) >= df['vwap'].shift(1)) &  # Was above VWAP
        (df['rsi'] < 50) & (df['rsi'] > 30) &     # RSI bearish but not oversold
        (df['ema_bull'] == 0) &                    # Short-term trend down
        (df['trade_period'] == 1) &
        (df['rth'] == 1)
    ).astype(int)

    # End of day - close positions
    df['eod'] = ((df['time_decimal'] >= 20.5) & (df['time_decimal'] < 21.0)).astype(int)

    # Cleanup
    df = df.drop(columns=['hour', 'minute', 'time_decimal', 'date', 'tr',
                          'typical_price', 'tp_volume', 'cum_tp_vol', 'cum_vol',
                          'prev_above_vwap'], errors='ignore')

    # Drop rows with NaN in essential columns
    essential = ['Open', 'High', 'Low', 'Close', 'Volume', 'atr', 'vwap', 'rsi']
    df = df.dropna(subset=essential)

    return df


class VWAPMomentumStrategy(Strategy):
    """
    VWAP Momentum Reclaim Strategy for ES Futures.

    Long: Price reclaims VWAP with bullish momentum
    Short: Price loses VWAP with bearish momentum
    Exit: Time-based (EOD) or ATR-based stops/targets
    """

    # Optimizable parameters
    stop_mult = 1.5       # Stop loss = stop_mult * ATR
    target_mult = 2.5     # Target = target_mult * ATR

    def init(self):
        self.vwap_long = self.I(lambda: self.data.vwap_reclaim_long)
        self.vwap_short = self.I(lambda: self.data.vwap_break_short)
        self.atr = self.I(lambda: self.data.atr)
        self.vwap = self.I(lambda: self.data.vwap)
        self.eod = self.I(lambda: self.data.eod)
        self.rth = self.I(lambda: self.data.rth)

    def next(self):
        price = self.data.Close[-1]
        atr = self.atr[-1]

        if np.isnan(atr) or atr <= 0:
            return

        # Close position at EOD
        if self.position and self.eod[-1] == 1:
            self.position.close()
            return

        # Skip if already in position
        if self.position:
            return

        # Only trade during RTH
        if self.rth[-1] != 1:
            return

        stop_dist = self.stop_mult * atr
        target_dist = self.target_mult * atr

        # VWAP Reclaim Long
        if self.vwap_long[-1] == 1:
            self.buy(sl=price - stop_dist, tp=price + target_dist)

        # VWAP Breakdown Short
        elif self.vwap_short[-1] == 1:
            self.sell(sl=price + stop_dist, tp=price - target_dist)


def calculate_cagr(total_return_pct: float, num_days: int) -> float:
    """Calculate CAGR from total return percentage and number of days."""
    if num_days <= 0:
        return 0
    years = num_days / 365.25
    if years <= 0:
        return 0
    total_return = 1 + (total_return_pct / 100)
    if total_return <= 0:
        return -100
    cagr = (total_return ** (1 / years) - 1) * 100
    return cagr


def run_backtest(df: pd.DataFrame, optimize: bool = False):
    """Run VWAP Momentum strategy backtest."""
    print("\n" + "=" * 70)
    print("ES VWAP MOMENTUM RECLAIM - 1 MINUTE DATA")
    print("=" * 70)

    bt = Backtest(
        df,
        VWAPMomentumStrategy,
        cash=50000,
        commission=0.00005,
        exclusive_orders=True,
        trade_on_close=True,
        hedging=True  # Allow both long and short
    )

    # Baseline run
    print("\nRunning baseline (stop=1.5, target=2.5)...")
    baseline = bt.run()

    num_days = (df.index.max() - df.index.min()).days
    cagr = calculate_cagr(baseline['Return [%]'], num_days)

    print(f"\nBaseline Results:")
    print(f"  Return:      {baseline['Return [%]']:.2f}%")
    print(f"  CAGR:        {cagr:.2f}%")
    print(f"  # Trades:    {baseline['# Trades']}")
    print(f"  Win Rate:    {baseline['Win Rate [%]']:.2f}%")
    print(f"  Sharpe:      {baseline['Sharpe Ratio']:.3f}")
    print(f"  Max DD:      {baseline['Max. Drawdown [%]']:.2f}%")
    print(f"  Profit Factor: {baseline['Profit Factor']:.3f}")

    if optimize:
        print("\n" + "-" * 70)
        print("OPTIMIZING PARAMETERS...")
        print("-" * 70)

        stats = bt.optimize(
            stop_mult=[1.0, 1.25, 1.5, 2.0, 2.5],
            target_mult=[1.5, 2.0, 2.5, 3.0, 4.0, 5.0],
            maximize='Sharpe Ratio',
            constraint=lambda p: p.target_mult >= p.stop_mult,
            return_heatmap=False
        )

        opt_cagr = calculate_cagr(stats['Return [%]'], num_days)

        print(f"\nOptimized Results (Max Sharpe):")
        print(f"  Stop Mult:    {stats._strategy.stop_mult}")
        print(f"  Target Mult:  {stats._strategy.target_mult}")
        print(f"  Return:       {stats['Return [%]']:.2f}%")
        print(f"  CAGR:         {opt_cagr:.2f}%")
        print(f"  # Trades:     {stats['# Trades']}")
        print(f"  Win Rate:     {stats['Win Rate [%]']:.2f}%")
        print(f"  Sharpe:       {stats['Sharpe Ratio']:.3f}")
        print(f"  Max DD:       {stats['Max. Drawdown [%]']:.2f}%")
        print(f"  Profit Factor: {stats['Profit Factor']:.3f}")

        # Also optimize for Return
        print("\n" + "-" * 70)
        print("OPTIMIZING FOR MAX RETURN...")
        print("-" * 70)

        stats_ret = bt.optimize(
            stop_mult=[1.0, 1.25, 1.5, 2.0, 2.5],
            target_mult=[1.5, 2.0, 2.5, 3.0, 4.0, 5.0],
            maximize='Return [%]',
            constraint=lambda p: p.target_mult >= p.stop_mult,
            return_heatmap=False
        )

        ret_cagr = calculate_cagr(stats_ret['Return [%]'], num_days)

        print(f"\nOptimized Results (Max Return):")
        print(f"  Stop Mult:    {stats_ret._strategy.stop_mult}")
        print(f"  Target Mult:  {stats_ret._strategy.target_mult}")
        print(f"  Return:       {stats_ret['Return [%]']:.2f}%")
        print(f"  CAGR:         {ret_cagr:.2f}%")
        print(f"  # Trades:     {stats_ret['# Trades']}")
        print(f"  Win Rate:     {stats_ret['Win Rate [%]']:.2f}%")
        print(f"  Sharpe:       {stats_ret['Sharpe Ratio']:.3f}")
        print(f"  Max DD:       {stats_ret['Max. Drawdown [%]']:.2f}%")

        # Summary comparison
        print("\n" + "=" * 70)
        print("COMPARISON WITH PREVIOUS STRATEGY (63% CAGR target)")
        print("=" * 70)
        print(f"{'Metric':<20} {'Baseline':>15} {'Max Sharpe':>15} {'Max Return':>15}")
        print("-" * 70)
        print(f"{'CAGR':<20} {cagr:>14.2f}% {opt_cagr:>14.2f}% {ret_cagr:>14.2f}%")
        print(f"{'Sharpe':<20} {baseline['Sharpe Ratio']:>15.3f} {stats['Sharpe Ratio']:>15.3f} {stats_ret['Sharpe Ratio']:>15.3f}")
        print(f"{'Max DD':<20} {baseline['Max. Drawdown [%]']:>14.2f}% {stats['Max. Drawdown [%]']:>14.2f}% {stats_ret['Max. Drawdown [%]']:>14.2f}%")
        print(f"{'# Trades':<20} {baseline['# Trades']:>15} {stats['# Trades']:>15} {stats_ret['# Trades']:>15}")

        return stats, stats_ret, baseline

    return baseline


if __name__ == "__main__":
    # Data path
    data_dir = r"C:\dev\databento\GLBX-20251122-DHFVWN9D6Q"

    # Load data
    df = load_databento_data(data_dir)

    # Prepare signals
    df = prepare_vwap_signals(df)
    print(f"\nPrepared data: {len(df):,} bars")
    print(f"VWAP Reclaim Longs: {df['vwap_reclaim_long'].sum():,}")
    print(f"VWAP Break Shorts: {df['vwap_break_short'].sum():,}")

    # Run backtest with optimization
    results = run_backtest(df, optimize=True)
