using System.Text.Json.Nodes;
using Xunit;

namespace Prisstyrning.Tests.Unit;

/// <summary>
/// Unit tests for the flexible eco scheduling algorithm in ScheduleAlgorithm.
/// </summary>
public class FlexibleEcoAlgorithmTests
{
    #region Helpers

    private static JsonArray CreatePriceArray(DateTimeOffset startDate, params decimal[] hourlyPrices)
    {
        var arr = new JsonArray();
        for (int i = 0; i < hourlyPrices.Length; i++)
        {
            var entry = new JsonObject
            {
                ["start"] = startDate.AddHours(i).ToString("o"),
                ["value"] = hourlyPrices[i].ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
            arr.Add(entry);
        }
        return arr;
    }

    #endregion

    #region GenerateFlexibleEco

    [Fact]
    public void GenerateFlexibleEco_NoPrices_ReturnsNoPrices()
    {
        // Arrange
        var lastEcoRun = new DateTimeOffset(2026, 2, 20, 10, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 2, 21, 10, 0, 0, TimeSpan.Zero);

        // Act
        var result = ScheduleAlgorithm.GenerateFlexibleEco(
            rawToday: null,
            rawTomorrow: null,
            lastEcoRun: lastEcoRun,
            intervalHours: 24,
            flexibilityHours: 12,
            nowOverride: now);

        // Assert
        Assert.Null(result.ScheduledHourUtc);
        Assert.Equal("no_prices", result.State);
    }

    [Fact]
    public void GenerateFlexibleEco_WindowNotYetOpen_ReturnsWaiting()
    {
        // Arrange: lastEcoRun at 10:00, interval=24h, flexibility=6h
        // Window opens at 10:00 + 24 - 6 = next day 04:00
        // Now is same day 12:00 → window hasn't opened
        var lastEcoRun = new DateTimeOffset(2026, 2, 21, 10, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 2, 21, 12, 0, 0, TimeSpan.Zero);

        var todayStart = new DateTimeOffset(2026, 2, 21, 0, 0, 0, TimeSpan.Zero);
        var rawToday = CreatePriceArray(todayStart,
            0.50m, 0.40m, 0.30m, 0.20m, 0.10m, 0.15m, 0.25m, 0.35m,
            0.45m, 0.55m, 0.65m, 0.75m, 0.85m, 0.95m, 1.05m, 1.15m,
            1.25m, 1.35m, 1.45m, 1.55m, 1.65m, 1.75m, 1.85m, 1.95m);

        // Act
        var result = ScheduleAlgorithm.GenerateFlexibleEco(
            rawToday: rawToday,
            rawTomorrow: null,
            lastEcoRun: lastEcoRun,
            intervalHours: 24,
            flexibilityHours: 6,
            nowOverride: now);

        // Assert
        Assert.Null(result.ScheduledHourUtc);
        Assert.Equal("waiting", result.State);
        Assert.Contains("opens", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GenerateFlexibleEco_WindowOpen_PicksCheapestHour()
    {
        // Arrange: lastEcoRun at Feb 20 10:00, interval=24h, flexibility=12h
        // Window: Feb 20 22:00 → Feb 21 22:00
        // Now is Feb 21 06:00 → inside window
        var lastEcoRun = new DateTimeOffset(2026, 2, 20, 10, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 2, 21, 6, 0, 0, TimeSpan.Zero);

        // Prices for Feb 21: hours 0-23, cheapest at hour 8 (0.05)
        var todayStart = new DateTimeOffset(2026, 2, 21, 0, 0, 0, TimeSpan.Zero);
        var rawToday = CreatePriceArray(todayStart,
            0.50m, 0.40m, 0.30m, 0.20m, 0.10m, 0.15m, 0.25m, 0.35m,
            0.05m, 0.55m, 0.65m, 0.75m, 0.85m, 0.95m, 1.05m, 1.15m,
            1.25m, 1.35m, 1.45m, 1.55m, 1.65m, 1.75m, 1.85m, 1.95m);

        // Act
        var result = ScheduleAlgorithm.GenerateFlexibleEco(
            rawToday: rawToday,
            rawTomorrow: null,
            lastEcoRun: lastEcoRun,
            intervalHours: 24,
            flexibilityHours: 12,
            nowOverride: now);

        // Assert: hour 8 is the cheapest future hour in window
        Assert.NotNull(result.ScheduledHourUtc);
        Assert.Equal("scheduled", result.State);
        Assert.Equal(8, result.ScheduledHourUtc!.Value.Hour);
        Assert.Equal(0.05m, 0.05m); // sanity
    }

    [Fact]
    public void GenerateFlexibleEco_AtDeadline_PicksCheapestAvailable()
    {
        // Arrange: lastEcoRun at Feb 20 10:00, interval=24h, flexibility=4h
        // Window: Feb 21 06:00 → Feb 21 14:00
        // Now is Feb 21 12:00 → only hours 12-13 remain in window
        var lastEcoRun = new DateTimeOffset(2026, 2, 20, 10, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 2, 21, 12, 0, 0, TimeSpan.Zero);

        // Prices for Feb 21: hours 0-23
        var todayStart = new DateTimeOffset(2026, 2, 21, 0, 0, 0, TimeSpan.Zero);
        var rawToday = CreatePriceArray(todayStart,
            0.50m, 0.40m, 0.30m, 0.20m, 0.10m, 0.15m, 0.25m, 0.35m,
            0.05m, 0.55m, 0.65m, 0.75m, 0.85m, 0.95m, 1.05m, 1.15m,
            1.25m, 1.35m, 1.45m, 1.55m, 1.65m, 1.75m, 1.85m, 1.95m);

        // Act
        var result = ScheduleAlgorithm.GenerateFlexibleEco(
            rawToday: rawToday,
            rawTomorrow: null,
            lastEcoRun: lastEcoRun,
            intervalHours: 24,
            flexibilityHours: 4,
            nowOverride: now);

        // Assert: only hours 12 (0.85) and 13 (0.95) are in window and in future
        // Hour 12 is cheaper
        Assert.NotNull(result.ScheduledHourUtc);
        Assert.Equal("scheduled", result.State);
        Assert.Equal(12, result.ScheduledHourUtc!.Value.Hour);
    }

    [Fact]
    public void GenerateFlexibleEco_OnlySomePricesInWindow_FiltersCorrectly()
    {
        // Arrange: lastEcoRun at Feb 20 10:00, interval=24h, flexibility=3h
        // Window: Feb 21 07:00 → Feb 21 13:00
        // Now is Feb 21 08:00
        // Cheapest hour overall is hour 2 (outside window), cheapest in window is hour 9
        var lastEcoRun = new DateTimeOffset(2026, 2, 20, 10, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 2, 21, 8, 0, 0, TimeSpan.Zero);

        var todayStart = new DateTimeOffset(2026, 2, 21, 0, 0, 0, TimeSpan.Zero);
        var rawToday = CreatePriceArray(todayStart,
            0.90m, 0.80m, 0.01m, 0.70m, 0.60m, 0.50m, 0.40m, 0.30m,
            0.50m, 0.10m, 0.55m, 0.65m, 0.75m, 0.85m, 0.02m, 1.15m,
            1.25m, 1.35m, 1.45m, 1.55m, 1.65m, 1.75m, 1.85m, 1.95m);

        // Act
        var result = ScheduleAlgorithm.GenerateFlexibleEco(
            rawToday: rawToday,
            rawTomorrow: null,
            lastEcoRun: lastEcoRun,
            intervalHours: 24,
            flexibilityHours: 3,
            nowOverride: now);

        // Assert: hour 2 (0.01) is outside window, hour 14 (0.02) is outside window
        // In-window future hours 8-12: 0.50, 0.10, 0.55, 0.65, 0.75
        // Cheapest in window is hour 9 (0.10)
        Assert.NotNull(result.ScheduledHourUtc);
        Assert.Equal("scheduled", result.State);
        Assert.Equal(9, result.ScheduledHourUtc!.Value.Hour);
    }

    [Fact]
    public void GenerateFlexibleEco_AllPricesInPast_ReturnsNoPrices()
    {
        // Arrange: all price hours are before 'now'
        var lastEcoRun = new DateTimeOffset(2026, 2, 20, 10, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 2, 22, 0, 0, 0, TimeSpan.Zero); // midnight next day

        // Window: Feb 20 10:00 + 24 - 12 = Feb 21 22:00 → Feb 22 10:00
        // Prices only for Feb 21 (all before now at midnight Feb 22)
        var todayStart = new DateTimeOffset(2026, 2, 21, 0, 0, 0, TimeSpan.Zero);
        var rawToday = CreatePriceArray(todayStart,
            0.50m, 0.40m, 0.30m, 0.20m, 0.10m, 0.15m, 0.25m, 0.35m,
            0.05m, 0.55m, 0.65m, 0.75m, 0.85m, 0.95m, 1.05m, 1.15m,
            1.25m, 1.35m, 1.45m, 1.55m, 1.65m, 1.75m, 1.85m, 1.95m);

        // Act
        var result = ScheduleAlgorithm.GenerateFlexibleEco(
            rawToday: rawToday,
            rawTomorrow: null,
            lastEcoRun: lastEcoRun,
            intervalHours: 24,
            flexibilityHours: 12,
            nowOverride: now);

        // Assert: all prices in the past → no prices in window
        Assert.Null(result.ScheduledHourUtc);
        Assert.Equal("no_prices", result.State);
    }

    #endregion

    #region ParseHourlyPrices

    [Fact]
    public void ParseHourlyPrices_NullInputs_ReturnsEmptyList()
    {
        var result = ScheduleAlgorithm.ParseHourlyPrices(null, null);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseHourlyPrices_SingleArray_ParsesCorrectly()
    {
        var start = new DateTimeOffset(2026, 2, 21, 0, 0, 0, TimeSpan.Zero);
        var arr = CreatePriceArray(start, 1.0m, 2.0m, 3.0m);

        var result = ScheduleAlgorithm.ParseHourlyPrices(arr, null);

        Assert.Equal(3, result.Count);
        Assert.Equal(1.0m, result[0].Price);
        Assert.Equal(2.0m, result[1].Price);
        Assert.Equal(3.0m, result[2].Price);
    }

    [Fact]
    public void ParseHourlyPrices_BothArrays_Deduplicates()
    {
        var start = new DateTimeOffset(2026, 2, 21, 22, 0, 0, TimeSpan.Zero);
        // Today has hours 22, 23
        var today = CreatePriceArray(start, 1.0m, 2.0m);
        // Tomorrow has hours 22, 23, 0, 1 (overlapping 22 and 23)
        var tomorrow = CreatePriceArray(start, 5.0m, 6.0m, 7.0m, 8.0m);

        var result = ScheduleAlgorithm.ParseHourlyPrices(today, tomorrow);

        // Hours 22, 23 should be deduplicated (tomorrow wins for dupes), plus hours 0, 1
        Assert.Equal(4, result.Count);
        // Sorted by start time
        Assert.Equal(start, result[0].Start);
        Assert.Equal(start.AddHours(3), result[3].Start);
    }

    [Fact]
    public void ParseHourlyPrices_ReturnsSortedByStartTime()
    {
        var start = new DateTimeOffset(2026, 2, 21, 0, 0, 0, TimeSpan.Zero);
        var arr = CreatePriceArray(start, 3.0m, 1.0m, 2.0m);

        var result = ScheduleAlgorithm.ParseHourlyPrices(arr, null);

        Assert.Equal(3, result.Count);
        Assert.True(result[0].Start < result[1].Start);
        Assert.True(result[1].Start < result[2].Start);
    }

    #endregion
}
