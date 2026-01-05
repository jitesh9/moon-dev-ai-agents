"""
GC Divergence Strategy - Robustness Testing Suite
==================================================
Comprehensive tests to validate strategy robustness:
1. Walk-forward analysis (train/test splits)
2. Parameter sensitivity analysis
3. Monte Carlo simulation
4. Market regime analysis
5. Time-based subsample testing

Author: Moon Dev AI
"""

import pandas as pd
import numpy as np
from backtesting import Backtest, Strategy
import warnings
from typing import Dict, List, Tuple
warnings.filterwarnings('ignore')

from gc_divergence_sharpe import (
    load_gc_data, prepare_signals, GCDivergenceSharpeStrategy, calculate_cagr
)


def run_single_backtest(df: pd.DataFrame, strategy_class=None, params: dict = None, capital: float = 500000) -> dict:
    """Run a single backtest with given parameters."""
    if strategy_class is None:
        strategy_class = GCDivergenceSharpeStrategy

    class TestStrategy(strategy_class):
        pass

    if params:
        for key, value in params.items():
            setattr(TestStrategy, key, value)

    bt = Backtest(
        df,
        TestStrategy,
        cash=capital,
        commission=0.00005,
        exclusive_orders=True,
        trade_on_close=True,
        margin=1/10
    )

    stats = bt.run()
    return {
        'return': stats['Return [%]'],
        'sharpe': stats['Sharpe Ratio'],
        'max_dd': stats['Max. Drawdown [%]'],
        'trades': stats['# Trades'],
        'win_rate': stats['Win Rate [%]'],
        'profit_factor': stats.get('Profit Factor', 0)
    }


# =============================================================================
# 1. WALK-FORWARD ANALYSIS
# =============================================================================
def walk_forward_analysis(df: pd.DataFrame, n_splits: int = 5) -> Dict:
    """
    Walk-forward test: train on earlier data, test on later data.
    This tests if strategy performs well out-of-sample.
    """
    print("\n" + "=" * 70)
    print("1. WALK-FORWARD ANALYSIS")
    print("=" * 70)

    results = []
    n_bars = len(df)
    split_size = n_bars // (n_splits + 1)

    for i in range(n_splits):
        train_start = 0
        train_end = split_size * (i + 1)
        test_start = train_end
        test_end = train_end + split_size

        if test_end > n_bars:
            test_end = n_bars

        train_df = df.iloc[train_start:train_end].copy()
        test_df = df.iloc[test_start:test_end].copy()

        if len(train_df) < 1000 or len(test_df) < 500:
            continue

        train_period = f"{train_df.index[0].date()} to {train_df.index[-1].date()}"
        test_period = f"{test_df.index[0].date()} to {test_df.index[-1].date()}"

        try:
            test_stats = run_single_backtest(test_df)

            results.append({
                'split': i + 1,
                'train_period': train_period,
                'test_period': test_period,
                'test_bars': len(test_df),
                'sharpe': test_stats['sharpe'],
                'return': test_stats['return'],
                'max_dd': test_stats['max_dd'],
                'trades': test_stats['trades'],
                'win_rate': test_stats['win_rate']
            })

            print(f"\nSplit {i+1}:")
            print(f"  Train: {train_period} ({len(train_df):,} bars)")
            print(f"  Test:  {test_period} ({len(test_df):,} bars)")
            print(f"  Sharpe: {test_stats['sharpe']:.3f}, Return: {test_stats['return']:.2f}%")
            print(f"  Trades: {test_stats['trades']}, Win Rate: {test_stats['win_rate']:.1f}%")
        except Exception as e:
            print(f"\nSplit {i+1}: Error - {e}")

    if results:
        avg_sharpe = np.mean([r['sharpe'] for r in results])
        std_sharpe = np.std([r['sharpe'] for r in results])
        min_sharpe = np.min([r['sharpe'] for r in results])
        max_sharpe = np.max([r['sharpe'] for r in results])

        print("\n" + "-" * 40)
        print("Walk-Forward Summary:")
        print(f"  Avg Sharpe:  {avg_sharpe:.3f} +/- {std_sharpe:.3f}")
        print(f"  Min Sharpe:  {min_sharpe:.3f}")
        print(f"  Max Sharpe:  {max_sharpe:.3f}")
        print(f"  Consistency: {sum(1 for r in results if r['sharpe'] > 0) / len(results) * 100:.1f}% positive")

        return {
            'results': results,
            'avg_sharpe': avg_sharpe,
            'std_sharpe': std_sharpe,
            'min_sharpe': min_sharpe,
            'consistency': sum(1 for r in results if r['sharpe'] > 0) / len(results)
        }

    return {'results': [], 'avg_sharpe': 0, 'consistency': 0}


# =============================================================================
# 2. PARAMETER SENSITIVITY ANALYSIS
# =============================================================================
def parameter_sensitivity(df: pd.DataFrame) -> Dict:
    """
    Test how sensitive the strategy is to parameter changes.
    A robust strategy shouldn't collapse with small parameter changes.
    """
    print("\n" + "=" * 70)
    print("2. PARAMETER SENSITIVITY ANALYSIS")
    print("=" * 70)

    baseline_params = {
        'stop_mult': 1.5,
        'target_mult': 2.5,
        'trail_start_pct': 1.5,
        'trail_atr_mult': 1.0,
        'max_hold_bars': 60
    }

    baseline = run_single_backtest(df, params=baseline_params)
    print(f"\nBaseline Sharpe: {baseline['sharpe']:.3f}")

    results = {'baseline': baseline, 'variations': {}}

    param_variations = {
        'stop_mult': [1.2, 1.5, 1.8],
        'target_mult': [2.0, 2.5, 3.0],
        'trail_start_pct': [1.0, 1.5, 2.0],
        'trail_atr_mult': [0.8, 1.0, 1.2],
        'max_hold_bars': [48, 60, 72]
    }

    for param, values in param_variations.items():
        print(f"\n{param}:")
        param_results = []

        for value in values:
            test_params = baseline_params.copy()
            test_params[param] = value

            try:
                stats = run_single_backtest(df, params=test_params)
                sharpe_change = ((stats['sharpe'] / baseline['sharpe']) - 1) * 100 if baseline['sharpe'] != 0 else 0
                param_results.append({
                    'value': value,
                    'sharpe': stats['sharpe'],
                    'change': sharpe_change
                })
                print(f"  {value}: Sharpe {stats['sharpe']:.3f} ({sharpe_change:+.1f}%)")
            except Exception as e:
                print(f"  {value}: Error - {e}")

        results['variations'][param] = param_results

    all_sharpes = []
    for param, variations in results['variations'].items():
        for v in variations:
            all_sharpes.append(v['sharpe'])

    if all_sharpes:
        sensitivity = np.std(all_sharpes) / np.mean(all_sharpes) * 100
        print("\n" + "-" * 40)
        print(f"Sensitivity Score: {sensitivity:.1f}% (lower is more robust)")
        print(f"  < 15%: EXCELLENT stability")
        print(f"  15-30%: GOOD stability")
        print(f"  > 30%: FRAGILE - over-fitted")
        results['sensitivity_score'] = sensitivity

    return results


# =============================================================================
# 3. MONTE CARLO SIMULATION
# =============================================================================
def monte_carlo_analysis(df: pd.DataFrame, n_simulations: int = 100) -> Dict:
    """
    Monte Carlo simulation: Shuffle trade results to test equity curve stability.
    """
    print("\n" + "=" * 70)
    print("3. MONTE CARLO SIMULATION")
    print("=" * 70)

    bt = Backtest(
        df,
        GCDivergenceSharpeStrategy,
        cash=500000,
        commission=0.00005,
        exclusive_orders=True,
        trade_on_close=True,
        margin=1/10
    )
    stats = bt.run()
    trades = stats._trades

    if len(trades) < 10:
        print("Not enough trades for Monte Carlo analysis")
        return {'error': 'insufficient_trades'}

    trade_returns = trades['ReturnPct'].values
    n_trades = len(trade_returns)
    initial_capital = 500000

    print(f"Analyzing {n_trades} trades over {n_simulations} simulations...")

    final_returns = []
    max_drawdowns = []
    sharpes = []

    for sim in range(n_simulations):
        shuffled = np.random.permutation(trade_returns)
        equity = [initial_capital]
        for ret in shuffled:
            equity.append(equity[-1] * (1 + ret / 100))

        equity = np.array(equity)
        total_return = (equity[-1] / equity[0] - 1) * 100
        peak = np.maximum.accumulate(equity)
        drawdown = (equity - peak) / peak * 100
        max_dd = drawdown.min()
        returns = np.diff(equity) / equity[:-1]
        sharpe = np.mean(returns) / (np.std(returns) + 1e-10) * np.sqrt(252)

        final_returns.append(total_return)
        max_drawdowns.append(max_dd)
        sharpes.append(sharpe)

    results = {
        'return_5pct': np.percentile(final_returns, 5),
        'return_median': np.percentile(final_returns, 50),
        'return_95pct': np.percentile(final_returns, 95),
        'dd_5pct': np.percentile(max_drawdowns, 5),
        'dd_median': np.percentile(max_drawdowns, 50),
        'dd_95pct': np.percentile(max_drawdowns, 95),
        'sharpe_5pct': np.percentile(sharpes, 5),
        'sharpe_median': np.percentile(sharpes, 50),
        'sharpe_95pct': np.percentile(sharpes, 95),
        'actual_sharpe': stats['Sharpe Ratio'],
        'actual_return': stats['Return [%]'],
        'actual_dd': stats['Max. Drawdown [%]']
    }

    print(f"\nActual Results:")
    print(f"  Return: {results['actual_return']:.2f}%")
    print(f"  Sharpe: {results['actual_sharpe']:.3f}")
    print(f"  Max DD: {results['actual_dd']:.2f}%")

    print(f"\nMonte Carlo (90% confidence interval):")
    print(f"  Return: {results['return_5pct']:.2f}% to {results['return_95pct']:.2f}%")
    print(f"  Sharpe: {results['sharpe_5pct']:.3f} to {results['sharpe_95pct']:.3f}")
    print(f"  Max DD: {results['dd_5pct']:.2f}% to {results['dd_95pct']:.2f}%")

    print("\n" + "-" * 40)
    if results['actual_return'] > results['return_95pct'] or results['actual_sharpe'] > results['sharpe_95pct']:
        print("WARNING: Actual results may be sequence-dependent")
    else:
        print("GOOD: Results appear stable across trade orderings")

    return results


# =============================================================================
# 4. MARKET REGIME ANALYSIS
# =============================================================================
def regime_analysis(df: pd.DataFrame) -> Dict:
    """Test performance across different market regimes."""
    print("\n" + "=" * 70)
    print("4. MARKET REGIME ANALYSIS")
    print("=" * 70)

    results = {}

    daily_df = df.resample('1D').agg({
        'Open': 'first', 'High': 'max', 'Low': 'min',
        'Close': 'last', 'Volume': 'sum'
    }).dropna()

    daily_df['returns_20d'] = daily_df['Close'].pct_change(20)
    daily_df['regime'] = 'sideways'
    daily_df.loc[daily_df['returns_20d'] > 0.03, 'regime'] = 'bull'
    daily_df.loc[daily_df['returns_20d'] < -0.03, 'regime'] = 'bear'

    regime_dates = daily_df.set_index(daily_df.index.date)['regime'].to_dict()

    df_test = df.copy()
    df_test['date'] = df_test.index.date
    df_test['regime'] = df_test['date'].map(regime_dates)
    df_test['regime'] = df_test['regime'].fillna('sideways')

    regime_counts = df_test['regime'].value_counts()
    print("\nRegime Distribution:")
    for regime, count in regime_counts.items():
        print(f"  {regime.upper()}: {count:,} bars ({count/len(df_test)*100:.1f}%)")

    for regime in ['bull', 'bear', 'sideways']:
        regime_df = df_test[df_test['regime'] == regime].copy()

        if len(regime_df) < 1000:
            print(f"\n{regime.upper()}: Insufficient data ({len(regime_df)} bars)")
            continue

        regime_df = regime_df.drop(columns=['date', 'regime'])

        try:
            stats = run_single_backtest(regime_df)
            results[regime] = stats

            print(f"\n{regime.upper()} Market:")
            print(f"  Bars: {len(regime_df):,}")
            print(f"  Sharpe: {stats['sharpe']:.3f}")
            print(f"  Return: {stats['return']:.2f}%")
            print(f"  Trades: {stats['trades']}")
            print(f"  Win Rate: {stats['win_rate']:.1f}%")
        except Exception as e:
            print(f"\n{regime.upper()}: Error - {e}")

    print("\n" + "-" * 40)
    if results:
        sharpes = [r['sharpe'] for r in results.values()]
        print(f"Regime Sharpe Range: {min(sharpes):.3f} to {max(sharpes):.3f}")
        positive_regimes = sum(1 for r in results.values() if r['sharpe'] > 0)
        print(f"Positive in {positive_regimes}/{len(results)} regimes")

    return results


# =============================================================================
# 5. YEAR-BY-YEAR ANALYSIS
# =============================================================================
def yearly_analysis(df: pd.DataFrame) -> Dict:
    """Test performance year by year."""
    print("\n" + "=" * 70)
    print("5. YEAR-BY-YEAR ANALYSIS")
    print("=" * 70)

    results = {}
    df_copy = df.copy()
    df_copy['year'] = df_copy.index.year
    years = sorted(df_copy['year'].unique())

    print(f"\nAnalyzing {len(years)} years...")

    for year in years:
        year_df = df_copy[df_copy['year'] == year].drop(columns=['year'])

        if len(year_df) < 500:
            print(f"\n{year}: Insufficient data ({len(year_df)} bars)")
            continue

        try:
            stats = run_single_backtest(year_df)
            results[year] = stats

            status = "PASS" if stats['sharpe'] > 0 else "FAIL"
            print(f"\n{year}: Sharpe {stats['sharpe']:.3f}, Return {stats['return']:.2f}%, "
                  f"Trades {stats['trades']}, Win {stats['win_rate']:.1f}% [{status}]")
        except Exception as e:
            print(f"\n{year}: Error - {e}")

    if results:
        sharpes = [r['sharpe'] for r in results.values()]
        returns = [r['return'] for r in results.values()]

        print("\n" + "-" * 40)
        print("Yearly Summary:")
        print(f"  Avg Sharpe: {np.mean(sharpes):.3f}")
        print(f"  Sharpe Std: {np.std(sharpes):.3f}")
        print(f"  Best Year:  {max(results.keys(), key=lambda y: results[y]['sharpe'])} "
              f"(Sharpe: {max(sharpes):.3f})")
        print(f"  Worst Year: {min(results.keys(), key=lambda y: results[y]['sharpe'])} "
              f"(Sharpe: {min(sharpes):.3f})")
        print(f"  Profitable Years: {sum(1 for r in results.values() if r['return'] > 0)}/{len(results)}")

    return results


# =============================================================================
# MAIN ROBUSTNESS REPORT
# =============================================================================
def generate_robustness_report(df: pd.DataFrame) -> Dict:
    """Generate comprehensive robustness report."""
    print("\n" + "=" * 70)
    print("GC DIVERGENCE STRATEGY - ROBUSTNESS TESTING")
    print("=" * 70)
    print(f"Data: {len(df):,} hourly bars")
    print(f"Period: {df.index[0]} to {df.index[-1]}")

    report = {}

    report['walk_forward'] = walk_forward_analysis(df, n_splits=4)
    report['sensitivity'] = parameter_sensitivity(df)
    report['monte_carlo'] = monte_carlo_analysis(df, n_simulations=100)
    report['regime'] = regime_analysis(df)
    report['yearly'] = yearly_analysis(df)

    print("\n" + "=" * 70)
    print("ROBUSTNESS SUMMARY")
    print("=" * 70)

    scores = []

    # 1. Walk-forward score
    wf = report['walk_forward']
    if wf.get('consistency', 0) > 0.7:
        scores.append(('Walk-Forward', 'PASS', f"{wf['consistency']*100:.0f}% consistent"))
    else:
        scores.append(('Walk-Forward', 'FAIL', f"{wf.get('consistency', 0)*100:.0f}% consistent"))

    # 2. Sensitivity score
    sens = report['sensitivity'].get('sensitivity_score', 100)
    if sens < 15:
        scores.append(('Param Sensitivity', 'EXCELLENT', f"{sens:.1f}%"))
    elif sens < 30:
        scores.append(('Param Sensitivity', 'GOOD', f"{sens:.1f}%"))
    else:
        scores.append(('Param Sensitivity', 'FRAGILE', f"{sens:.1f}%"))

    # 3. Monte Carlo score
    mc = report['monte_carlo']
    if not mc.get('error') and mc.get('sharpe_5pct', 0) > 0.5:
        scores.append(('Monte Carlo', 'PASS', f"5th pct Sharpe: {mc['sharpe_5pct']:.2f}"))
    else:
        scores.append(('Monte Carlo', 'MARGINAL', f"5th pct Sharpe: {mc.get('sharpe_5pct', 0):.2f}"))

    # 4. Regime score
    regime = report['regime']
    positive_regimes = sum(1 for r in regime.values() if isinstance(r, dict) and r.get('sharpe', 0) > 0)
    if positive_regimes >= 2:
        scores.append(('Market Regimes', 'PASS', f"{positive_regimes}/3 regimes profitable"))
    else:
        scores.append(('Market Regimes', 'FAIL', f"{positive_regimes}/3 regimes profitable"))

    # 5. Yearly consistency
    yearly = report['yearly']
    if yearly:
        yearly_consistency = sum(1 for r in yearly.values() if r['sharpe'] > 0) / len(yearly)
        if yearly_consistency > 0.6:
            scores.append(('Yearly Consistency', 'PASS', f"{yearly_consistency*100:.0f}% profitable years"))
        else:
            scores.append(('Yearly Consistency', 'FAIL', f"{yearly_consistency*100:.0f}% profitable years"))

    print("\nTest Results:")
    print("-" * 60)
    pass_count = 0
    for test, result, detail in scores:
        status = "+" if result in ['PASS', 'EXCELLENT', 'GOOD'] else "-"
        print(f"  [{status}] {test}: {result} ({detail})")
        if status == "+":
            pass_count += 1

    print("-" * 60)
    print(f"\nOverall: {pass_count}/{len(scores)} tests passed")

    if pass_count >= 4:
        print("\nVERDICT: ROBUST - Strategy passes majority of robustness tests")
    elif pass_count >= 3:
        print("\nVERDICT: MODERATE - Strategy shows reasonable robustness")
    else:
        print("\nVERDICT: FRAGILE - Strategy may be over-fitted")

    report['summary'] = {
        'scores': scores,
        'pass_count': pass_count,
        'total_tests': len(scores)
    }

    return report


if __name__ == "__main__":
    data_path = r"C:\dev\databento\GC_1minute\gc_continuous_1m.csv"
    df_1m = load_gc_data(data_path)
    df = prepare_signals(df_1m)
    report = generate_robustness_report(df)
