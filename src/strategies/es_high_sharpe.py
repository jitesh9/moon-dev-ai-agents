"""
ES High Sharpe Strategy from Databento 1-Minute Data
=====================================================
Combines key patterns from winning high Sharpe strategies:
1. TrendCapturePro: Multi-level trailing stops, ADX filter
2. SelectiveMomentumSwing: Quality pullbacks, multiple confirmations
3. DivergenceVolatilityEnhanced: Dynamic position sizing, momentum exits

Target: 2.0+ Sharpe Ratio with 100-500 trades
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


def calculate_adx(high, low, close, period=14):
    """Calculate ADX (Average Directional Index)."""
    h = high
    l = low
    c = close

    # Calculate directional movement
    dm_plus = h.diff()
    dm_minus = l.diff() * -1

    # Zero out opposite movements
    dm_plus = dm_plus.where(dm_plus > dm_minus, 0)
    dm_minus = dm_minus.where(dm_minus > dm_plus, 0)
    dm_plus = dm_plus.where(dm_plus > 0, 0)
    dm_minus = dm_minus.where(dm_minus > 0, 0)

    # Calculate True Range
    tr1 = h - l
    tr2 = abs(h - c.shift(1))
    tr3 = abs(l - c.shift(1))
    tr = pd.concat([tr1, tr2, tr3], axis=1).max(axis=1)

    # Smooth the values
    atr_val = tr.rolling(window=period).mean()
    di_plus = (dm_plus.rolling(window=period).mean() / atr_val) * 100
    di_minus = (dm_minus.rolling(window=period).mean() / atr_val) * 100

    # Calculate ADX
    dx = abs(di_plus - di_minus) / (di_plus + di_minus + 1e-10) * 100
    adx = dx.rolling(window=period).mean()

    return adx, di_plus, di_minus


def prepare_signals(df_1m: pd.DataFrame) -> pd.DataFrame:
    """
    Aggregate to HOURLY and prepare signals for high Sharpe strategy.
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

    # ---- Hourly Indicators ----

    # Triple EMA system (like TrendCapturePro)
    df_1h['ema_13'] = df_1h['Close'].ewm(span=13, adjust=False).mean()
    df_1h['ema_34'] = df_1h['Close'].ewm(span=34, adjust=False).mean()
    df_1h['ema_55'] = df_1h['Close'].ewm(span=55, adjust=False).mean()

    # EMA alignment for trend
    df_1h['ema_aligned_bull'] = (
        (df_1h['ema_13'] > df_1h['ema_34']) &
        (df_1h['ema_34'] > df_1h['ema_55']) &
        (df_1h['Close'] > df_1h['ema_13'])
    ).astype(int)

    # SuperTrend (period=10, mult=3.0)
    st, st_dir = calculate_supertrend(
        df_1h['High'], df_1h['Low'], df_1h['Close'],
        period=10, multiplier=3.0
    )
    df_1h['st_direction'] = st_dir

    # ADX for trend strength (key for high Sharpe)
    adx, di_plus, di_minus = calculate_adx(
        df_1h['High'], df_1h['Low'], df_1h['Close'], period=14
    )
    df_1h['adx'] = adx
    df_1h['di_plus'] = di_plus
    df_1h['di_minus'] = di_minus

    # ADX strong trend filter
    df_1h['adx_strong'] = (df_1h['adx'] > 25).astype(int)
    df_1h['adx_increasing'] = (df_1h['adx'] > df_1h['adx'].shift(1)).astype(int)

    # Directional strength
    df_1h['di_bull'] = (df_1h['di_plus'] > df_1h['di_minus'] * 1.2).astype(int)

    # ATR for stops
    tr = pd.concat([
        df_1h['High'] - df_1h['Low'],
        abs(df_1h['High'] - df_1h['Close'].shift(1)),
        abs(df_1h['Low'] - df_1h['Close'].shift(1))
    ], axis=1).max(axis=1)
    df_1h['atr'] = tr.rolling(window=14).mean()

    # ATR ratio for volatility confirmation
    df_1h['atr_sma'] = df_1h['atr'].rolling(window=20).mean()
    df_1h['volatility_ok'] = (df_1h['atr'] > df_1h['atr_sma'] * 1.1).astype(int)

    # Volume confirmation
    df_1h['volume_sma'] = df_1h['Volume'].rolling(window=20).mean()
    df_1h['volume_spike'] = (df_1h['Volume'] > df_1h['volume_sma'] * 1.5).astype(int)

    # RSI for momentum
    delta = df_1h['Close'].diff()
    gain = delta.where(delta > 0, 0).rolling(window=14).mean()
    loss = (-delta.where(delta < 0, 0)).rolling(window=14).mean()
    rs = gain / (loss + 1e-10)
    df_1h['rsi'] = 100 - (100 / (1 + rs))
    df_1h['rsi_good'] = ((df_1h['rsi'] > 40) & (df_1h['rsi'] < 70)).astype(int)

    # MACD for momentum confirmation
    ema_12 = df_1h['Close'].ewm(span=12, adjust=False).mean()
    ema_26 = df_1h['Close'].ewm(span=26, adjust=False).mean()
    df_1h['macd'] = ema_12 - ema_26
    df_1h['macd_signal'] = df_1h['macd'].ewm(span=9, adjust=False).mean()
    df_1h['macd_bull'] = (
        (df_1h['macd'] > df_1h['macd_signal']) &
        (df_1h['macd'] > df_1h['macd'].shift(1))
    ).astype(int)

    # RTH Filter (9 AM - 4 PM local)
    df_1h['hour'] = df_1h.index.hour
    df_1h['rth_ok'] = ((df_1h['hour'] >= 9) & (df_1h['hour'] < 16)).astype(int)

    # Pullback Entry (quality pullbacks like SelectiveMomentumSwing)
    ema_tolerance = 0.003
    df_1h['pullback_long'] = (
        (df_1h['Low'] <= df_1h['ema_34'] * (1 + ema_tolerance)) &
        (df_1h['Close'] > df_1h['ema_34']) &
        (df_1h['Close'] > df_1h['Open']) &
        (df_1h['Close'].shift(1) > df_1h['ema_34'].shift(1))
    ).astype(int)

    # Count confirmations (key for high Sharpe)
    df_1h['confirmations'] = (
        df_1h['bull_regime'] +          # 1. Daily regime
        df_1h['st_direction'].apply(lambda x: 1 if x == 1 else 0) +  # 2. SuperTrend
        df_1h['ema_aligned_bull'] +     # 3. Triple EMA aligned
        df_1h['adx_strong'] +           # 4. ADX strength
        df_1h['di_bull'] +              # 5. Directional strength
        df_1h['macd_bull'] +            # 6. MACD momentum
        df_1h['rsi_good'] +             # 7. RSI not extreme
        df_1h['volatility_ok']          # 8. Volatility adequate
    )

    # Final long signal - REQUIRE 6+ confirmations (high quality)
    df_1h['long_signal'] = (
        (df_1h['confirmations'] >= 6) &
        (df_1h['pullback_long'] == 1) &
        (df_1h['rth_ok'] == 1)
    ).astype(int)

    # Prepare final
    df_final = df_1h[['Open', 'High', 'Low', 'Close', 'Volume',
                      'atr', 'ema_34', 'long_signal', 'rth_ok',
                      'bull_regime', 'st_direction', 'adx',
                      'confirmations', 'macd', 'macd_signal', 'rsi']].copy()
    df_final = df_final.dropna()

    print(f"\nPrepared: {len(df_final):,} hourly bars")
    print(f"Long signals: {df_final['long_signal'].sum():,}")
    print(f"Avg confirmations on signal: {df_final[df_final['long_signal']==1]['confirmations'].mean():.1f}")

    return df_final


class HighSharpeStrategy(Strategy):
    """
    High Sharpe Ratio Strategy for ES Futures.

    Key features from winning strategies:
    - Multiple confirmation system (6+ factors)
    - Multi-level trailing stops (5 levels)
    - Longer hold periods for trend capture
    - Quality over quantity approach
    """
    # Entry parameters
    min_confirmations = 6

    # Stop and target parameters
    stop_mult = 1.5       # ATR multiplier for initial stop
    target_mult = 4.0     # ATR multiplier for target (bigger targets for Sharpe)

    # Multi-level trailing stops (like TrendCapturePro)
    trail_level_1 = 1.0   # Start trailing at 1% profit
    trail_level_2 = 2.0   # Tighten at 2%
    trail_level_3 = 3.0   # Tighten more at 3%
    trail_level_4 = 5.0   # Very tight at 5%

    # Maximum hold
    max_hold_bars = 100   # Hold longer for trend capture

    def init(self):
        self.long_sig = self.I(lambda: self.data.long_signal)
        self.atr = self.I(lambda: self.data.atr)
        self.adx = self.I(lambda: self.data.adx)
        self.confirmations = self.I(lambda: self.data.confirmations)
        self.st_dir = self.I(lambda: self.data.st_direction)
        self.macd = self.I(lambda: self.data.macd)
        self.macd_sig = self.I(lambda: self.data.macd_signal)
        self.rsi = self.I(lambda: self.data.rsi)

        # Trade management
        self.entry_price = None
        self.entry_bar = None
        self.initial_stop = None
        self.current_stop = None
        self.target = None
        self.trail_level = 0
        self.max_profit_seen = 0

    def next(self):
        price = self.data.Close[-1]
        atr = self.atr[-1]

        if np.isnan(atr) or atr <= 0:
            return

        # Manage existing position
        if self.position:
            self.manage_position()
            return

        # Look for new entry
        if self.long_sig[-1] == 1:
            stop_dist = self.stop_mult * atr
            target_dist = self.target_mult * atr

            self.entry_price = price
            self.entry_bar = len(self.data)
            self.initial_stop = price - stop_dist
            self.current_stop = self.initial_stop
            self.target = price + target_dist
            self.trail_level = 0
            self.max_profit_seen = 0

            self.buy(sl=self.initial_stop, tp=self.target)

    def manage_position(self):
        """Advanced position management with multi-level trailing."""
        price = self.data.Close[-1]
        atr = self.atr[-1]
        current_bar = len(self.data)

        # Calculate profit %
        pnl_pct = ((price / self.entry_price) - 1) * 100

        # Track max profit
        if pnl_pct > self.max_profit_seen:
            self.max_profit_seen = pnl_pct

        # Multi-level trailing stop system
        if pnl_pct >= self.trail_level_4 and self.trail_level < 4:
            # Level 4: Very tight stop (0.5 ATR)
            new_stop = price - (atr * 0.5)
            if new_stop > self.current_stop:
                self.current_stop = new_stop
                self.trail_level = 4

        elif pnl_pct >= self.trail_level_3 and self.trail_level < 3:
            # Level 3: Tight stop (0.8 ATR)
            new_stop = price - (atr * 0.8)
            if new_stop > self.current_stop:
                self.current_stop = new_stop
                self.trail_level = 3

        elif pnl_pct >= self.trail_level_2 and self.trail_level < 2:
            # Level 2: Medium stop (1.0 ATR)
            new_stop = price - (atr * 1.0)
            if new_stop > self.current_stop:
                self.current_stop = new_stop
                self.trail_level = 2

        elif pnl_pct >= self.trail_level_1 and self.trail_level < 1:
            # Level 1: Wide stop (1.3 ATR)
            new_stop = price - (atr * 1.3)
            if new_stop > self.current_stop:
                self.current_stop = new_stop
                self.trail_level = 1

        # Check stop hit (manual check for trailing)
        if price <= self.current_stop:
            self.position.close()
            return

        # Time-based exit
        bars_held = current_bar - self.entry_bar
        if bars_held >= self.max_hold_bars:
            self.position.close()
            return

        # Trend reversal exit (if profitable)
        if pnl_pct > 1.0:
            # SuperTrend reversal
            if self.st_dir[-1] == -1:
                self.position.close()
                return

            # MACD reversal (declining for 3 bars)
            if (len(self.macd) > 3 and
                self.macd[-1] < self.macd[-2] < self.macd[-3] and
                self.macd[-1] < self.macd_sig[-1]):
                self.position.close()
                return


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
    print("ES HIGH SHARPE STRATEGY")
    print("(Combining TrendCapturePro + SelectiveMomentumSwing patterns)")
    print("=" * 70)

    bt = Backtest(
        df,
        HighSharpeStrategy,
        cash=50000,
        commission=0.00005,
        exclusive_orders=True,
        trade_on_close=True
    )

    print("\nRunning baseline...")
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
        print("OPTIMIZING FOR SHARPE RATIO...")
        print("-" * 70)

        stats = bt.optimize(
            stop_mult=[1.0, 1.25, 1.5, 2.0],
            target_mult=[3.0, 4.0, 5.0, 6.0],
            trail_level_1=[0.8, 1.0, 1.5],
            trail_level_2=[1.5, 2.0, 2.5],
            max_hold_bars=[60, 80, 100, 150],
            maximize='Sharpe Ratio',
            constraint=lambda p: p.target_mult >= p.stop_mult * 2,
            return_heatmap=False
        )

        opt_cagr = calculate_cagr(stats['Return [%]'], num_days)

        print(f"\nOptimized (Max Sharpe):")
        print(f"  Stop: {stats._strategy.stop_mult}, Target: {stats._strategy.target_mult}")
        print(f"  Trail L1: {stats._strategy.trail_level_1}, L2: {stats._strategy.trail_level_2}")
        print(f"  Max Hold: {stats._strategy.max_hold_bars}")
        print(f"  Return: {stats['Return [%]']:.2f}%, CAGR: {opt_cagr:.2f}%")
        print(f"  Trades: {stats['# Trades']}, Win Rate: {stats['Win Rate [%]']:.2f}%")
        print(f"  Sharpe: {stats['Sharpe Ratio']:.3f}, Max DD: {stats['Max. Drawdown [%]']:.2f}%")

        # Validation
        print("\n" + "=" * 70)
        print("SHARPE RATIO VALIDATION")
        print("=" * 70)

        sharpe_target = 2.0
        trade_target = 100

        baseline_pass = baseline['Sharpe Ratio'] >= sharpe_target
        opt_pass = stats['Sharpe Ratio'] >= sharpe_target
        trade_pass = stats['# Trades'] >= trade_target

        print(f"Baseline Sharpe: {baseline['Sharpe Ratio']:.3f} {'PASS' if baseline_pass else 'FAIL'}")
        print(f"Optimized Sharpe: {stats['Sharpe Ratio']:.3f} {'PASS' if opt_pass else 'FAIL'}")
        print(f"Trade Count: {stats['# Trades']} {'PASS' if trade_pass else 'FAIL'} (target: {trade_target}+)")

        if opt_pass and trade_pass:
            print("\nSUCCESS! Strategy achieves 2.0+ Sharpe with 100+ trades!")
        elif stats['Sharpe Ratio'] >= 1.5:
            print(f"\nGood progress! Sharpe {stats['Sharpe Ratio']:.3f} is close to 2.0 target")
        else:
            print(f"\nNeeds more work. Current Sharpe: {stats['Sharpe Ratio']:.3f}")

        return stats, baseline

    return baseline


if __name__ == "__main__":
    data_dir = r"C:\dev\databento\GLBX-20251122-DHFVWN9D6Q"
    df_1m = load_databento_data(data_dir)
    df = prepare_signals(df_1m)
    results = run_backtest(df, optimize=True)
