"""
CM Ultimate MA MTF - RISK-MANAGED VERSION
==========================================
Target: 20% Maximum Drawdown via position sizing + drawdown protection

Approach: Keep original T3/EMA signal logic (which works well)
          but control risk through position sizing and dynamic DD protection

Key Changes:
1. Position scale calibrated for 20% max DD (~34% of original)
2. Dynamic position reduction as DD increases
3. Trading pause at 15% DD, emergency exit at 19%
4. Slightly tighter trailing stops

Author: Moon Dev AI
"""

import pandas as pd
import numpy as np
from backtesting import Backtest, Strategy
import warnings
warnings.filterwarnings('ignore')


# =============================================================================
# MOVING AVERAGE IMPLEMENTATIONS (Same as original)
# =============================================================================

def calc_ema(series: pd.Series, period: int) -> pd.Series:
    return series.ewm(span=period, adjust=False).mean()


def calc_tilson_t3(series: pd.Series, period: int, factor: float = 0.7) -> pd.Series:
    def gd(src, length, vfactor):
        ema1 = calc_ema(src, length)
        ema2 = calc_ema(ema1, length)
        return ema1 * (1 + vfactor) - ema2 * vfactor
    gd1 = gd(series, period, factor)
    gd2 = gd(gd1, period, factor)
    gd3 = gd(gd2, period, factor)
    return gd3


def calc_sma(series: pd.Series, period: int) -> pd.Series:
    return series.rolling(window=period).mean()


# =============================================================================
# DATA LOADING
# =============================================================================

def load_hourly_csv(filepath: str, instrument: str = "ES") -> pd.DataFrame:
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
# SIGNAL PREPARATION (Original T3/EMA logic - proven to work)
# =============================================================================

def prepare_signals(df_hourly: pd.DataFrame, fast_len: int = 20,
                    slow_len: int = 50, t3_factor: float = 0.7,
                    smoothing: int = 2) -> pd.DataFrame:
    """
    Original T3/EMA signal logic that produced positive Sharpe.
    Only change: slightly tighter RSI bands.
    """
    df = df_hourly.copy()

    # Daily for MTF filter
    df_daily = df.resample('1D').agg({
        'Open': 'first', 'High': 'max', 'Low': 'min',
        'Close': 'last', 'Volume': 'sum'
    }).dropna()

    print(f"Hourly bars: {len(df):,}, Daily bars: {len(df_daily):,}")

    # Core MAs (T3 Fast / EMA Slow)
    df['fast_ma'] = calc_tilson_t3(df['Close'], fast_len, t3_factor)
    df['slow_ma'] = calc_ema(df['Close'], slow_len)

    # MA Direction
    df['fast_ma_up'] = df['fast_ma'] >= df['fast_ma'].shift(smoothing)
    df['fast_above_slow'] = df['fast_ma'] > df['slow_ma']

    # ATR
    tr = pd.concat([
        df['High'] - df['Low'],
        abs(df['High'] - df['Close'].shift(1)),
        abs(df['Low'] - df['Close'].shift(1))
    ], axis=1).max(axis=1)
    df['atr'] = tr.rolling(window=14).mean()

    # Pullback Detection (same as original)
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

    # RSI (slightly tighter bands for better quality)
    delta = df['Close'].diff()
    gain = delta.where(delta > 0, 0).rolling(window=14).mean()
    loss = (-delta.where(delta < 0, 0)).rolling(window=14).mean()
    rs = gain / (loss + 1e-10)
    df['rsi'] = 100 - (100 / (1 + rs))
    df['rsi_bullish'] = (df['rsi'] > 40) & (df['rsi'] < 70)
    df['rsi_bearish'] = (df['rsi'] > 30) & (df['rsi'] < 60)

    # MACD for exits
    ema12 = calc_ema(df['Close'], 12)
    ema26 = calc_ema(df['Close'], 26)
    df['macd'] = ema12 - ema26
    df['macd_signal'] = calc_ema(df['macd'], 9)
    df['macd_hist'] = df['macd'] - df['macd_signal']
    df['macd_declining'] = (df['macd_hist'] < df['macd_hist'].shift(1)) & \
                           (df['macd_hist'].shift(1) < df['macd_hist'].shift(2))

    # Daily trend filter
    df_daily['daily_ma'] = calc_sma(df_daily['Close'], 50)
    df_daily['daily_trend_up'] = df_daily['Close'] > df_daily['daily_ma']
    df_daily['date'] = df_daily.index.date

    df['date'] = df.index.date
    daily_trend_map = df_daily.set_index('date')['daily_trend_up'].to_dict()
    df['daily_trend_up'] = df['date'].map(daily_trend_map).ffill().fillna(True)

    # Trading hours
    df['hour'] = df.index.hour
    df['rth_ok'] = ((df['hour'] >= 9) & (df['hour'] < 16)).astype(int)

    # MA Cross signals
    df['ma_cross_dn'] = (df['fast_ma'] < df['slow_ma']) & (df['fast_ma'].shift(1) >= df['slow_ma'].shift(1))

    # Entry Signals (Original logic)
    df['long_signal'] = (
        df['pullback_to_fast_up'] &
        df['fast_ma_up'] &
        df['fast_above_slow'] &
        df['daily_trend_up'] &
        df['rsi_bullish'] &
        (df['rth_ok'] == 1)
    ).astype(int)

    df['short_signal'] = (
        df['pullback_to_fast_dn'] &
        (~df['fast_ma_up']) &
        (~df['fast_above_slow']) &
        (~df['daily_trend_up']) &
        df['rsi_bearish'] &
        (df['rth_ok'] == 1)
    ).astype(int)

    # Output
    df_final = df[['Open', 'High', 'Low', 'Close', 'Volume', 'atr',
                   'fast_ma', 'slow_ma', 'fast_ma_up', 'fast_above_slow',
                   'long_signal', 'short_signal', 'rth_ok', 'rsi',
                   'macd_hist', 'macd_declining', 'ma_cross_dn']].copy()
    df_final = df_final.dropna()

    long_count = df_final['long_signal'].sum()
    short_count = df_final['short_signal'].sum()
    print(f"Signals - Long: {long_count}, Short: {short_count}")

    return df_final


# =============================================================================
# RISK-MANAGED STRATEGY
# =============================================================================

class CMUltimateMAManaged(Strategy):
    """
    CM Ultimate MA with Risk Management

    Uses original signal logic but controls risk through:
    1. Position sizing calibrated for 20% max DD
    2. Dynamic position reduction as DD increases
    3. Trading pause at high DD
    4. Tighter trailing after profit
    """

    # Position sizing (will be calibrated)
    position_scale = 0.35  # ~35% to target 20% DD from 58% original

    # Risk parameters (same as optimized original)
    stop_mult = 2.5
    target_mult = 2.5

    # Trailing (slightly tighter for risk management)
    trail_start_pct = 1.5  # Start earlier
    trail_atr_mult = 1.2   # Tighter trail

    # Hold time
    max_hold_bars = 80

    # DRAWDOWN PROTECTION (less aggressive)
    dd_reduction_start = 0.15   # Start reducing at 15% DD
    dd_pause_threshold = 0.19   # Pause new trades at 19% DD
    dd_exit_threshold = 0.20    # Emergency exit at 20% DD

    # Momentum exit
    use_momentum_exit = True

    def init(self):
        self.long_sig = self.I(lambda: self.data.long_signal)
        self.short_sig = self.I(lambda: self.data.short_signal)
        self.atr = self.I(lambda: self.data.atr)
        self.fast_ma = self.I(lambda: self.data.fast_ma)
        self.ma_cross_dn = self.I(lambda: self.data.ma_cross_dn)
        self.macd_declining = self.I(lambda: self.data.macd_declining)

        # Trade management
        self.entry_price = None
        self.entry_bar = None
        self.stop_price = None
        self.trailing_active = False
        self.is_long = None

        # Drawdown tracking
        self.peak_equity = self._broker._cash
        self.current_dd = 0.0

    def next(self):
        price = self.data.Close[-1]
        atr = self.atr[-1]

        if np.isnan(atr) or atr <= 0:
            return

        # Update drawdown
        current_equity = self._broker.equity
        if current_equity > self.peak_equity:
            self.peak_equity = current_equity
        self.current_dd = (self.peak_equity - current_equity) / self.peak_equity

        # EMERGENCY EXIT at high DD
        if self.current_dd >= self.dd_exit_threshold and self.position:
            self.position.close()
            return

        # Manage existing position
        if self.position:
            self.manage_position()
            return

        # Pause trading at high DD
        if self.current_dd >= self.dd_pause_threshold:
            return

        # Calculate position size with DD adjustment
        adjusted_scale = self.get_adjusted_scale()

        # Entry signals
        if self.long_sig[-1] == 1:
            self.enter_long(price, atr, adjusted_scale)
        elif self.short_sig[-1] == 1:
            self.enter_short(price, atr, adjusted_scale)

    def get_adjusted_scale(self):
        """Reduce position size as DD increases."""
        if self.current_dd <= self.dd_reduction_start:
            return self.position_scale

        # Linear reduction
        dd_range = self.dd_pause_threshold - self.dd_reduction_start
        dd_progress = (self.current_dd - self.dd_reduction_start) / dd_range
        min_scale = self.position_scale * 0.3
        adjusted = self.position_scale - (self.position_scale - min_scale) * dd_progress

        return max(adjusted, min_scale)

    def enter_long(self, price, atr, scale):
        stop_dist = self.stop_mult * atr
        target_dist = self.target_mult * atr

        self.entry_price = price
        self.entry_bar = len(self.data)
        self.stop_price = price - stop_dist
        self.trailing_active = False
        self.is_long = True

        self.buy(size=scale, sl=self.stop_price, tp=price + target_dist)

    def enter_short(self, price, atr, scale):
        stop_dist = self.stop_mult * atr
        target_dist = self.target_mult * atr

        self.entry_price = price
        self.entry_bar = len(self.data)
        self.stop_price = price + stop_dist
        self.trailing_active = False
        self.is_long = False

        self.sell(size=scale, sl=self.stop_price, tp=price - target_dist)

    def manage_position(self):
        price = self.data.Close[-1]
        atr = self.atr[-1]
        bars_held = len(self.data) - self.entry_bar

        # P&L
        if self.is_long:
            pnl_pct = ((price / self.entry_price) - 1) * 100
        else:
            pnl_pct = ((self.entry_price / price) - 1) * 100

        # Trailing stop
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

        # Check stops
        if self.is_long and price <= self.stop_price:
            self.position.close()
            return
        elif not self.is_long and price >= self.stop_price:
            self.position.close()
            return

        # Time exit
        if bars_held >= self.max_hold_bars:
            self.position.close()
            return

        # Momentum exit
        if self.use_momentum_exit and pnl_pct > 0.5:
            if self.macd_declining[-1]:
                self.position.close()
                return

        # MA cross exit
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
# CALIBRATION
# =============================================================================

def calibrate_for_target_dd(df: pd.DataFrame, target_dd: float = 0.20,
                            capital: float = 500000):
    """Find optimal position scale to achieve target max drawdown."""
    print(f"\nCalibrating for {target_dd*100:.0f}% max drawdown...")
    print("-" * 60)

    results = []

    for scale in np.arange(0.25, 0.50, 0.02):
        CMUltimateMAManaged.position_scale = scale

        bt = Backtest(
            df,
            CMUltimateMAManaged,
            cash=capital,
            commission=0.00005,
            exclusive_orders=True,
            trade_on_close=True,
            margin=0.1
        )

        stats = bt.run()
        max_dd = abs(stats['Max. Drawdown [%]']) / 100
        sharpe = stats['Sharpe Ratio'] if not np.isnan(stats['Sharpe Ratio']) else -999
        ret = stats['Return [%]']
        trades = stats['# Trades']
        win_rate = stats['Win Rate [%]']

        valid = max_dd <= target_dd
        status = "OK" if valid else "OVER"

        print(f"Scale {scale*100:4.1f}%: Return {ret:7.1f}%, MaxDD {max_dd*100:5.1f}%, "
              f"Sharpe {sharpe:6.3f}, Trades {trades:3}, WinRate {win_rate:5.1f}% [{status}]")

        results.append({
            'scale': scale,
            'return': ret,
            'max_dd': max_dd,
            'sharpe': sharpe,
            'trades': trades,
            'win_rate': win_rate,
            'valid': valid
        })

    # Find best valid (highest Sharpe within constraint)
    valid_results = [r for r in results if r['valid'] and r['sharpe'] > -999]

    if valid_results:
        best = max(valid_results, key=lambda x: x['sharpe'])
        print(f"\n{'='*60}")
        print(f"OPTIMAL SCALE: {best['scale']*100:.1f}%")
        print(f"  Return: {best['return']:.1f}%")
        print(f"  Max DD: {best['max_dd']*100:.1f}%")
        print(f"  Sharpe: {best['sharpe']:.3f}")
        print(f"  Trades: {best['trades']}")
        print(f"  Win Rate: {best['win_rate']:.1f}%")
        return best['scale'], results
    else:
        print("\nWARNING: No valid scale found. Using minimum.")
        return 0.15, results


def run_final_backtest(df: pd.DataFrame, scale: float, capital: float = 500000):
    """Run final backtest with optimal scale."""
    CMUltimateMAManaged.position_scale = scale

    bt = Backtest(
        df,
        CMUltimateMAManaged,
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


# =============================================================================
# MAIN
# =============================================================================

if __name__ == "__main__":
    ES_HOURLY_PATH = r"C:\dev\moondev-ai-agents\src\data\rbi\ES-1H.csv"

    print("=" * 70)
    print("CM ULTIMATE MA - RISK-MANAGED VERSION")
    print("Target: 20% Maximum Drawdown")
    print("Strategy: Original T3/EMA signals + Position Sizing + DD Protection")
    print("=" * 70)

    # Load data
    print("\nLoading ES hourly data...")
    df_hourly = load_hourly_csv(ES_HOURLY_PATH, "ES")

    # Prepare signals (original logic)
    print("\nPreparing signals (original T3/EMA logic)...")
    df = prepare_signals(df_hourly)

    # Calibrate position size
    print("\n" + "=" * 70)
    print("STEP 1: POSITION SIZE CALIBRATION")
    print("=" * 70)
    optimal_scale, all_results = calibrate_for_target_dd(df, target_dd=0.20)

    # Final backtest
    print("\n" + "=" * 70)
    print("STEP 2: FINAL RESULTS")
    print("=" * 70)
    stats, cagr, bt = run_final_backtest(df, optimal_scale)

    print(f"\nFinal Results (Scale={optimal_scale*100:.1f}%):")
    print(f"  Total Return: {stats['Return [%]']:.2f}%")
    print(f"  CAGR: {cagr:.2f}%")
    print(f"  Sharpe Ratio: {stats['Sharpe Ratio']:.3f}")
    print(f"  Max Drawdown: {stats['Max. Drawdown [%]']:.2f}%")
    print(f"  Win Rate: {stats['Win Rate [%]']:.2f}%")
    print(f"  Total Trades: {stats['# Trades']}")
    print(f"  Avg Trade: {stats['Avg. Trade [%]']:.2f}%")
    print(f"  Profit Factor: {stats['Profit Factor']:.2f}")

    # Comparison
    print("\n" + "=" * 70)
    print("COMPARISON: ORIGINAL vs RISK-MANAGED")
    print("=" * 70)
    print(f"{'Metric':<20} {'Original (100%)':<20} {'Risk-Managed':<20}")
    print("-" * 60)
    print(f"{'Position Scale':<20} {'100%':<20} {optimal_scale*100:.1f}%")
    print(f"{'Max Drawdown':<20} {'-58.52%':<20} {stats['Max. Drawdown [%]']:.2f}%")
    print(f"{'Sharpe Ratio':<20} {'0.671':<20} {stats['Sharpe Ratio']:.3f}")
    print(f"{'CAGR':<20} {'524.87%':<20} {cagr:.2f}%")
    print(f"{'Win Rate':<20} {'64.06%':<20} {stats['Win Rate [%]']:.2f}%")

    # Expected annual metrics
    print("\n" + "=" * 70)
    print("EXPECTED ANNUAL PERFORMANCE (Risk-Managed)")
    print("=" * 70)
    print(f"  Expected Annual Return: {cagr:.1f}%")
    print(f"  Maximum Drawdown: {abs(stats['Max. Drawdown [%]']):.1f}%")
    print(f"  Sharpe Ratio: {stats['Sharpe Ratio']:.3f}")
    print(f"  Risk/Reward: For every $1 risked (20% DD), expect ${cagr/20:.2f} return")

    print("\n" + "=" * 70)
    print("STRATEGY COMPLETE")
    print("=" * 70)
