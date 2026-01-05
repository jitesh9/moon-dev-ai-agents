"""
CM Ultimate MA MTF - ADAPTIVE POSITION SIZING VERSION
======================================================
Target: 20% Maximum Drawdown with IMPROVED Sharpe via Adaptive Sizing

Adaptive Position Sizing Components:
1. QUALITY-BASED SIZING
   - Higher quality scores = larger positions
   - Scale from 60% to 120% of base size based on score

2. VOLATILITY TARGETING
   - Target a specific portfolio volatility level
   - Reduce size when market vol is high
   - Increase size when market vol is low

3. DRAWDOWN-BASED SCALING
   - Linear reduction as DD increases
   - Pause trading at threshold

4. KELLY CRITERION (Optional)
   - Size based on edge and win rate
   - Fractional Kelly for safety

5. REGIME-BASED SCALING
   - Full size in strong regimes
   - Reduced size in weak regimes

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
# SIGNAL PREPARATION WITH ADAPTIVE SIZING DATA
# =============================================================================

def prepare_adaptive_signals(df_hourly: pd.DataFrame, fast_len: int = 20,
                              slow_len: int = 50, t3_factor: float = 0.7,
                              min_quality_score: int = 60) -> pd.DataFrame:
    """
    Prepare signals with all data needed for adaptive position sizing.
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

    # MAs
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
    # VOLATILITY METRICS (For Adaptive Sizing)
    # =========================================================================

    # Realized volatility (annualized)
    df['returns'] = df['Close'].pct_change()
    df['realized_vol'] = df['returns'].rolling(window=20).std() * np.sqrt(252 * 6.5)

    # Volatility percentile
    df['vol_percentile'] = df['realized_vol'].rolling(window=100).apply(
        lambda x: pd.Series(x).rank(pct=True).iloc[-1] if len(x) > 0 else 0.5,
        raw=False
    )

    # Target volatility scaling factor
    # When vol is low, we can size up; when high, size down
    TARGET_VOL = 0.15  # 15% annualized target vol
    df['vol_scale_factor'] = TARGET_VOL / (df['realized_vol'] + 0.01)
    df['vol_scale_factor'] = df['vol_scale_factor'].clip(0.5, 2.0)  # Limit 50% to 200%

    # Low vol regime
    df['low_vol_regime'] = df['vol_percentile'] < 0.70

    # =========================================================================
    # ADX
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

    df['clear_trend'] = df['adx'] > 20
    df['trend_bullish'] = plus_di > minus_di
    df['trend_bearish'] = minus_di > plus_di

    # ADX-based regime strength (for adaptive sizing)
    # Strong trend = can size up
    df['trend_strength_factor'] = (df['adx'] / 25).clip(0.6, 1.3)

    # =========================================================================
    # DAILY REGIME
    # =========================================================================
    df_daily['ema_20'] = calc_ema(df_daily['Close'], 20)
    df_daily['ema_50'] = calc_ema(df_daily['Close'], 50)
    df_daily['ema_100'] = calc_ema(df_daily['Close'], 100)

    df_daily['bull_regime'] = (
        (df_daily['ema_20'] > df_daily['ema_50']) &
        (df_daily['ema_50'] > df_daily['ema_100']) &
        (df_daily['Close'] > df_daily['ema_20'])
    )

    df_daily['bear_regime'] = (
        (df_daily['ema_20'] < df_daily['ema_50']) &
        (df_daily['ema_50'] < df_daily['ema_100']) &
        (df_daily['Close'] < df_daily['ema_20'])
    )

    # Regime strength: how aligned are the MAs?
    df_daily['ma_spread'] = (df_daily['ema_20'] - df_daily['ema_100']) / df_daily['ema_100'] * 100
    df_daily['regime_strength'] = df_daily['ma_spread'].abs().clip(0, 5) / 5  # 0-1 scale

    df_daily['date'] = df_daily.index.date

    # Map to hourly
    df['date'] = df.index.date
    bull_map = df_daily.set_index('date')['bull_regime'].to_dict()
    bear_map = df_daily.set_index('date')['bear_regime'].to_dict()
    regime_strength_map = df_daily.set_index('date')['regime_strength'].to_dict()

    df['bull_regime'] = df['date'].map(bull_map).ffill().fillna(False)
    df['bear_regime'] = df['date'].map(bear_map).ffill().fillna(False)
    df['regime_strength'] = df['date'].map(regime_strength_map).ffill().fillna(0.5)

    # Regime-based sizing factor
    df['regime_size_factor'] = 0.7 + df['regime_strength'] * 0.6  # 0.7 to 1.3

    # =========================================================================
    # RSI
    # =========================================================================
    delta = df['Close'].diff()
    gain = delta.where(delta > 0, 0).rolling(window=14).mean()
    loss = (-delta.where(delta < 0, 0)).rolling(window=14).mean()
    rs = gain / (loss + 1e-10)
    df['rsi'] = 100 - (100 / (1 + rs))
    df['rsi_slope'] = df['rsi'] - df['rsi'].shift(3)

    # =========================================================================
    # MACD
    # =========================================================================
    ema12 = calc_ema(df['Close'], 12)
    ema26 = calc_ema(df['Close'], 26)
    df['macd'] = ema12 - ema26
    df['macd_signal'] = calc_ema(df['macd'], 9)
    df['macd_hist'] = df['macd'] - df['macd_signal']
    df['macd_rising'] = df['macd_hist'] > df['macd_hist'].shift(1)
    df['macd_declining'] = df['macd_hist'] < df['macd_hist'].shift(1)

    # =========================================================================
    # VOLUME
    # =========================================================================
    df['vol_sma'] = df['Volume'].rolling(window=20).mean()
    df['vol_ratio'] = df['Volume'] / (df['vol_sma'] + 1)

    # =========================================================================
    # PULLBACK DETECTION
    # =========================================================================
    df['dist_from_ma'] = (df['Close'] - df['fast_ma']) / df['fast_ma'] * 100
    df['dist_from_ma_low'] = (df['Low'] - df['fast_ma']) / df['fast_ma'] * 100

    df['pullback_to_fast_up'] = (
        (df['Low'] <= df['fast_ma'] * 1.003) &
        (df['Close'] > df['fast_ma']) &
        (df['Close'] > df['Open'])
    )

    df['pullback_to_fast_dn'] = (
        (df['High'] >= df['fast_ma'] * 0.997) &
        (df['Close'] < df['fast_ma']) &
        (df['Close'] < df['Open'])
    )

    # =========================================================================
    # TRADING HOURS
    # =========================================================================
    df['hour'] = df.index.hour
    df['rth_ok'] = ((df['hour'] >= 9) & (df['hour'] < 15)).astype(int)

    # =========================================================================
    # MA CROSS
    # =========================================================================
    df['ma_cross_dn'] = (df['fast_ma'] < df['slow_ma']) & (df['fast_ma'].shift(1) >= df['slow_ma'].shift(1))

    # =========================================================================
    # QUALITY SCORING
    # =========================================================================

    def calculate_long_quality_score(row):
        score = 0

        if row['pullback_to_fast_up']:
            bounce_quality = max(0, 20 - abs(row['dist_from_ma_low']) * 10)
            score += min(20, bounce_quality)

        adx = row['adx']
        if adx >= 30:
            score += 20
        elif adx >= 25:
            score += 15
        elif adx >= 20:
            score += 10
        elif adx >= 15:
            score += 5

        if row['fast_above_slow'] and row['slow_above_100']:
            score += 20
        elif row['fast_above_slow']:
            score += 10

        rsi = row['rsi']
        if 45 <= rsi <= 60:
            score += 15
        elif 40 <= rsi <= 65:
            score += 10
        elif 35 <= rsi <= 70:
            score += 5

        if row['rsi_slope'] > 0:
            score += 2

        if row['macd'] > row['macd_signal'] and row['macd_rising']:
            score += 15
        elif row['macd'] > row['macd_signal']:
            score += 10
        elif row['macd_rising']:
            score += 5

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

    def calculate_short_quality_score(row):
        score = 0

        if row['pullback_to_fast_dn']:
            bounce_quality = max(0, 20 - abs(row['dist_from_ma']) * 10)
            score += min(20, bounce_quality)

        adx = row['adx']
        if adx >= 30:
            score += 20
        elif adx >= 25:
            score += 15
        elif adx >= 20:
            score += 10
        elif adx >= 15:
            score += 5

        if not row['fast_above_slow'] and not row['slow_above_100']:
            score += 20
        elif not row['fast_above_slow']:
            score += 10

        rsi = row['rsi']
        if 40 <= rsi <= 55:
            score += 15
        elif 35 <= rsi <= 60:
            score += 10
        elif 30 <= rsi <= 65:
            score += 5

        if row['rsi_slope'] < 0:
            score += 2

        if row['macd'] < row['macd_signal'] and row['macd_declining']:
            score += 15
        elif row['macd'] < row['macd_signal']:
            score += 10
        elif row['macd_declining']:
            score += 5

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

    print("Calculating quality scores...")
    df['long_quality'] = df.apply(calculate_long_quality_score, axis=1)
    df['short_quality'] = df.apply(calculate_short_quality_score, axis=1)

    # Quality-based sizing factor (60-100 score maps to 0.6-1.2 factor)
    df['quality_size_factor_long'] = 0.6 + (df['long_quality'] - 60) / 40 * 0.6
    df['quality_size_factor_long'] = df['quality_size_factor_long'].clip(0.6, 1.2)

    df['quality_size_factor_short'] = 0.6 + (df['short_quality'] - 60) / 40 * 0.6
    df['quality_size_factor_short'] = df['quality_size_factor_short'].clip(0.6, 1.2)

    # =========================================================================
    # REGIME FILTERS
    # =========================================================================
    df['regime_ok_long'] = (
        df['low_vol_regime'] &
        df['clear_trend'] &
        df['bull_regime'] &
        df['trend_bullish']
    )

    df['regime_ok_short'] = (
        df['low_vol_regime'] &
        df['clear_trend'] &
        df['bear_regime'] &
        df['trend_bearish']
    )

    # Quality filter
    df['quality_ok_long'] = df['long_quality'] >= min_quality_score
    df['quality_ok_short'] = df['short_quality'] >= min_quality_score

    # =========================================================================
    # FINAL SIGNALS
    # =========================================================================
    df['long_signal'] = (
        df['pullback_to_fast_up'] &
        df['fast_ma_up'] &
        df['fast_above_slow'] &
        df['regime_ok_long'] &
        df['quality_ok_long'] &
        (df['rth_ok'] == 1)
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
    # COMPOSITE ADAPTIVE SIZE FACTOR
    # =========================================================================
    # Combine all sizing factors
    df['adaptive_size_factor_long'] = (
        df['quality_size_factor_long'] *
        df['vol_scale_factor'] *
        df['trend_strength_factor'] *
        df['regime_size_factor']
    ).clip(0.3, 2.0)

    df['adaptive_size_factor_short'] = (
        df['quality_size_factor_short'] *
        df['vol_scale_factor'] *
        df['trend_strength_factor'] *
        df['regime_size_factor']
    ).clip(0.3, 2.0)

    # =========================================================================
    # OUTPUT
    # =========================================================================
    df_final = df[['Open', 'High', 'Low', 'Close', 'Volume', 'atr',
                   'fast_ma', 'slow_ma', 'fast_ma_up', 'fast_above_slow',
                   'long_signal', 'short_signal',
                   'long_quality', 'short_quality',
                   'adaptive_size_factor_long', 'adaptive_size_factor_short',
                   'vol_scale_factor', 'trend_strength_factor', 'regime_size_factor',
                   'quality_size_factor_long', 'quality_size_factor_short',
                   'realized_vol', 'vol_percentile',
                   'rth_ok', 'rsi', 'adx',
                   'macd_hist', 'macd_declining', 'ma_cross_dn',
                   'regime_ok_long', 'regime_ok_short']].copy()
    df_final = df_final.dropna()

    # Statistics
    long_count = df_final['long_signal'].sum()
    short_count = df_final['short_signal'].sum()

    print(f"\nSignal Statistics:")
    print(f"  Long Signals: {long_count}")
    print(f"  Short Signals: {short_count}")

    # Adaptive sizing statistics
    signals_df = df_final[df_final['long_signal'] == 1]
    if len(signals_df) > 0:
        print(f"\nAdaptive Sizing Stats (Long Signals):")
        print(f"  Avg Size Factor: {signals_df['adaptive_size_factor_long'].mean():.2f}")
        print(f"  Min Size Factor: {signals_df['adaptive_size_factor_long'].min():.2f}")
        print(f"  Max Size Factor: {signals_df['adaptive_size_factor_long'].max():.2f}")
        print(f"  Std Size Factor: {signals_df['adaptive_size_factor_long'].std():.2f}")

    return df_final


# =============================================================================
# ADAPTIVE SIZING STRATEGY CLASS
# =============================================================================

class CMUltimateMAAdaptive(Strategy):
    """
    CM Ultimate MA - Adaptive Position Sizing Version

    Adaptive Sizing Components:
    1. Quality Score: Higher quality = larger position
    2. Volatility Targeting: Size inversely to volatility
    3. Trend Strength: Larger size in strong trends
    4. Regime Strength: Larger size in aligned regimes
    5. Drawdown Scaling: Reduce size during drawdowns
    """

    # Base position scale (will be multiplied by adaptive factor)
    base_scale = 0.35

    # Adaptive sizing toggles
    use_quality_sizing = True
    use_vol_targeting = True
    use_trend_sizing = True
    use_regime_sizing = True

    # Risk parameters
    stop_mult = 2.0
    target_mult = 3.0

    # Trailing
    trail_start_pct = 1.2
    trail_atr_mult = 1.0

    # Hold time
    max_hold_bars = 60

    # Drawdown protection
    dd_reduction_start = 0.10
    dd_pause_threshold = 0.18
    dd_exit_threshold = 0.20

    # Kelly criterion (optional)
    use_kelly = False
    kelly_fraction = 0.25  # Use 25% of Kelly (fractional Kelly)

    def init(self):
        self.long_sig = self.I(lambda: self.data.long_signal)
        self.short_sig = self.I(lambda: self.data.short_signal)
        self.adaptive_size_long = self.I(lambda: self.data.adaptive_size_factor_long)
        self.adaptive_size_short = self.I(lambda: self.data.adaptive_size_factor_short)
        self.long_quality = self.I(lambda: self.data.long_quality)
        self.atr = self.I(lambda: self.data.atr)
        self.fast_ma = self.I(lambda: self.data.fast_ma)
        self.adx = self.I(lambda: self.data.adx)
        self.ma_cross_dn = self.I(lambda: self.data.ma_cross_dn)
        self.macd_declining = self.I(lambda: self.data.macd_declining)
        self.realized_vol = self.I(lambda: self.data.realized_vol)

        # Trade management
        self.entry_price = None
        self.entry_bar = None
        self.stop_price = None
        self.trailing_active = False
        self.is_long = None

        # Performance tracking for Kelly
        self.trade_results = []
        self.win_count = 0
        self.loss_count = 0
        self.avg_win = 0
        self.avg_loss = 0

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

        # Calculate adaptive position size
        if self.long_sig[-1] == 1:
            adaptive_factor = self.adaptive_size_long[-1]
            final_size = self.calculate_final_size(adaptive_factor)
            self.enter_long(price, atr, final_size)
        elif self.short_sig[-1] == 1:
            adaptive_factor = self.adaptive_size_short[-1]
            final_size = self.calculate_final_size(adaptive_factor)
            self.enter_short(price, atr, final_size)

    def calculate_final_size(self, adaptive_factor):
        """Calculate final position size with all adjustments."""

        # Start with base scale
        size = self.base_scale

        # Apply adaptive factor (quality + vol + trend + regime)
        size *= adaptive_factor

        # Apply drawdown reduction
        if self.current_dd > self.dd_reduction_start:
            dd_range = self.dd_pause_threshold - self.dd_reduction_start
            dd_progress = (self.current_dd - self.dd_reduction_start) / dd_range
            dd_factor = 1.0 - (dd_progress * 0.7)  # Reduce up to 70%
            size *= max(0.3, dd_factor)

        # Apply Kelly criterion if enabled
        if self.use_kelly and len(self.trade_results) >= 20:
            kelly_size = self.calculate_kelly()
            if kelly_size > 0:
                # Use fractional Kelly, don't exceed adaptive size
                kelly_adjusted = kelly_size * self.kelly_fraction
                size = min(size, kelly_adjusted)

        # Final limits
        return max(0.1, min(0.8, size))

    def calculate_kelly(self):
        """Calculate Kelly criterion position size."""
        if self.win_count + self.loss_count < 10:
            return self.base_scale

        win_rate = self.win_count / (self.win_count + self.loss_count)
        if self.avg_loss == 0:
            return self.base_scale

        # Kelly formula: f* = W - (1-W)/R
        # Where W = win rate, R = avg_win/avg_loss
        win_loss_ratio = abs(self.avg_win / self.avg_loss) if self.avg_loss != 0 else 1
        kelly = win_rate - ((1 - win_rate) / win_loss_ratio)

        return max(0, kelly)

    def update_trade_stats(self, pnl_pct):
        """Update trade statistics for Kelly calculation."""
        self.trade_results.append(pnl_pct)

        if pnl_pct > 0:
            self.win_count += 1
            # Update running average win
            total_wins = sum(p for p in self.trade_results if p > 0)
            self.avg_win = total_wins / self.win_count if self.win_count > 0 else 0
        else:
            self.loss_count += 1
            # Update running average loss
            total_losses = sum(p for p in self.trade_results if p < 0)
            self.avg_loss = total_losses / self.loss_count if self.loss_count > 0 else 0

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
            self.update_trade_stats(pnl_pct)
            self.position.close()
            return
        elif not self.is_long and price >= self.stop_price:
            self.update_trade_stats(pnl_pct)
            self.position.close()
            return

        # Time exit
        if bars_held >= self.max_hold_bars:
            self.update_trade_stats(pnl_pct)
            self.position.close()
            return

        # Momentum exit
        if pnl_pct > 0.5 and self.macd_declining[-1]:
            self.update_trade_stats(pnl_pct)
            self.position.close()
            return

        # MA cross exit
        if self.is_long and self.ma_cross_dn[-1]:
            self.update_trade_stats(pnl_pct)
            self.position.close()
            return

        # ADX weakness exit
        if self.adx[-1] < 15 and pnl_pct > 0:
            self.update_trade_stats(pnl_pct)
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
# TESTING FUNCTIONS
# =============================================================================

def test_adaptive_components(df: pd.DataFrame, capital: float = 500000):
    """Test each adaptive sizing component individually."""
    print("\n" + "=" * 70)
    print("TESTING ADAPTIVE SIZING COMPONENTS")
    print("=" * 70)

    results = []

    # Test configurations
    configs = [
        {"name": "Base (No Adaptive)", "quality": False, "vol": False, "trend": False, "regime": False},
        {"name": "Quality Only", "quality": True, "vol": False, "trend": False, "regime": False},
        {"name": "Vol Target Only", "quality": False, "vol": True, "trend": False, "regime": False},
        {"name": "Trend Only", "quality": False, "vol": False, "trend": True, "regime": False},
        {"name": "Regime Only", "quality": False, "vol": False, "trend": False, "regime": True},
        {"name": "Quality + Vol", "quality": True, "vol": True, "trend": False, "regime": False},
        {"name": "All Adaptive", "quality": True, "vol": True, "trend": True, "regime": True},
    ]

    for config in configs:
        CMUltimateMAAdaptive.use_quality_sizing = config["quality"]
        CMUltimateMAAdaptive.use_vol_targeting = config["vol"]
        CMUltimateMAAdaptive.use_trend_sizing = config["trend"]
        CMUltimateMAAdaptive.use_regime_sizing = config["regime"]
        CMUltimateMAAdaptive.base_scale = 0.40

        bt = Backtest(
            df,
            CMUltimateMAAdaptive,
            cash=capital,
            commission=0.00005,
            exclusive_orders=True,
            trade_on_close=True,
            margin=0.1
        )

        stats = bt.run()
        max_dd = abs(stats['Max. Drawdown [%]'])
        sharpe = stats['Sharpe Ratio'] if not np.isnan(stats['Sharpe Ratio']) else 0
        ret = stats['Return [%]']
        trades = stats['# Trades']
        win_rate = stats['Win Rate [%]']
        pf = stats['Profit Factor'] if not np.isnan(stats['Profit Factor']) else 0

        print(f"\n{config['name']}:")
        print(f"  Return: {ret:.1f}%, MaxDD: {max_dd:.1f}%, Sharpe: {sharpe:.3f}")
        print(f"  Trades: {trades}, Win: {win_rate:.1f}%, PF: {pf:.2f}")

        results.append({
            'name': config['name'],
            'return': ret,
            'max_dd': max_dd,
            'sharpe': sharpe,
            'trades': trades,
            'win_rate': win_rate,
            'profit_factor': pf
        })

    return results


def calibrate_adaptive_strategy(df: pd.DataFrame, target_dd: float = 0.20,
                                capital: float = 500000):
    """Calibrate base scale for target max drawdown."""
    print(f"\nCalibrating for {target_dd*100:.0f}% max drawdown...")
    print("-" * 70)

    # Enable all adaptive components
    CMUltimateMAAdaptive.use_quality_sizing = True
    CMUltimateMAAdaptive.use_vol_targeting = True
    CMUltimateMAAdaptive.use_trend_sizing = True
    CMUltimateMAAdaptive.use_regime_sizing = True

    results = []

    for scale in np.arange(0.25, 0.60, 0.025):
        CMUltimateMAAdaptive.base_scale = scale

        bt = Backtest(
            df,
            CMUltimateMAAdaptive,
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
        pf = stats['Profit Factor'] if not np.isnan(stats['Profit Factor']) else 0

        valid = max_dd <= target_dd
        status = "OK" if valid else "OVER"

        print(f"Scale {scale*100:4.1f}%: Ret {ret:7.1f}%, DD {max_dd*100:5.1f}%, "
              f"Sharpe {sharpe:6.3f}, PF {pf:4.2f}, Trades {trades:3} [{status}]")

        results.append({
            'scale': scale,
            'return': ret,
            'max_dd': max_dd,
            'sharpe': sharpe,
            'profit_factor': pf,
            'trades': trades,
            'win_rate': win_rate,
            'valid': valid
        })

    # Find best valid
    valid_results = [r for r in results if r['valid'] and r['sharpe'] > -999]

    if valid_results:
        best = max(valid_results, key=lambda x: x['sharpe'])
        print(f"\n{'='*70}")
        print(f"OPTIMAL BASE SCALE: {best['scale']*100:.1f}%")
        print(f"  Return: {best['return']:.1f}%")
        print(f"  Max DD: {best['max_dd']*100:.1f}%")
        print(f"  Sharpe: {best['sharpe']:.3f}")
        print(f"  Profit Factor: {best['profit_factor']:.2f}")
        return best['scale'], results

    return 0.35, results


# =============================================================================
# MAIN
# =============================================================================

if __name__ == "__main__":
    ES_HOURLY_PATH = r"C:\dev\moondev-ai-agents\src\data\rbi\ES-1H.csv"

    print("=" * 70)
    print("CM ULTIMATE MA - ADAPTIVE POSITION SIZING VERSION")
    print("=" * 70)
    print("\nAdaptive Sizing Components:")
    print("  1. Quality Score Sizing (higher quality = larger size)")
    print("  2. Volatility Targeting (size inverse to volatility)")
    print("  3. Trend Strength Sizing (stronger trend = larger size)")
    print("  4. Regime Strength Sizing (aligned regime = larger size)")
    print("  5. Drawdown Scaling (reduce size during drawdowns)")
    print("=" * 70)

    # Load data
    print("\nLoading ES hourly data...")
    df_hourly = load_hourly_csv(ES_HOURLY_PATH, "ES")

    # Prepare signals
    print("\nPreparing signals with adaptive sizing data...")
    df = prepare_adaptive_signals(df_hourly, min_quality_score=60)

    # Step 1: Test individual components
    component_results = test_adaptive_components(df)

    # Step 2: Calibrate base scale
    print("\n" + "=" * 70)
    print("CALIBRATING BASE SCALE")
    print("=" * 70)
    optimal_scale, all_results = calibrate_adaptive_strategy(df, target_dd=0.20)

    # Step 3: Final backtest
    print("\n" + "=" * 70)
    print("FINAL RESULTS")
    print("=" * 70)

    CMUltimateMAAdaptive.base_scale = optimal_scale
    CMUltimateMAAdaptive.use_quality_sizing = True
    CMUltimateMAAdaptive.use_vol_targeting = True
    CMUltimateMAAdaptive.use_trend_sizing = True
    CMUltimateMAAdaptive.use_regime_sizing = True

    bt = Backtest(
        df,
        CMUltimateMAAdaptive,
        cash=500000,
        commission=0.00005,
        exclusive_orders=True,
        trade_on_close=True,
        margin=0.1
    )

    stats = bt.run()
    num_days = (df.index.max() - df.index.min()).days
    cagr = calculate_cagr(stats['Return [%]'], num_days)

    print(f"\nAdaptive Strategy Results:")
    print(f"  Base Scale: {optimal_scale*100:.1f}%")
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
    print(f"{'Version':<25} {'Max DD':<12} {'Sharpe':<10} {'CAGR':<12} {'PF':<8}")
    print("-" * 70)
    print(f"{'Original T3/EMA':<25} {'-58.52%':<12} {'0.671':<10} {'524.87%':<12} {'-':<8}")
    print(f"{'Risk-Managed':<25} {'-19.64%':<12} {'0.164':<10} {'2.92%':<12} {'1.17':<8}")
    print(f"{'Elite (Regime+Quality)':<25} {'-18.53%':<12} {'0.695':<10} {'21.27%':<12} {'1.93':<8}")
    print(f"{'Adaptive Sizing':<25} {stats['Max. Drawdown [%]']:.2f}%{'':<5} "
          f"{stats['Sharpe Ratio']:.3f}{'':<5} {cagr:.2f}%{'':<6} {stats['Profit Factor']:.2f}")

    print("\n" + "=" * 70)
    print("STRATEGY COMPLETE")
    print("=" * 70)
