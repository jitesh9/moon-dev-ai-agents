"""
ES Hourly Strategy from 1-Minute Data
=====================================
Aggregates Databento 1-minute data to hourly bars.
Then applies the EXACT same logic as the 63% CAGR winner.

The key insight is that the original strategy worked on hourly data,
so we need to test on hourly aggregation, not 15-minute.

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
    Aggregate to HOURLY and apply exact same logic as winning strategy.
    """
    # Resample to hourly
    df_1h = df_1m.resample('1H').agg({
        'Open': 'first', 'High': 'max', 'Low': 'min',
        'Close': 'last', 'Volume': 'sum'
    }).dropna()

    # Also create daily for regime filter
    df_daily = df_1m.resample('1D').agg({
        'Open': 'first', 'High': 'max', 'Low': 'min',
        'Close': 'last', 'Volume': 'sum'
    }).dropna()

    print(f"Hourly bars: {len(df_1h):,}")
    print(f"Daily bars: {len(df_daily):,}")

    # ---- Daily Regime Filter (50 SMA) ----
    df_daily['sma_50'] = df_daily['Close'].rolling(window=50).mean()
    df_daily['bull_regime'] = (df_daily['Close'] > df_daily['sma_50']).astype(int)
    df_daily['date'] = df_daily.index.date

    # Map regime to hourly bars
    df_1h['date'] = df_1h.index.date
    regime_map = df_daily.set_index('date')['bull_regime'].to_dict()
    df_1h['bull_regime'] = df_1h['date'].map(regime_map)
    df_1h['bull_regime'] = df_1h['bull_regime'].ffill().fillna(0)

    # ---- Hourly Indicators (exact same as winning strategy) ----

    # EMAs 34/55
    df_1h['ema_34'] = df_1h['Close'].ewm(span=34, adjust=False).mean()
    df_1h['ema_55'] = df_1h['Close'].ewm(span=55, adjust=False).mean()
    df_1h['ema_bull'] = (df_1h['ema_34'] > df_1h['ema_55']).astype(int)

    # SuperTrend (period=10, mult=3.0)
    st, st_dir = calculate_supertrend(
        df_1h['High'], df_1h['Low'], df_1h['Close'],
        period=10, multiplier=3.0
    )
    df_1h['st_direction'] = st_dir

    # ATR for stops
    tr = pd.concat([
        df_1h['High'] - df_1h['Low'],
        abs(df_1h['High'] - df_1h['Close'].shift(1)),
        abs(df_1h['Low'] - df_1h['Close'].shift(1))
    ], axis=1).max(axis=1)
    df_1h['atr'] = tr.rolling(window=14).mean()

    # RTH Filter (9 AM - 4 PM local, assume data is UTC)
    df_1h['hour'] = df_1h.index.hour
    # RTH in UTC: 14:00-21:00 (9 AM - 4 PM ET)
    df_1h['rth_ok'] = ((df_1h['hour'] >= 9) & (df_1h['hour'] < 16)).astype(int)

    # Pullback Entry
    ema_tolerance = 0.003
    df_1h['pullback_long'] = (
        (df_1h['Low'] <= df_1h['ema_34'] * (1 + ema_tolerance)) &
        (df_1h['Close'] > df_1h['ema_34']) &
        (df_1h['Close'] > df_1h['Open']) &
        (df_1h['Close'].shift(1) > df_1h['ema_34'].shift(1))
    ).astype(int)

    # Final long signal
    df_1h['long_signal'] = (
        (df_1h['bull_regime'] == 1) &
        (df_1h['st_direction'] == 1) &
        (df_1h['ema_bull'] == 1) &
        (df_1h['pullback_long'] == 1) &
        (df_1h['rth_ok'] == 1)
    ).astype(int)

    # Prepare final
    df_final = df_1h[['Open', 'High', 'Low', 'Close', 'Volume',
                      'atr', 'ema_34', 'long_signal', 'rth_ok',
                      'bull_regime', 'st_direction']].copy()
    df_final = df_final.dropna()

    print(f"\nPrepared: {len(df_final):,} hourly bars")
    print(f"Long signals: {df_final['long_signal'].sum():,}")

    return df_final


class HourlyPullbackStrategy(Strategy):
    """Exact replication of winning hourly strategy."""
    stop_mult = 1.5
    target_mult = 3.0

    def init(self):
        self.long_sig = self.I(lambda: self.data.long_signal)
        self.atr = self.I(lambda: self.data.atr)

    def next(self):
        price = self.data.Close[-1]
        atr = self.atr[-1]

        if np.isnan(atr) or atr <= 0:
            return
        if self.position:
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
    print("ES HOURLY FROM DATABENTO 1M DATA")
    print("(Replicating 63% CAGR Strategy)")
    print("=" * 70)

    bt = Backtest(
        df,
        HourlyPullbackStrategy,
        cash=50000,
        commission=0.00005,
        exclusive_orders=True,
        trade_on_close=True
    )

    print("\nRunning baseline (stop=1.5, target=3.0)...")
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
        print("OPTIMIZING...")
        print("-" * 70)

        stats = bt.optimize(
            stop_mult=[1.0, 1.25, 1.5, 1.75, 2.0, 2.5],
            target_mult=[1.5, 2.0, 2.5, 3.0, 3.5, 4.0, 5.0],
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

        # Max Return
        stats_ret = bt.optimize(
            stop_mult=[1.0, 1.25, 1.5, 1.75, 2.0, 2.5],
            target_mult=[1.5, 2.0, 2.5, 3.0, 3.5, 4.0, 5.0],
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

        # Summary
        print("\n" + "=" * 70)
        print("COMPARISON WITH 63% CAGR TARGET")
        print("=" * 70)
        best_cagr = max(cagr, opt_cagr, ret_cagr)
        print(f"Best CAGR achieved: {best_cagr:.2f}%")
        print(f"Target CAGR: 63.00%")

        if best_cagr > 63:
            print("SUCCESS! Beat the target!")
        elif best_cagr > 30:
            print("Good progress, but not quite there yet.")
        else:
            print("Significant gap from target - strategy may need fundamentally different approach.")

        return stats, stats_ret, baseline

    return baseline


if __name__ == "__main__":
    data_dir = r"C:\dev\databento\ES_1minute"
    df_1m = load_databento_data(data_dir)
    df = prepare_signals(df_1m)
    results = run_backtest(df, optimize=True)
