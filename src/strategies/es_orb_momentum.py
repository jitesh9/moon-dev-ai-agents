"""
ES Opening Range Breakout (ORB) Momentum Strategy
==================================================
1-Minute timeframe momentum strategy targeting >63% CAGR

Strategy Logic:
- Define Opening Range: First N minutes of RTH (9:30 AM ET)
- Long: Break above OR high with momentum confirmation
- Short: Break below OR low with momentum confirmation
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


def prepare_orb_signals(df: pd.DataFrame, or_minutes: int = 30) -> pd.DataFrame:
    """
    Prepare Opening Range Breakout signals.

    Parameters:
    - or_minutes: Minutes to define opening range (default 30 = 9:30-10:00)
    """
    df = df.copy()

    # Identify RTH hours (9:30 AM - 4:00 PM ET)
    # Note: Data appears to be in UTC, RTH is 14:30-21:00 UTC (EST+5)
    df['hour'] = df.index.hour
    df['minute'] = df.index.minute
    df['time_decimal'] = df['hour'] + df['minute'] / 60

    # RTH: 14:30-21:00 UTC = 9:30-16:00 ET
    df['rth'] = ((df['time_decimal'] >= 14.5) & (df['time_decimal'] < 21.0)).astype(int)

    # Opening range: 14:30-15:00 UTC = 9:30-10:00 ET (first 30 min)
    or_end_time = 14.5 + (or_minutes / 60)
    df['or_period'] = ((df['time_decimal'] >= 14.5) & (df['time_decimal'] < or_end_time)).astype(int)

    # Date for grouping
    df['date'] = df.index.date

    # Calculate Opening Range High/Low per day
    or_data = df[df['or_period'] == 1].groupby('date').agg({
        'High': 'max',
        'Low': 'min',
        'Volume': 'sum'
    }).rename(columns={'High': 'or_high', 'Low': 'or_low', 'Volume': 'or_volume'})

    # Merge back
    df['or_high'] = df['date'].map(or_data['or_high'])
    df['or_low'] = df['date'].map(or_data['or_low'])
    df['or_range'] = df['or_high'] - df['or_low']

    # ATR for stops (14-period on 1-min bars, or ~15 minute window)
    df['tr'] = pd.concat([
        df['High'] - df['Low'],
        abs(df['High'] - df['Close'].shift(1)),
        abs(df['Low'] - df['Close'].shift(1))
    ], axis=1).max(axis=1)
    df['atr'] = df['tr'].rolling(window=14, min_periods=1).mean()

    # EMA for trend filter (20-period on 1-min ≈ 20 min trend)
    df['ema_20'] = df['Close'].ewm(span=20, adjust=False).mean()
    df['ema_50'] = df['Close'].ewm(span=50, adjust=False).mean()
    df['trend_up'] = (df['ema_20'] > df['ema_50']).astype(int)

    # RSI for momentum confirmation
    delta = df['Close'].diff()
    gain = delta.where(delta > 0, 0).rolling(window=14, min_periods=1).mean()
    loss = (-delta.where(delta < 0, 0)).rolling(window=14, min_periods=1).mean()
    rs = gain / (loss + 1e-10)
    df['rsi'] = 100 - (100 / (1 + rs))

    # VWAP for additional context
    df['vwap'] = (df['Close'] * df['Volume']).cumsum() / df['Volume'].cumsum()

    # Trading period: After opening range, during RTH
    df['trade_period'] = ((df['time_decimal'] >= or_end_time) & (df['time_decimal'] < 20.5)).astype(int)

    # Breakout signals
    df['breakout_long'] = (
        (df['Close'] > df['or_high']) &
        (df['Close'].shift(1) <= df['or_high'].shift(1)) &  # First break
        (df['trade_period'] == 1) &
        (df['rth'] == 1)
    ).astype(int)

    df['breakout_short'] = (
        (df['Close'] < df['or_low']) &
        (df['Close'].shift(1) >= df['or_low'].shift(1)) &  # First break
        (df['trade_period'] == 1) &
        (df['rth'] == 1)
    ).astype(int)

    # End of day - close positions
    df['eod'] = ((df['time_decimal'] >= 20.5) & (df['time_decimal'] < 21.0)).astype(int)

    # Forward-fill NaN values
    df['or_high'] = df['or_high'].ffill()
    df['or_low'] = df['or_low'].ffill()
    df['or_range'] = df['or_range'].ffill()

    # Drop setup columns
    df = df.drop(columns=['hour', 'minute', 'time_decimal', 'date', 'or_period', 'tr'], errors='ignore')

    # Drop rows with NaN in essential columns
    essential = ['Open', 'High', 'Low', 'Close', 'Volume', 'atr', 'or_high', 'or_low']
    df = df.dropna(subset=essential)

    return df


class ORBStrategy(Strategy):
    """
    Opening Range Breakout Strategy for ES Futures.

    Long: Price breaks above OR high during trade period
    Short: Price breaks below OR low during trade period
    Exit: Time-based (EOD) or ATR-based stops/targets
    """

    # Optimizable parameters
    stop_mult = 1.5       # Stop loss = stop_mult * ATR
    target_mult = 2.0     # Target = target_mult * stop distance
    use_trend_filter = 1  # 1 = use EMA trend filter, 0 = don't use

    def init(self):
        self.breakout_long = self.I(lambda: self.data.breakout_long)
        self.breakout_short = self.I(lambda: self.data.breakout_short)
        self.atr = self.I(lambda: self.data.atr)
        self.or_high = self.I(lambda: self.data.or_high)
        self.or_low = self.I(lambda: self.data.or_low)
        self.trend_up = self.I(lambda: self.data.trend_up)
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
        target_dist = self.target_mult * stop_dist

        # Long breakout
        if self.breakout_long[-1] == 1:
            if self.use_trend_filter == 0 or self.trend_up[-1] == 1:
                self.buy(sl=price - stop_dist, tp=price + target_dist)

        # Short breakout
        elif self.breakout_short[-1] == 1:
            if self.use_trend_filter == 0 or self.trend_up[-1] == 0:
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
    """Run ORB strategy backtest."""
    print("\n" + "=" * 70)
    print("ES OPENING RANGE BREAKOUT - 1 MINUTE DATA")
    print("=" * 70)

    bt = Backtest(
        df,
        ORBStrategy,
        cash=50000,
        commission=0.00005,
        exclusive_orders=True,
        trade_on_close=True,
        hedging=True  # Allow both long and short
    )

    # Baseline run
    print("\nRunning baseline (stop=1.5, target=2.0)...")
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
            stop_mult=[1.0, 1.25, 1.5, 2.0, 2.5, 3.0],
            target_mult=[1.5, 2.0, 2.5, 3.0, 4.0, 5.0],
            use_trend_filter=[0, 1],
            maximize='Sharpe Ratio',
            constraint=lambda p: p.target_mult >= p.stop_mult,
            return_heatmap=False
        )

        opt_cagr = calculate_cagr(stats['Return [%]'], num_days)

        print(f"\nOptimized Results (Max Sharpe):")
        print(f"  Stop Mult:    {stats._strategy.stop_mult}")
        print(f"  Target Mult:  {stats._strategy.target_mult}")
        print(f"  Trend Filter: {'Yes' if stats._strategy.use_trend_filter else 'No'}")
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
            stop_mult=[1.0, 1.25, 1.5, 2.0, 2.5, 3.0],
            target_mult=[1.5, 2.0, 2.5, 3.0, 4.0, 5.0],
            use_trend_filter=[0, 1],
            maximize='Return [%]',
            constraint=lambda p: p.target_mult >= p.stop_mult,
            return_heatmap=False
        )

        ret_cagr = calculate_cagr(stats_ret['Return [%]'], num_days)

        print(f"\nOptimized Results (Max Return):")
        print(f"  Stop Mult:    {stats_ret._strategy.stop_mult}")
        print(f"  Target Mult:  {stats_ret._strategy.target_mult}")
        print(f"  Trend Filter: {'Yes' if stats_ret._strategy.use_trend_filter else 'No'}")
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
    df = prepare_orb_signals(df, or_minutes=30)
    print(f"\nPrepared data: {len(df):,} bars")
    print(f"Breakout longs: {df['breakout_long'].sum():,}")
    print(f"Breakout shorts: {df['breakout_short'].sum():,}")

    # Run backtest with optimization
    results = run_backtest(df, optimize=True)
