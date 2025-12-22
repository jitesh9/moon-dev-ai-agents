/*
 * Unit Tests for Contract Expiry Logic
 * Tests GC futures contract month selection
 */

using FluentAssertions;
using Xunit;

namespace GCTradingApp.Tests;

/// <summary>
/// Tests for GC futures contract expiry month calculation
/// GC contracts trade in February, April, June, August, October, December (even months)
/// </summary>
public class ContractExpiryTests
{
    // Contract months for GC futures (GHJMQVZ = Feb, Apr, Jun, Aug, Oct, Dec)
    private static readonly int[] ContractMonths = { 2, 4, 6, 8, 10, 12 };

    /// <summary>
    /// Gets the next valid contract month for GC futures
    /// Replicates the logic from IBKRClient.CreateGCContract
    /// </summary>
    private static (int year, int month) GetNextContractMonth(DateTime date)
    {
        int year = date.Year;
        int month = date.Month;
        int day = date.Day;

        // Find the next contract month
        bool isContractMonth = ContractMonths.Contains(month);
        bool currentMonthStillValid = isContractMonth && day <= 25;

        if (currentMonthStillValid)
        {
            // Current month is valid and hasn't expired
            return (year, month);
        }

        // Find next contract month
        int nextMonth = month;
        int nextYear = year;

        while (true)
        {
            nextMonth++;
            if (nextMonth > 12)
            {
                nextMonth = 1;
                nextYear++;
            }

            if (ContractMonths.Contains(nextMonth))
            {
                return (nextYear, nextMonth);
            }
        }
    }

    #region Current Month Tests

    [Fact]
    public void GetNextContractMonth_DecemberDay15_ReturnsDecember()
    {
        // Arrange - December 15th, before expiry
        var date = new DateTime(2024, 12, 15);

        // Act
        var (year, month) = GetNextContractMonth(date);

        // Assert - Should still be December
        year.Should().Be(2024);
        month.Should().Be(12);
    }

    [Fact]
    public void GetNextContractMonth_DecemberDay25_ReturnsDecember()
    {
        // Arrange - December 25th, exactly at expiry boundary
        var date = new DateTime(2024, 12, 25);

        // Act
        var (year, month) = GetNextContractMonth(date);

        // Assert - Should still be December (day <= 25)
        year.Should().Be(2024);
        month.Should().Be(12);
    }

    [Fact]
    public void GetNextContractMonth_DecemberDay26_ReturnsFebruary()
    {
        // Arrange - December 26th, after expiry
        var date = new DateTime(2024, 12, 26);

        // Act
        var (year, month) = GetNextContractMonth(date);

        // Assert - Should roll to February next year
        year.Should().Be(2025);
        month.Should().Be(2);
    }

    [Fact]
    public void GetNextContractMonth_FebruaryDay10_ReturnsFebruary()
    {
        // Arrange - February 10th
        var date = new DateTime(2025, 2, 10);

        // Act
        var (year, month) = GetNextContractMonth(date);

        // Assert
        year.Should().Be(2025);
        month.Should().Be(2);
    }

    #endregion

    #region Non-Contract Month Tests

    [Fact]
    public void GetNextContractMonth_January_ReturnsFebruary()
    {
        // Arrange - January (not a contract month)
        var date = new DateTime(2025, 1, 15);

        // Act
        var (year, month) = GetNextContractMonth(date);

        // Assert - Should return February
        year.Should().Be(2025);
        month.Should().Be(2);
    }

    [Fact]
    public void GetNextContractMonth_March_ReturnsApril()
    {
        // Arrange - March (not a contract month)
        var date = new DateTime(2025, 3, 15);

        // Act
        var (year, month) = GetNextContractMonth(date);

        // Assert - Should return April
        year.Should().Be(2025);
        month.Should().Be(4);
    }

    [Fact]
    public void GetNextContractMonth_May_ReturnsJune()
    {
        // Arrange - May (not a contract month)
        var date = new DateTime(2025, 5, 15);

        // Act
        var (year, month) = GetNextContractMonth(date);

        // Assert - Should return June
        year.Should().Be(2025);
        month.Should().Be(6);
    }

    [Fact]
    public void GetNextContractMonth_July_ReturnsAugust()
    {
        // Arrange - July (not a contract month)
        var date = new DateTime(2025, 7, 15);

        // Act
        var (year, month) = GetNextContractMonth(date);

        // Assert - Should return August
        year.Should().Be(2025);
        month.Should().Be(8);
    }

    [Fact]
    public void GetNextContractMonth_September_ReturnsOctober()
    {
        // Arrange - September (not a contract month)
        var date = new DateTime(2025, 9, 15);

        // Act
        var (year, month) = GetNextContractMonth(date);

        // Assert - Should return October
        year.Should().Be(2025);
        month.Should().Be(10);
    }

    [Fact]
    public void GetNextContractMonth_November_ReturnsDecember()
    {
        // Arrange - November (not a contract month)
        var date = new DateTime(2025, 11, 15);

        // Act
        var (year, month) = GetNextContractMonth(date);

        // Assert - Should return December
        year.Should().Be(2025);
        month.Should().Be(12);
    }

    #endregion

    #region Year Rollover Tests

    [Fact]
    public void GetNextContractMonth_DecemberAfterExpiry_ReturnsNextYearFebruary()
    {
        // Arrange
        var date = new DateTime(2024, 12, 28);

        // Act
        var (year, month) = GetNextContractMonth(date);

        // Assert
        year.Should().Be(2025);
        month.Should().Be(2);
    }

    [Fact]
    public void GetNextContractMonth_LeapYear_HandlesCorrectly()
    {
        // Arrange - February 29 in leap year
        var date = new DateTime(2024, 2, 29);

        // Act
        var (year, month) = GetNextContractMonth(date);

        // Assert - Should still be April (Feb > 25)
        year.Should().Be(2024);
        month.Should().Be(4);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void GetNextContractMonth_FirstDayOfYear_ReturnsFebruary()
    {
        // Arrange
        var date = new DateTime(2025, 1, 1);

        // Act
        var (year, month) = GetNextContractMonth(date);

        // Assert
        year.Should().Be(2025);
        month.Should().Be(2);
    }

    [Fact]
    public void GetNextContractMonth_LastDayOfYear_ReturnsNextYearFebruary()
    {
        // Arrange
        var date = new DateTime(2024, 12, 31);

        // Act
        var (year, month) = GetNextContractMonth(date);

        // Assert
        year.Should().Be(2025);
        month.Should().Be(2);
    }

    [Theory]
    [InlineData(2024, 2, 1, 2024, 2)]   // Feb 1 -> Feb
    [InlineData(2024, 2, 25, 2024, 2)]  // Feb 25 -> Feb
    [InlineData(2024, 2, 26, 2024, 4)]  // Feb 26 -> Apr
    [InlineData(2024, 4, 1, 2024, 4)]   // Apr 1 -> Apr
    [InlineData(2024, 4, 25, 2024, 4)]  // Apr 25 -> Apr
    [InlineData(2024, 4, 26, 2024, 6)]  // Apr 26 -> Jun
    [InlineData(2024, 6, 15, 2024, 6)]  // Jun 15 -> Jun
    [InlineData(2024, 8, 15, 2024, 8)]  // Aug 15 -> Aug
    [InlineData(2024, 10, 15, 2024, 10)] // Oct 15 -> Oct
    [InlineData(2024, 12, 15, 2024, 12)] // Dec 15 -> Dec
    public void GetNextContractMonth_VariousDates_ReturnsCorrectMonth(
        int inputYear, int inputMonth, int inputDay, int expectedYear, int expectedMonth)
    {
        // Arrange
        var date = new DateTime(inputYear, inputMonth, inputDay);

        // Act
        var (year, month) = GetNextContractMonth(date);

        // Assert
        year.Should().Be(expectedYear);
        month.Should().Be(expectedMonth);
    }

    #endregion

    #region Contract Symbol Tests

    [Fact]
    public void ContractMonthCodes_AreCorrect()
    {
        // GC uses standard futures month codes:
        // G=Feb, J=Apr, M=Jun, Q=Aug, V=Oct, Z=Dec
        var monthCodes = new Dictionary<int, char>
        {
            { 2, 'G' },
            { 4, 'J' },
            { 6, 'M' },
            { 8, 'Q' },
            { 10, 'V' },
            { 12, 'Z' }
        };

        foreach (var month in ContractMonths)
        {
            monthCodes.Should().ContainKey(month);
        }
    }

    #endregion
}
