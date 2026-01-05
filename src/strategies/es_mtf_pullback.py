"""
ES Multi-Timeframe Pullback Strategy (Replicating Hourly Winner)
================================================================
Closely mirrors the 63% CAGR hourly strategy but uses Databento 1m data
resampled to 15-minute bars.

Key success factors from hourly strategy:
1. LONG-ONLY with daily bull regime filter (Close > 50 SMA)
2. SuperTrend bullish confirmation
3. EMA pullback entry (price touches EMA_34, bounces)
4. RTH filter (10 AM - 3 PM core hours)
5. Stop: 1.25 × ATR, Target: 1.5 × stop

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


def load_databento_data(data_dir: str) -> pd.DataFrame:
    """Load Databento 1-minute data."""
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
                    df = df[df['symbol'].str.match(r'^ES[A-Z]\d$', na=False)]
                    if len(df) > 0:
                        all_data.append(df)
        except:
            continue

    if not all_data:
        raise ValueError("No data loaded!")

    print(f"Loaded {len(all_data)} days of data")
    df = pd.concat(all_data, ignore_index=True)
    df['datetime'] = pd.to_datetime(df['ts_event'])

    # Front month by highest volume
    df_agg = df.groupby('datetime').apply(
        lambda x: x.loc[x['volume'].idxmax()]
    ).reset_index(drop=True)

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
    df_final = df_final[~df_final.index.duplicated(keep='first')]

    print(f"Final: {len(df_final):,} 1-minute bars")
    print(f"Range: {df_final.index.min()} to {df_final.index.max()}")
    return df_final


def calculate_supertrend(high, low, close, period=10, multiplier=3.0):
    """SuperTrend indicator."""
    tr = pd.concat([
        high - low,
        abs(high - close.shift(1)),
        abs(low - close.shift(1))
    ], axis=1).max(axis=1)
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


def prepare_signals(df_1m: pd.DataFrame) -> pd.DataFrame:
    """
    Prepare signals using:
    - 15-minute bars for primary trading signals
    - Daily bars for regime filter (exactly like hourly strategy)
    """
    # Resample to multiple timeframes
    df_15m = df_1m.resample('15min').agg({
        'Open': 'first', 'High': 'max', 'Low': 'min',
        'Close': 'last', 'Volume': 'sum'
    }).dropna()

    df_daily = df_1m.resample('1D').agg({
        'Open': 'first', 'High': 'max', 'Low': 'min',
        'Close': 'last', 'Volume': 'sum'
    }).dropna()

    print(f"15-min bars: {len(df_15m):,}")
    print(f"Daily bars: {len(df_daily):,}")

    # ---- Daily Regime Filter (exactly like hourly strategy) ----
    df_daily['sma_50'] = df_daily['Close'].rolling(window=50).mean()
    df_daily['bull_regime'] = (df_daily['Close'] > df_daily['sma_50']).astype(int)
    df_daily['date'] = df_daily.index.date

    # Map regime to 15-minute bars
    df_15m['date'] = df_15m.index.date
    regime_map = df_daily.set_index('date')['bull_regime'].to_dict()
    df_15m['bull_regime'] = df_15m['date'].map(regime_map)
    df_15m['bull_regime'] = df_15m['bull_regime'].ffill().fillna(0)

    # ---- 15-Minute Indicators ----

    # EMAs (using 34/55 like hourly strategy)
    df_15m['ema_34'] = df_15m['Close'].ewm(span=34, adjust=False).mean()
    df_15m['ema_55'] = df_15m['Close'].ewm(span=55, adjust=False).mean()
    df_15m['ema_bull'] = (df_15m['ema_34'] > df_15m['ema_55']).astype(int)

    # SuperTrend
    st, st_dir = calculate_supertrend(
        df_15m['High'], df_15m['Low'], df_15m['Close'],
        period=10, multiplier=3.0
    )
    df_15m['st_direction'] = st_dir

    # ATR for stops
    tr = pd.concat([
        df_15m['High'] - df_15m['Low'],
        abs(df_15m['High'] - df_15m['Close'].shift(1)),
        abs(df_15m['Low'] - df_15m['Close'].shift(1))
    ], axis=1).max(axis=1)
    df_15m['atr'] = tr.rolling(window=14).mean()

    # RTH hours (core hours: 10 AM - 3 PM ET = 15:00-20:00 UTC)
    df_15m['hour'] = df_15m.index.hour
    df_15m['minute'] = df_15m.index.minute
    df_15m['time_decimal'] = df_15m['hour'] + df_15m['minute'] / 60
    df_15m['rth_ok'] = (
        (df_15m['time_decimal'] >= 15.0) &
        (df_15m['time_decimal'] < 20.0)
    ).astype(int)

    # Pullback Entry (exactly like hourly strategy)
    ema_tolerance = 0.003  # 0.3% tolerance
    df_15m['pullback_long'] = (
        (df_15m['Low'] <= df_15m['ema_34'] * (1 + ema_tolerance)) &
        (df_15m['Close'] > df_15m['ema_34']) &
        (df_15m['Close'] > df_15m['Open']) &
        (df_15m['Close'].shift(1) > df_15m['ema_34'].shift(1))
    ).astype(int)

    # LONG ONLY signal (like hourly strategy)
    df_15m['long_signal'] = (
        (df_15m['bull_regime'] == 1) &      # Daily regime bullish
        (df_15m['st_direction'] == 1) &      # SuperTrend bullish
        (df_15m['ema_bull'] == 1) &          # 15m EMA bullish
        (df_15m['pullback_long'] == 1) &     # Pullback to EMA
        (df_15m['rth_ok'] == 1)              # During RTH
    ).astype(int)

    # EOD exit
    df_15m['eod'] = ((df_15m['time_decimal'] >= 20.5) & (df_15m['time_decimal'] < 21.0)).astype(int)

    # Prepare final dataframe
    df_final = df_15m[['Open', 'High', 'Low', 'Close', 'Volume',
                       'atr', 'ema_34', 'long_signal', 'rth_ok', 'eod',
                       'bull_regime', 'st_direction']].copy()

    df_final = df_final.dropna()

    print(f"\nPrepared: {len(df_final):,} 15-minute bars")
    print(f"Long signals: {df_final['long_signal'].sum():,}")
    print(f"Bull regime days: {(df_final['bull_regime'] == 1).sum():,} / {len(df_final):,}")

    return df_final


class MTFPullbackStrategy(Strategy):
    """
    Multi-Timeframe Pullback Strategy - Long Only.
    Mirrors the 63% CAGR hourly strategy.
    """
    stop_mult = 1.25
    target_mult = 1.5

    def init(self):
        self.long_sig = self.I(lambda: self.data.long_signal)
        self.atr = self.I(lambda: self.data.atr)
        self.rth_ok = self.I(lambda: self.data.rth_ok)
        self.eod = self.I(lambda: self.data.eod)

    def next(self):
        price = self.data.Close[-1]
        atr = self.atr[-1]

        if np.isnan(atr) or atr <= 0:
            return

        # EOD exit
        if self.position and self.eod[-1] == 1:
            self.position.close()
            return

        if self.position:
            return

        if self.rth_ok[-1] != 1:
            return

        stop_dist = self.stop_mult * atr
        target_dist = self.target_mult * stop_dist

        if self.long_sig[-1] == 1:
            self.buy(sl=price - stop_dist, tp=price + target_dist)


def calculate_cagr(total_return_pct: float, num_days: int) -> float:
    if num_days <= 0:
        return 0
    years = num_days / 365.25
    if years <= 0:
        return 0
    total_return = 1 + (total_return_pct / 100)
    if total_return <= 0:
        return -100
    return (total_return ** (1 / years) - 1) * 100


def run_backtest(df: pd.DataFrame, optimize: bool = False):
    print("\n" + "=" * 70)
    print("ES MTF PULLBACK (Replicating 63% CAGR Hourly Strategy)")
    print("=" * 70)

    bt = Backtest(
        df,
        MTFPullbackStrategy,
        cash=50000,
        commission=0.00005,
        exclusive_orders=True,
        trade_on_close=True
    )

    print("\nRunning baseline (stop=1.25, target=1.5 - same as hourly winner)...")
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

    if optimize:
        print("\n" + "-" * 70)
        print("OPTIMIZING PARAMETERS...")
        print("-" * 70)

        stats = bt.optimize(
            stop_mult=[1.0, 1.25, 1.5, 1.75, 2.0],
            target_mult=[1.25, 1.5, 2.0, 2.5, 3.0, 3.5],
            maximize='Sharpe Ratio',
            constraint=lambda p: p.target_mult >= p.stop_mult,
            return_heatmap=False
        )

        opt_cagr = calculate_cagr(stats['Return [%]'], num_days)

        print(f"\nOptimized (Max Sharpe):")
        print(f"  Stop: {stats._strategy.stop_mult}, Target: {stats._strategy.target_mult}")
        print(f"  Return: {stats['Return [%]']:.2f}%, CAGR: {opt_cagr:.2f}%")
        print(f"  Trades: {stats['# Trades']}, Win Rate: {stats['Win Rate [%]']:.2f}%")
        print(f"  Sharpe: {stats['Sharpe Ratio']:.3f}, Max DD: {stats['Max. Drawdown [%]']:.2f}%")

        print("\n" + "-" * 70)
        print("OPTIMIZING FOR MAX RETURN...")
        print("-" * 70)

        stats_ret = bt.optimize(
            stop_mult=[1.0, 1.25, 1.5, 1.75, 2.0],
            target_mult=[1.25, 1.5, 2.0, 2.5, 3.0, 3.5],
            maximize='Return [%]',
            constraint=lambda p: p.target_mult >= p.stop_mult,
            return_heatmap=False
        )

        ret_cagr = calculate_cagr(stats_ret['Return [%]'], num_days)

        print(f"\nOptimized (Max Return):")
        print(f"  Stop: {stats_ret._strategy.stop_mult}, Target: {stats_ret._strategy.target_mult}")
        print(f"  Return: {stats_ret['Return [%]']:.2f}%, CAGR: {ret_cagr:.2f}%")
        print(f"  Trades: {stats_ret['# Trades']}, Win Rate: {stats_ret['Win Rate [%]']:.2f}%")
        print(f"  Sharpe: {stats_ret['Sharpe Ratio']:.3f}, Max DD: {stats_ret['Max. Drawdown [%]']:.2f}%")

        # Comparison
        print("\n" + "=" * 70)
        print("COMPARISON WITH 63% CAGR TARGET")
        print("=" * 70)
        print(f"{'Metric':<15} {'Baseline':>12} {'Max Sharpe':>12} {'Max Return':>12} {'Target':>12}")
        print("-" * 70)
        print(f"{'CAGR':<15} {cagr:>11.2f}% {opt_cagr:>11.2f}% {ret_cagr:>11.2f}% {63:>11.2f}%")
        print(f"{'Sharpe':<15} {baseline['Sharpe Ratio']:>12.3f} {stats['Sharpe Ratio']:>12.3f} {stats_ret['Sharpe Ratio']:>12.3f} {2.40:>12.3f}")

        best_cagr = max(cagr, opt_cagr, ret_cagr)
        if best_cagr > 63:
            print(f"\nSUCCESS! Best CAGR: {best_cagr:.2f}% BEATS 63% target!")
        else:
            print(f"\nBest CAGR: {best_cagr:.2f}% (target: 63%)")

        return stats, stats_ret, baseline

    return baseline


if __name__ == "__main__":
    data_dir = r"C:\dev\databento\GLBX-20251122-DHFVWN9D6Q"
    df_1m = load_databento_data(data_dir)
    df = prepare_signals(df_1m)
    results = run_backtest(df, optimize=True)
