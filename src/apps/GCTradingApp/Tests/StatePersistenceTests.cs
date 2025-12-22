/*
 * Feature Tests for State Persistence
 * Tests saving and loading of application and strategy state
 */

using FluentAssertions;
using Newtonsoft.Json;
using Xunit;

namespace GCTradingApp.Tests;

/// <summary>
/// Tests for state persistence (save/load) functionality
/// </summary>
public class StatePersistenceTests : IDisposable
{
    private readonly string _testStateFile;

    public StatePersistenceTests()
    {
        _testStateFile = Path.Combine(Path.GetTempPath(), $"gc_test_state_{Guid.NewGuid()}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_testStateFile))
        {
            File.Delete(_testStateFile);
        }
    }

    #region AppState Serialization Tests

    [Fact]
    public void AppState_DefaultValues_SerializesCorrectly()
    {
        // Arrange
        var state = new AppState();

        // Act
        var json = JsonConvert.SerializeObject(state, Formatting.Indented);
        var deserialized = JsonConvert.DeserializeObject<AppState>(json);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.Host.Should().Be("127.0.0.1");
        deserialized.Port.Should().Be(7497);
        deserialized.ClientId.Should().Be(1);
        deserialized.AggressiveEnabled.Should().BeFalse();
        deserialized.ConservativeEnabled.Should().BeTrue();
        deserialized.SizingMode.Should().Be("Contracts");
    }

    [Fact]
    public void AppState_WithCustomSettings_RoundTrips()
    {
        // Arrange
        var state = new AppState
        {
            Host = "192.168.1.100",
            Port = 7496,
            ClientId = 5,
            AggressiveEnabled = true,
            ConservativeEnabled = false,
            SizingMode = "Capital",
            AggressiveSize = 100000,
            ConservativeSize = 50000,
            StrategyRunning = true,
            CurrentEquity = 250000,
            PeakEquity = 275000,
            CurrentDrawdown = 9.09
        };

        // Act
        var json = JsonConvert.SerializeObject(state);
        var deserialized = JsonConvert.DeserializeObject<AppState>(json);

        // Assert
        deserialized.Should().BeEquivalentTo(state);
    }

    [Fact]
    public void AppState_WithOrders_RoundTrips()
    {
        // Arrange
        var state = new AppState();
        state.Orders[1] = new OrderRecord
        {
            OrderId = 1,
            Time = new DateTime(2024, 12, 22, 10, 30, 0),
            Strategy = "Aggressive",
            Action = "BUY",
            Quantity = 2,
            OrderType = "MKT",
            Status = "Filled",
            Filled = 2,
            Remaining = 0
        };
        state.Orders[2] = new OrderRecord
        {
            OrderId = 2,
            Time = new DateTime(2024, 12, 22, 11, 0, 0),
            Strategy = "Conservative",
            Action = "BUY",
            Quantity = 1,
            OrderType = "LMT",
            LimitPrice = 2050.5,
            Status = "Submitted"
        };

        // Act
        var json = JsonConvert.SerializeObject(state, Formatting.Indented);
        var deserialized = JsonConvert.DeserializeObject<AppState>(json);

        // Assert
        deserialized!.Orders.Should().HaveCount(2);
        deserialized.Orders[1].Strategy.Should().Be("Aggressive");
        deserialized.Orders[2].LimitPrice.Should().Be(2050.5);
    }

    [Fact]
    public void AppState_WithFills_RoundTrips()
    {
        // Arrange
        var state = new AppState();
        state.Fills.Add(new FillRecord
        {
            ExecId = "EXEC001",
            Time = DateTime.Now,
            Strategy = "Aggressive",
            Action = "BUY",
            Quantity = 2,
            Price = 2055.25,
            Commission = 4.50,
            RealizedPnL = 0
        });
        state.Fills.Add(new FillRecord
        {
            ExecId = "EXEC002",
            Time = DateTime.Now.AddHours(2),
            Strategy = "Aggressive",
            Action = "SELL",
            Quantity = 2,
            Price = 2070.00,
            Commission = 4.50,
            RealizedPnL = 1475.00  // (2070-2055.25) * 2 * 100 - 4.50*2
        });

        // Act
        var json = JsonConvert.SerializeObject(state);
        var deserialized = JsonConvert.DeserializeObject<AppState>(json);

        // Assert
        deserialized!.Fills.Should().HaveCount(2);
        deserialized.Fills[1].RealizedPnL.Should().Be(1475.00);
    }

    [Fact]
    public void AppState_WithPositions_RoundTrips()
    {
        // Arrange
        var state = new AppState();
        state.Positions["GC_DU123456"] = new PositionRecord
        {
            Symbol = "GC",
            Position = 2,
            AvgCost = 2055.25,
            MarketPrice = 2060.00,
            MarketValue = 412000,
            UnrealizedPnL = 950.00,
            RealizedPnL = 0
        };

        // Act
        var json = JsonConvert.SerializeObject(state);
        var deserialized = JsonConvert.DeserializeObject<AppState>(json);

        // Assert
        deserialized!.Positions.Should().ContainKey("GC_DU123456");
        deserialized.Positions["GC_DU123456"].Position.Should().Be(2);
    }

    #endregion

    #region StrategyState Serialization Tests

    [Fact]
    public void StrategyState_WhenInPosition_SerializesAllFields()
    {
        // Arrange
        var state = new StrategyState
        {
            InPosition = true,
            EntryPrice = 2055.50,
            EntryTime = new DateTime(2024, 12, 22, 10, 30, 0),
            EntryBarCount = 42,
            StopPrice = 2040.00,
            TargetPrice = 2080.00,
            CurrentOrderId = 12345,
            PositionQuantity = 2
        };

        // Act
        var json = JsonConvert.SerializeObject(state);
        var deserialized = JsonConvert.DeserializeObject<StrategyState>(json);

        // Assert
        deserialized.Should().BeEquivalentTo(state);
    }

    [Fact]
    public void StrategyState_WhenFlat_SerializesCorrectly()
    {
        // Arrange
        var state = new StrategyState
        {
            InPosition = false,
            EntryPrice = 0,
            EntryBarCount = 0,
            StopPrice = 0,
            TargetPrice = 0,
            CurrentOrderId = 0,
            PositionQuantity = 0
        };

        // Act
        var json = JsonConvert.SerializeObject(state);
        var deserialized = JsonConvert.DeserializeObject<StrategyState>(json);

        // Assert
        deserialized!.InPosition.Should().BeFalse();
        deserialized.EntryPrice.Should().Be(0);
        deserialized.PositionQuantity.Should().Be(0);
    }

    [Fact]
    public void AppState_WithStrategyStates_RoundTrips()
    {
        // Arrange
        var state = new AppState
        {
            AggressiveState = new StrategyState
            {
                InPosition = true,
                EntryPrice = 2055.50,
                StopPrice = 2040.00,
                TargetPrice = 2080.00,
                PositionQuantity = 2
            },
            ConservativeState = new StrategyState
            {
                InPosition = false
            }
        };

        // Act
        var json = JsonConvert.SerializeObject(state, Formatting.Indented);
        var deserialized = JsonConvert.DeserializeObject<AppState>(json);

        // Assert
        deserialized!.AggressiveState.InPosition.Should().BeTrue();
        deserialized.AggressiveState.EntryPrice.Should().Be(2055.50);
        deserialized.AggressiveState.PositionQuantity.Should().Be(2);
        deserialized.ConservativeState.InPosition.Should().BeFalse();
    }

    #endregion

    #region File Persistence Tests

    [Fact]
    public void SaveState_CreatesValidJsonFile()
    {
        // Arrange
        var state = new AppState
        {
            Host = "localhost",
            Port = 7497,
            AggressiveEnabled = true
        };

        // Act
        var json = JsonConvert.SerializeObject(state, Formatting.Indented);
        File.WriteAllText(_testStateFile, json);

        // Assert
        File.Exists(_testStateFile).Should().BeTrue();
        var content = File.ReadAllText(_testStateFile);
        content.Should().Contain("\"Host\": \"localhost\"");
        content.Should().Contain("\"AggressiveEnabled\": true");
    }

    [Fact]
    public void LoadState_FromValidFile_ReturnsState()
    {
        // Arrange
        var state = new AppState
        {
            Host = "192.168.1.1",
            Port = 7496,
            ClientId = 10,
            AggressiveSize = 5
        };
        var json = JsonConvert.SerializeObject(state, Formatting.Indented);
        File.WriteAllText(_testStateFile, json);

        // Act
        var loadedJson = File.ReadAllText(_testStateFile);
        var loadedState = JsonConvert.DeserializeObject<AppState>(loadedJson);

        // Assert
        loadedState!.Host.Should().Be("192.168.1.1");
        loadedState.Port.Should().Be(7496);
        loadedState.ClientId.Should().Be(10);
        loadedState.AggressiveSize.Should().Be(5);
    }

    [Fact]
    public void LoadState_FromMissingFile_ReturnsNewState()
    {
        // Arrange
        var missingFile = Path.Combine(Path.GetTempPath(), $"nonexistent_{Guid.NewGuid()}.json");

        // Act
        AppState state;
        if (File.Exists(missingFile))
        {
            var json = File.ReadAllText(missingFile);
            state = JsonConvert.DeserializeObject<AppState>(json) ?? new AppState();
        }
        else
        {
            state = new AppState();
        }

        // Assert
        state.Should().NotBeNull();
        state.Host.Should().Be("127.0.0.1"); // Default value
    }

    [Fact]
    public void LoadState_FromCorruptedFile_ReturnsNewState()
    {
        // Arrange
        File.WriteAllText(_testStateFile, "{ invalid json }}}");

        // Act
        AppState state;
        try
        {
            var json = File.ReadAllText(_testStateFile);
            state = JsonConvert.DeserializeObject<AppState>(json) ?? new AppState();
        }
        catch
        {
            state = new AppState();
        }

        // Assert
        state.Should().NotBeNull();
        state.Host.Should().Be("127.0.0.1");
    }

    [Fact]
    public void SaveAndLoad_PreservesAllData()
    {
        // Arrange - Create a fully populated state
        var originalState = new AppState
        {
            Host = "10.0.0.1",
            Port = 7496,
            ClientId = 42,
            AggressiveEnabled = true,
            ConservativeEnabled = true,
            SizingMode = "Capital",
            AggressiveSize = 100000,
            ConservativeSize = 50000,
            StrategyRunning = true,
            CurrentEquity = 500000,
            PeakEquity = 550000,
            CurrentDrawdown = 9.09,
            AggressiveState = new StrategyState
            {
                InPosition = true,
                EntryPrice = 2055.50,
                EntryTime = new DateTime(2024, 12, 22, 10, 30, 0),
                EntryBarCount = 42,
                StopPrice = 2040.00,
                TargetPrice = 2080.00,
                CurrentOrderId = 12345,
                PositionQuantity = 3
            },
            ConservativeState = new StrategyState
            {
                InPosition = false
            }
        };

        // Add some orders
        originalState.Orders[1] = new OrderRecord
        {
            OrderId = 1,
            Time = DateTime.Now,
            Strategy = "Aggressive",
            Action = "BUY",
            Quantity = 3,
            Status = "Filled"
        };

        // Add some fills
        originalState.Fills.Add(new FillRecord
        {
            ExecId = "EXEC001",
            Time = DateTime.Now,
            Strategy = "Aggressive",
            Action = "BUY",
            Quantity = 3,
            Price = 2055.50
        });

        // Add position
        originalState.Positions["GC_ACC1"] = new PositionRecord
        {
            Symbol = "GC",
            Position = 3,
            AvgCost = 2055.50
        };

        // Act - Save and reload
        var json = JsonConvert.SerializeObject(originalState, Formatting.Indented);
        File.WriteAllText(_testStateFile, json);

        var loadedJson = File.ReadAllText(_testStateFile);
        var loadedState = JsonConvert.DeserializeObject<AppState>(loadedJson);

        // Assert
        loadedState.Should().NotBeNull();
        loadedState!.Host.Should().Be("10.0.0.1");
        loadedState.Port.Should().Be(7496);
        loadedState.CurrentEquity.Should().Be(500000);
        loadedState.AggressiveState.InPosition.Should().BeTrue();
        loadedState.AggressiveState.EntryPrice.Should().Be(2055.50);
        loadedState.AggressiveState.PositionQuantity.Should().Be(3);
        loadedState.Orders.Should().HaveCount(1);
        loadedState.Fills.Should().HaveCount(1);
        loadedState.Positions.Should().HaveCount(1);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void StrategyState_WithZeroValues_HandledCorrectly()
    {
        // Arrange
        var state = new StrategyState
        {
            InPosition = true,
            EntryPrice = 0, // Edge case: entry at 0 (shouldn't happen but test it)
            StopPrice = 0,
            TargetPrice = 0
        };

        // Act
        var json = JsonConvert.SerializeObject(state);
        var deserialized = JsonConvert.DeserializeObject<StrategyState>(json);

        // Assert
        deserialized!.InPosition.Should().BeTrue();
        deserialized.EntryPrice.Should().Be(0);
    }

    [Fact]
    public void StrategyState_WithNegativeValues_HandledCorrectly()
    {
        // Arrange - Negative values might occur in edge cases
        var state = new StrategyState
        {
            InPosition = true,
            EntryPrice = 2000,
            StopPrice = 1950,
            TargetPrice = 2100,
            EntryBarCount = -1 // Edge case: shouldn't happen
        };

        // Act
        var json = JsonConvert.SerializeObject(state);
        var deserialized = JsonConvert.DeserializeObject<StrategyState>(json);

        // Assert
        deserialized!.EntryBarCount.Should().Be(-1);
    }

    [Fact]
    public void AppState_WithEmptyCollections_SerializesCorrectly()
    {
        // Arrange
        var state = new AppState
        {
            Orders = new Dictionary<int, OrderRecord>(),
            Fills = new List<FillRecord>(),
            Positions = new Dictionary<string, PositionRecord>()
        };

        // Act
        var json = JsonConvert.SerializeObject(state);
        var deserialized = JsonConvert.DeserializeObject<AppState>(json);

        // Assert
        deserialized!.Orders.Should().BeEmpty();
        deserialized.Fills.Should().BeEmpty();
        deserialized.Positions.Should().BeEmpty();
    }

    [Fact]
    public void AppState_WithLargeData_SerializesCorrectly()
    {
        // Arrange - Large number of fills (simulating extended trading)
        var state = new AppState();
        for (int i = 0; i < 1000; i++)
        {
            state.Fills.Add(new FillRecord
            {
                ExecId = $"EXEC_{i:D5}",
                Time = DateTime.Now.AddMinutes(-i),
                Strategy = i % 2 == 0 ? "Aggressive" : "Conservative",
                Action = i % 2 == 0 ? "BUY" : "SELL",
                Quantity = 1,
                Price = 2000 + (i * 0.5),
                RealizedPnL = i * 10
            });
        }

        // Act
        var json = JsonConvert.SerializeObject(state);
        var deserialized = JsonConvert.DeserializeObject<AppState>(json);

        // Assert
        deserialized!.Fills.Should().HaveCount(1000);
        deserialized.Fills[999].ExecId.Should().Be("EXEC_00999");
    }

    #endregion
}
