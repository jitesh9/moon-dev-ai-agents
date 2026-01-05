"""
GC (Gold Futures) Divergence-Based High Sharpe Strategy
========================================================
RSI/MACD divergence detection adapted for Gold futures characteristics.

A bullish divergence occurs when:
- Price makes a LOWER low
- But RSI or MACD makes a HIGHER low

Gold-specific adaptations:
- Different volatility profile than ES
- Extended trading hours (nearly 24h)
- Higher tick value ($10 per 0.10 point)

Target: 2.0+ Sharpe Ratio with 100-500 trades
Author: Moon Dev AI
"""

import pandas as pd
import numpy as np
from backtesting import Backtest, Strategy
import warnings
warnings.filterwarnings('ignore')


def load_gc_data(data_path: str) -> pd.DataFrame:
    """Load GC continuous contract 1-minute data."""
    print(f"Loading GC data from: {data_path}")

    df = pd.read_csv(data_path)
    print(f"Raw data: {len(df):,} rows")

    # Parse datetime
    df['datetime'] = pd.to_datetime(df['ts_event'])

    # Rename columns for backtesting.py
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

    print(f"Date range: {df_final.index.min()} to {df_final.index.max()}")
    print(f"Final: {len(df_final):,} 1-minute bars")

    return df_final


def detect_swing_lows(prices: pd.Series, lookback: int = 10) -> pd.Series:
    """Detect swing lows (local minima)."""
    swing_lows = pd.Series(index=prices.index, dtype=bool)
    swing_lows[:] = False

    for i in range(lookback, len(prices) - lookback):
        window = prices.iloc[i-lookback:i+lookback+1]
        if prices.iloc[i] == window.min():
            swing_lows.iloc[i] = True

    return swing_lows


def detect_divergence(df: pd.DataFrame, swing_period: int = 12, min_separation: int = 8) -> pd.Series:
    """
    Detect bullish divergence: Lower price low + Higher RSI/MACD low.
    This is the KEY pattern for high Sharpe strategies.
    """
    divergence_signal = pd.Series(index=df.index, dtype=int)
    divergence_signal[:] = 0

    # Detect swing lows
    swing_lows = detect_swing_lows(df['Low'], lookback=swing_period // 2)

    # Track swing low history
    swing_history = []  # (index_position, price_low, rsi_low, macd_low)

    for i in range(len(df)):
        if not swing_lows.iloc[i]:
            continue

        price_low = df['Low'].iloc[i]
        rsi_low = df['rsi'].iloc[i]
        macd_low = df['macd_hist'].iloc[i]

        # Store swing
        swing_history.append((i, price_low, rsi_low, macd_low))

        # Keep only recent swings
        if len(swing_history) > 6:
            swing_history.pop(0)

        # Check for divergence against previous swings
        if len(swing_history) >= 2:
            for j in range(len(swing_history) - 1):
                prev_idx, prev_price, prev_rsi, prev_macd = swing_history[j]

                # Check separation
                if i - prev_idx < min_separation:
                    continue

                # BULLISH DIVERGENCE:
                # Price makes LOWER low, but RSI or MACD makes HIGHER low
                price_lower = price_low < prev_price * 0.998  # 0.2% lower
                rsi_higher = rsi_low > prev_rsi * 1.02  # RSI 2% higher
                macd_higher = macd_low > prev_macd  # MACD higher (can be negative)

                if price_lower and (rsi_higher or macd_higher):
                    divergence_signal.iloc[i] = 1
                    break

    return divergence_signal


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
    Aggregate to HOURLY and prepare divergence-based signals for GC.
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

    # EMAs
    df_1h['ema_13'] = df_1h['Close'].ewm(span=13, adjust=False).mean()
    df_1h['ema_34'] = df_1h['Close'].ewm(span=34, adjust=False).mean()
    df_1h['ema_55'] = df_1h['Close'].ewm(span=55, adjust=False).mean()

    # SuperTrend
    st, st_dir = calculate_supertrend(
        df_1h['High'], df_1h['Low'], df_1h['Close'],
        period=10, multiplier=3.0
    )
    df_1h['st_direction'] = st_dir

    # ATR - Gold specific (larger values due to higher price)
    tr = pd.concat([
        df_1h['High'] - df_1h['Low'],
        abs(df_1h['High'] - df_1h['Close'].shift(1)),
        abs(df_1h['Low'] - df_1h['Close'].shift(1))
    ], axis=1).max(axis=1)
    df_1h['atr'] = tr.rolling(window=14).mean()
    df_1h['atr_sma'] = df_1h['atr'].rolling(window=20).mean()

    # RSI (key for divergence)
    delta = df_1h['Close'].diff()
    gain = delta.where(delta > 0, 0).rolling(window=14).mean()
    loss = (-delta.where(delta < 0, 0)).rolling(window=14).mean()
    rs = gain / (loss + 1e-10)
    df_1h['rsi'] = 100 - (100 / (1 + rs))

    # MACD and histogram (key for divergence)
    ema_12 = df_1h['Close'].ewm(span=12, adjust=False).mean()
    ema_26 = df_1h['Close'].ewm(span=26, adjust=False).mean()
    df_1h['macd'] = ema_12 - ema_26
    df_1h['macd_signal'] = df_1h['macd'].ewm(span=9, adjust=False).mean()
    df_1h['macd_hist'] = df_1h['macd'] - df_1h['macd_signal']

    # Volume confirmation
    df_1h['volume_sma'] = df_1h['Volume'].rolling(window=20).mean()
    df_1h['volume_spike'] = (df_1h['Volume'] > df_1h['volume_sma'] * 1.5).astype(int)

    # GC Trading Hours Filter (main liquidity hours: 8 AM - 5 PM ET)
    # Gold trades nearly 24h but main session is COMEX hours
    df_1h['hour'] = df_1h.index.hour
    df_1h['rth_ok'] = ((df_1h['hour'] >= 8) & (df_1h['hour'] < 17)).astype(int)

    # ---- DIVERGENCE DETECTION (the key pattern!) ----
    print("\nDetecting divergences...")
    df_1h['divergence'] = detect_divergence(df_1h, swing_period=12, min_separation=8)

    # Confirmation factors
    df_1h['ema_bull'] = (df_1h['ema_13'] > df_1h['ema_34']).astype(int)
    df_1h['st_bull'] = (df_1h['st_direction'] == 1).astype(int)
    df_1h['rsi_good'] = ((df_1h['rsi'] > 30) & (df_1h['rsi'] < 70)).astype(int)
    df_1h['macd_improving'] = (df_1h['macd_hist'] > df_1h['macd_hist'].shift(1)).astype(int)
    df_1h['volatility_ok'] = (df_1h['atr'] > df_1h['atr_sma'] * 0.8).astype(int)

    # Count confirmations
    df_1h['confirmations'] = (
        df_1h['bull_regime'] +
        df_1h['ema_bull'] +
        df_1h['st_bull'] +
        df_1h['rsi_good'] +
        df_1h['macd_improving'] +
        df_1h['volatility_ok']
    )

    # DIVERGENCE-BASED ENTRY (the key difference!)
    # Enter on divergence + 4+ confirmations
    df_1h['long_signal'] = (
        (df_1h['divergence'] == 1) &
        (df_1h['confirmations'] >= 4) &
        (df_1h['rth_ok'] == 1)
    ).astype(int)

    # Prepare final
    df_final = df_1h[['Open', 'High', 'Low', 'Close', 'Volume',
                      'atr', 'ema_34', 'long_signal', 'rth_ok',
                      'bull_regime', 'st_direction', 'rsi',
                      'macd', 'macd_signal', 'macd_hist',
                      'confirmations', 'divergence']].copy()
    df_final = df_final.dropna()

    print(f"\nPrepared: {len(df_final):,} hourly bars")
    print(f"Divergence signals: {df_final['divergence'].sum():,}")
    print(f"Long signals (div + confirmations): {df_final['long_signal'].sum():,}")

    return df_final


class GCDivergenceSharpeStrategy(Strategy):
    """
    Divergence-Based High Sharpe Strategy for Gold Futures.

    Key insight: Divergence signals are more reliable than trend-following,
    leading to higher win rates and better Sharpe ratios.
    """
    # Risk parameters - adjusted for gold volatility
    stop_mult = 1.5       # ATR multiplier for stop
    target_mult = 2.5     # ATR multiplier for target

    # Trailing parameters
    trail_start_pct = 1.5   # Start trailing at 1.5% profit
    trail_atr_mult = 1.0    # Trail by 1.0 ATR

    # Hold parameters
    max_hold_bars = 60    # Don't hold too long

    def init(self):
        self.long_sig = self.I(lambda: self.data.long_signal)
        self.atr = self.I(lambda: self.data.atr)
        self.st_dir = self.I(lambda: self.data.st_direction)
        self.macd_hist = self.I(lambda: self.data.macd_hist)
        self.rsi = self.I(lambda: self.data.rsi)
        self.divergence = self.I(lambda: self.data.divergence)

        # Trade management
        self.entry_price = None
        self.entry_bar = None
        self.stop_price = None
        self.target_price = None
        self.trailing_active = False

    def next(self):
        price = self.data.Close[-1]
        atr = self.atr[-1]

        if np.isnan(atr) or atr <= 0:
            return

        # Manage existing position
        if self.position:
            self.manage_position()
            return

        # Look for divergence-based entry
        if self.long_sig[-1] == 1:
            stop_dist = self.stop_mult * atr
            target_dist = self.target_mult * atr

            self.entry_price = price
            self.entry_bar = len(self.data)
            self.stop_price = price - stop_dist
            self.target_price = price + target_dist
            self.trailing_active = False

            self.buy(sl=self.stop_price, tp=self.target_price)

    def manage_position(self):
        """Position management with trailing stop."""
        price = self.data.Close[-1]
        atr = self.atr[-1]
        current_bar = len(self.data)

        # Calculate profit %
        pnl_pct = ((price / self.entry_price) - 1) * 100

        # Start trailing after threshold
        if pnl_pct >= self.trail_start_pct:
            self.trailing_active = True
            new_stop = price - (self.trail_atr_mult * atr)
            if new_stop > self.stop_price:
                self.stop_price = new_stop

        # Check manual stop (for trailing)
        if price <= self.stop_price:
            self.position.close()
            return

        # Time exit
        bars_held = current_bar - self.entry_bar
        if bars_held >= self.max_hold_bars:
            self.position.close()
            return

        # Momentum reversal exit (only if profitable)
        if pnl_pct > 0.5:
            # MACD histogram turning down
            if (len(self.macd_hist) > 3 and
                self.macd_hist[-1] < self.macd_hist[-2] < self.macd_hist[-3]):
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


def run_backtest(df: pd.DataFrame, optimize: bool = False, capital: float = 500000):
    print("\n" + "=" * 70)
    print("GC (GOLD) DIVERGENCE-BASED SHARPE STRATEGY")
    print("(Using RSI/MACD divergence - key pattern for 2.0+ Sharpe)")
    print("=" * 70)

    bt = Backtest(
        df,
        GCDivergenceSharpeStrategy,
        cash=capital,
        commission=0.00005,
        exclusive_orders=True,
        trade_on_close=True,
        margin=1/10  # 10:1 margin for futures
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
            stop_mult=[1.0, 1.25, 1.5, 2.0, 2.5],
            target_mult=[1.5, 2.0, 2.5, 3.0, 4.0],
            trail_start_pct=[1.0, 1.5, 2.0, 2.5],
            trail_atr_mult=[0.8, 1.0, 1.2, 1.5],
            max_hold_bars=[40, 60, 80, 100],
            maximize='Sharpe Ratio',
            constraint=lambda p: p.target_mult >= p.stop_mult,
            return_heatmap=False
        )

        opt_cagr = calculate_cagr(stats['Return [%]'], num_days)

        print(f"\nOptimized (Max Sharpe):")
        print(f"  Stop: {stats._strategy.stop_mult}, Target: {stats._strategy.target_mult}")
        print(f"  Trail Start: {stats._strategy.trail_start_pct}%, ATR: {stats._strategy.trail_atr_mult}")
        print(f"  Max Hold: {stats._strategy.max_hold_bars}")
        print(f"  Return: {stats['Return [%]']:.2f}%, CAGR: {opt_cagr:.2f}%")
        print(f"  Trades: {stats['# Trades']}, Win Rate: {stats['Win Rate [%]']:.2f}%")
        print(f"  Sharpe: {stats['Sharpe Ratio']:.3f}, Max DD: {stats['Max. Drawdown [%]']:.2f}%")

        # Validation
        print("\n" + "=" * 70)
        print("SHARPE RATIO VALIDATION (Target: 2.0)")
        print("=" * 70)

        sharpe_target = 2.0
        trade_target = 100

        opt_pass = stats['Sharpe Ratio'] >= sharpe_target
        trade_pass = stats['# Trades'] >= trade_target

        print(f"Optimized Sharpe: {stats['Sharpe Ratio']:.3f} {'PASS' if opt_pass else 'FAIL'}")
        print(f"Trade Count: {stats['# Trades']} {'PASS' if trade_pass else 'FAIL'} (target: {trade_target}+)")

        if opt_pass and trade_pass:
            print("\nSUCCESS! Strategy achieves 2.0+ Sharpe with 100+ trades!")
        elif stats['Sharpe Ratio'] >= 1.5:
            print(f"\nGood progress! Sharpe {stats['Sharpe Ratio']:.3f} is approaching 2.0 target")
        else:
            print(f"\nCurrent Sharpe: {stats['Sharpe Ratio']:.3f} - divergence approach may need tuning")

        return stats, baseline

    return baseline


if __name__ == "__main__":
    data_path = r"C:\dev\databento\GC_1minute\gc_continuous_1m.csv"
    df_1m = load_gc_data(data_path)
    df = prepare_signals(df_1m)
    results = run_backtest(df, optimize=True, capital=500000)
