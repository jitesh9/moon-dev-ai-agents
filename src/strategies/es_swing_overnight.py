"""
ES Swing Trading Strategy - Overnight Holds
============================================
Modified from winning intraday strategy for swing trading.
Uses Databento 1-minute data aggregated to hourly.

Key Changes from Intraday:
- No EOD exit - hold positions overnight/multiple days
- Wider stops (2-3×ATR) to handle overnight gaps
- Larger targets (3-5×stop) for swing moves
- SuperTrend reversal as exit signal
- Relaxed RTH filter (can enter anytime)

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
    """Load Databento 1-minute data and aggregate to hourly."""
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

    df_1m = pd.DataFrame({
        'datetime': df_agg['datetime'],
        'Open': df_agg['open'],
        'High': df_agg['high'],
        'Low': df_agg['low'],
        'Close': df_agg['close'],
        'Volume': df_agg['volume']
    })
    df_1m.set_index('datetime', inplace=True)
    df_1m.sort_index(inplace=True)
    df_1m = df_1m[~df_1m.index.duplicated(keep='first')]

    # Aggregate to hourly
    df_hourly = df_1m.resample('1h').agg({
        'Open': 'first', 'High': 'max', 'Low': 'min',
        'Close': 'last', 'Volume': 'sum'
    }).dropna()

    print(f"Final: {len(df_hourly):,} hourly bars")
    print(f"Range: {df_hourly.index.min()} to {df_hourly.index.max()}")
    return df_hourly


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


def calculate_adx(high, low, close, period=14):
    """ADX indicator for trend strength."""
    tr = pd.concat([
        high - low,
        abs(high - close.shift(1)),
        abs(low - close.shift(1))
    ], axis=1).max(axis=1)

    plus_dm = high.diff()
    minus_dm = -low.diff()

    plus_dm = plus_dm.where((plus_dm > minus_dm) & (plus_dm > 0), 0)
    minus_dm = minus_dm.where((minus_dm > plus_dm) & (minus_dm > 0), 0)

    atr = tr.rolling(window=period).mean()
    plus_di = 100 * (plus_dm.rolling(window=period).mean() / atr)
    minus_di = 100 * (minus_dm.rolling(window=period).mean() / atr)

    dx = 100 * abs(plus_di - minus_di) / (plus_di + minus_di + 1e-10)
    adx = dx.rolling(window=period).mean()

    return adx


def prepare_signals(df_hourly: pd.DataFrame) -> pd.DataFrame:
    """
    Prepare signals for swing trading with overnight holds.
    """
    df = df_hourly.copy()

    # Create daily data for regime filter
    df_daily = df.resample('1D').agg({
        'Open': 'first', 'High': 'max', 'Low': 'min',
        'Close': 'last', 'Volume': 'sum'
    }).dropna()

    # Daily regime filter (50 SMA)
    df_daily['sma_50'] = df_daily['Close'].rolling(window=50).mean()
    df_daily['bull_regime'] = (df_daily['Close'] > df_daily['sma_50']).astype(int)
    df_daily['date'] = df_daily.index.date

    # Map regime to hourly
    df['date'] = df.index.date
    regime_map = df_daily.set_index('date')['bull_regime'].to_dict()
    df['bull_regime'] = df['date'].map(regime_map)
    df['bull_regime'] = df['bull_regime'].ffill().fillna(0)

    # EMAs 34/55
    df['ema_34'] = df['Close'].ewm(span=34, adjust=False).mean()
    df['ema_55'] = df['Close'].ewm(span=55, adjust=False).mean()
    df['ema_bull'] = (df['ema_34'] > df['ema_55']).astype(int)

    # SuperTrend
    st, st_dir = calculate_supertrend(df['High'], df['Low'], df['Close'], period=10, multiplier=3.0)
    df['supertrend'] = st
    df['st_direction'] = st_dir

    # Track SuperTrend direction changes for exit signals
    df['st_flip_bear'] = ((df['st_direction'] == -1) & (df['st_direction'].shift(1) == 1)).astype(int)
    df['st_flip_bull'] = ((df['st_direction'] == 1) & (df['st_direction'].shift(1) == -1)).astype(int)

    # ATR for stops (wider for swing trading)
    tr = pd.concat([
        df['High'] - df['Low'],
        abs(df['High'] - df['Close'].shift(1)),
        abs(df['Low'] - df['Close'].shift(1))
    ], axis=1).max(axis=1)
    df['atr'] = tr.rolling(window=14).mean()

    # ADX for trend strength filter
    df['adx'] = calculate_adx(df['High'], df['Low'], df['Close'], period=14)

    # Pullback Entry (same as winning strategy)
    ema_tolerance = 0.003
    df['pullback_long'] = (
        (df['Low'] <= df['ema_34'] * (1 + ema_tolerance)) &
        (df['Close'] > df['ema_34']) &
        (df['Close'] > df['Open']) &
        (df['Close'].shift(1) > df['ema_34'].shift(1))
    ).astype(int)

    df['pullback_short'] = (
        (df['High'] >= df['ema_34'] * (1 - ema_tolerance)) &
        (df['Close'] < df['ema_34']) &
        (df['Close'] < df['Open']) &
        (df['Close'].shift(1) < df['ema_34'].shift(1))
    ).astype(int)

    # SWING LONG signal (no RTH filter - can enter anytime)
    # Added ADX filter for stronger trends worth holding overnight
    df['long_signal'] = (
        (df['bull_regime'] == 1) &
        (df['st_direction'] == 1) &
        (df['ema_bull'] == 1) &
        (df['pullback_long'] == 1) &
        (df['adx'] > 20)  # Trend strength filter
    ).astype(int)

    # SWING SHORT signal (for bear markets)
    df['short_signal'] = (
        (df['bull_regime'] == 0) &
        (df['st_direction'] == -1) &
        (df['ema_bull'] == 0) &
        (df['pullback_short'] == 1) &
        (df['adx'] > 20)
    ).astype(int)

    # Prepare final dataframe
    df_final = df[['Open', 'High', 'Low', 'Close', 'Volume',
                   'atr', 'ema_34', 'supertrend', 'st_direction',
                   'st_flip_bear', 'st_flip_bull',
                   'long_signal', 'short_signal', 'adx',
                   'bull_regime', 'ema_bull']].copy()

    df_final = df_final.dropna()

    print(f"\nPrepared: {len(df_final):,} hourly bars")
    print(f"Long signals: {df_final['long_signal'].sum():,}")
    print(f"Short signals: {df_final['short_signal'].sum():,}")
    print(f"Bull regime bars: {(df_final['bull_regime']==1).sum():,}")

    return df_final


class SwingOvernightStrategy(Strategy):
    """
    ES Swing Trading Strategy with Overnight Holds.

    Entry: Pullback to EMA with SuperTrend + regime confirmation
    Exit: Stop loss, take profit, OR SuperTrend reversal
    No EOD exit - positions held overnight
    """

    # Wider stops and larger targets for swing trading
    stop_mult = 2.0       # Stop = 2.0×ATR (wider than intraday 1.25)
    target_mult = 3.0     # Target = 3.0×stop distance
    use_st_exit = 1       # 1 = exit on SuperTrend reversal, 0 = don't

    def init(self):
        self.long_sig = self.I(lambda: self.data.long_signal)
        self.short_sig = self.I(lambda: self.data.short_signal)
        self.atr = self.I(lambda: self.data.atr)
        self.st_dir = self.I(lambda: self.data.st_direction)
        self.st_flip_bear = self.I(lambda: self.data.st_flip_bear)
        self.st_flip_bull = self.I(lambda: self.data.st_flip_bull)

    def next(self):
        price = self.data.Close[-1]
        atr = self.atr[-1]

        if np.isnan(atr) or atr <= 0:
            return

        # Exit on SuperTrend reversal (trend-based exit)
        if self.position and self.use_st_exit == 1:
            if self.position.is_long and self.st_flip_bear[-1] == 1:
                self.position.close()
                return
            elif self.position.is_short and self.st_flip_bull[-1] == 1:
                self.position.close()
                return

        # Skip if already in position
        if self.position:
            return

        stop_dist = self.stop_mult * atr
        target_dist = self.target_mult * stop_dist

        # Long entry
        if self.long_sig[-1] == 1:
            self.buy(sl=price - stop_dist, tp=price + target_dist)

        # Short entry
        elif self.short_sig[-1] == 1:
            self.sell(sl=price + stop_dist, tp=price - target_dist)


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
    print("ES SWING TRADING - OVERNIGHT HOLDS")
    print("(Using Databento Data)")
    print("=" * 70)

    bt = Backtest(
        df,
        SwingOvernightStrategy,
        cash=50000,
        commission=0.00005,
        exclusive_orders=True,
        trade_on_close=True,
        hedging=True  # Allow both long and short
    )

    # Baseline run with swing trading parameters
    print("\nRunning baseline (stop=2.0, target=3.0, ST exit=ON)...")
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
    if baseline['Profit Factor']:
        print(f"  Profit Factor: {baseline['Profit Factor']:.3f}")

    if optimize:
        print("\n" + "-" * 70)
        print("OPTIMIZING FOR SWING TRADING...")
        print("-" * 70)

        # Optimize with swing trading parameter ranges
        stats = bt.optimize(
            stop_mult=[1.5, 2.0, 2.5, 3.0],
            target_mult=[2.0, 3.0, 4.0, 5.0],
            use_st_exit=[0, 1],
            maximize='Sharpe Ratio',
            constraint=lambda p: p.target_mult >= p.stop_mult,
            return_heatmap=False
        )

        opt_cagr = calculate_cagr(stats['Return [%]'], num_days)

        print(f"\nOptimized (Max Sharpe):")
        print(f"  Stop Mult:    {stats._strategy.stop_mult}")
        print(f"  Target Mult:  {stats._strategy.target_mult}")
        print(f"  ST Exit:      {'ON' if stats._strategy.use_st_exit else 'OFF'}")
        print(f"  Return:       {stats['Return [%]']:.2f}%")
        print(f"  CAGR:         {opt_cagr:.2f}%")
        print(f"  # Trades:     {stats['# Trades']}")
        print(f"  Win Rate:     {stats['Win Rate [%]']:.2f}%")
        print(f"  Sharpe:       {stats['Sharpe Ratio']:.3f}")
        print(f"  Max DD:       {stats['Max. Drawdown [%]']:.2f}%")

        # Optimize for Return
        print("\n" + "-" * 70)
        print("OPTIMIZING FOR MAX RETURN...")
        print("-" * 70)

        stats_ret = bt.optimize(
            stop_mult=[1.5, 2.0, 2.5, 3.0],
            target_mult=[2.0, 3.0, 4.0, 5.0],
            use_st_exit=[0, 1],
            maximize='Return [%]',
            constraint=lambda p: p.target_mult >= p.stop_mult,
            return_heatmap=False
        )

        ret_cagr = calculate_cagr(stats_ret['Return [%]'], num_days)

        print(f"\nOptimized (Max Return):")
        print(f"  Stop Mult:    {stats_ret._strategy.stop_mult}")
        print(f"  Target Mult:  {stats_ret._strategy.target_mult}")
        print(f"  ST Exit:      {'ON' if stats_ret._strategy.use_st_exit else 'OFF'}")
        print(f"  Return:       {stats_ret['Return [%]']:.2f}%")
        print(f"  CAGR:         {ret_cagr:.2f}%")
        print(f"  # Trades:     {stats_ret['# Trades']}")
        print(f"  Win Rate:     {stats_ret['Win Rate [%]']:.2f}%")
        print(f"  Sharpe:       {stats_ret['Sharpe Ratio']:.3f}")
        print(f"  Max DD:       {stats_ret['Max. Drawdown [%]']:.2f}%")

        # Summary
        print("\n" + "=" * 70)
        print("SWING TRADING RESULTS COMPARISON")
        print("=" * 70)
        print(f"{'Metric':<15} {'Baseline':>12} {'Max Sharpe':>12} {'Max Return':>12}")
        print("-" * 55)
        print(f"{'CAGR':<15} {cagr:>11.2f}% {opt_cagr:>11.2f}% {ret_cagr:>11.2f}%")
        print(f"{'Sharpe':<15} {baseline['Sharpe Ratio']:>12.3f} {stats['Sharpe Ratio']:>12.3f} {stats_ret['Sharpe Ratio']:>12.3f}")
        print(f"{'Max DD':<15} {baseline['Max. Drawdown [%]']:>11.2f}% {stats['Max. Drawdown [%]']:>11.2f}% {stats_ret['Max. Drawdown [%]']:>11.2f}%")
        print(f"{'Trades':<15} {baseline['# Trades']:>12} {stats['# Trades']:>12} {stats_ret['# Trades']:>12}")
        print(f"{'Win Rate':<15} {baseline['Win Rate [%]']:>11.2f}% {stats['Win Rate [%]']:>11.2f}% {stats_ret['Win Rate [%]']:>11.2f}%")

        print("\n" + "=" * 70)
        print("COMPARISON: Intraday (10% CAGR on Databento) vs Swing Trading")
        print("=" * 70)
        best_cagr = max(cagr, opt_cagr, ret_cagr)
        print(f"Best Swing CAGR: {best_cagr:.2f}%")
        print(f"Previous Intraday CAGR (Databento): 10.08%")

        if best_cagr > 10.08:
            print(f"IMPROVEMENT: +{best_cagr - 10.08:.2f}% CAGR over intraday!")
        else:
            print("Swing trading did not outperform intraday on this data.")

        return stats, stats_ret, baseline

    return baseline


if __name__ == "__main__":
    # Data path
    data_dir = r"C:\dev\databento\GLBX-20251122-DHFVWN9D6Q"

    # Load and prepare data
    df_hourly = load_databento_data(data_dir)
    df = prepare_signals(df_hourly)

    # Run backtest with optimization
    results = run_backtest(df, optimize=True)
