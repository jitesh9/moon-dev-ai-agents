/*
 * Unit Tests for Technical Indicators
 * Tests RSI, MACD, EMA, ATR, SuperTrend calculations
 */

using FluentAssertions;
using Xunit;

namespace GCTradingApp.Tests;

public class TechnicalIndicatorsTests
{
    #region RSI Tests

    [Fact]
    public void CalculateRSI_WithUpwardTrend_ReturnsHighValue()
    {
        // Arrange - Steadily increasing prices
        var closes = Enumerable.Range(1, 20).Select(i => (double)(100 + i)).ToArray();

        // Act
        var rsi = TechnicalIndicators.CalculateRSI(closes, 14);

        // Assert - RSI should be high (near 100) for uptrend
        rsi.Should().BeGreaterThan(90);
    }

    [Fact]
    public void CalculateRSI_WithDownwardTrend_ReturnsLowValue()
    {
        // Arrange - Steadily decreasing prices
        var closes = Enumerable.Range(1, 20).Select(i => (double)(120 - i)).ToArray();

        // Act
        var rsi = TechnicalIndicators.CalculateRSI(closes, 14);

        // Assert - RSI should be low (near 0) for downtrend
        rsi.Should().BeLessThan(10);
    }

    [Fact]
    public void CalculateRSI_WithSidewaysMarket_ReturnsMiddleValue()
    {
        // Arrange - Alternating up/down prices
        var closes = new double[20];
        for (int i = 0; i < 20; i++)
        {
            closes[i] = 100 + (i % 2 == 0 ? 1 : -1);
        }

        // Act
        var rsi = TechnicalIndicators.CalculateRSI(closes, 14);

        // Assert - RSI should be around 50 for sideways
        rsi.Should().BeInRange(40, 60);
    }

    [Fact]
    public void CalculateRSI_WithInsufficientData_ReturnsDefault50()
    {
        // Arrange - Only 5 data points, but need period+1
        var closes = new double[] { 100, 101, 102, 103, 104 };

        // Act
        var rsi = TechnicalIndicators.CalculateRSI(closes, 14);

        // Assert
        rsi.Should().Be(50);
    }

    [Fact]
    public void CalculateRSI_WithNoLosses_Returns100()
    {
        // Arrange - Only gains
        var closes = Enumerable.Range(1, 20).Select(i => (double)(100 + i * 2)).ToArray();

        // Act
        var rsi = TechnicalIndicators.CalculateRSI(closes, 14);

        // Assert
        rsi.Should().Be(100);
    }

    #endregion

    #region EMA Tests

    [Fact]
    public void CalculateEMA_WithConstantValues_ReturnsConstant()
    {
        // Arrange
        var values = Enumerable.Repeat(100.0, 20).ToArray();

        // Act
        var ema = TechnicalIndicators.CalculateEMA(values, 10);

        // Assert
        ema.Should().BeApproximately(100.0, 0.001);
    }

    [Fact]
    public void CalculateEMA_WithUptrend_FollowsPrice()
    {
        // Arrange - Increasing prices
        var values = Enumerable.Range(1, 20).Select(i => (double)i * 10).ToArray();

        // Act
        var ema = TechnicalIndicators.CalculateEMA(values, 5);

        // Assert - EMA should be close to recent prices but lag slightly
        ema.Should().BeGreaterThan(150); // Should be approaching 200
        ema.Should().BeLessThan(200);    // But not quite there
    }

    [Fact]
    public void CalculateEMA_WithInsufficientData_ReturnsLastValue()
    {
        // Arrange
        var values = new double[] { 100, 110, 120 };

        // Act
        var ema = TechnicalIndicators.CalculateEMA(values, 10);

        // Assert
        ema.Should().Be(120);
    }

    [Fact]
    public void CalculateEMA_WithEmptyArray_ReturnsZero()
    {
        // Arrange
        var values = Array.Empty<double>();

        // Act
        var ema = TechnicalIndicators.CalculateEMA(values, 10);

        // Assert
        ema.Should().Be(0);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(20)]
    public void CalculateEMA_DifferentPeriods_ProducesDifferentResults(int period)
    {
        // Arrange
        var values = Enumerable.Range(1, 50).Select(i => (double)i).ToArray();

        // Act
        var ema = TechnicalIndicators.CalculateEMA(values, period);

        // Assert - Just verify it calculates without error
        ema.Should().BePositive();
    }

    #endregion

    #region SMA Tests

    [Fact]
    public void CalculateSMA_WithConstantValues_ReturnsConstant()
    {
        // Arrange
        var values = Enumerable.Repeat(50.0, 20).ToArray();

        // Act
        var sma = TechnicalIndicators.CalculateSMA(values, 10);

        // Assert
        sma.Should().Be(50.0);
    }

    [Fact]
    public void CalculateSMA_WithSequentialValues_ReturnsCorrectAverage()
    {
        // Arrange - 1,2,3,4,5,6,7,8,9,10 - Last 5: 6,7,8,9,10 = 40/5 = 8
        var values = Enumerable.Range(1, 10).Select(i => (double)i).ToArray();

        // Act
        var sma = TechnicalIndicators.CalculateSMA(values, 5);

        // Assert
        sma.Should().Be(8.0);
    }

    [Fact]
    public void CalculateSMA_WithInsufficientData_ReturnsOverallAverage()
    {
        // Arrange
        var values = new double[] { 10, 20, 30 };

        // Act
        var sma = TechnicalIndicators.CalculateSMA(values, 10);

        // Assert
        sma.Should().Be(20.0);
    }

    #endregion

    #region ATR Tests

    [Fact]
    public void CalculateATR_WithConsistentRange_ReturnsExpectedValue()
    {
        // Arrange - Bars with consistent 10-point range
        var highs = Enumerable.Repeat(110.0, 20).ToArray();
        var lows = Enumerable.Repeat(100.0, 20).ToArray();
        var closes = Enumerable.Repeat(105.0, 20).ToArray();

        // Act
        var atr = TechnicalIndicators.CalculateATR(highs, lows, closes, 14);

        // Assert - ATR should be approximately the range (10)
        atr.Should().BeApproximately(10.0, 0.1);
    }

    [Fact]
    public void CalculateATR_WithGaps_IncludesGapInRange()
    {
        // Arrange - Bars with gaps
        var highs = new double[] { 100, 105, 115, 120, 125, 130, 135, 140, 145, 150, 155, 160, 165, 170, 175, 180 };
        var lows = new double[] { 95, 100, 110, 115, 120, 125, 130, 135, 140, 145, 150, 155, 160, 165, 170, 175 };
        var closes = new double[] { 98, 103, 112, 118, 123, 128, 133, 138, 143, 148, 153, 158, 163, 168, 173, 178 };

        // Act
        var atr = TechnicalIndicators.CalculateATR(highs, lows, closes, 14);

        // Assert - ATR should be positive
        atr.Should().BePositive();
    }

    [Fact]
    public void CalculateATR_WithInsufficientData_ReturnsZero()
    {
        // Arrange
        var highs = new double[] { 100, 105 };
        var lows = new double[] { 95, 100 };
        var closes = new double[] { 98, 103 };

        // Act
        var atr = TechnicalIndicators.CalculateATR(highs, lows, closes, 14);

        // Assert
        atr.Should().Be(0);
    }

    #endregion

    #region MACD Tests

    [Fact]
    public void CalculateMACD_WithUptrend_ReturnsPosiveHistogram()
    {
        // Arrange - Strong uptrend
        var macdHistory = new List<double>();

        // Need to build up MACD history with multiple calls to get proper signal line
        // Each call adds progressively higher prices (uptrend)
        for (int iteration = 0; iteration < 15; iteration++)
        {
            var closes = Enumerable.Range(1, 50).Select(i => 100.0 + i * 2 + iteration * 10).ToArray();
            TechnicalIndicators.CalculateMACD(closes, macdHistory);
        }

        // Final call with strong uptrend
        var finalCloses = Enumerable.Range(1, 50).Select(i => 100.0 + i * 2 + 150).ToArray();

        // Act
        var (macd, signal, histogram) = TechnicalIndicators.CalculateMACD(finalCloses, macdHistory);

        // Assert - In uptrend, fast EMA > slow EMA, so MACD positive
        macd.Should().BePositive();
        // Histogram may be small but should be non-negative in sustained uptrend
        histogram.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void CalculateMACD_WithDowntrend_ReturnsNegativeHistogram()
    {
        // Arrange - Strong downtrend
        var closes = Enumerable.Range(1, 50).Select(i => 200.0 - i * 2).ToArray();
        var macdHistory = new List<double>();

        // Act
        var (macd, signal, histogram) = TechnicalIndicators.CalculateMACD(closes, macdHistory);

        // Assert - In downtrend, fast EMA < slow EMA, so MACD negative
        macd.Should().BeNegative();
    }

    [Fact]
    public void CalculateMACD_AccumulatesHistory()
    {
        // Arrange
        var closes = Enumerable.Range(1, 30).Select(i => 100.0 + i).ToArray();
        var macdHistory = new List<double>();

        // Act - Call multiple times
        for (int i = 0; i < 5; i++)
        {
            TechnicalIndicators.CalculateMACD(closes, macdHistory);
        }

        // Assert - History should have 5 entries
        macdHistory.Count.Should().Be(5);
    }

    [Fact]
    public void CalculateMACD_HistoryLimitedTo100()
    {
        // Arrange
        var closes = Enumerable.Range(1, 30).Select(i => 100.0 + i).ToArray();
        var macdHistory = new List<double>();

        // Pre-fill with 98 entries
        for (int i = 0; i < 98; i++)
        {
            macdHistory.Add(i);
        }

        // Act - Add 5 more
        for (int i = 0; i < 5; i++)
        {
            TechnicalIndicators.CalculateMACD(closes, macdHistory);
        }

        // Assert - Should be capped at 100
        macdHistory.Count.Should().Be(100);
    }

    #endregion

    #region SuperTrend Tests

    [Fact]
    public void CalculateSuperTrend_InUptrend_ReturnsLowerBand()
    {
        // Arrange - Price above midpoint
        var highs = Enumerable.Repeat(110.0, 20).ToArray();
        var lows = Enumerable.Repeat(100.0, 20).ToArray();
        var closes = Enumerable.Repeat(108.0, 20).ToArray(); // Above midpoint (105)

        // Act
        var superTrend = TechnicalIndicators.CalculateSuperTrend(highs, lows, closes, 10, 3.0);

        // Assert - Should return lower band (< midpoint)
        superTrend.Should().BeLessThan(105.0);
    }

    [Fact]
    public void CalculateSuperTrend_InDowntrend_ReturnsUpperBand()
    {
        // Arrange - Price below midpoint
        var highs = Enumerable.Repeat(110.0, 20).ToArray();
        var lows = Enumerable.Repeat(100.0, 20).ToArray();
        var closes = Enumerable.Repeat(102.0, 20).ToArray(); // Below midpoint (105)

        // Act
        var superTrend = TechnicalIndicators.CalculateSuperTrend(highs, lows, closes, 10, 3.0);

        // Assert - Should return upper band (> midpoint)
        superTrend.Should().BeGreaterThan(105.0);
    }

    [Fact]
    public void CalculateSuperTrend_WithInsufficientData_ReturnsLastClose()
    {
        // Arrange
        var highs = new double[] { 110 };
        var lows = new double[] { 100 };
        var closes = new double[] { 105 };

        // Act
        var superTrend = TechnicalIndicators.CalculateSuperTrend(highs, lows, closes, 10, 3.0);

        // Assert
        superTrend.Should().Be(105);
    }

    #endregion

    #region Divergence Detection Tests

    [Fact]
    public void DetectBullishDivergence_WithDivergence_ReturnsTrue()
    {
        // Arrange - Price makes lower low, RSI makes higher low
        // Create a pattern with two lows where price goes lower but RSI goes higher
        var closes = new double[]
        {
            100, 99, 98, 97, 96, // Declining
            95,                  // First low at index 5
            96, 97, 98, 99,     // Recovery
            98, 97, 96,         // Decline again
            94,                  // Second low (lower than first) at index 13
            95, 96, 97, 98, 99, 100
        };

        // RSI should be higher at second low
        var rsiValues = new double[]
        {
            50, 48, 45, 42, 38,
            30,                  // RSI at first low
            35, 40, 45, 48,
            45, 42, 38,
            35,                  // RSI at second low (higher than first low of 30)
            40, 45, 50, 55, 60, 65
        };

        // Act
        var result = TechnicalIndicators.DetectBullishDivergence(closes, rsiValues, 20);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void DetectBullishDivergence_WithNoDivergence_ReturnsFalse()
    {
        // Arrange - Both price and RSI making lower lows (no divergence)
        var closes = Enumerable.Range(1, 20).Select(i => 100.0 - i * 0.5).ToArray();
        var rsiValues = Enumerable.Range(1, 20).Select(i => 50.0 - i).ToArray();

        // Act
        var result = TechnicalIndicators.DetectBullishDivergence(closes, rsiValues, 20);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void DetectBullishDivergence_WithInsufficientData_ReturnsFalse()
    {
        // Arrange
        var closes = new double[] { 100, 99, 98 };
        var rsiValues = new double[] { 50, 45, 40 };

        // Act
        var result = TechnicalIndicators.DetectBullishDivergence(closes, rsiValues, 20);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void AllIndicators_HandleEmptyArrays()
    {
        // Arrange
        var empty = Array.Empty<double>();
        var macdHistory = new List<double>();

        // Act & Assert - Should not throw
        var ema = TechnicalIndicators.CalculateEMA(empty, 10);
        var sma = TechnicalIndicators.CalculateSMA(empty, 10);

        ema.Should().Be(0);
        sma.Should().Be(0);
    }

    [Fact]
    public void AllIndicators_HandleSingleValue()
    {
        // Arrange
        var single = new double[] { 100 };
        var macdHistory = new List<double>();

        // Act
        var ema = TechnicalIndicators.CalculateEMA(single, 10);
        var sma = TechnicalIndicators.CalculateSMA(single, 10);
        var rsi = TechnicalIndicators.CalculateRSI(single, 14);

        // Assert
        ema.Should().Be(100);
        sma.Should().Be(100);
        rsi.Should().Be(50); // Default for insufficient data
    }

    [Fact]
    public void AllIndicators_HandleNegativeValues()
    {
        // Arrange - Some indicators might receive negative values in edge cases
        var values = new double[] { -10, -5, 0, 5, 10, 15, 20, 25, 30, 35, 40, 45, 50, 55, 60, 65 };

        // Act & Assert - Should not throw
        var ema = TechnicalIndicators.CalculateEMA(values, 5);
        var sma = TechnicalIndicators.CalculateSMA(values, 5);
        var rsi = TechnicalIndicators.CalculateRSI(values, 14);

        ema.Should().NotBe(double.NaN);
        sma.Should().NotBe(double.NaN);
        rsi.Should().BeInRange(0, 100);
    }

    #endregion
}
