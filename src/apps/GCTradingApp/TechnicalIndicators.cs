/*
 * Technical Indicators for GC Trading Strategy
 * Extracted for testability
 */

namespace GCTradingApp;

/// <summary>
/// Static class containing technical indicator calculations
/// </summary>
public static class TechnicalIndicators
{
    /// <summary>
    /// Calculate Average True Range (ATR)
    /// </summary>
    public static double CalculateATR(double[] highs, double[] lows, double[] closes, int period)
    {
        if (closes.Length < period + 1) return 0;

        var trValues = new List<double>();
        for (int i = 1; i < closes.Length; i++)
        {
            var tr = Math.Max(highs[i] - lows[i],
                     Math.Max(Math.Abs(highs[i] - closes[i - 1]),
                              Math.Abs(lows[i] - closes[i - 1])));
            trValues.Add(tr);
        }

        return trValues.TakeLast(period).Average();
    }

    /// <summary>
    /// Calculate Relative Strength Index (RSI)
    /// </summary>
    public static double CalculateRSI(double[] closes, int period)
    {
        if (closes.Length < period + 1) return 50;

        var gains = new List<double>();
        var losses = new List<double>();

        for (int i = 1; i < closes.Length; i++)
        {
            var change = closes[i] - closes[i - 1];
            gains.Add(Math.Max(0, change));
            losses.Add(Math.Max(0, -change));
        }

        var avgGain = gains.TakeLast(period).Average();
        var avgLoss = losses.TakeLast(period).Average();

        if (avgLoss == 0) return 100;
        var rs = avgGain / avgLoss;
        return 100 - (100 / (1 + rs));
    }

    /// <summary>
    /// Calculate Exponential Moving Average (EMA)
    /// </summary>
    public static double CalculateEMA(double[] values, int period)
    {
        if (values.Length == 0) return 0;
        if (values.Length < period) return values.Last();

        var multiplier = 2.0 / (period + 1);
        var ema = values.Take(period).Average();

        for (int i = period; i < values.Length; i++)
        {
            ema = (values[i] - ema) * multiplier + ema;
        }

        return ema;
    }

    /// <summary>
    /// Calculate Simple Moving Average (SMA)
    /// </summary>
    public static double CalculateSMA(double[] values, int period)
    {
        if (values.Length == 0) return 0;
        if (values.Length < period) return values.Average();
        return values.TakeLast(period).Average();
    }

    /// <summary>
    /// Calculate MACD (Moving Average Convergence Divergence)
    /// Returns (macd, signal, histogram)
    /// </summary>
    public static (double macd, double signal, double histogram) CalculateMACD(
        double[] closes,
        List<double> macdHistory,
        int fastPeriod = 12,
        int slowPeriod = 26,
        int signalPeriod = 9)
    {
        var ema12 = CalculateEMA(closes, fastPeriod);
        var ema26 = CalculateEMA(closes, slowPeriod);
        var macd = ema12 - ema26;

        // Store MACD value for signal line calculation
        macdHistory.Add(macd);
        if (macdHistory.Count > 100)
            macdHistory.RemoveAt(0);

        // Signal line is 9-period EMA of MACD values
        double signal;
        if (macdHistory.Count >= signalPeriod)
        {
            signal = CalculateEMA(macdHistory.ToArray(), signalPeriod);
        }
        else
        {
            signal = macdHistory.Average();
        }

        var histogram = macd - signal;

        return (macd, signal, histogram);
    }

    /// <summary>
    /// Calculate SuperTrend indicator (simple version)
    /// </summary>
    public static double CalculateSuperTrend(double[] highs, double[] lows, double[] closes, int period, double multiplier)
    {
        if (closes.Length < period) return closes.Length > 0 ? closes.Last() : 0;

        var atr = CalculateATR(highs, lows, closes, period);
        var hl2 = (highs.Last() + lows.Last()) / 2;

        var upperBand = hl2 + (multiplier * atr);
        var lowerBand = hl2 - (multiplier * atr);

        // Simplified: return lower band for bullish
        return closes.Last() > hl2 ? lowerBand : upperBand;
    }

    /// <summary>
    /// Calculate SuperTrend with proper state tracking (stateful version)
    /// Returns (superTrendValue, direction) where direction is 1 (bullish) or -1 (bearish)
    /// </summary>
    public static (double value, int direction) CalculateSuperTrendStateful(
        double[] highs, double[] lows, double[] closes,
        int period, double multiplier,
        ref double prevSuperTrend, ref int prevDirection)
    {
        if (closes.Length < period)
            return (closes.Length > 0 ? closes.Last() : 0, 0);

        var atr = CalculateATR(highs, lows, closes, period);
        var hl2 = (highs.Last() + lows.Last()) / 2;

        var upperBand = hl2 + (multiplier * atr);
        var lowerBand = hl2 - (multiplier * atr);

        double superTrend;
        int direction;

        if (prevDirection == 0)
        {
            // First calculation - determine initial direction based on price position
            if (closes.Last() > hl2)
            {
                superTrend = lowerBand;
                direction = 1;  // Bullish
            }
            else
            {
                superTrend = upperBand;
                direction = -1;  // Bearish
            }
        }
        else if (prevDirection == -1)
        {
            // Previous was bearish (using upper band)
            if (closes.Last() > prevSuperTrend)
            {
                // Price crossed above - flip to bullish
                superTrend = lowerBand;
                direction = 1;
            }
            else
            {
                // Stay bearish, use lower of current and previous upper band
                superTrend = Math.Min(upperBand, prevSuperTrend);
                direction = -1;
            }
        }
        else
        {
            // Previous was bullish (using lower band)
            if (closes.Last() < prevSuperTrend)
            {
                // Price crossed below - flip to bearish
                superTrend = upperBand;
                direction = -1;
            }
            else
            {
                // Stay bullish, use higher of current and previous lower band
                superTrend = Math.Max(lowerBand, prevSuperTrend);
                direction = 1;
            }
        }

        // Update state for next call
        prevSuperTrend = superTrend;
        prevDirection = direction;

        return (superTrend, direction);
    }

    /// <summary>
    /// Detect bullish divergence between price and RSI
    /// Returns true if price makes lower low but RSI makes higher low
    /// </summary>
    public static bool DetectBullishDivergence(double[] closes, double[] rsiValues, int lookback = 20)
    {
        if (closes.Length < lookback || rsiValues.Length < lookback)
            return false;

        var recentCloses = closes.TakeLast(lookback).ToArray();
        var recentRsi = rsiValues.TakeLast(lookback).ToArray();

        // Find local lows
        int priceLow1 = -1, priceLow2 = -1;
        for (int i = 5; i < 15; i++)
        {
            if (i >= recentCloses.Length - 2) break;

            if (recentCloses[i] < recentCloses[i - 1] && recentCloses[i] < recentCloses[i + 1] &&
                recentCloses[i] < recentCloses[i - 2] && recentCloses[i] < recentCloses[i + 2])
            {
                if (priceLow1 < 0) priceLow1 = i;
                else priceLow2 = i;
            }
        }

        if (priceLow1 < 0 || priceLow2 < 0) return false;

        // Check for divergence: price lower low, RSI higher low
        bool priceLowerLow = recentCloses[priceLow2] < recentCloses[priceLow1];
        bool rsiHigherLow = recentRsi[priceLow2] > recentRsi[priceLow1];

        return priceLowerLow && rsiHigherLow;
    }

    /// <summary>
    /// Detect bearish divergence between price and RSI
    /// Returns true if price makes higher high but RSI makes lower high
    /// </summary>
    public static bool DetectBearishDivergence(double[] closes, double[] rsiValues, int lookback = 20)
    {
        if (closes.Length < lookback || rsiValues.Length < lookback)
            return false;

        var recentCloses = closes.TakeLast(lookback).ToArray();
        var recentRsi = rsiValues.TakeLast(lookback).ToArray();

        // Find local highs
        int priceHigh1 = -1, priceHigh2 = -1;
        for (int i = 5; i < 15; i++)
        {
            if (i >= recentCloses.Length - 2) break;

            if (recentCloses[i] > recentCloses[i - 1] && recentCloses[i] > recentCloses[i + 1] &&
                recentCloses[i] > recentCloses[i - 2] && recentCloses[i] > recentCloses[i + 2])
            {
                if (priceHigh1 < 0) priceHigh1 = i;
                else priceHigh2 = i;
            }
        }

        if (priceHigh1 < 0 || priceHigh2 < 0) return false;

        // Check for divergence: price higher high, RSI lower high
        bool priceHigherHigh = recentCloses[priceHigh2] > recentCloses[priceHigh1];
        bool rsiLowerHigh = recentRsi[priceHigh2] < recentRsi[priceHigh1];

        return priceHigherHigh && rsiLowerHigh;
    }
}
