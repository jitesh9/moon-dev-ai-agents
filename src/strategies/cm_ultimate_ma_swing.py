"""
CM Ultimate MA MTF Swing Trading Strategy
==========================================
Based on ChrisMoody's CM_Ultimate_MA_MTF_V2 TradingView indicator.

Implements all 8 MA types with multi-timeframe analysis:
1. SMA  - Simple Moving Average
2. EMA  - Exponential Moving Average
3. WMA  - Weighted Moving Average
4. Hull - Hull Moving Average (reduced lag)
5. VWMA - Volume Weighted Moving Average
6. RMA  - Relative Moving Average (Wilder's smoothing)
7. TEMA - Triple Exponential Moving Average
8. T3   - Tilson T3 (smoothest, configurable factor)

Entry Logic:
- Price crosses above fast MA (bullish) or below (bearish)
- Fast MA > Slow MA (trend confirmation)
- Daily MA direction filter (multi-timeframe)
- MA direction change (color change in original indicator)

Exit Logic:
- ATR-based stops and targets
- Trailing stop after profit threshold
- MA cross reversal exit
- Time-based exit

Author: Moon Dev AI
Data: Databento ES/GC 1-minute futures data
"""

import pandas as pd
import numpy as np
from backtesting import Backtest, Strategy
import zstandard as zstd
import io
from glob import glob
import warnings
warnings.filterwarnings('ignore')


# =============================================================================
# MOVING AVERAGE IMPLEMENTATIONS
# =============================================================================

def calc_sma(series: pd.Series, period: int) -> pd.Series:
    """Simple Moving Average"""
    return series.rolling(window=period).mean()


def calc_ema(series: pd.Series, period: int) -> pd.Series:
    """Exponential Moving Average"""
    return series.ewm(span=period, adjust=False).mean()


def calc_wma(series: pd.Series, period: int) -> pd.Series:
    """Weighted Moving Average"""
    weights = np.arange(1, period + 1)
    return series.rolling(window=period).apply(
        lambda x: np.dot(x, weights) / weights.sum(), raw=True
    )


def calc_hull(series: pd.Series, period: int) -> pd.Series:
    """Hull Moving Average - reduced lag"""
    half_period = max(1, period // 2)
    sqrt_period = max(1, int(np.sqrt(period)))

    wma_half = calc_wma(series, half_period)
    wma_full = calc_wma(series, period)
    raw_hull = 2 * wma_half - wma_full

    return calc_wma(raw_hull, sqrt_period)


def calc_rma(series: pd.Series, period: int) -> pd.Series:
    """Relative Moving Average (Wilder's smoothing)"""
    alpha = 1.0 / period
    return series.ewm(alpha=alpha, adjust=False).mean()


def calc_tema(series: pd.Series, period: int) -> pd.Series:
    """Triple Exponential Moving Average"""
    ema1 = calc_ema(series, period)
    ema2 = calc_ema(ema1, period)
    ema3 = calc_ema(ema2, period)
    return 3 * (ema1 - ema2) + ema3


def calc_tilson_t3(series: pd.Series, period: int, factor: float = 0.7) -> pd.Series:
    """
    Tilson T3 Moving Average
    factor: typically 0.7 (from indicator: factorT3 * 0.10, default 7 = 0.7)
    """
    def gd(src, length, vfactor):
        ema1 = calc_ema(src, length)
        ema2 = calc_ema(ema1, length)
        return ema1 * (1 + vfactor) - ema2 * vfactor

    gd1 = gd(series, period, factor)
    gd2 = gd(gd1, period, factor)
    gd3 = gd(gd2, period, factor)
    return gd3


def calc_vwma(close: pd.Series, volume: pd.Series, period: int) -> pd.Series:
    """Volume Weighted Moving Average"""
    return (close * volume).rolling(window=period).sum() / volume.rolling(window=period).sum()


def get_ma(series: pd.Series, period: int, ma_type: int, volume: pd.Series = None, t3_factor: float = 0.7) -> pd.Series:
    """
    Get Moving Average by type number (matching CM indicator)
    1=SMA, 2=EMA, 3=WMA, 4=HullMA, 5=VWMA, 6=RMA, 7=TEMA, 8=Tilson T3
    """
    if ma_type == 1:
        return calc_sma(series, period)
    elif ma_type == 2:
        return calc_ema(series, period)
    elif ma_type == 3:
        return calc_wma(series, period)
    elif ma_type == 4:
        return calc_hull(series, period)
    elif ma_type == 5:
        if volume is not None:
            return calc_vwma(series, volume, period)
        return calc_ema(series, period)  # Fallback if no volume
    elif ma_type == 6:
        return calc_rma(series, period)
    elif ma_type == 7:
        return calc_tema(series, period)
    elif ma_type == 8:
        return calc_tilson_t3(series, period, t3_factor)
    else:
        return calc_ema(series, period)


MA_NAMES = {
    1: 'SMA',
    2: 'EMA',
    3: 'WMA',
    4: 'Hull',
    5: 'VWMA',
    6: 'RMA',
    7: 'TEMA',
    8: 'T3'
}


# =============================================================================
# DATA LOADING
# =============================================================================

def load_databento_es(data_dir: str) -> pd.DataFrame:
    """Load Databento ES 1-minute data from zstd compressed files."""
    print(f"Loading ES data from: {data_dir}")
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
        raise ValueError("No ES data loaded!")

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

    print(f"ES: {len(df_final):,} 1-minute bars")
    return df_final


def load_gc_data(data_path: str) -> pd.DataFrame:
    """Load GC continuous contract 1-minute data."""
    print(f"Loading GC data from: {data_path}")

    df = pd.read_csv(data_path)
    df['datetime'] = pd.to_datetime(df['ts_event'])

    df_final = pd.DataFrame({
        'datetime': df['datetime'],
        'Open': df['open'],
        'High': df['high'],
        'Low': df['low'],
        'Close': df['close'],
        'Volume': df['volume']
    })

    df_final.set_index('datetime', inplace=True)
    df_final.sort_index(inplace=True)
    df_final = df_final[~df_final.index.duplicated(keep='first')]

    print(f"GC: {len(df_final):,} 1-minute bars")
    return df_final


# =============================================================================
# SIGNAL PREPARATION
# =============================================================================

def prepare_signals_from_hourly(df_hourly: pd.DataFrame, fast_ma_type: int = 2, slow_ma_type: int = 1,
                                 fast_len: int = 20, slow_len: int = 50, t3_factor: float = 0.7,
                                 smoothing: int = 2) -> pd.DataFrame:
    """
    Prepare CM Ultimate MA signals on hourly data (for pre-aggregated data).
    Uses PULLBACK-TO-MA entries for better signal quality.
    """
    df_1h = df_hourly.copy()

    # Create daily for MTF filter
    df_daily = df_1h.resample('1D').agg({
        'Open': 'first', 'High': 'max', 'Low': 'min',
        'Close': 'last', 'Volume': 'sum'
    }).dropna()

    print(f"Hourly bars: {len(df_1h):,}, Daily bars: {len(df_daily):,}")

    # ---- Calculate MAs on Hourly ----
    df_1h['fast_ma'] = get_ma(df_1h['Close'], fast_len, fast_ma_type,
                              df_1h['Volume'], t3_factor)
    df_1h['slow_ma'] = get_ma(df_1h['Close'], slow_len, slow_ma_type,
                              df_1h['Volume'], t3_factor)

    # ---- MA Direction (color change logic from CM indicator) ----
    df_1h['fast_ma_up'] = df_1h['fast_ma'] >= df_1h['fast_ma'].shift(smoothing)
    df_1h['slow_ma_up'] = df_1h['slow_ma'] >= df_1h['slow_ma'].shift(smoothing)

    # ---- PULLBACK Detection (more reliable than simple cross) ----
    # Price touched/near fast MA (within 0.5 ATR) then bounced
    df_1h['atr_temp'] = (df_1h['High'] - df_1h['Low']).rolling(14).mean()

    # Pullback to fast MA: Low touched MA zone then closed above
    df_1h['pullback_to_fast_up'] = (
        (df_1h['Low'] <= df_1h['fast_ma'] * 1.002) &  # Low near or below MA
        (df_1h['Close'] > df_1h['fast_ma']) &          # Closed above MA
        (df_1h['Close'] > df_1h['Open'])               # Bullish candle
    )

    df_1h['pullback_to_fast_dn'] = (
        (df_1h['High'] >= df_1h['fast_ma'] * 0.998) &  # High near or above MA
        (df_1h['Close'] < df_1h['fast_ma']) &          # Closed below MA
        (df_1h['Close'] < df_1h['Open'])               # Bearish candle
    )

    # ---- MA Crossover ----
    df_1h['ma_cross_up'] = (df_1h['fast_ma'] > df_1h['slow_ma']) & (df_1h['fast_ma'].shift(1) <= df_1h['slow_ma'].shift(1))
    df_1h['ma_cross_dn'] = (df_1h['fast_ma'] < df_1h['slow_ma']) & (df_1h['fast_ma'].shift(1) >= df_1h['slow_ma'].shift(1))

    # ---- Trend State ----
    df_1h['fast_above_slow'] = df_1h['fast_ma'] > df_1h['slow_ma']
    df_1h['price_above_fast'] = df_1h['Close'] > df_1h['fast_ma']
    df_1h['price_above_slow'] = df_1h['Close'] > df_1h['slow_ma']

    # ---- RSI for momentum confirmation ----
    delta = df_1h['Close'].diff()
    gain = delta.where(delta > 0, 0).rolling(window=14).mean()
    loss = (-delta.where(delta < 0, 0)).rolling(window=14).mean()
    rs = gain / (loss + 1e-10)
    df_1h['rsi'] = 100 - (100 / (1 + rs))

    # RSI conditions for quality entries
    df_1h['rsi_bullish'] = (df_1h['rsi'] > 40) & (df_1h['rsi'] < 70)  # Not oversold, not overbought
    df_1h['rsi_bearish'] = (df_1h['rsi'] > 30) & (df_1h['rsi'] < 60)  # Not overbought for shorts

    # ---- Daily Trend Filter (MTF) ----
    df_daily['daily_ma'] = get_ma(df_daily['Close'], 50, 1)  # 50 SMA on daily
    df_daily['daily_trend_up'] = df_daily['Close'] > df_daily['daily_ma']
    df_daily['date'] = df_daily.index.date

    # Map daily trend to hourly
    df_1h['date'] = df_1h.index.date
    daily_trend_map = df_daily.set_index('date')['daily_trend_up'].to_dict()
    df_1h['daily_trend_up'] = df_1h['date'].map(daily_trend_map)
    df_1h['daily_trend_up'] = df_1h['daily_trend_up'].ffill().fillna(True)

    # ---- ATR for stops ----
    tr = pd.concat([
        df_1h['High'] - df_1h['Low'],
        abs(df_1h['High'] - df_1h['Close'].shift(1)),
        abs(df_1h['Low'] - df_1h['Close'].shift(1))
    ], axis=1).max(axis=1)
    df_1h['atr'] = tr.rolling(window=14).mean()

    # ---- Trading Hours Filter ----
    df_1h['hour'] = df_1h.index.hour
    df_1h['rth_ok'] = ((df_1h['hour'] >= 9) & (df_1h['hour'] < 16)).astype(int)

    # ---- PULLBACK Entry Signals (more selective than simple cross) ----
    # LONG: Pullback to fast MA + trend up + fast > slow + daily trend up + RSI confirmation
    df_1h['long_signal'] = (
        df_1h['pullback_to_fast_up'] &
        df_1h['fast_ma_up'] &
        df_1h['fast_above_slow'] &
        df_1h['daily_trend_up'] &
        df_1h['rsi_bullish'] &
        (df_1h['rth_ok'] == 1)
    ).astype(int)

    # SHORT: Pullback to fast MA + trend down + fast < slow + daily trend down + RSI
    df_1h['short_signal'] = (
        df_1h['pullback_to_fast_dn'] &
        (~df_1h['fast_ma_up']) &
        (~df_1h['fast_above_slow']) &
        (~df_1h['daily_trend_up']) &
        df_1h['rsi_bearish'] &
        (df_1h['rth_ok'] == 1)
    ).astype(int)

    # Prepare final dataframe
    df_final = df_1h[['Open', 'High', 'Low', 'Close', 'Volume', 'atr',
                       'fast_ma', 'slow_ma', 'fast_ma_up', 'fast_above_slow',
                       'long_signal', 'short_signal', 'rth_ok', 'rsi',
                       'pullback_to_fast_up', 'pullback_to_fast_dn',
                       'ma_cross_up', 'ma_cross_dn']].copy()
    df_final = df_final.dropna()

    long_count = df_final['long_signal'].sum()
    short_count = df_final['short_signal'].sum()
    print(f"Signals - Long: {long_count}, Short: {short_count}")

    return df_final


def prepare_signals(df_1m: pd.DataFrame, fast_ma_type: int = 2, slow_ma_type: int = 1,
                    fast_len: int = 20, slow_len: int = 50, t3_factor: float = 0.7,
                    smoothing: int = 2) -> pd.DataFrame:
    """
    Prepare CM Ultimate MA signals on hourly data with daily trend filter.
    Uses PULLBACK-TO-MA entries for better signal quality.

    Parameters:
    - fast_ma_type: 1-8 (MA type for fast MA)
    - slow_ma_type: 1-8 (MA type for slow MA)
    - fast_len: Fast MA period (default 20)
    - slow_len: Slow MA period (default 50)
    - t3_factor: Tilson T3 factor (default 0.7)
    - smoothing: Direction smoothing period (default 2)
    """
    # Resample to hourly
    df_1h = df_1m.resample('1H').agg({
        'Open': 'first', 'High': 'max', 'Low': 'min',
        'Close': 'last', 'Volume': 'sum'
    }).dropna()

    return prepare_signals_from_hourly(df_1h, fast_ma_type, slow_ma_type,
                                       fast_len, slow_len, t3_factor, smoothing)


# =============================================================================
# STRATEGY CLASS
# =============================================================================

class CMUltimateMAStrategy(Strategy):
    """
    CM Ultimate MA MTF Swing Strategy

    Based on ChrisMoody's multi-timeframe moving average indicator.
    Uses price cross MA + MA direction + dual MA confirmation.
    """
    # Risk parameters
    stop_mult = 1.5       # ATR multiplier for stop
    target_mult = 2.5     # ATR multiplier for target

    # Trailing parameters
    trail_start_pct = 1.5   # Start trailing at 1.5% profit
    trail_atr_mult = 1.0    # Trail by 1.0 ATR

    # Hold parameters
    max_hold_bars = 60      # Max hold time

    # Trade direction
    long_only = True        # Set to False to enable shorts

    def init(self):
        self.long_sig = self.I(lambda: self.data.long_signal)
        self.short_sig = self.I(lambda: self.data.short_signal)
        self.atr = self.I(lambda: self.data.atr)
        self.fast_ma = self.I(lambda: self.data.fast_ma)
        self.slow_ma = self.I(lambda: self.data.slow_ma)
        self.fast_ma_up = self.I(lambda: self.data.fast_ma_up)
        self.fast_above_slow = self.I(lambda: self.data.fast_above_slow)
        self.ma_cross_dn = self.I(lambda: self.data.ma_cross_dn)

        # Trade management
        self.entry_price = None
        self.entry_bar = None
        self.stop_price = None
        self.target_price = None
        self.trailing_active = False
        self.is_long = None

    def next(self):
        price = self.data.Close[-1]
        atr = self.atr[-1]

        if np.isnan(atr) or atr <= 0:
            return

        # Manage existing position
        if self.position:
            self.manage_position()
            return

        # Look for entry signals
        if self.long_sig[-1] == 1:
            self.enter_long(price, atr)
        elif not self.long_only and self.short_sig[-1] == 1:
            self.enter_short(price, atr)

    def enter_long(self, price, atr):
        stop_dist = self.stop_mult * atr
        target_dist = self.target_mult * atr

        self.entry_price = price
        self.entry_bar = len(self.data)
        self.stop_price = price - stop_dist
        self.target_price = price + target_dist
        self.trailing_active = False
        self.is_long = True

        self.buy(sl=self.stop_price, tp=self.target_price)

    def enter_short(self, price, atr):
        stop_dist = self.stop_mult * atr
        target_dist = self.target_mult * atr

        self.entry_price = price
        self.entry_bar = len(self.data)
        self.stop_price = price + stop_dist
        self.target_price = price - target_dist
        self.trailing_active = False
        self.is_long = False

        self.sell(sl=self.stop_price, tp=self.target_price)

    def manage_position(self):
        """Position management with trailing stop and MA-based exits."""
        price = self.data.Close[-1]
        atr = self.atr[-1]
        current_bar = len(self.data)

        if self.is_long:
            pnl_pct = ((price / self.entry_price) - 1) * 100
        else:
            pnl_pct = ((self.entry_price / price) - 1) * 100

        # Start trailing after threshold
        if pnl_pct >= self.trail_start_pct:
            self.trailing_active = True
            if self.is_long:
                new_stop = price - (self.trail_atr_mult * atr)
                if new_stop > self.stop_price:
                    self.stop_price = new_stop
            else:
                new_stop = price + (self.trail_atr_mult * atr)
                if new_stop < self.stop_price:
                    self.stop_price = new_stop

        # Check manual stop (for trailing)
        if self.is_long and price <= self.stop_price:
            self.position.close()
            return
        elif not self.is_long and price >= self.stop_price:
            self.position.close()
            return

        # Time exit
        bars_held = current_bar - self.entry_bar
        if bars_held >= self.max_hold_bars:
            self.position.close()
            return

        # MA cross exit (trend reversal)
        if self.is_long and self.ma_cross_dn[-1]:
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


# =============================================================================
# BACKTEST RUNNER
# =============================================================================

def run_single_backtest(df: pd.DataFrame, capital: float = 500000,
                        commission: float = 0.00005, margin: float = 0.1):
    """Run a single backtest and return results."""
    bt = Backtest(
        df,
        CMUltimateMAStrategy,
        cash=capital,
        commission=commission,
        exclusive_orders=True,
        trade_on_close=True,
        margin=margin
    )

    return bt.run()


def test_ma_combinations(df_1m: pd.DataFrame, instrument: str = "ES",
                         capital: float = 500000):
    """
    Test all MA type combinations and find the best performer.
    Tests: fast_ma_type x slow_ma_type (8 x 8 = 64 combinations)
    """
    print("\n" + "=" * 80)
    print(f"CM ULTIMATE MA MTF - TESTING ALL MA COMBINATIONS ON {instrument}")
    print("=" * 80)

    results = []

    # Test key combinations (not all 64, focus on most promising)
    # Fast MAs: EMA, Hull, TEMA (responsive)
    # Slow MAs: SMA, EMA, RMA (stable)
    fast_types = [2, 4, 7, 8]  # EMA, Hull, TEMA, T3
    slow_types = [1, 2, 6]     # SMA, EMA, RMA

    total_tests = len(fast_types) * len(slow_types)
    test_num = 0

    for fast_type in fast_types:
        for slow_type in slow_types:
            test_num += 1
            fast_name = MA_NAMES[fast_type]
            slow_name = MA_NAMES[slow_type]

            print(f"\n[{test_num}/{total_tests}] Testing Fast={fast_name}(20) / Slow={slow_name}(50)...")

            try:
                df = prepare_signals(df_1m, fast_ma_type=fast_type, slow_ma_type=slow_type)
                stats = run_single_backtest(df, capital=capital)

                num_days = (df.index.max() - df.index.min()).days
                cagr = calculate_cagr(stats['Return [%]'], num_days)

                result = {
                    'fast_ma': fast_name,
                    'slow_ma': slow_name,
                    'fast_type': fast_type,
                    'slow_type': slow_type,
                    'return_pct': stats['Return [%]'],
                    'cagr': cagr,
                    'sharpe': stats['Sharpe Ratio'],
                    'max_dd': stats['Max. Drawdown [%]'],
                    'trades': stats['# Trades'],
                    'win_rate': stats['Win Rate [%]'],
                    'calmar': cagr / abs(stats['Max. Drawdown [%]']) if stats['Max. Drawdown [%]'] != 0 else 0
                }
                results.append(result)

                print(f"   Return: {stats['Return [%]']:.1f}%, Sharpe: {stats['Sharpe Ratio']:.3f}, "
                      f"Trades: {stats['# Trades']}, Win: {stats['Win Rate [%]']:.1f}%")

            except Exception as e:
                print(f"   ERROR: {e}")

    # Sort by Sharpe ratio
    results_df = pd.DataFrame(results)
    results_df = results_df.sort_values('sharpe', ascending=False)

    print("\n" + "=" * 80)
    print(f"RESULTS SUMMARY - {instrument}")
    print("=" * 80)
    print("\nTop 5 by Sharpe Ratio:")
    print("-" * 80)

    for i, row in results_df.head(5).iterrows():
        print(f"{row['fast_ma']}/{row['slow_ma']}: Sharpe={row['sharpe']:.3f}, "
              f"Return={row['return_pct']:.1f}%, CAGR={row['cagr']:.1f}%, "
              f"MaxDD={row['max_dd']:.1f}%, Trades={row['trades']}")

    return results_df


def run_optimized_backtest(df_1m: pd.DataFrame, fast_type: int, slow_type: int,
                           instrument: str = "ES", capital: float = 500000):
    """Run backtest with optimization on the best MA combination."""
    print("\n" + "=" * 80)
    print(f"OPTIMIZING {MA_NAMES[fast_type]}/{MA_NAMES[slow_type]} ON {instrument}")
    print("=" * 80)

    df = prepare_signals(df_1m, fast_ma_type=fast_type, slow_ma_type=slow_type)

    bt = Backtest(
        df,
        CMUltimateMAStrategy,
        cash=capital,
        commission=0.00005,
        exclusive_orders=True,
        trade_on_close=True,
        margin=0.1
    )

    # Run baseline
    baseline = bt.run()
    num_days = (df.index.max() - df.index.min()).days
    baseline_cagr = calculate_cagr(baseline['Return [%]'], num_days)

    print(f"\nBaseline Results:")
    print(f"  Return: {baseline['Return [%]']:.2f}%, CAGR: {baseline_cagr:.2f}%")
    print(f"  Sharpe: {baseline['Sharpe Ratio']:.3f}, Max DD: {baseline['Max. Drawdown [%]']:.2f}%")
    print(f"  Trades: {baseline['# Trades']}, Win Rate: {baseline['Win Rate [%]']:.2f}%")

    # Optimize
    print("\nOptimizing parameters...")
    optimized = bt.optimize(
        stop_mult=[1.0, 1.5, 2.0, 2.5],
        target_mult=[2.0, 2.5, 3.0, 4.0],
        trail_start_pct=[1.0, 1.5, 2.0],
        trail_atr_mult=[0.8, 1.0, 1.5],
        max_hold_bars=[40, 60, 80],
        maximize='Sharpe Ratio',
        constraint=lambda p: p.target_mult >= p.stop_mult,
        return_heatmap=False
    )

    opt_cagr = calculate_cagr(optimized['Return [%]'], num_days)

    print(f"\nOptimized Results:")
    print(f"  Stop: {optimized._strategy.stop_mult}, Target: {optimized._strategy.target_mult}")
    print(f"  Trail Start: {optimized._strategy.trail_start_pct}%, ATR: {optimized._strategy.trail_atr_mult}")
    print(f"  Max Hold: {optimized._strategy.max_hold_bars}")
    print(f"  Return: {optimized['Return [%]']:.2f}%, CAGR: {opt_cagr:.2f}%")
    print(f"  Sharpe: {optimized['Sharpe Ratio']:.3f}, Max DD: {optimized['Max. Drawdown [%]']:.2f}%")
    print(f"  Trades: {optimized['# Trades']}, Win Rate: {optimized['Win Rate [%]']:.2f}%")

    return baseline, optimized


def load_hourly_csv(filepath: str, instrument: str = "ES") -> pd.DataFrame:
    """Load pre-aggregated hourly CSV data."""
    print(f"Loading {instrument} hourly data from: {filepath}")

    df = pd.read_csv(filepath)

    # Handle various datetime column names
    date_col = None
    for col in ['datetime', 'date', 'Date', 'Datetime', 'timestamp']:
        if col in df.columns:
            date_col = col
            break

    if date_col is None:
        # Try first column as index
        df = pd.read_csv(filepath, index_col=0, parse_dates=True)
    else:
        df['datetime'] = pd.to_datetime(df[date_col])
        df.set_index('datetime', inplace=True)

    # Standardize column names
    col_map = {}
    for col in df.columns:
        col_lower = col.lower()
        if 'open' in col_lower:
            col_map[col] = 'Open'
        elif 'high' in col_lower:
            col_map[col] = 'High'
        elif 'low' in col_lower:
            col_map[col] = 'Low'
        elif 'close' in col_lower:
            col_map[col] = 'Close'
        elif 'vol' in col_lower:
            col_map[col] = 'Volume'

    df.rename(columns=col_map, inplace=True)
    df = df[['Open', 'High', 'Low', 'Close', 'Volume']].dropna()
    df.sort_index(inplace=True)

    print(f"{instrument}: {len(df):,} hourly bars from {df.index.min()} to {df.index.max()}")
    return df


def test_ma_combinations_hourly(df_hourly: pd.DataFrame, instrument: str = "ES",
                                capital: float = 500000):
    """
    Test all MA type combinations on pre-aggregated hourly data.
    """
    print("\n" + "=" * 80)
    print(f"CM ULTIMATE MA MTF - TESTING ALL MA COMBINATIONS ON {instrument}")
    print("=" * 80)

    results = []

    # Test key combinations
    fast_types = [2, 4, 7, 8]  # EMA, Hull, TEMA, T3
    slow_types = [1, 2, 6]     # SMA, EMA, RMA

    total_tests = len(fast_types) * len(slow_types)
    test_num = 0

    for fast_type in fast_types:
        for slow_type in slow_types:
            test_num += 1
            fast_name = MA_NAMES[fast_type]
            slow_name = MA_NAMES[slow_type]

            print(f"\n[{test_num}/{total_tests}] Testing Fast={fast_name}(20) / Slow={slow_name}(50)...")

            try:
                df = prepare_signals_from_hourly(df_hourly, fast_ma_type=fast_type, slow_ma_type=slow_type)
                stats = run_single_backtest(df, capital=capital)

                num_days = (df.index.max() - df.index.min()).days
                cagr = calculate_cagr(stats['Return [%]'], num_days)

                result = {
                    'fast_ma': fast_name,
                    'slow_ma': slow_name,
                    'fast_type': fast_type,
                    'slow_type': slow_type,
                    'return_pct': stats['Return [%]'],
                    'cagr': cagr,
                    'sharpe': stats['Sharpe Ratio'],
                    'max_dd': stats['Max. Drawdown [%]'],
                    'trades': stats['# Trades'],
                    'win_rate': stats['Win Rate [%]'],
                    'calmar': cagr / abs(stats['Max. Drawdown [%]']) if stats['Max. Drawdown [%]'] != 0 else 0
                }
                results.append(result)

                print(f"   Return: {stats['Return [%]']:.1f}%, Sharpe: {stats['Sharpe Ratio']:.3f}, "
                      f"Trades: {stats['# Trades']}, Win: {stats['Win Rate [%]']:.1f}%")

            except Exception as e:
                print(f"   ERROR: {e}")

    # Sort by Sharpe ratio
    results_df = pd.DataFrame(results)
    results_df = results_df.sort_values('sharpe', ascending=False)

    print("\n" + "=" * 80)
    print(f"RESULTS SUMMARY - {instrument}")
    print("=" * 80)
    print("\nTop 5 by Sharpe Ratio:")
    print("-" * 80)

    for i, row in results_df.head(5).iterrows():
        print(f"{row['fast_ma']}/{row['slow_ma']}: Sharpe={row['sharpe']:.3f}, "
              f"Return={row['return_pct']:.1f}%, CAGR={row['cagr']:.1f}%, "
              f"MaxDD={row['max_dd']:.1f}%, Trades={row['trades']}")

    return results_df


def run_optimized_backtest_hourly(df_hourly: pd.DataFrame, fast_type: int, slow_type: int,
                                   instrument: str = "ES", capital: float = 500000):
    """Run backtest with optimization on pre-aggregated hourly data."""
    print("\n" + "=" * 80)
    print(f"OPTIMIZING {MA_NAMES[fast_type]}/{MA_NAMES[slow_type]} ON {instrument}")
    print("=" * 80)

    df = prepare_signals_from_hourly(df_hourly, fast_ma_type=fast_type, slow_ma_type=slow_type)

    bt = Backtest(
        df,
        CMUltimateMAStrategy,
        cash=capital,
        commission=0.00005,
        exclusive_orders=True,
        trade_on_close=True,
        margin=0.1
    )

    # Run baseline
    baseline = bt.run()
    num_days = (df.index.max() - df.index.min()).days
    baseline_cagr = calculate_cagr(baseline['Return [%]'], num_days)

    print(f"\nBaseline Results:")
    print(f"  Return: {baseline['Return [%]']:.2f}%, CAGR: {baseline_cagr:.2f}%")
    print(f"  Sharpe: {baseline['Sharpe Ratio']:.3f}, Max DD: {baseline['Max. Drawdown [%]']:.2f}%")
    print(f"  Trades: {baseline['# Trades']}, Win Rate: {baseline['Win Rate [%]']:.2f}%")

    # Optimize
    print("\nOptimizing parameters...")
    optimized = bt.optimize(
        stop_mult=[1.0, 1.5, 2.0, 2.5],
        target_mult=[2.0, 2.5, 3.0, 4.0],
        trail_start_pct=[1.0, 1.5, 2.0],
        trail_atr_mult=[0.8, 1.0, 1.5],
        max_hold_bars=[40, 60, 80],
        maximize='Sharpe Ratio',
        constraint=lambda p: p.target_mult >= p.stop_mult,
        return_heatmap=False
    )

    opt_cagr = calculate_cagr(optimized['Return [%]'], num_days)

    print(f"\nOptimized Results:")
    print(f"  Stop: {optimized._strategy.stop_mult}, Target: {optimized._strategy.target_mult}")
    print(f"  Trail Start: {optimized._strategy.trail_start_pct}%, ATR: {optimized._strategy.trail_atr_mult}")
    print(f"  Max Hold: {optimized._strategy.max_hold_bars}")
    print(f"  Return: {optimized['Return [%]']:.2f}%, CAGR: {opt_cagr:.2f}%")
    print(f"  Sharpe: {optimized['Sharpe Ratio']:.3f}, Max DD: {optimized['Max. Drawdown [%]']:.2f}%")
    print(f"  Trades: {optimized['# Trades']}, Win Rate: {optimized['Win Rate [%]']:.2f}%")

    return baseline, optimized


# =============================================================================
# MAIN
# =============================================================================

if __name__ == "__main__":
    # Data paths - pre-aggregated hourly data
    ES_HOURLY_PATH = r"C:\dev\moondev-ai-agents\src\data\rbi\ES-1H.csv"
    GC_1M_PATH = r"C:\dev\databento\GC_1minute\gc_continuous_1m.csv"

    print("=" * 80)
    print("CM ULTIMATE MA MTF SWING STRATEGY")
    print("Based on ChrisMoody's TradingView Indicator")
    print("Pullback-to-MA Entry Logic with RSI Confirmation")
    print("=" * 80)

    # Test on ES (hourly data)
    print("\n\nLOADING ES HOURLY DATA...")
    try:
        df_es_hourly = load_hourly_csv(ES_HOURLY_PATH, "ES")
        es_results = test_ma_combinations_hourly(df_es_hourly, instrument="ES")

        # Get best combination and optimize
        if len(es_results) > 0:
            best_es = es_results.iloc[0]
            print(f"\nBest ES combination: {best_es['fast_ma']}/{best_es['slow_ma']}")
            es_baseline, es_optimized = run_optimized_backtest_hourly(
                df_es_hourly, int(best_es['fast_type']), int(best_es['slow_type']), "ES"
            )
    except Exception as e:
        print(f"ES Error: {e}")
        import traceback
        traceback.print_exc()

    # Test on GC (1-minute data -> aggregate to hourly)
    print("\n\nLOADING GC DATA...")
    try:
        df_gc = load_gc_data(GC_1M_PATH)
        gc_results = test_ma_combinations(df_gc, instrument="GC")

        # Get best combination and optimize
        if len(gc_results) > 0:
            best_gc = gc_results.iloc[0]
            print(f"\nBest GC combination: {best_gc['fast_ma']}/{best_gc['slow_ma']}")
            gc_baseline, gc_optimized = run_optimized_backtest(
                df_gc, int(best_gc['fast_type']), int(best_gc['slow_type']), "GC"
            )
    except Exception as e:
        print(f"GC Error: {e}")
        import traceback
        traceback.print_exc()

    print("\n" + "=" * 80)
    print("TESTING COMPLETE")
    print("=" * 80)
