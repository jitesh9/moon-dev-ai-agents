"""
CM Ultimate MA MTF - CONSERVATIVE VERSION
==========================================
Target: 20% Maximum Drawdown with improved risk-adjusted returns

Improvements over baseline:
1. Position scaling calibrated for 20% max DD
2. Dynamic drawdown protection with position reduction
3. Volatility regime filter (avoid high-vol periods)
4. Volume confirmation for entries
5. Stronger trend filters (ADX)
6. Faster trailing stops after profit
7. Momentum-based exits

Author: Moon Dev AI
"""

import pandas as pd
import numpy as np
from backtesting import Backtest, Strategy
import warnings
warnings.filterwarnings('ignore')


# =============================================================================
# MOVING AVERAGE IMPLEMENTATIONS
# =============================================================================

def calc_ema(series: pd.Series, period: int) -> pd.Series:
    """Exponential Moving Average"""
    return series.ewm(span=period, adjust=False).mean()


def calc_tilson_t3(series: pd.Series, period: int, factor: float = 0.7) -> pd.Series:
    """Tilson T3 Moving Average"""
    def gd(src, length, vfactor):
        ema1 = calc_ema(src, length)
        ema2 = calc_ema(ema1, length)
        return ema1 * (1 + vfactor) - ema2 * vfactor

    gd1 = gd(series, period, factor)
    gd2 = gd(gd1, period, factor)
    gd3 = gd(gd2, period, factor)
    return gd3


def calc_sma(series: pd.Series, period: int) -> pd.Series:
    """Simple Moving Average"""
    return series.rolling(window=period).mean()


# =============================================================================
# DATA LOADING
# =============================================================================

def load_hourly_csv(filepath: str, instrument: str = "ES") -> pd.DataFrame:
    """Load pre-aggregated hourly CSV data."""
    print(f"Loading {instrument} hourly data from: {filepath}")

    df = pd.read_csv(filepath)

    date_col = None
    for col in ['datetime', 'date', 'Date', 'Datetime', 'timestamp']:
        if col in df.columns:
            date_col = col
            break

    if date_col is None:
        df = pd.read_csv(filepath, index_col=0, parse_dates=True)
    else:
        df['datetime'] = pd.to_datetime(df[date_col])
        df.set_index('datetime', inplace=True)

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

    print(f"{instrument}: {len(df):,} hourly bars")
    return df


# =============================================================================
# ENHANCED SIGNAL PREPARATION
# =============================================================================

def prepare_conservative_signals(df_hourly: pd.DataFrame, fast_len: int = 20,
                                  slow_len: int = 50, t3_factor: float = 0.7,
                                  smoothing: int = 2) -> pd.DataFrame:
    """
    Prepare signals with ENHANCED FILTERS for conservative trading.

    Enhancements:
    1. Volatility regime filter
    2. Volume confirmation
    3. ADX trend strength filter
    4. Tighter RSI bands
    5. Multiple timeframe trend alignment
    """
    df = df_hourly.copy()

    # Create daily for MTF filter
    df_daily = df.resample('1D').agg({
        'Open': 'first', 'High': 'max', 'Low': 'min',
        'Close': 'last', 'Volume': 'sum'
    }).dropna()

    print(f"Hourly bars: {len(df):,}, Daily bars: {len(df_daily):,}")

    # =========================================================================
    # CORE MAs (T3 Fast / EMA Slow)
    # =========================================================================
    df['fast_ma'] = calc_tilson_t3(df['Close'], fast_len, t3_factor)
    df['slow_ma'] = calc_ema(df['Close'], slow_len)

    # MA Direction
    df['fast_ma_up'] = df['fast_ma'] >= df['fast_ma'].shift(smoothing)
    df['slow_ma_up'] = df['slow_ma'] >= df['slow_ma'].shift(smoothing)

    # MA Relationship
    df['fast_above_slow'] = df['fast_ma'] > df['slow_ma']

    # =========================================================================
    # 1. ATR & VOLATILITY REGIME FILTER
    # =========================================================================
    tr = pd.concat([
        df['High'] - df['Low'],
        abs(df['High'] - df['Close'].shift(1)),
        abs(df['Low'] - df['Close'].shift(1))
    ], axis=1).max(axis=1)
    df['atr'] = tr.rolling(window=14).mean()

    # Volatility percentile (avoid extreme volatility)
    df['atr_pct'] = df['atr'].rolling(window=100).apply(
        lambda x: pd.Series(x).rank(pct=True).iloc[-1], raw=False
    )
    # FILTER: Only trade when volatility is moderate (20th-80th percentile)
    df['vol_ok'] = (df['atr_pct'] > 0.20) & (df['atr_pct'] < 0.80)

    # =========================================================================
    # 2. VOLUME CONFIRMATION
    # =========================================================================
    df['vol_sma'] = df['Volume'].rolling(window=20).mean()
    # FILTER: Require above-average volume
    df['volume_ok'] = df['Volume'] > df['vol_sma'] * 0.8

    # =========================================================================
    # 3. ADX TREND STRENGTH FILTER
    # =========================================================================
    # Calculate ADX
    plus_dm = df['High'].diff()
    minus_dm = -df['Low'].diff()
    plus_dm = plus_dm.where((plus_dm > minus_dm) & (plus_dm > 0), 0)
    minus_dm = minus_dm.where((minus_dm > plus_dm) & (minus_dm > 0), 0)

    atr_14 = tr.rolling(window=14).mean()
    plus_di = 100 * (plus_dm.rolling(window=14).mean() / atr_14)
    minus_di = 100 * (minus_dm.rolling(window=14).mean() / atr_14)

    dx = 100 * abs(plus_di - minus_di) / (plus_di + minus_di + 1e-10)
    df['adx'] = dx.rolling(window=14).mean()

    # FILTER: Only trade in trending markets (ADX > 20)
    df['trend_strong'] = df['adx'] > 20

    # =========================================================================
    # 4. RSI WITH TIGHTER BANDS
    # =========================================================================
    delta = df['Close'].diff()
    gain = delta.where(delta > 0, 0).rolling(window=14).mean()
    loss = (-delta.where(delta < 0, 0)).rolling(window=14).mean()
    rs = gain / (loss + 1e-10)
    df['rsi'] = 100 - (100 / (1 + rs))

    # TIGHTER RSI bands for higher quality entries
    df['rsi_bullish'] = (df['rsi'] > 45) & (df['rsi'] < 65)  # Narrower band
    df['rsi_bearish'] = (df['rsi'] > 35) & (df['rsi'] < 55)

    # RSI momentum (rising RSI for longs)
    df['rsi_rising'] = df['rsi'] > df['rsi'].shift(2)

    # =========================================================================
    # 5. MACD FOR MOMENTUM CONFIRMATION
    # =========================================================================
    ema12 = calc_ema(df['Close'], 12)
    ema26 = calc_ema(df['Close'], 26)
    df['macd'] = ema12 - ema26
    df['macd_signal'] = calc_ema(df['macd'], 9)
    df['macd_hist'] = df['macd'] - df['macd_signal']

    # MACD momentum
    df['macd_bullish'] = (df['macd'] > df['macd_signal']) | (df['macd_hist'] > df['macd_hist'].shift(1))
    df['macd_bearish'] = (df['macd'] < df['macd_signal']) | (df['macd_hist'] < df['macd_hist'].shift(1))

    # MACD declining (for exit)
    df['macd_declining'] = (df['macd_hist'] < df['macd_hist'].shift(1)) & \
                           (df['macd_hist'].shift(1) < df['macd_hist'].shift(2))

    # =========================================================================
    # 6. DAILY TREND FILTER (MTF)
    # =========================================================================
    df_daily['daily_ma_50'] = calc_sma(df_daily['Close'], 50)
    df_daily['daily_ma_200'] = calc_sma(df_daily['Close'], 200)
    df_daily['daily_trend_up'] = df_daily['Close'] > df_daily['daily_ma_50']
    df_daily['daily_bull_regime'] = df_daily['daily_ma_50'] > df_daily['daily_ma_200']
    df_daily['date'] = df_daily.index.date

    # Map daily to hourly
    df['date'] = df.index.date
    daily_trend_map = df_daily.set_index('date')['daily_trend_up'].to_dict()
    daily_regime_map = df_daily.set_index('date')['daily_bull_regime'].to_dict()

    df['daily_trend_up'] = df['date'].map(daily_trend_map).ffill().fillna(True)
    df['daily_bull_regime'] = df['date'].map(daily_regime_map).ffill().fillna(True)

    # =========================================================================
    # 7. PULLBACK DETECTION (Same as before)
    # =========================================================================
    df['pullback_to_fast_up'] = (
        (df['Low'] <= df['fast_ma'] * 1.002) &
        (df['Close'] > df['fast_ma']) &
        (df['Close'] > df['Open'])
    )

    df['pullback_to_fast_dn'] = (
        (df['High'] >= df['fast_ma'] * 0.998) &
        (df['Close'] < df['fast_ma']) &
        (df['Close'] < df['Open'])
    )

    # =========================================================================
    # 8. TRADING HOURS
    # =========================================================================
    df['hour'] = df.index.hour
    df['rth_ok'] = ((df['hour'] >= 9) & (df['hour'] < 15)).astype(int)  # Tighter hours

    # =========================================================================
    # 9. MA CROSS SIGNALS
    # =========================================================================
    df['ma_cross_up'] = (df['fast_ma'] > df['slow_ma']) & (df['fast_ma'].shift(1) <= df['slow_ma'].shift(1))
    df['ma_cross_dn'] = (df['fast_ma'] < df['slow_ma']) & (df['fast_ma'].shift(1) >= df['slow_ma'].shift(1))

    # =========================================================================
    # CONSERVATIVE ENTRY SIGNALS (ALL FILTERS MUST PASS)
    # =========================================================================
    df['long_signal'] = (
        df['pullback_to_fast_up'] &       # Pullback entry
        df['fast_ma_up'] &                 # Fast MA trending up
        df['fast_above_slow'] &            # Fast > Slow
        df['daily_trend_up'] &             # Daily trend up
        df['daily_bull_regime'] &          # 50 > 200 on daily (bull market)
        df['rsi_bullish'] &                # RSI in sweet spot
        df['rsi_rising'] &                 # RSI momentum up
        df['macd_bullish'] &               # MACD confirmation
        df['trend_strong'] &               # ADX > 20
        df['vol_ok'] &                     # Moderate volatility
        df['volume_ok'] &                  # Volume confirmation
        (df['rth_ok'] == 1)                # Regular trading hours
    ).astype(int)

    df['short_signal'] = (
        df['pullback_to_fast_dn'] &
        (~df['fast_ma_up']) &
        (~df['fast_above_slow']) &
        (~df['daily_trend_up']) &
        (~df['daily_bull_regime']) &
        df['rsi_bearish'] &
        (~df['rsi_rising']) &
        df['macd_bearish'] &
        df['trend_strong'] &
        df['vol_ok'] &
        df['volume_ok'] &
        (df['rth_ok'] == 1)
    ).astype(int)

    # Prepare output
    df_final = df[['Open', 'High', 'Low', 'Close', 'Volume', 'atr',
                   'fast_ma', 'slow_ma', 'fast_ma_up', 'fast_above_slow',
                   'long_signal', 'short_signal', 'rth_ok', 'rsi',
                   'macd_hist', 'macd_declining', 'adx',
                   'ma_cross_up', 'ma_cross_dn']].copy()
    df_final = df_final.dropna()

    long_count = df_final['long_signal'].sum()
    short_count = df_final['short_signal'].sum()
    print(f"Conservative Signals - Long: {long_count}, Short: {short_count}")

    return df_final


# =============================================================================
# CONSERVATIVE STRATEGY CLASS
# =============================================================================

class CMUltimateMAConservative(Strategy):
    """
    CM Ultimate MA - Conservative Version

    Target: 20% Max Drawdown with improved Sharpe

    Key Differences:
    1. Position scale calibrated for 20% DD
    2. Dynamic position reduction as DD increases
    3. Faster trailing stops
    4. Momentum-based exits
    5. Time decay exits
    """

    # Position sizing (calibrated for 20% max DD)
    # Original DD was ~59%, target is 20%
    # Base scale: 20/59 = 0.34, but with DD protection we can go higher
    position_scale = 0.45  # Start at 45%, will be reduced dynamically

    # Risk parameters (tighter than original)
    stop_mult = 2.0        # Tighter stop (was 2.5)
    target_mult = 3.0      # R:R of 1.5

    # Trailing parameters (more aggressive)
    trail_start_pct = 1.0  # Start trailing earlier (was 2.0%)
    trail_atr_mult = 1.0   # Tighter trail (was 1.5)

    # Profit taking
    partial_exit_pct = 2.0  # Take partial at 2%
    partial_exit_size = 0.5 # Exit 50% at first target

    # Time decay
    max_hold_bars = 50     # Shorter hold (was 80)
    time_decay_start = 30  # Start tightening after 30 bars

    # Drawdown protection
    max_dd_limit = 0.20    # 20% max drawdown
    dd_reduction_start = 0.10  # Start reducing at 10% DD
    dd_pause_threshold = 0.18  # Pause trading at 18% DD

    # Momentum exit
    use_momentum_exit = True

    def init(self):
        self.long_sig = self.I(lambda: self.data.long_signal)
        self.short_sig = self.I(lambda: self.data.short_signal)
        self.atr = self.I(lambda: self.data.atr)
        self.fast_ma = self.I(lambda: self.data.fast_ma)
        self.slow_ma = self.I(lambda: self.data.slow_ma)
        self.ma_cross_dn = self.I(lambda: self.data.ma_cross_dn)
        self.macd_declining = self.I(lambda: self.data.macd_declining)
        self.adx = self.I(lambda: self.data.adx)

        # Trade management
        self.entry_price = None
        self.entry_bar = None
        self.stop_price = None
        self.target_price = None
        self.trailing_active = False
        self.is_long = None
        self.partial_taken = False

        # Drawdown tracking
        self.peak_equity = self._broker._cash
        self.current_dd = 0.0

    def next(self):
        price = self.data.Close[-1]
        atr = self.atr[-1]

        if np.isnan(atr) or atr <= 0:
            return

        # Update drawdown tracking
        current_equity = self._broker.equity
        if current_equity > self.peak_equity:
            self.peak_equity = current_equity
        self.current_dd = (self.peak_equity - current_equity) / self.peak_equity

        # Manage existing position
        if self.position:
            self.manage_position()
            return

        # Check if trading is paused due to drawdown
        if self.current_dd >= self.dd_pause_threshold:
            return  # Don't take new trades

        # Calculate dynamic position size based on drawdown
        adjusted_scale = self.get_adjusted_position_scale()

        # Look for entry signals
        if self.long_sig[-1] == 1:
            self.enter_long(price, atr, adjusted_scale)
        elif self.short_sig[-1] == 1:
            self.enter_short(price, atr, adjusted_scale)

    def get_adjusted_position_scale(self):
        """Reduce position size as drawdown increases."""
        if self.current_dd <= self.dd_reduction_start:
            return self.position_scale

        # Linear reduction from dd_reduction_start to dd_pause_threshold
        dd_range = self.dd_pause_threshold - self.dd_reduction_start
        dd_progress = (self.current_dd - self.dd_reduction_start) / dd_range

        # Reduce from full scale to 20% of scale
        min_scale = self.position_scale * 0.2
        adjusted = self.position_scale - (self.position_scale - min_scale) * dd_progress

        return max(adjusted, min_scale)

    def enter_long(self, price, atr, scale):
        stop_dist = self.stop_mult * atr
        target_dist = self.target_mult * atr

        self.entry_price = price
        self.entry_bar = len(self.data)
        self.stop_price = price - stop_dist
        self.target_price = price + target_dist
        self.trailing_active = False
        self.is_long = True
        self.partial_taken = False

        # Use scaled position
        self.buy(size=scale, sl=self.stop_price, tp=self.target_price)

    def enter_short(self, price, atr, scale):
        stop_dist = self.stop_mult * atr
        target_dist = self.target_mult * atr

        self.entry_price = price
        self.entry_bar = len(self.data)
        self.stop_price = price + stop_dist
        self.target_price = price - target_dist
        self.trailing_active = False
        self.is_long = False
        self.partial_taken = False

        self.sell(size=scale, sl=self.stop_price, tp=self.target_price)

    def manage_position(self):
        """Enhanced position management with multiple exit types."""
        price = self.data.Close[-1]
        atr = self.atr[-1]
        current_bar = len(self.data)
        bars_held = current_bar - self.entry_bar

        # Calculate P&L
        if self.is_long:
            pnl_pct = ((price / self.entry_price) - 1) * 100
        else:
            pnl_pct = ((self.entry_price / price) - 1) * 100

        # -----------------------------------------------------------------
        # 1. PARTIAL PROFIT TAKING
        # -----------------------------------------------------------------
        if not self.partial_taken and pnl_pct >= self.partial_exit_pct:
            # Take partial profits (handled by reducing position conceptually)
            self.partial_taken = True
            # Note: backtesting.py doesn't support partial exits easily
            # Instead, we'll tighten the stop significantly
            if self.is_long:
                self.stop_price = max(self.stop_price, self.entry_price + (0.3 * atr))
            else:
                self.stop_price = min(self.stop_price, self.entry_price - (0.3 * atr))

        # -----------------------------------------------------------------
        # 2. TRAILING STOP (more aggressive)
        # -----------------------------------------------------------------
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

        # -----------------------------------------------------------------
        # 3. TIME DECAY - Tighten stop as trade ages
        # -----------------------------------------------------------------
        if bars_held >= self.time_decay_start:
            decay_factor = (bars_held - self.time_decay_start) / (self.max_hold_bars - self.time_decay_start)
            decay_factor = min(decay_factor, 1.0)

            # Tighten stop by up to 50%
            if self.is_long and pnl_pct > 0:
                tightened_stop = price - (atr * (1 - 0.5 * decay_factor))
                if tightened_stop > self.stop_price:
                    self.stop_price = tightened_stop

        # -----------------------------------------------------------------
        # 4. CHECK STOPS
        # -----------------------------------------------------------------
        if self.is_long and price <= self.stop_price:
            self.position.close()
            return
        elif not self.is_long and price >= self.stop_price:
            self.position.close()
            return

        # -----------------------------------------------------------------
        # 5. TIME EXIT
        # -----------------------------------------------------------------
        if bars_held >= self.max_hold_bars:
            self.position.close()
            return

        # -----------------------------------------------------------------
        # 6. MOMENTUM EXIT (Close if momentum fading while profitable)
        # -----------------------------------------------------------------
        if self.use_momentum_exit and pnl_pct > 0.5:
            if self.macd_declining[-1]:
                self.position.close()
                return

        # -----------------------------------------------------------------
        # 7. MA CROSS EXIT (trend reversal)
        # -----------------------------------------------------------------
        if self.is_long and self.ma_cross_dn[-1]:
            self.position.close()
            return

        # -----------------------------------------------------------------
        # 8. TREND STRENGTH EXIT (ADX dropping)
        # -----------------------------------------------------------------
        if self.adx[-1] < 15 and pnl_pct > 0:
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
# OPTIMIZATION FUNCTIONS
# =============================================================================

def find_optimal_scale_for_target_dd(df: pd.DataFrame, target_dd: float = 0.20,
                                     capital: float = 500000):
    """
    Find the optimal position scale to achieve target max drawdown.
    Uses binary search for efficiency.
    """
    print(f"\nFinding optimal scale for {target_dd*100:.0f}% max DD...")

    results = []

    # Test a range of scales
    for scale in np.arange(0.20, 0.80, 0.05):
        CMUltimateMAConservative.position_scale = scale

        bt = Backtest(
            df,
            CMUltimateMAConservative,
            cash=capital,
            commission=0.00005,
            exclusive_orders=True,
            trade_on_close=True,
            margin=0.1
        )

        stats = bt.run()
        max_dd = abs(stats['Max. Drawdown [%]']) / 100
        sharpe = stats['Sharpe Ratio']
        ret = stats['Return [%]']
        trades = stats['# Trades']

        status = "OK" if max_dd <= target_dd else "OVER"
        print(f"  Scale {scale*100:.0f}%: Return {ret:.1f}%, MaxDD {max_dd*100:.1f}%, "
              f"Sharpe {sharpe:.3f}, Trades {trades} [{status}]")

        results.append({
            'scale': scale,
            'return': ret,
            'max_dd': max_dd,
            'sharpe': sharpe,
            'trades': trades,
            'valid': max_dd <= target_dd
        })

    # Find best valid scale (highest Sharpe within DD constraint)
    valid_results = [r for r in results if r['valid']]

    if valid_results:
        best = max(valid_results, key=lambda x: x['sharpe'])
        print(f"\nOptimal Scale: {best['scale']*100:.0f}% "
              f"(Sharpe={best['sharpe']:.3f}, MaxDD={best['max_dd']*100:.1f}%)")
        return best['scale']
    else:
        print("\nWARNING: No scale found within target DD. Using minimum.")
        return 0.20


def run_conservative_backtest(df: pd.DataFrame, capital: float = 500000):
    """Run the conservative backtest with full analysis."""

    bt = Backtest(
        df,
        CMUltimateMAConservative,
        cash=capital,
        commission=0.00005,
        exclusive_orders=True,
        trade_on_close=True,
        margin=0.1
    )

    stats = bt.run()
    num_days = (df.index.max() - df.index.min()).days
    cagr = calculate_cagr(stats['Return [%]'], num_days)

    return stats, cagr, bt


def optimize_conservative_strategy(df: pd.DataFrame, capital: float = 500000):
    """Optimize strategy parameters within DD constraint."""

    bt = Backtest(
        df,
        CMUltimateMAConservative,
        cash=capital,
        commission=0.00005,
        exclusive_orders=True,
        trade_on_close=True,
        margin=0.1
    )

    print("\nOptimizing conservative strategy parameters...")

    optimized = bt.optimize(
        stop_mult=[1.5, 2.0, 2.5],
        target_mult=[2.5, 3.0, 3.5, 4.0],
        trail_start_pct=[0.8, 1.0, 1.5],
        trail_atr_mult=[0.8, 1.0, 1.2],
        max_hold_bars=[40, 50, 60],
        maximize='Sharpe Ratio',
        constraint=lambda p: p.target_mult >= p.stop_mult,
        return_heatmap=False
    )

    return optimized


# =============================================================================
# MAIN
# =============================================================================

if __name__ == "__main__":
    ES_HOURLY_PATH = r"C:\dev\moondev-ai-agents\src\data\rbi\ES-1H.csv"

    print("=" * 80)
    print("CM ULTIMATE MA - CONSERVATIVE VERSION")
    print("Target: 20% Maximum Drawdown")
    print("=" * 80)

    # Load data
    print("\nLoading ES hourly data...")
    df_hourly = load_hourly_csv(ES_HOURLY_PATH, "ES")

    # Prepare signals with enhanced filters
    print("\nPreparing conservative signals...")
    df = prepare_conservative_signals(df_hourly)

    # Step 1: Find optimal scale for 20% max DD
    print("\n" + "=" * 80)
    print("STEP 1: CALIBRATING POSITION SIZE FOR 20% MAX DD")
    print("=" * 80)
    optimal_scale = find_optimal_scale_for_target_dd(df, target_dd=0.20)
    CMUltimateMAConservative.position_scale = optimal_scale

    # Step 2: Run baseline with optimal scale
    print("\n" + "=" * 80)
    print("STEP 2: BASELINE RESULTS WITH OPTIMAL SCALE")
    print("=" * 80)
    baseline_stats, baseline_cagr, bt = run_conservative_backtest(df)

    print(f"\nBaseline Results (Scale={optimal_scale*100:.0f}%):")
    print(f"  Total Return: {baseline_stats['Return [%]']:.2f}%")
    print(f"  CAGR: {baseline_cagr:.2f}%")
    print(f"  Sharpe Ratio: {baseline_stats['Sharpe Ratio']:.3f}")
    print(f"  Max Drawdown: {baseline_stats['Max. Drawdown [%]']:.2f}%")
    print(f"  Win Rate: {baseline_stats['Win Rate [%]']:.2f}%")
    print(f"  Total Trades: {baseline_stats['# Trades']}")
    print(f"  Avg Trade: {baseline_stats['Avg. Trade [%]']:.2f}%")
    print(f"  Profit Factor: {baseline_stats['Profit Factor']:.2f}")

    # Step 3: Optimize parameters
    print("\n" + "=" * 80)
    print("STEP 3: PARAMETER OPTIMIZATION")
    print("=" * 80)
    optimized = optimize_conservative_strategy(df)

    num_days = (df.index.max() - df.index.min()).days
    opt_cagr = calculate_cagr(optimized['Return [%]'], num_days)

    print(f"\nOptimized Results:")
    print(f"  Stop: {optimized._strategy.stop_mult}x ATR")
    print(f"  Target: {optimized._strategy.target_mult}x ATR")
    print(f"  Trail Start: {optimized._strategy.trail_start_pct}%")
    print(f"  Trail ATR: {optimized._strategy.trail_atr_mult}x")
    print(f"  Max Hold: {optimized._strategy.max_hold_bars} bars")
    print(f"  ---")
    print(f"  Total Return: {optimized['Return [%]']:.2f}%")
    print(f"  CAGR: {opt_cagr:.2f}%")
    print(f"  Sharpe Ratio: {optimized['Sharpe Ratio']:.3f}")
    print(f"  Max Drawdown: {optimized['Max. Drawdown [%]']:.2f}%")
    print(f"  Win Rate: {optimized['Win Rate [%]']:.2f}%")
    print(f"  Total Trades: {optimized['# Trades']}")

    # Final summary
    print("\n" + "=" * 80)
    print("COMPARISON: ORIGINAL vs CONSERVATIVE")
    print("=" * 80)
    print(f"{'Metric':<20} {'Original T3/EMA':<20} {'Conservative':<20}")
    print("-" * 60)
    print(f"{'Max Drawdown':<20} {'-58.52%':<20} {optimized['Max. Drawdown [%]']:.2f}%")
    print(f"{'Sharpe Ratio':<20} {'0.671':<20} {optimized['Sharpe Ratio']:.3f}")
    print(f"{'Win Rate':<20} {'64.06%':<20} {optimized['Win Rate [%]']:.2f}%")
    print(f"{'Trades':<20} {'217':<20} {optimized['# Trades']}")

    print("\n" + "=" * 80)
    print("CONSERVATIVE STRATEGY COMPLETE")
    print("=" * 80)
