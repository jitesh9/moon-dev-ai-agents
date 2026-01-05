"""
ES Intraday Swing Strategy Backtest - With Additional Filters
=============================================================
Converted from PineScript: ES Intraday Swing 2025 – Final Production Version

Strategy Components:
1. Daily 50 SMA Regime Filter (bull/bear regime)
2. SuperTrend (10 period ATR, 3.0 multiplier)
3. 34/55 EMA Crossover
4. Pullback Entry Trigger (best performer)
5. ATR-based Exits: 1.5x stop, 3R target

Additional Filters:
- Volume Filter: Above average volume
- Time Filter: Regular Trading Hours only
- Volatility Filter: ATR within normal range
- Trend Strength: ADX filter

Author: Moon Dev AI
"""

import pandas as pd
import numpy as np
from backtesting import Backtest, Strategy
import warnings
warnings.filterwarnings('ignore')


def load_data(filepath: str) -> pd.DataFrame:
    """Load OHLCV data from CSV file."""
    df = pd.read_csv(filepath, parse_dates=['datetime'])
    df.set_index('datetime', inplace=True)
    df.columns = [c.capitalize() for c in df.columns]
    return df


def calculate_supertrend(high, low, close, period=10, multiplier=3.0):
    """Calculate SuperTrend indicator."""
    tr1 = high - low
    tr2 = abs(high - close.shift(1))
    tr3 = abs(low - close.shift(1))
    tr = pd.concat([tr1, tr2, tr3], axis=1).max(axis=1)
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
    """Calculate ADX (Average Directional Index) for trend strength."""
    # True Range
    tr1 = high - low
    tr2 = abs(high - close.shift(1))
    tr3 = abs(low - close.shift(1))
    tr = pd.concat([tr1, tr2, tr3], axis=1).max(axis=1)

    # Directional Movement
    up_move = high - high.shift(1)
    down_move = low.shift(1) - low

    plus_dm = pd.Series(np.where((up_move > down_move) & (up_move > 0), up_move, 0), index=high.index)
    minus_dm = pd.Series(np.where((down_move > up_move) & (down_move > 0), down_move, 0), index=high.index)

    # Smoothed values
    atr = tr.rolling(window=period, min_periods=1).mean()
    plus_di = 100 * plus_dm.rolling(window=period, min_periods=1).mean() / (atr + 0.0001)
    minus_di = 100 * minus_dm.rolling(window=period, min_periods=1).mean() / (atr + 0.0001)

    # ADX
    dx = 100 * abs(plus_di - minus_di) / (plus_di + minus_di + 0.0001)
    adx = dx.rolling(window=period, min_periods=1).mean()

    return adx, plus_di, minus_di


def prepare_data(hourly_path: str, daily_path: str, filters: dict = None, debug: bool = False) -> pd.DataFrame:
    """
    Prepare multi-timeframe data for backtesting with optional filters.

    Args:
        filters: dict with filter settings:
            - volume: bool - require above average volume
            - rth_only: bool - Regular Trading Hours only (9:30-16:00 ET)
            - volatility: bool - filter extreme volatility
            - adx: bool - require trending market (ADX > threshold)
            - adx_threshold: float - ADX threshold (default 20)
    """
    if filters is None:
        filters = {}

    df_1h = load_data(hourly_path)
    df_daily = load_data(daily_path)

    print(f"Loaded 1H data: {len(df_1h):,} bars")
    print(f"Loaded Daily data: {len(df_daily):,} bars")

    # Daily 50 SMA regime filter
    df_daily['sma_50'] = df_daily['Close'].rolling(window=50).mean()
    df_daily['bull_regime'] = (df_daily['Close'] > df_daily['sma_50']).astype(int)

    df_daily['date'] = df_daily.index.date
    daily_regime = df_daily[['date', 'sma_50', 'bull_regime']].copy()
    daily_regime.columns = ['date', 'daily_sma_50', 'daily_bull_regime']

    df_1h['date'] = df_1h.index.date
    df = df_1h.merge(daily_regime, on='date', how='left')
    df.set_index(df_1h.index, inplace=True)

    df['daily_sma_50'] = df['daily_sma_50'].ffill()
    df['daily_bull_regime'] = df['daily_bull_regime'].ffill().fillna(0)

    # EMAs (34 and 55)
    df['ema_34'] = df['Close'].ewm(span=34, adjust=False).mean()
    df['ema_55'] = df['Close'].ewm(span=55, adjust=False).mean()
    df['ema_bull'] = (df['ema_34'] > df['ema_55']).astype(int)

    # SuperTrend
    st, st_dir = calculate_supertrend(df['High'], df['Low'], df['Close'], period=10, multiplier=3.0)
    df['supertrend'] = st
    df['st_direction'] = st_dir

    # ATR for stops
    tr = pd.concat([
        df['High'] - df['Low'],
        abs(df['High'] - df['Close'].shift(1)),
        abs(df['Low'] - df['Close'].shift(1))
    ], axis=1).max(axis=1)
    df['atr_14'] = tr.rolling(window=14).mean()

    # ============================================================
    # ADDITIONAL FILTERS
    # ============================================================

    # 1. VOLUME FILTER: Above 20-period average volume
    df['vol_sma_20'] = df['Volume'].rolling(window=20).mean()
    df['volume_ok'] = (df['Volume'] >= df['vol_sma_20'] * 0.8).astype(int)  # Allow 80% of avg

    # 2. TIME FILTER: Regular Trading Hours (approximation for futures)
    # ES futures: Main session roughly 9:30 AM - 4:00 PM ET
    df['hour'] = df.index.hour
    # Assuming data is in ET timezone, RTH is roughly 9-16
    df['rth_ok'] = ((df['hour'] >= 9) & (df['hour'] < 16)).astype(int)

    # 3. VOLATILITY FILTER: ATR within normal range (not too high, not too low)
    df['atr_sma_50'] = df['atr_14'].rolling(window=50).mean()
    df['atr_ratio'] = df['atr_14'] / df['atr_sma_50']
    # Filter: ATR between 0.5x and 2x of average (avoid extremes)
    df['volatility_ok'] = ((df['atr_ratio'] >= 0.5) & (df['atr_ratio'] <= 2.0)).astype(int)

    # 4. ADX FILTER: Trending market (ADX > threshold)
    adx, plus_di, minus_di = calculate_adx(df['High'], df['Low'], df['Close'], period=14)
    df['adx'] = adx
    df['plus_di'] = plus_di
    df['minus_di'] = minus_di
    adx_threshold = filters.get('adx_threshold', 20)
    df['adx_ok'] = (df['adx'] >= adx_threshold).astype(int)

    # 5. TREND ALIGNMENT: +DI > -DI for longs (stronger uptrend)
    df['di_aligned_long'] = (df['plus_di'] > df['minus_di']).astype(int)
    df['di_aligned_short'] = (df['minus_di'] > df['plus_di']).astype(int)

    # ============================================================
    # PULLBACK ENTRY TRIGGER (Best performer)
    # ============================================================
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

    # ============================================================
    # BUILD FILTER MASK
    # ============================================================
    filter_mask_long = pd.Series(True, index=df.index)
    filter_mask_short = pd.Series(True, index=df.index)
    active_filters = []

    if filters.get('volume', False):
        filter_mask_long &= (df['volume_ok'] == 1)
        filter_mask_short &= (df['volume_ok'] == 1)
        active_filters.append('Volume')

    if filters.get('rth_only', False):
        filter_mask_long &= (df['rth_ok'] == 1)
        filter_mask_short &= (df['rth_ok'] == 1)
        active_filters.append('RTH')

    if filters.get('volatility', False):
        filter_mask_long &= (df['volatility_ok'] == 1)
        filter_mask_short &= (df['volatility_ok'] == 1)
        active_filters.append('Volatility')

    if filters.get('adx', False):
        filter_mask_long &= (df['adx_ok'] == 1)
        filter_mask_short &= (df['adx_ok'] == 1)
        active_filters.append(f'ADX>{adx_threshold}')

    if filters.get('di_alignment', False):
        filter_mask_long &= (df['di_aligned_long'] == 1)
        filter_mask_short &= (df['di_aligned_short'] == 1)
        active_filters.append('DI Alignment')

    # ============================================================
    # FINAL SIGNALS
    # ============================================================
    df['long_signal'] = (
        (df['daily_bull_regime'] == 1) &
        (df['st_direction'] == 1) &
        (df['ema_bull'] == 1) &
        (df['pullback_long'] == 1) &
        filter_mask_long
    ).astype(int)

    df['short_signal'] = (
        (df['daily_bull_regime'] == 0) &
        (df['st_direction'] == -1) &
        (df['ema_bull'] == 0) &
        (df['pullback_short'] == 1) &
        filter_mask_short
    ).astype(int)

    # Only drop rows where essential columns have NaN
    essential_cols = ['Open', 'High', 'Low', 'Close', 'Volume', 'atr_14', 'ema_34', 'ema_55']
    df = df.dropna(subset=essential_cols)
    df = df.drop(columns=['date', 'hour'], errors='ignore')

    # Fill any remaining NaN in filter columns with safe defaults
    df['adx'] = df['adx'].fillna(0)
    df['plus_di'] = df['plus_di'].fillna(0)
    df['minus_di'] = df['minus_di'].fillna(0)
    df['adx_ok'] = df['adx_ok'].fillna(0)
    df['di_aligned_long'] = df['di_aligned_long'].fillna(0)
    df['di_aligned_short'] = df['di_aligned_short'].fillna(0)

    if debug:
        print(f"\n{'='*60}")
        filter_str = ', '.join(active_filters) if active_filters else 'None'
        print(f"FILTERS ACTIVE: {filter_str}")
        print(f"{'='*60}")
        print(f"Volume OK bars:       {(df['volume_ok'] == 1).sum():,}")
        print(f"RTH OK bars:          {(df['rth_ok'] == 1).sum():,}")
        print(f"Volatility OK bars:   {(df['volatility_ok'] == 1).sum():,}")
        print(f"ADX OK bars:          {(df['adx_ok'] == 1).sum():,}")
        print(f"DI Aligned (Long):    {(df['di_aligned_long'] == 1).sum():,}")
        print(f"Pullback Long:        {(df['pullback_long'] == 1).sum():,}")
        print(f"Final Long Signals:   {(df['long_signal'] == 1).sum():,}")

    print(f"Final dataset: {len(df):,} bars")
    return df


class ESIntradaySwing(Strategy):
    """ES Intraday Swing Strategy."""

    stop_mult = 1.5
    target_mult = 3.0
    enable_shorts = False

    def init(self):
        self.long_signal = self.I(lambda: self.data.long_signal)
        self.short_signal = self.I(lambda: self.data.short_signal)
        self.atr = self.I(lambda: self.data.atr_14)

    def next(self):
        if self.position:
            return

        price = self.data.Close[-1]
        atr = self.atr[-1]

        if np.isnan(atr) or atr <= 0:
            return

        stop_dist = self.stop_mult * atr
        target_dist = self.target_mult * stop_dist

        if self.long_signal[-1] == 1:
            self.buy(sl=price - stop_dist, tp=price + target_dist)
            return

        if self.enable_shorts and self.short_signal[-1] == 1:
            self.sell(sl=price + stop_dist, tp=price - target_dist)


def run_backtest(
    hourly_path: str,
    daily_path: str,
    filters: dict = None,
    initial_cash: float = 50000,
    commission: float = 0.00005,
    debug: bool = True
):
    """Run backtest with specified filters."""
    filter_str = ', '.join([k for k, v in (filters or {}).items() if v]) or 'None'
    print(f"\n{'='*70}")
    print(f"ES INTRADAY SWING - PULLBACK + FILTERS: {filter_str}")
    print(f"{'='*70}")

    df = prepare_data(hourly_path, daily_path, filters=filters, debug=debug)

    bt = Backtest(
        df,
        ESIntradaySwing,
        cash=initial_cash,
        commission=commission,
        exclusive_orders=True,
        trade_on_close=True
    )

    stats = bt.run()

    print(f"\n{'-'*60}")
    print(f"RESULTS")
    print(f"{'-'*60}")
    print(f"  Return:          {stats['Return [%]']:.2f}%")
    print(f"  Buy & Hold:      {stats['Buy & Hold Return [%]']:.2f}%")
    print(f"  # Trades:        {stats['# Trades']}")
    if stats['# Trades'] > 0:
        print(f"  Win Rate:        {stats['Win Rate [%]']:.2f}%")
        print(f"  Profit Factor:   {stats['Profit Factor']:.3f}")
        print(f"  Max Drawdown:    {stats['Max. Drawdown [%]']:.2f}%")
        print(f"  Sharpe Ratio:    {stats['Sharpe Ratio']:.3f}")
        print(f"  Avg Trade:       {stats['Avg. Trade [%]']:.3f}%")

    return stats, bt


def compare_filters(hourly_path: str, daily_path: str):
    """Compare different filter combinations."""
    filter_configs = [
        {'name': 'Baseline (No Filters)', 'filters': {}},
        {'name': 'Volume Only', 'filters': {'volume': True}},
        {'name': 'RTH Only', 'filters': {'rth_only': True}},
        {'name': 'Volatility Only', 'filters': {'volatility': True}},
        {'name': 'ADX > 20', 'filters': {'adx': True, 'adx_threshold': 20}},
        {'name': 'ADX > 25', 'filters': {'adx': True, 'adx_threshold': 25}},
        {'name': 'DI Alignment', 'filters': {'di_alignment': True}},
        {'name': 'Volume + RTH', 'filters': {'volume': True, 'rth_only': True}},
        {'name': 'Volume + ADX', 'filters': {'volume': True, 'adx': True}},
        {'name': 'All Filters', 'filters': {'volume': True, 'rth_only': True, 'volatility': True, 'adx': True}},
    ]

    results = {}

    for config in filter_configs:
        stats, bt = run_backtest(
            hourly_path=hourly_path,
            daily_path=daily_path,
            filters=config['filters'],
            debug=False
        )
        results[config['name']] = stats

    # Comparison table
    print("\n" + "=" * 100)
    print("FILTER COMPARISON - PULLBACK ENTRY")
    print("=" * 100)
    print(f"{'Filter Config':<25} {'Return':>10} {'Trades':>8} {'Win%':>8} {'PF':>8} {'MaxDD':>10} {'Sharpe':>8}")
    print("-" * 100)

    for name, stats in results.items():
        ret = stats['Return [%]']
        trades = stats['# Trades']
        win_rate = stats.get('Win Rate [%]', 0) or 0
        pf = stats.get('Profit Factor', 0) or 0
        max_dd = stats.get('Max. Drawdown [%]', 0) or 0
        sharpe = stats.get('Sharpe Ratio', 0) or 0

        print(f"{name:<25} {ret:>9.2f}% {trades:>8} {win_rate:>7.1f}% {pf:>8.2f} {max_dd:>9.2f}% {sharpe:>8.3f}")

    # Find best configurations
    print("-" * 100)

    # Filter out configs with 0 trades
    valid_results = {k: v for k, v in results.items() if v['# Trades'] > 0}

    if valid_results:
        best_return = max(valid_results.items(), key=lambda x: x[1]['Return [%]'])
        best_sharpe = max(valid_results.items(), key=lambda x: x[1].get('Sharpe Ratio', 0) or 0)
        best_pf = max(valid_results.items(), key=lambda x: x[1].get('Profit Factor', 0) or 0)
        best_winrate = max(valid_results.items(), key=lambda x: x[1].get('Win Rate [%]', 0) or 0)
        lowest_dd = max(valid_results.items(), key=lambda x: x[1].get('Max. Drawdown [%]', -100))

        print(f"Best Return:        {best_return[0]} ({best_return[1]['Return [%]']:.2f}%)")
        print(f"Best Sharpe:        {best_sharpe[0]} ({best_sharpe[1].get('Sharpe Ratio', 0):.3f})")
        print(f"Best Profit Factor: {best_pf[0]} ({best_pf[1].get('Profit Factor', 0):.3f})")
        print(f"Best Win Rate:      {best_winrate[0]} ({best_winrate[1].get('Win Rate [%]', 0):.2f}%)")
        print(f"Lowest Drawdown:    {lowest_dd[0]} ({lowest_dd[1].get('Max. Drawdown [%]', 0):.2f}%)")

    return results


if __name__ == "__main__":
    import os

    script_dir = os.path.dirname(os.path.abspath(__file__))
    project_root = os.path.dirname(os.path.dirname(script_dir))

    hourly_path = os.path.join(project_root, 'src', 'data', 'rbi', 'ES-1H.csv')
    daily_path = os.path.join(project_root, 'src', 'data', 'rbi', 'ES-1D.csv')

    # Compare all filter combinations
    results = compare_filters(hourly_path, daily_path)
