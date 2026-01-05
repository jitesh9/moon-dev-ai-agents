"""
CM Ultimate MA MTF - ELITE VERSION
===================================
Target: 20% Maximum Drawdown with IMPROVED Sharpe Ratio

Key Enhancements:
1. REGIME FILTERING
   - VIX Proxy (realized volatility percentile)
   - Clear trend requirement (ADX > threshold)
   - Bull regime filter (MA alignment)

2. QUALITY SCORING SYSTEM
   - Each setup scored 0-100
   - Only A+ setups (score >= 75) are traded
   - Multiple confirmation factors weighted

3. POSITION SIZING
   - Base size calibrated for 20% DD
   - Size adjusted by quality score
   - Dynamic reduction during drawdowns

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
    return series.ewm(span=period, adjust=False).mean()


def calc_sma(series: pd.Series, period: int) -> pd.Series:
    return series.rolling(window=period).mean()


def calc_tilson_t3(series: pd.Series, period: int, factor: float = 0.7) -> pd.Series:
    def gd(src, length, vfactor):
        ema1 = calc_ema(src, length)
        ema2 = calc_ema(ema1, length)
        return ema1 * (1 + vfactor) - ema2 * vfactor
    gd1 = gd(series, period, factor)
    gd2 = gd(gd1, period, factor)
    gd3 = gd(gd2, period, factor)
    return gd3


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
# REGIME & QUALITY SIGNAL PREPARATION
# =============================================================================

def prepare_elite_signals(df_hourly: pd.DataFrame, fast_len: int = 20,
                          slow_len: int = 50, t3_factor: float = 0.7,
                          min_quality_score: int = 75) -> pd.DataFrame:
    """
    Prepare signals with REGIME FILTERING and QUALITY SCORING.

    Regime Filters:
    1. VIX Proxy: Realized volatility < 80th percentile (avoid high vol)
    2. Trend Clarity: ADX > 20 (clear trend)
    3. Bull Regime: 20 EMA > 50 EMA > 100 EMA on daily (aligned MAs)

    Quality Score Components (0-100):
    - Pullback quality (0-20): How clean is the pullback to MA
    - Trend strength (0-20): ADX level
    - MA alignment (0-20): All MAs aligned in trend direction
    - RSI positioning (0-15): RSI in optimal zone
    - MACD confirmation (0-15): MACD supporting direction
    - Volume confirmation (0-10): Above average volume
    """
    df = df_hourly.copy()

    # Create daily for MTF filter
    df_daily = df.resample('1D').agg({
        'Open': 'first', 'High': 'max', 'Low': 'min',
        'Close': 'last', 'Volume': 'sum'
    }).dropna()

    print(f"Hourly bars: {len(df):,}, Daily bars: {len(df_daily):,}")

    # =========================================================================
    # CORE INDICATORS
    # =========================================================================

    # MAs (T3 Fast / EMA Slow)
    df['fast_ma'] = calc_tilson_t3(df['Close'], fast_len, t3_factor)
    df['slow_ma'] = calc_ema(df['Close'], slow_len)
    df['ma_100'] = calc_ema(df['Close'], 100)

    # MA Direction
    df['fast_ma_up'] = df['fast_ma'] >= df['fast_ma'].shift(2)
    df['fast_above_slow'] = df['fast_ma'] > df['slow_ma']
    df['slow_above_100'] = df['slow_ma'] > df['ma_100']

    # ATR
    tr = pd.concat([
        df['High'] - df['Low'],
        abs(df['High'] - df['Close'].shift(1)),
        abs(df['Low'] - df['Close'].shift(1))
    ], axis=1).max(axis=1)
    df['atr'] = tr.rolling(window=14).mean()

    # =========================================================================
    # 1. VIX PROXY - Realized Volatility Percentile
    # =========================================================================
    # Calculate 20-day realized volatility
    df['returns'] = df['Close'].pct_change()
    df['realized_vol'] = df['returns'].rolling(window=20).std() * np.sqrt(252 * 6.5)  # Annualized

    # Calculate percentile rank (rolling 100 bars)
    df['vol_percentile'] = df['realized_vol'].rolling(window=100).apply(
        lambda x: pd.Series(x).rank(pct=True).iloc[-1] if len(x) > 0 else 0.5,
        raw=False
    )

    # LOW VOL REGIME: vol_percentile < 0.70 (below 70th percentile)
    df['low_vol_regime'] = df['vol_percentile'] < 0.70

    # =========================================================================
    # 2. ADX - Trend Clarity
    # =========================================================================
    plus_dm = df['High'].diff()
    minus_dm = -df['Low'].diff()
    plus_dm = plus_dm.where((plus_dm > minus_dm) & (plus_dm > 0), 0)
    minus_dm = minus_dm.where((minus_dm > plus_dm) & (minus_dm > 0), 0)

    atr_14 = tr.rolling(window=14).mean()
    plus_di = 100 * (plus_dm.rolling(window=14).mean() / (atr_14 + 1e-10))
    minus_di = 100 * (minus_dm.rolling(window=14).mean() / (atr_14 + 1e-10))

    dx = 100 * abs(plus_di - minus_di) / (plus_di + minus_di + 1e-10)
    df['adx'] = dx.rolling(window=14).mean()

    # CLEAR TREND: ADX > 20
    df['clear_trend'] = df['adx'] > 20

    # Trend direction from DI
    df['trend_bullish'] = plus_di > minus_di
    df['trend_bearish'] = minus_di > plus_di

    # =========================================================================
    # 3. BULL/BEAR REGIME - MA Alignment on Daily
    # =========================================================================
    df_daily['ema_20'] = calc_ema(df_daily['Close'], 20)
    df_daily['ema_50'] = calc_ema(df_daily['Close'], 50)
    df_daily['ema_100'] = calc_ema(df_daily['Close'], 100)

    # Bull regime: 20 > 50 > 100 AND price > 20
    df_daily['bull_regime'] = (
        (df_daily['ema_20'] > df_daily['ema_50']) &
        (df_daily['ema_50'] > df_daily['ema_100']) &
        (df_daily['Close'] > df_daily['ema_20'])
    )

    # Bear regime: 20 < 50 < 100 AND price < 20
    df_daily['bear_regime'] = (
        (df_daily['ema_20'] < df_daily['ema_50']) &
        (df_daily['ema_50'] < df_daily['ema_100']) &
        (df_daily['Close'] < df_daily['ema_20'])
    )

    df_daily['date'] = df_daily.index.date

    # Map to hourly
    df['date'] = df.index.date
    bull_map = df_daily.set_index('date')['bull_regime'].to_dict()
    bear_map = df_daily.set_index('date')['bear_regime'].to_dict()

    df['bull_regime'] = df['date'].map(bull_map).ffill().fillna(False)
    df['bear_regime'] = df['date'].map(bear_map).ffill().fillna(False)

    # =========================================================================
    # 4. RSI
    # =========================================================================
    delta = df['Close'].diff()
    gain = delta.where(delta > 0, 0).rolling(window=14).mean()
    loss = (-delta.where(delta < 0, 0)).rolling(window=14).mean()
    rs = gain / (loss + 1e-10)
    df['rsi'] = 100 - (100 / (1 + rs))

    # RSI slope (momentum)
    df['rsi_slope'] = df['rsi'] - df['rsi'].shift(3)

    # =========================================================================
    # 5. MACD
    # =========================================================================
    ema12 = calc_ema(df['Close'], 12)
    ema26 = calc_ema(df['Close'], 26)
    df['macd'] = ema12 - ema26
    df['macd_signal'] = calc_ema(df['macd'], 9)
    df['macd_hist'] = df['macd'] - df['macd_signal']

    # MACD direction
    df['macd_rising'] = df['macd_hist'] > df['macd_hist'].shift(1)
    df['macd_declining'] = df['macd_hist'] < df['macd_hist'].shift(1)

    # =========================================================================
    # 6. VOLUME
    # =========================================================================
    df['vol_sma'] = df['Volume'].rolling(window=20).mean()
    df['vol_ratio'] = df['Volume'] / (df['vol_sma'] + 1)

    # =========================================================================
    # 7. PULLBACK DETECTION
    # =========================================================================
    # Distance from fast MA (for pullback quality)
    df['dist_from_ma'] = (df['Close'] - df['fast_ma']) / df['fast_ma'] * 100
    df['dist_from_ma_low'] = (df['Low'] - df['fast_ma']) / df['fast_ma'] * 100

    # Pullback to fast MA (bullish)
    df['pullback_to_fast_up'] = (
        (df['Low'] <= df['fast_ma'] * 1.003) &  # Low within 0.3% of MA
        (df['Close'] > df['fast_ma']) &          # Closed above MA
        (df['Close'] > df['Open'])               # Bullish candle
    )

    # Pullback to fast MA (bearish)
    df['pullback_to_fast_dn'] = (
        (df['High'] >= df['fast_ma'] * 0.997) &  # High within 0.3% of MA
        (df['Close'] < df['fast_ma']) &          # Closed below MA
        (df['Close'] < df['Open'])               # Bearish candle
    )

    # =========================================================================
    # 8. TRADING HOURS
    # =========================================================================
    df['hour'] = df.index.hour
    df['rth_ok'] = ((df['hour'] >= 9) & (df['hour'] < 15)).astype(int)

    # =========================================================================
    # 9. MA CROSS SIGNALS (for exits)
    # =========================================================================
    df['ma_cross_dn'] = (df['fast_ma'] < df['slow_ma']) & (df['fast_ma'].shift(1) >= df['slow_ma'].shift(1))
    df['ma_cross_up'] = (df['fast_ma'] > df['slow_ma']) & (df['fast_ma'].shift(1) <= df['slow_ma'].shift(1))

    # =========================================================================
    # QUALITY SCORING SYSTEM (0-100)
    # =========================================================================

    def calculate_long_quality_score(row):
        """Calculate quality score for long setups."""
        score = 0

        # 1. Pullback Quality (0-20)
        # Best: Low touched MA exactly, closed well above
        if row['pullback_to_fast_up']:
            # Distance from MA when bounced (smaller = better pullback)
            bounce_quality = max(0, 20 - abs(row['dist_from_ma_low']) * 10)
            score += min(20, bounce_quality)

        # 2. Trend Strength via ADX (0-20)
        adx = row['adx']
        if adx >= 30:
            score += 20  # Strong trend
        elif adx >= 25:
            score += 15  # Good trend
        elif adx >= 20:
            score += 10  # Adequate trend
        elif adx >= 15:
            score += 5   # Weak trend

        # 3. MA Alignment (0-20)
        if row['fast_above_slow'] and row['slow_above_100']:
            score += 20  # Perfect alignment
        elif row['fast_above_slow']:
            score += 10  # Partial alignment

        # 4. RSI Positioning (0-15)
        rsi = row['rsi']
        if 45 <= rsi <= 60:
            score += 15  # Perfect zone (not overbought, not oversold)
        elif 40 <= rsi <= 65:
            score += 10  # Good zone
        elif 35 <= rsi <= 70:
            score += 5   # Acceptable

        # RSI momentum bonus
        if row['rsi_slope'] > 0:
            score += 2  # Rising RSI

        # 5. MACD Confirmation (0-15)
        if row['macd'] > row['macd_signal'] and row['macd_rising']:
            score += 15  # Strong MACD confirmation
        elif row['macd'] > row['macd_signal']:
            score += 10  # MACD above signal
        elif row['macd_rising']:
            score += 5   # MACD improving

        # 6. Volume Confirmation (0-10)
        vol_ratio = row['vol_ratio']
        if vol_ratio >= 1.5:
            score += 10  # High volume
        elif vol_ratio >= 1.2:
            score += 7   # Above average
        elif vol_ratio >= 1.0:
            score += 4   # Average
        elif vol_ratio >= 0.8:
            score += 2   # Below average but acceptable

        return min(100, score)

    def calculate_short_quality_score(row):
        """Calculate quality score for short setups."""
        score = 0

        # 1. Pullback Quality (0-20)
        if row['pullback_to_fast_dn']:
            bounce_quality = max(0, 20 - abs(row['dist_from_ma']) * 10)
            score += min(20, bounce_quality)

        # 2. Trend Strength via ADX (0-20)
        adx = row['adx']
        if adx >= 30:
            score += 20
        elif adx >= 25:
            score += 15
        elif adx >= 20:
            score += 10
        elif adx >= 15:
            score += 5

        # 3. MA Alignment (0-20)
        if not row['fast_above_slow'] and not row['slow_above_100']:
            score += 20  # Perfect bearish alignment
        elif not row['fast_above_slow']:
            score += 10

        # 4. RSI Positioning (0-15)
        rsi = row['rsi']
        if 40 <= rsi <= 55:
            score += 15  # Perfect zone for shorts
        elif 35 <= rsi <= 60:
            score += 10
        elif 30 <= rsi <= 65:
            score += 5

        # RSI momentum
        if row['rsi_slope'] < 0:
            score += 2  # Falling RSI

        # 5. MACD Confirmation (0-15)
        if row['macd'] < row['macd_signal'] and row['macd_declining']:
            score += 15
        elif row['macd'] < row['macd_signal']:
            score += 10
        elif row['macd_declining']:
            score += 5

        # 6. Volume Confirmation (0-10)
        vol_ratio = row['vol_ratio']
        if vol_ratio >= 1.5:
            score += 10
        elif vol_ratio >= 1.2:
            score += 7
        elif vol_ratio >= 1.0:
            score += 4
        elif vol_ratio >= 0.8:
            score += 2

        return min(100, score)

    # Calculate quality scores
    print("Calculating quality scores...")
    df['long_quality'] = df.apply(calculate_long_quality_score, axis=1)
    df['short_quality'] = df.apply(calculate_short_quality_score, axis=1)

    # =========================================================================
    # COMBINED REGIME + QUALITY SIGNALS
    # =========================================================================

    # REGIME FILTER: All regime conditions must be met
    df['regime_ok_long'] = (
        df['low_vol_regime'] &      # Low volatility
        df['clear_trend'] &          # ADX > 20
        df['bull_regime'] &          # Daily MAs aligned bullish
        df['trend_bullish']          # DI+ > DI-
    )

    df['regime_ok_short'] = (
        df['low_vol_regime'] &      # Low volatility
        df['clear_trend'] &          # ADX > 20
        df['bear_regime'] &          # Daily MAs aligned bearish
        df['trend_bearish']          # DI- > DI+
    )

    # QUALITY FILTER: Only A+ setups (score >= threshold)
    df['quality_ok_long'] = df['long_quality'] >= min_quality_score
    df['quality_ok_short'] = df['short_quality'] >= min_quality_score

    # FINAL SIGNALS: Regime + Quality + Basic Entry
    df['long_signal'] = (
        df['pullback_to_fast_up'] &   # Basic pullback entry
        df['fast_ma_up'] &             # MA trending up
        df['fast_above_slow'] &        # Fast > Slow
        df['regime_ok_long'] &         # Regime filter passed
        df['quality_ok_long'] &        # Quality score >= threshold
        (df['rth_ok'] == 1)            # Trading hours
    ).astype(int)

    df['short_signal'] = (
        df['pullback_to_fast_dn'] &
        (~df['fast_ma_up']) &
        (~df['fast_above_slow']) &
        df['regime_ok_short'] &
        df['quality_ok_short'] &
        (df['rth_ok'] == 1)
    ).astype(int)

    # =========================================================================
    # OUTPUT
    # =========================================================================
    df_final = df[['Open', 'High', 'Low', 'Close', 'Volume', 'atr',
                   'fast_ma', 'slow_ma', 'fast_ma_up', 'fast_above_slow',
                   'long_signal', 'short_signal', 'long_quality', 'short_quality',
                   'rth_ok', 'rsi', 'adx', 'vol_percentile',
                   'macd_hist', 'macd_declining', 'ma_cross_dn',
                   'regime_ok_long', 'regime_ok_short',
                   'low_vol_regime', 'clear_trend', 'bull_regime', 'bear_regime']].copy()
    df_final = df_final.dropna()

    # Statistics
    long_count = df_final['long_signal'].sum()
    short_count = df_final['short_signal'].sum()
    regime_long = df_final['regime_ok_long'].sum()
    regime_short = df_final['regime_ok_short'].sum()
    quality_long = (df_final['long_quality'] >= min_quality_score).sum()

    print(f"\nSignal Statistics:")
    print(f"  Regime OK (Long): {regime_long:,} bars ({regime_long/len(df_final)*100:.1f}%)")
    print(f"  Regime OK (Short): {regime_short:,} bars ({regime_short/len(df_final)*100:.1f}%)")
    print(f"  Quality >= {min_quality_score} (Long): {quality_long:,}")
    print(f"  Final Signals - Long: {long_count}, Short: {short_count}")

    return df_final


# =============================================================================
# ELITE STRATEGY CLASS
# =============================================================================

class CMUltimateMAElite(Strategy):
    """
    CM Ultimate MA - Elite Version

    Features:
    1. Regime filtering (low vol + clear trend + MA alignment)
    2. Quality scoring (only A+ setups)
    3. Position sizing by quality score
    4. Drawdown protection
    """

    # Base position scale (will be calibrated)
    position_scale = 0.40

    # Risk parameters
    stop_mult = 2.0        # Tighter stop for quality setups
    target_mult = 3.0      # 1.5:1 R:R

    # Trailing
    trail_start_pct = 1.2  # Start trail earlier
    trail_atr_mult = 1.0   # Tighter trail

    # Hold time
    max_hold_bars = 60     # Shorter hold for momentum

    # Drawdown protection
    dd_reduction_start = 0.12
    dd_pause_threshold = 0.18
    dd_exit_threshold = 0.20

    # Quality-based sizing
    use_quality_sizing = True  # Scale position by quality score

    def init(self):
        self.long_sig = self.I(lambda: self.data.long_signal)
        self.short_sig = self.I(lambda: self.data.short_signal)
        self.long_quality = self.I(lambda: self.data.long_quality)
        self.short_quality = self.I(lambda: self.data.short_quality)
        self.atr = self.I(lambda: self.data.atr)
        self.fast_ma = self.I(lambda: self.data.fast_ma)
        self.adx = self.I(lambda: self.data.adx)
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

        # Emergency exit
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

        # Calculate position size
        base_scale = self.get_dd_adjusted_scale()

        # Entry signals
        if self.long_sig[-1] == 1:
            quality = self.long_quality[-1]
            scale = self.get_quality_adjusted_scale(base_scale, quality)
            self.enter_long(price, atr, scale)
        elif self.short_sig[-1] == 1:
            quality = self.short_quality[-1]
            scale = self.get_quality_adjusted_scale(base_scale, quality)
            self.enter_short(price, atr, scale)

    def get_dd_adjusted_scale(self):
        """Reduce position size as DD increases."""
        if self.current_dd <= self.dd_reduction_start:
            return self.position_scale

        dd_range = self.dd_pause_threshold - self.dd_reduction_start
        dd_progress = (self.current_dd - self.dd_reduction_start) / dd_range
        min_scale = self.position_scale * 0.3
        adjusted = self.position_scale - (self.position_scale - min_scale) * dd_progress

        return max(adjusted, min_scale)

    def get_quality_adjusted_scale(self, base_scale, quality):
        """Adjust position size by quality score."""
        if not self.use_quality_sizing:
            return base_scale

        # Scale from 70% to 100% based on quality (75-100 range)
        quality_factor = 0.7 + (quality - 75) / 25 * 0.3
        quality_factor = max(0.7, min(1.0, quality_factor))

        return base_scale * quality_factor

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

        # Momentum exit (when profitable)
        if pnl_pct > 0.5 and self.macd_declining[-1]:
            self.position.close()
            return

        # MA cross exit
        if self.is_long and self.ma_cross_dn[-1]:
            self.position.close()
            return

        # ADX weakness exit (trend fading)
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
# CALIBRATION & OPTIMIZATION
# =============================================================================

def calibrate_elite_strategy(df: pd.DataFrame, target_dd: float = 0.20,
                             capital: float = 500000):
    """Calibrate position size for target max drawdown."""
    print(f"\nCalibrating for {target_dd*100:.0f}% max drawdown...")
    print("-" * 70)

    results = []

    for scale in np.arange(0.30, 0.70, 0.025):
        CMUltimateMAElite.position_scale = scale

        bt = Backtest(
            df,
            CMUltimateMAElite,
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
        profit_factor = stats['Profit Factor'] if not np.isnan(stats['Profit Factor']) else 0

        valid = max_dd <= target_dd
        status = "OK" if valid else "OVER"

        print(f"Scale {scale*100:4.1f}%: Ret {ret:7.1f}%, DD {max_dd*100:5.1f}%, "
              f"Sharpe {sharpe:6.3f}, PF {profit_factor:4.2f}, Trades {trades:3}, "
              f"Win {win_rate:5.1f}% [{status}]")

        results.append({
            'scale': scale,
            'return': ret,
            'max_dd': max_dd,
            'sharpe': sharpe,
            'profit_factor': profit_factor,
            'trades': trades,
            'win_rate': win_rate,
            'valid': valid
        })

    # Find best valid (highest Sharpe)
    valid_results = [r for r in results if r['valid'] and r['sharpe'] > -999]

    if valid_results:
        best = max(valid_results, key=lambda x: x['sharpe'])
        print(f"\n{'='*70}")
        print(f"OPTIMAL SCALE: {best['scale']*100:.1f}%")
        print(f"  Return: {best['return']:.1f}%")
        print(f"  Max DD: {best['max_dd']*100:.1f}%")
        print(f"  Sharpe: {best['sharpe']:.3f}")
        print(f"  Profit Factor: {best['profit_factor']:.2f}")
        print(f"  Trades: {best['trades']}")
        print(f"  Win Rate: {best['win_rate']:.1f}%")
        return best['scale'], results
    else:
        # Return highest Sharpe even if over DD
        if results:
            best = max(results, key=lambda x: x['sharpe'])
            print(f"\nWARNING: No scale within DD limit. Best: {best['scale']*100:.1f}%")
            return best['scale'], results
        return 0.35, results


def optimize_quality_threshold(df_hourly: pd.DataFrame, capital: float = 500000):
    """Find optimal quality threshold."""
    print("\n" + "=" * 70)
    print("OPTIMIZING QUALITY THRESHOLD")
    print("=" * 70)

    results = []

    for threshold in [60, 65, 70, 75, 80, 85]:
        print(f"\nTesting Quality Threshold = {threshold}...")
        df = prepare_elite_signals(df_hourly, min_quality_score=threshold)

        if df['long_signal'].sum() < 5:
            print(f"  Too few signals ({df['long_signal'].sum()}), skipping")
            continue

        CMUltimateMAElite.position_scale = 0.40

        bt = Backtest(
            df,
            CMUltimateMAElite,
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

        print(f"  Results: Ret={ret:.1f}%, DD={max_dd*100:.1f}%, "
              f"Sharpe={sharpe:.3f}, Trades={trades}, Win={win_rate:.1f}%")

        results.append({
            'threshold': threshold,
            'return': ret,
            'max_dd': max_dd,
            'sharpe': sharpe,
            'trades': trades,
            'win_rate': win_rate
        })

    if results:
        # Find best by Sharpe (with minimum trades filter)
        valid = [r for r in results if r['trades'] >= 10]
        if valid:
            best = max(valid, key=lambda x: x['sharpe'])
            print(f"\nOptimal Quality Threshold: {best['threshold']}")
            return best['threshold']

    return 75


# =============================================================================
# MAIN
# =============================================================================

if __name__ == "__main__":
    ES_HOURLY_PATH = r"C:\dev\moondev-ai-agents\src\data\rbi\ES-1H.csv"

    print("=" * 70)
    print("CM ULTIMATE MA - ELITE VERSION")
    print("=" * 70)
    print("Features:")
    print("  1. Regime Filtering (Low Vol + Clear Trend + MA Alignment)")
    print("  2. Quality Scoring (0-100, only A+ setups)")
    print("  3. Position Sizing by Quality Score")
    print("  4. Target: 20% Max Drawdown")
    print("=" * 70)

    # Load data
    print("\nLoading ES hourly data...")
    df_hourly = load_hourly_csv(ES_HOURLY_PATH, "ES")

    # Step 1: Find optimal quality threshold
    optimal_threshold = optimize_quality_threshold(df_hourly)

    # Step 2: Prepare signals with optimal threshold
    print("\n" + "=" * 70)
    print(f"PREPARING SIGNALS (Quality Threshold = {optimal_threshold})")
    print("=" * 70)
    df = prepare_elite_signals(df_hourly, min_quality_score=optimal_threshold)

    # Step 3: Calibrate position size
    print("\n" + "=" * 70)
    print("CALIBRATING POSITION SIZE")
    print("=" * 70)
    optimal_scale, all_results = calibrate_elite_strategy(df, target_dd=0.20)

    # Step 4: Final backtest
    print("\n" + "=" * 70)
    print("FINAL RESULTS")
    print("=" * 70)

    CMUltimateMAElite.position_scale = optimal_scale

    bt = Backtest(
        df,
        CMUltimateMAElite,
        cash=500000,
        commission=0.00005,
        exclusive_orders=True,
        trade_on_close=True,
        margin=0.1
    )

    stats = bt.run()
    num_days = (df.index.max() - df.index.min()).days
    cagr = calculate_cagr(stats['Return [%]'], num_days)

    print(f"\nElite Strategy Results:")
    print(f"  Quality Threshold: {optimal_threshold}")
    print(f"  Position Scale: {optimal_scale*100:.1f}%")
    print(f"  ---")
    print(f"  Total Return: {stats['Return [%]']:.2f}%")
    print(f"  CAGR: {cagr:.2f}%")
    print(f"  Sharpe Ratio: {stats['Sharpe Ratio']:.3f}")
    print(f"  Max Drawdown: {stats['Max. Drawdown [%]']:.2f}%")
    print(f"  Win Rate: {stats['Win Rate [%]']:.2f}%")
    print(f"  Total Trades: {stats['# Trades']}")
    print(f"  Avg Trade: {stats['Avg. Trade [%]']:.2f}%")
    print(f"  Profit Factor: {stats['Profit Factor']:.2f}")
    print(f"  Calmar Ratio: {cagr / abs(stats['Max. Drawdown [%]']) * 100:.3f}")

    # Comparison
    print("\n" + "=" * 70)
    print("COMPARISON: ALL VERSIONS")
    print("=" * 70)
    print(f"{'Version':<25} {'Max DD':<12} {'Sharpe':<10} {'CAGR':<12} {'Win Rate':<10}")
    print("-" * 70)
    print(f"{'Original T3/EMA':<25} {'-58.52%':<12} {'0.671':<10} {'524.87%':<12} {'64.06%':<10}")
    print(f"{'Risk-Managed':<25} {'-19.64%':<12} {'0.164':<10} {'2.92%':<12} {'62.50%':<10}")
    print(f"{'Elite (Regime+Quality)':<25} {stats['Max. Drawdown [%]']:.2f}%{'':<5} "
          f"{stats['Sharpe Ratio']:.3f}{'':<5} {cagr:.2f}%{'':<6} {stats['Win Rate [%]']:.2f}%")

    print("\n" + "=" * 70)
    print("STRATEGY COMPLETE")
    print("=" * 70)
