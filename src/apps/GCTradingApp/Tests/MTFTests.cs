/*
 * MTF (Multi-Timeframe) Tests for GC Trading Application
 * Tests bar aggregation, SuperTrend tracking, and alignment detection
 */

using Xunit;

namespace GCTradingApp.Tests;

/// <summary>
/// Tests for MultiTimeframeBarManager bar aggregation
/// </summary>
public class BarAggregationTests
{
    [Fact]
    public void ProcessBar_Aggregates5SecBarsInto1MinBars()
    {
        var manager = new MultiTimeframeBarManager();
        manager.Configure(TimeframePreset.Preset_1m_5m_15m);

        var baseTime = new DateTime(2024, 1, 1, 9, 0, 0);
        int completedBars = 0;
        manager.OnTimeframeBarCompleted += (name, bar) =>
        {
            if (name == "1m") completedBars++;
        };

        // Send 12 five-second bars (1 minute worth)
        for (int i = 0; i < 12; i++)
        {
            var bar = new BarData
            {
                Time = baseTime.AddSeconds(i * 5),
                Open = 2650 + i * 0.1,
                High = 2651 + i * 0.1,
                Low = 2649 + i * 0.1,
                Close = 2650.5 + i * 0.1,
                Volume = 100
            };
            manager.ProcessBar(bar);
        }

        // Send first bar of next minute to complete previous
        var nextMinuteBar = new BarData
        {
            Time = baseTime.AddSeconds(60),
            Open = 2652,
            High = 2653,
            Low = 2651,
            Close = 2652.5,
            Volume = 100
        };
        manager.ProcessBar(nextMinuteBar);

        Assert.Equal(1, completedBars);
    }

    [Fact]
    public void ProcessBar_AggregatesHighLowCorrectly()
    {
        var manager = new MultiTimeframeBarManager();
        manager.Configure(TimeframePreset.Preset_1m_5m_15m);

        var baseTime = new DateTime(2024, 1, 1, 9, 0, 0);
        BarData? completedBar = null;
        manager.OnTimeframeBarCompleted += (name, bar) =>
        {
            if (name == "1m") completedBar = bar;
        };

        // First bar
        manager.ProcessBar(new BarData
        {
            Time = baseTime,
            Open = 2650,
            High = 2655,
            Low = 2648,
            Close = 2653,
            Volume = 100
        });

        // Second bar with higher high
        manager.ProcessBar(new BarData
        {
            Time = baseTime.AddSeconds(5),
            Open = 2653,
            High = 2660,  // New high
            Low = 2651,
            Close = 2658,
            Volume = 150
        });

        // Third bar with lower low
        manager.ProcessBar(new BarData
        {
            Time = baseTime.AddSeconds(10),
            Open = 2658,
            High = 2659,
            Low = 2645,  // New low
            Close = 2647,
            Volume = 200
        });

        // Complete the bar by sending next minute's bar
        manager.ProcessBar(new BarData
        {
            Time = baseTime.AddSeconds(60),
            Open = 2647,
            High = 2650,
            Low = 2646,
            Close = 2649,
            Volume = 100
        });

        Assert.NotNull(completedBar);
        Assert.Equal(2650, completedBar!.Open);   // First bar's open
        Assert.Equal(2660, completedBar.High);    // Highest high
        Assert.Equal(2645, completedBar.Low);     // Lowest low
        Assert.Equal(2647, completedBar.Close);   // Last bar's close
        Assert.Equal(450, completedBar.Volume);   // Sum of volumes
    }

    [Fact]
    public void GetPresetConfigs_Returns3TimeframesForEachPreset()
    {
        var presets = new[]
        {
            TimeframePreset.Preset_5m_15m_1H,
            TimeframePreset.Preset_1m_5m_15m,
            TimeframePreset.Preset_15m_1H_4H,
            TimeframePreset.Preset_5m_1H_Daily
        };

        foreach (var preset in presets)
        {
            var configs = MultiTimeframeBarManager.GetPresetConfigs(preset);
            Assert.Equal(3, configs.Length);
        }
    }

    [Fact]
    public void GetPresetConfigs_5m15m1H_HasCorrectSeconds()
    {
        var configs = MultiTimeframeBarManager.GetPresetConfigs(TimeframePreset.Preset_5m_15m_1H);

        Assert.Equal("5m", configs[0].Name);
        Assert.Equal(300, configs[0].SecondsPerBar);

        Assert.Equal("15m", configs[1].Name);
        Assert.Equal(900, configs[1].SecondsPerBar);

        Assert.Equal("1H", configs[2].Name);
        Assert.Equal(3600, configs[2].SecondsPerBar);
    }

    [Fact]
    public void IsWarmedUp_ReturnsFalseInitially()
    {
        var manager = new MultiTimeframeBarManager(superTrendPeriod: 10);
        manager.Configure(TimeframePreset.Preset_5m_15m_1H);

        Assert.False(manager.IsWarmedUp());
    }
}

/// <summary>
/// Tests for stateful SuperTrend calculation
/// </summary>
public class StatefulSuperTrendTests
{
    [Fact]
    public void CalculateSuperTrendStateful_InitialDirection_BasedOnPricePosition()
    {
        double prevSuperTrend = 0;
        int prevDirection = 0;

        // Price above HL2 should be bullish
        var highs = new double[] { 2655, 2656, 2657, 2658, 2659, 2660, 2661, 2662, 2663, 2664, 2665 };
        var lows = new double[] { 2645, 2646, 2647, 2648, 2649, 2650, 2651, 2652, 2653, 2654, 2655 };
        var closes = new double[] { 2653, 2654, 2655, 2656, 2657, 2658, 2659, 2660, 2661, 2662, 2663 };

        var (value, direction) = TechnicalIndicators.CalculateSuperTrendStateful(
            highs, lows, closes, 10, 3.0,
            ref prevSuperTrend, ref prevDirection);

        Assert.Equal(1, direction);  // Bullish
    }

    [Fact]
    public void CalculateSuperTrendStateful_MaintainsDirection_WhenNoFlip()
    {
        double prevSuperTrend = 2640;
        int prevDirection = 1;  // Bullish

        // Price stays above SuperTrend
        var highs = new double[] { 2655, 2656, 2657, 2658, 2659, 2660, 2661, 2662, 2663, 2664, 2665 };
        var lows = new double[] { 2645, 2646, 2647, 2648, 2649, 2650, 2651, 2652, 2653, 2654, 2655 };
        var closes = new double[] { 2653, 2654, 2655, 2656, 2657, 2658, 2659, 2660, 2661, 2662, 2663 };

        var (value, direction) = TechnicalIndicators.CalculateSuperTrendStateful(
            highs, lows, closes, 10, 3.0,
            ref prevSuperTrend, ref prevDirection);

        Assert.Equal(1, direction);  // Still bullish
    }

    [Fact]
    public void CalculateSuperTrendStateful_FlipsToBearish_WhenPriceCrossesBelow()
    {
        double prevSuperTrend = 2650;
        int prevDirection = 1;  // Was bullish

        // Price drops below SuperTrend
        var highs = new double[] { 2655, 2656, 2657, 2658, 2659, 2660, 2651, 2642, 2643, 2644, 2640 };
        var lows = new double[] { 2645, 2646, 2647, 2648, 2649, 2650, 2641, 2632, 2633, 2634, 2630 };
        var closes = new double[] { 2653, 2654, 2655, 2656, 2657, 2658, 2649, 2640, 2641, 2642, 2635 };

        var (value, direction) = TechnicalIndicators.CalculateSuperTrendStateful(
            highs, lows, closes, 10, 3.0,
            ref prevSuperTrend, ref prevDirection);

        Assert.Equal(-1, direction);  // Flipped to bearish
    }

    [Fact]
    public void CalculateSuperTrendStateful_InsufficientData_ReturnsZeroDirection()
    {
        double prevSuperTrend = 0;
        int prevDirection = 0;

        var highs = new double[] { 2655, 2656, 2657 };
        var lows = new double[] { 2645, 2646, 2647 };
        var closes = new double[] { 2653, 2654, 2655 };

        var (value, direction) = TechnicalIndicators.CalculateSuperTrendStateful(
            highs, lows, closes, 10, 3.0,
            ref prevSuperTrend, ref prevDirection);

        Assert.Equal(0, direction);  // Not enough data
    }
}

/// <summary>
/// Tests for MTF alignment detection
/// </summary>
public class AlignmentTests
{
    [Fact]
    public void GetAlignment_AllUnknown_Initially()
    {
        var manager = new MultiTimeframeBarManager();
        manager.Configure(TimeframePreset.Preset_5m_15m_1H);

        var alignment = manager.GetAlignment();

        Assert.False(alignment.AllBullish);
        Assert.False(alignment.AllBearish);
        Assert.False(alignment.Aligned);
    }

    [Fact]
    public void GetAlignment_ReturnsCorrectTimeframeNames()
    {
        var manager = new MultiTimeframeBarManager();
        manager.Configure(TimeframePreset.Preset_5m_15m_1H);

        var names = manager.GetTimeframeNames();

        Assert.Contains("5m", names);
        Assert.Contains("15m", names);
        Assert.Contains("1H", names);
    }

    [Fact]
    public void MTFAlignmentResult_Aligned_WhenAllBullish()
    {
        var result = new MTFAlignmentResult
        {
            AllBullish = true,
            AllBearish = false
        };

        Assert.True(result.Aligned);
    }

    [Fact]
    public void MTFAlignmentResult_Aligned_WhenAllBearish()
    {
        var result = new MTFAlignmentResult
        {
            AllBullish = false,
            AllBearish = true
        };

        Assert.True(result.Aligned);
    }

    [Fact]
    public void MTFAlignmentResult_NotAligned_WhenMixed()
    {
        var result = new MTFAlignmentResult
        {
            AllBullish = false,
            AllBearish = false
        };

        Assert.False(result.Aligned);
    }
}

/// <summary>
/// Tests for bearish divergence detection
/// </summary>
public class BearishDivergenceTests
{
    [Fact]
    public void DetectBearishDivergence_ReturnsTrue_WhenPriceHigherHighRsiLowerHigh()
    {
        // Use exactly 20 elements - the function takes TakeLast(20) and looks for highs in indices 5-15
        var closes = new double[20];
        var rsiValues = new double[20];

        // Base values
        for (int i = 0; i < 20; i++)
        {
            closes[i] = 2650;
            rsiValues[i] = 50;
        }

        // First high at index 7 (within 5-15 range)
        closes[5] = 2652;
        closes[6] = 2658;
        closes[7] = 2665;  // First high peak
        closes[8] = 2658;
        closes[9] = 2652;

        // Second (higher) high at index 12 (within 5-15 range)
        closes[10] = 2655;
        closes[11] = 2668;
        closes[12] = 2680;  // Higher high peak (price higher high)
        closes[13] = 2668;
        closes[14] = 2655;

        // RSI divergence: lower high at second price peak
        rsiValues[7] = 75;   // First RSI high
        rsiValues[12] = 65;  // Lower RSI high (divergence!)

        var result = TechnicalIndicators.DetectBearishDivergence(closes, rsiValues, 20);

        Assert.True(result);
    }

    [Fact]
    public void DetectBearishDivergence_ReturnsFalse_WhenInsufficientData()
    {
        var closes = new double[] { 2650, 2651, 2652 };
        var rsiValues = new double[] { 50, 51, 52 };

        var result = TechnicalIndicators.DetectBearishDivergence(closes, rsiValues, 20);

        Assert.False(result);
    }

    [Fact]
    public void DetectBearishDivergence_ReturnsFalse_WhenNoHighs()
    {
        // Flat price with no distinct highs
        var closes = new double[25];
        var rsiValues = new double[25];
        for (int i = 0; i < 25; i++)
        {
            closes[i] = 2650;
            rsiValues[i] = 50;
        }

        var result = TechnicalIndicators.DetectBearishDivergence(closes, rsiValues, 20);

        Assert.False(result);
    }
}

/// <summary>
/// Tests for MTFStrategyConfig
/// </summary>
public class MTFStrategyConfigTests
{
    [Fact]
    public void DefaultConfig_HasCorrectDefaults()
    {
        var config = new MTFStrategyConfig();

        Assert.Equal("MTF", config.Name);
        Assert.Equal(TimeframePreset.Preset_5m_15m_1H, config.TimeframePreset);
        Assert.Equal(0.80, config.PositionScale);
        Assert.True(config.DrawdownProtection);
        Assert.Equal(0.11, config.MaxDrawdown);
        Assert.Equal(1, config.FixedContracts);
        Assert.False(config.AllowShorts);
        Assert.Equal(10, config.SuperTrendPeriod);
        Assert.Equal(3.0, config.SuperTrendMultiplier);
    }
}

/// <summary>
/// Tests for MTFStrategyState
/// </summary>
public class MTFStrategyStateTests
{
    [Fact]
    public void MTFStrategyState_InheritsFromStrategyState()
    {
        var state = new MTFStrategyState
        {
            InPosition = true,
            EntryPrice = 2650.50,
            PositionQuantity = 2,
            PositionDirection = 1  // Long
        };

        Assert.True(state.InPosition);
        Assert.Equal(2650.50, state.EntryPrice);
        Assert.Equal(2, state.PositionQuantity);
        Assert.Equal(1, state.PositionDirection);
    }

    [Fact]
    public void MTFStrategyState_SupportsShortDirection()
    {
        var state = new MTFStrategyState
        {
            InPosition = true,
            PositionDirection = -1  // Short
        };

        Assert.Equal(-1, state.PositionDirection);
    }

    [Fact]
    public void MTFStrategyState_SupportsActivePreset()
    {
        var state = new MTFStrategyState
        {
            ActivePreset = TimeframePreset.Preset_15m_1H_4H
        };

        Assert.Equal(TimeframePreset.Preset_15m_1H_4H, state.ActivePreset);
    }
}

/// <summary>
/// Integration tests for MTF system
/// </summary>
public class MTFIntegrationTests
{
    [Fact]
    public void MultiTimeframeBarManager_Clear_ResetsAllState()
    {
        var manager = new MultiTimeframeBarManager();
        manager.Configure(TimeframePreset.Preset_5m_15m_1H);

        // Add some bars
        var bar = new BarData
        {
            Time = DateTime.Now,
            Open = 2650,
            High = 2655,
            Low = 2645,
            Close = 2653,
            Volume = 100
        };
        manager.ProcessBar(bar);

        // Clear
        manager.Clear();

        // Verify reset
        Assert.Equal(0, manager.GetMinBarCount());
        Assert.False(manager.IsWarmedUp());
    }

    [Fact]
    public void TimeframeConfig_HasRequiredProperties()
    {
        var config = new TimeframeConfig
        {
            Name = "5m",
            SecondsPerBar = 300,
            LookbackBars = 100
        };

        Assert.Equal("5m", config.Name);
        Assert.Equal(300, config.SecondsPerBar);
        Assert.Equal(100, config.LookbackBars);
    }

    [Fact]
    public void TimeframeBarData_InitializesCorrectly()
    {
        var tfData = new TimeframeBarData
        {
            Config = new TimeframeConfig { Name = "1H", SecondsPerBar = 3600, LookbackBars = 50 },
            SuperTrendDirection = 1
        };

        Assert.Equal("1H", tfData.Config.Name);
        Assert.Equal(1, tfData.SuperTrendDirection);
        Assert.NotNull(tfData.Bars);
        Assert.Empty(tfData.Bars);
    }
}
