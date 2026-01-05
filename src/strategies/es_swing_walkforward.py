"""
ES Intraday Swing Strategy - Walk-Forward Validation
=====================================================
Tests strategy robustness by:
1. Training on rolling windows (in-sample)
2. Testing on subsequent periods (out-of-sample)
3. Aggregating out-of-sample results

This helps detect overfitting and validates parameter stability.

Author: Moon Dev AI
"""

import pandas as pd
import numpy as np
from backtesting import Backtest, Strategy
from datetime import datetime, timedelta
import warnings
warnings.filterwarnings('ignore')


def load_data(filepath: str) -> pd.DataFrame:
    df = pd.read_csv(filepath, parse_dates=['datetime'])
    df.set_index('datetime', inplace=True)
    df.columns = [c.capitalize() for c in df.columns]
    return df


def calculate_supertrend(high, low, close, period=10, multiplier=3.0):
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


def prepare_data(hourly_path: str, daily_path: str) -> pd.DataFrame:
    """Prepare data with RTH filter (best performer)."""
    df_1h = load_data(hourly_path)
    df_daily = load_data(daily_path)

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

    # EMAs
    df['ema_34'] = df['Close'].ewm(span=34, adjust=False).mean()
    df['ema_55'] = df['Close'].ewm(span=55, adjust=False).mean()
    df['ema_bull'] = (df['ema_34'] > df['ema_55']).astype(int)

    # SuperTrend
    st, st_dir = calculate_supertrend(df['High'], df['Low'], df['Close'], period=10, multiplier=3.0)
    df['supertrend'] = st
    df['st_direction'] = st_dir

    # ATR
    tr = pd.concat([
        df['High'] - df['Low'],
        abs(df['High'] - df['Close'].shift(1)),
        abs(df['Low'] - df['Close'].shift(1))
    ], axis=1).max(axis=1)
    df['atr_14'] = tr.rolling(window=14).mean()

    # RTH Filter (9 AM - 4 PM)
    df['hour'] = df.index.hour
    df['rth_ok'] = ((df['hour'] >= 9) & (df['hour'] < 16)).astype(int)

    # Pullback entry
    ema_tolerance = 0.003
    df['pullback_long'] = (
        (df['Low'] <= df['ema_34'] * (1 + ema_tolerance)) &
        (df['Close'] > df['ema_34']) &
        (df['Close'] > df['Open']) &
        (df['Close'].shift(1) > df['ema_34'].shift(1))
    ).astype(int)

    # Final signal with RTH filter
    df['long_signal'] = (
        (df['daily_bull_regime'] == 1) &
        (df['st_direction'] == 1) &
        (df['ema_bull'] == 1) &
        (df['pullback_long'] == 1) &
        (df['rth_ok'] == 1)
    ).astype(int)

    essential_cols = ['Open', 'High', 'Low', 'Close', 'Volume', 'atr_14', 'ema_34', 'ema_55']
    df = df.dropna(subset=essential_cols)
    df = df.drop(columns=['date', 'hour'], errors='ignore')

    return df


class ESSwingStrategy(Strategy):
    """ES Swing Strategy with configurable parameters."""

    stop_mult = 1.25
    target_mult = 1.5

    def init(self):
        self.long_signal = self.I(lambda: self.data.long_signal)
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


def calculate_cagr(start_value, end_value, years):
    """Calculate Compound Annual Growth Rate."""
    if start_value <= 0 or years <= 0:
        return 0
    return (pow(end_value / start_value, 1 / years) - 1) * 100


def calculate_annualized_max_dd(max_dd_pct, duration_days):
    """
    Annualize max drawdown.
    If drawdown happened over X days, what would it be over a full year?
    """
    if duration_days <= 0:
        return max_dd_pct
    # Scale factor based on trading days (252)
    years = duration_days / 365
    if years >= 1:
        return max_dd_pct  # Already at least a year
    # Annualize using sqrt of time (volatility scaling)
    return max_dd_pct * np.sqrt(1 / years) if years > 0 else max_dd_pct


def run_single_backtest(df, stop_mult, target_mult, cash=50000):
    """Run a single backtest and return stats."""
    ESSwingStrategy.stop_mult = stop_mult
    ESSwingStrategy.target_mult = target_mult

    bt = Backtest(
        df,
        ESSwingStrategy,
        cash=cash,
        commission=0.00005,
        exclusive_orders=True,
        trade_on_close=True
    )

    return bt.run()


def walk_forward_validation(df, n_splits=5, train_pct=0.7,
                            stop_mult=1.25, target_mult=1.5,
                            optimize_params=False):
    """
    Perform walk-forward validation.

    Args:
        df: Full dataset
        n_splits: Number of walk-forward periods
        train_pct: Percentage of each period for training
        stop_mult: Stop loss multiplier
        target_mult: Take profit multiplier
        optimize_params: Whether to optimize params on each training set
    """
    results = []
    total_bars = len(df)
    split_size = total_bars // n_splits

    print(f"\nWalk-Forward Validation: {n_splits} splits")
    print(f"Total bars: {total_bars:,}")
    print(f"Split size: ~{split_size:,} bars each")
    print(f"Train/Test ratio: {train_pct*100:.0f}%/{(1-train_pct)*100:.0f}%")
    print("=" * 80)

    for i in range(n_splits):
        # Define period boundaries
        start_idx = i * split_size
        end_idx = min((i + 1) * split_size, total_bars)

        if end_idx - start_idx < 1000:  # Skip if too few bars
            continue

        period_df = df.iloc[start_idx:end_idx].copy()
        train_size = int(len(period_df) * train_pct)

        train_df = period_df.iloc[:train_size].copy()
        test_df = period_df.iloc[train_size:].copy()

        if len(test_df) < 100:  # Skip if test set too small
            continue

        period_start = period_df.index[0].strftime('%Y-%m-%d')
        period_end = period_df.index[-1].strftime('%Y-%m-%d')
        test_start = test_df.index[0].strftime('%Y-%m-%d')

        print(f"\nPeriod {i+1}: {period_start} to {period_end}")
        print(f"  Train: {len(train_df):,} bars | Test: {len(test_df):,} bars")

        # Optimize on training set if requested
        if optimize_params and len(train_df) > 1000:
            try:
                ESSwingStrategy.stop_mult = 1.5
                ESSwingStrategy.target_mult = 3.0

                bt_train = Backtest(train_df, ESSwingStrategy, cash=50000,
                                   commission=0.00005, exclusive_orders=True)
                opt_stats = bt_train.optimize(
                    stop_mult=[1.0, 1.25, 1.5, 1.75, 2.0],
                    target_mult=[1.5, 2.0, 2.5, 3.0, 4.0],
                    maximize='Sharpe Ratio',
                    constraint=lambda p: p.target_mult >= p.stop_mult,
                )
                best_stop = opt_stats._strategy.stop_mult
                best_target = opt_stats._strategy.target_mult
                print(f"  Optimized params: stop={best_stop}, target={best_target}")
            except:
                best_stop = stop_mult
                best_target = target_mult
        else:
            best_stop = stop_mult
            best_target = target_mult

        # Test on out-of-sample data
        try:
            test_stats = run_single_backtest(test_df, best_stop, best_target)

            # Calculate metrics
            duration_days = (test_df.index[-1] - test_df.index[0]).days
            years = duration_days / 365

            ret = test_stats['Return [%]']
            equity_final = 50000 * (1 + ret / 100)
            cagr = calculate_cagr(50000, equity_final, years) if years > 0 else ret

            max_dd = test_stats['Max. Drawdown [%]']
            # Get drawdown duration if available
            dd_duration = test_stats.get('Max. Drawdown Duration', pd.Timedelta(days=duration_days))
            if isinstance(dd_duration, pd.Timedelta):
                dd_days = dd_duration.days
            else:
                dd_days = duration_days

            ann_max_dd = max_dd  # Keep as-is for comparison

            result = {
                'period': i + 1,
                'test_start': test_start,
                'test_end': test_df.index[-1].strftime('%Y-%m-%d'),
                'days': duration_days,
                'return': ret,
                'cagr': cagr,
                'trades': test_stats['# Trades'],
                'win_rate': test_stats.get('Win Rate [%]', 0) or 0,
                'profit_factor': test_stats.get('Profit Factor', 0) or 0,
                'max_dd': max_dd,
                'sharpe': test_stats.get('Sharpe Ratio', 0) or 0,
                'stop_mult': best_stop,
                'target_mult': best_target,
            }
            results.append(result)

            print(f"  OUT-OF-SAMPLE: Return={ret:.2f}%, CAGR={cagr:.2f}%, MaxDD={max_dd:.2f}%, Trades={test_stats['# Trades']}")

        except Exception as e:
            print(f"  Error in period {i+1}: {e}")

    return results


def run_full_analysis(hourly_path: str, daily_path: str):
    """Run complete walk-forward analysis."""
    print("=" * 80)
    print("ES INTRADAY SWING STRATEGY - WALK-FORWARD VALIDATION")
    print("=" * 80)

    # Prepare data
    print("\nLoading and preparing data...")
    df = prepare_data(hourly_path, daily_path)
    print(f"Dataset: {len(df):,} bars from {df.index[0]} to {df.index[-1]}")

    # Calculate full period duration
    total_days = (df.index[-1] - df.index[0]).days
    total_years = total_days / 365
    print(f"Period: {total_days} days ({total_years:.2f} years)")

    # =========================================================================
    # 1. FULL BACKTEST WITH OPTIMIZED PARAMS
    # =========================================================================
    print("\n" + "=" * 80)
    print("1. FULL BACKTEST - OPTIMIZED PARAMETERS (stop=1.25, target=1.5)")
    print("=" * 80)

    stats_opt = run_single_backtest(df, stop_mult=1.25, target_mult=1.5)

    # Calculate CAGR
    ret_opt = stats_opt['Return [%]']
    equity_final_opt = 50000 * (1 + ret_opt / 100)
    cagr_opt = calculate_cagr(50000, equity_final_opt, total_years)

    max_dd_opt = stats_opt['Max. Drawdown [%]']

    print(f"\n{'Metric':<25} {'Value':>15}")
    print("-" * 42)
    print(f"{'Total Return':<25} {ret_opt:>14.2f}%")
    print(f"{'CAGR':<25} {cagr_opt:>14.2f}%")
    print(f"{'# Trades':<25} {stats_opt['# Trades']:>15}")
    print(f"{'Win Rate':<25} {stats_opt['Win Rate [%]']:>14.2f}%")
    print(f"{'Profit Factor':<25} {stats_opt['Profit Factor']:>15.3f}")
    print(f"{'Max Drawdown':<25} {max_dd_opt:>14.2f}%")
    print(f"{'Sharpe Ratio':<25} {stats_opt['Sharpe Ratio']:>15.3f}")
    print(f"{'Sortino Ratio':<25} {stats_opt.get('Sortino Ratio', 0):>15.3f}")
    print(f"{'Calmar Ratio (CAGR/DD)':<25} {abs(cagr_opt/max_dd_opt) if max_dd_opt != 0 else 0:>15.3f}")

    # =========================================================================
    # 2. FULL BACKTEST WITH BASELINE PARAMS
    # =========================================================================
    print("\n" + "=" * 80)
    print("2. FULL BACKTEST - BASELINE PARAMETERS (stop=1.5, target=3.0)")
    print("=" * 80)

    stats_base = run_single_backtest(df, stop_mult=1.5, target_mult=3.0)

    ret_base = stats_base['Return [%]']
    equity_final_base = 50000 * (1 + ret_base / 100)
    cagr_base = calculate_cagr(50000, equity_final_base, total_years)
    max_dd_base = stats_base['Max. Drawdown [%]']

    print(f"\n{'Metric':<25} {'Value':>15}")
    print("-" * 42)
    print(f"{'Total Return':<25} {ret_base:>14.2f}%")
    print(f"{'CAGR':<25} {cagr_base:>14.2f}%")
    print(f"{'# Trades':<25} {stats_base['# Trades']:>15}")
    print(f"{'Win Rate':<25} {stats_base['Win Rate [%]']:>14.2f}%")
    print(f"{'Profit Factor':<25} {stats_base['Profit Factor']:>15.3f}")
    print(f"{'Max Drawdown':<25} {max_dd_base:>14.2f}%")
    print(f"{'Sharpe Ratio':<25} {stats_base['Sharpe Ratio']:>15.3f}")
    print(f"{'Calmar Ratio (CAGR/DD)':<25} {abs(cagr_base/max_dd_base) if max_dd_base != 0 else 0:>15.3f}")

    # =========================================================================
    # 3. WALK-FORWARD VALIDATION (Fixed Parameters)
    # =========================================================================
    print("\n" + "=" * 80)
    print("3. WALK-FORWARD VALIDATION - FIXED OPTIMIZED PARAMS")
    print("=" * 80)

    wf_results_fixed = walk_forward_validation(
        df, n_splits=5, train_pct=0.6,
        stop_mult=1.25, target_mult=1.5,
        optimize_params=False
    )

    # =========================================================================
    # 4. WALK-FORWARD VALIDATION (Re-optimized Each Period)
    # =========================================================================
    print("\n" + "=" * 80)
    print("4. WALK-FORWARD VALIDATION - RE-OPTIMIZED EACH PERIOD")
    print("=" * 80)

    wf_results_opt = walk_forward_validation(
        df, n_splits=5, train_pct=0.6,
        stop_mult=1.25, target_mult=1.5,
        optimize_params=True
    )

    # =========================================================================
    # 5. SUMMARY COMPARISON
    # =========================================================================
    print("\n" + "=" * 80)
    print("5. WALK-FORWARD SUMMARY")
    print("=" * 80)

    if wf_results_fixed:
        print("\nFIXED PARAMS (stop=1.25, target=1.5) - Out-of-Sample Results:")
        print(f"{'Period':<8} {'Return':>10} {'CAGR':>10} {'MaxDD':>10} {'Trades':>8} {'WinRate':>10} {'Sharpe':>10}")
        print("-" * 70)

        total_ret = 0
        total_trades = 0
        total_sharpe = 0
        valid_periods = 0

        for r in wf_results_fixed:
            print(f"{r['period']:<8} {r['return']:>9.2f}% {r['cagr']:>9.2f}% {r['max_dd']:>9.2f}% {r['trades']:>8} {r['win_rate']:>9.2f}% {r['sharpe']:>10.3f}")
            total_ret += r['return']
            total_trades += r['trades']
            if r['sharpe'] and not np.isnan(r['sharpe']):
                total_sharpe += r['sharpe']
                valid_periods += 1

        avg_ret = total_ret / len(wf_results_fixed)
        avg_sharpe = total_sharpe / valid_periods if valid_periods > 0 else 0

        print("-" * 70)
        print(f"{'AVERAGE':<8} {avg_ret:>9.2f}% {'-':>10} {'-':>10} {total_trades:>8} {'-':>10} {avg_sharpe:>10.3f}")

    if wf_results_opt:
        print("\nRE-OPTIMIZED EACH PERIOD - Out-of-Sample Results:")
        print(f"{'Period':<8} {'Return':>10} {'CAGR':>10} {'MaxDD':>10} {'Trades':>8} {'Stop':>8} {'Target':>8}")
        print("-" * 70)

        total_ret_opt = 0
        for r in wf_results_opt:
            print(f"{r['period']:<8} {r['return']:>9.2f}% {r['cagr']:>9.2f}% {r['max_dd']:>9.2f}% {r['trades']:>8} {r['stop_mult']:>8} {r['target_mult']:>8}")
            total_ret_opt += r['return']

        avg_ret_opt = total_ret_opt / len(wf_results_opt)
        print("-" * 70)
        print(f"{'AVERAGE':<8} {avg_ret_opt:>9.2f}%")

    # =========================================================================
    # 6. FINAL RECOMMENDATION
    # =========================================================================
    print("\n" + "=" * 80)
    print("6. FINAL ANALYSIS & RECOMMENDATION")
    print("=" * 80)

    print(f"\n{'Configuration':<30} {'CAGR':>10} {'MaxDD':>10} {'Sharpe':>10} {'Calmar':>10}")
    print("-" * 75)
    print(f"{'Optimized (1.25/1.5)':<30} {cagr_opt:>9.2f}% {max_dd_opt:>9.2f}% {stats_opt['Sharpe Ratio']:>10.3f} {abs(cagr_opt/max_dd_opt):>10.3f}")
    print(f"{'Baseline (1.5/3.0)':<30} {cagr_base:>9.2f}% {max_dd_base:>9.2f}% {stats_base['Sharpe Ratio']:>10.3f} {abs(cagr_base/max_dd_base):>10.3f}")

    if wf_results_fixed:
        wf_profitable = sum(1 for r in wf_results_fixed if r['return'] > 0)
        wf_total = len(wf_results_fixed)
        consistency = wf_profitable / wf_total * 100

        print(f"\nWalk-Forward Consistency: {wf_profitable}/{wf_total} periods profitable ({consistency:.1f}%)")

        if consistency >= 80:
            print("\n[ROBUST] Strategy shows strong out-of-sample consistency.")
        elif consistency >= 60:
            print("\n[MODERATE] Strategy shows reasonable out-of-sample performance.")
        else:
            print("\n[CAUTION] Strategy may be overfit - inconsistent out-of-sample results.")

    # Save final chart
    try:
        ESSwingStrategy.stop_mult = 1.25
        ESSwingStrategy.target_mult = 1.5
        bt = Backtest(df, ESSwingStrategy, cash=50000, commission=0.00005,
                     exclusive_orders=True, trade_on_close=True)
        bt.run()
        bt.plot(filename='src/data/rbi/es_swing_walkforward.html', open_browser=False)
        print(f"\nChart saved to: src/data/rbi/es_swing_walkforward.html")
    except Exception as e:
        print(f"\nCould not save chart: {e}")

    return stats_opt, stats_base, wf_results_fixed, wf_results_opt


if __name__ == "__main__":
    import os

    script_dir = os.path.dirname(os.path.abspath(__file__))
    project_root = os.path.dirname(os.path.dirname(script_dir))

    hourly_path = os.path.join(project_root, 'src', 'data', 'rbi', 'ES-1H.csv')
    daily_path = os.path.join(project_root, 'src', 'data', 'rbi', 'ES-1D.csv')

    results = run_full_analysis(hourly_path, daily_path)
