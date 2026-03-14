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
    public void GenerateFlexibleEco_WindowNotYetOpen_NoPricesInWindow_ReturnsWaiting()
    {
        // Arrange: lastEcoRun at 10:00, interval=24h, flexibility=6h
        // Window: [next day 04:00, next day 16:00]
        // Now is same day 12:00 → window hasn't opened
        // Prices only for today (hours 0-23 of Feb 21) → no prices cover the window (Feb 22 04:00-16:00)
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

        // Assert: no prices in the window → still waiting
        Assert.Null(result.ScheduledHourUtc);
        Assert.Equal("waiting", result.State);
        Assert.Contains("opens", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GenerateFlexibleEco_WindowNotYetOpen_WithPricesInWindow_ShouldPreSchedule()
    {
        // Arrange: lastEcoRun at 10:00, interval=24h, flexibility=6h
        // Window: [next day 04:00, next day 16:00]
        // Now is same day 12:00 → window hasn't opened
        // Tomorrow's prices are available (Feb 22 00:00-23:00) → prices exist in window
        var lastEcoRun = new DateTimeOffset(2026, 2, 21, 10, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 2, 21, 12, 0, 0, TimeSpan.Zero);

        var todayStart = new DateTimeOffset(2026, 2, 21, 0, 0, 0, TimeSpan.Zero);
        var rawToday = CreatePriceArray(todayStart,
            0.50m, 0.40m, 0.30m, 0.20m, 0.10m, 0.15m, 0.25m, 0.35m,
            0.45m, 0.55m, 0.65m, 0.75m, 0.85m, 0.95m, 1.05m, 1.15m,
            1.25m, 1.35m, 1.45m, 1.55m, 1.65m, 1.75m, 1.85m, 1.95m);

        // Tomorrow Feb 22: cheapest hour is 05:00 (0.03), which is inside window [04:00, 16:00]
        var tomorrowStart = new DateTimeOffset(2026, 2, 22, 0, 0, 0, TimeSpan.Zero);
        var rawTomorrow = CreatePriceArray(tomorrowStart,
            0.80m, 0.70m, 0.60m, 0.50m, 0.40m, 0.03m, 0.25m, 0.35m,
            0.45m, 0.55m, 0.65m, 0.75m, 0.85m, 0.95m, 1.05m, 1.15m,
            1.25m, 1.35m, 1.45m, 1.55m, 1.65m, 1.75m, 1.85m, 1.95m);

        // Act
        var result = ScheduleAlgorithm.GenerateFlexibleEco(
            rawToday: rawToday,
            rawTomorrow: rawTomorrow,
            lastEcoRun: lastEcoRun,
            intervalHours: 24,
            flexibilityHours: 6,
            nowOverride: now);

        // Assert: pre-scheduled at cheapest hour in window (05:00 on Feb 22)
        Assert.NotNull(result.ScheduledHourUtc);
        Assert.Equal("scheduled", result.State);
        Assert.Equal(5, result.ScheduledHourUtc!.Value.Hour);
        Assert.Contains("pre-schedul", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GenerateFlexibleEco_FirstRun_ShouldScheduleEco()
    {
        // Arrange: Simulate first run where lastEcoRun is backdated by intervalHours
        // This is what BatchRunner does: lastEcoRun = now.AddHours(-intervalHours)
        var intervalHours = 17;
        var flexibilityHours = 3;
        var now = new DateTimeOffset(2026, 2, 21, 21, 56, 0, TimeSpan.Zero);
        var lastEcoRun = now.AddHours(-intervalHours); // Feb 21 04:56 UTC

        // Window: lastEcoRun + (17-3) = 04:56 + 14h = 18:56
        //         lastEcoRun + (17+3) = 04:56 + 20h = 00:56 next day
        // now (21:56) is inside window [18:56, 00:56+1d]

        // Prices for today (Feb 21)
        var todayStart = new DateTimeOffset(2026, 2, 21, 0, 0, 0, TimeSpan.Zero);
        var rawToday = CreatePriceArray(todayStart,
            0.50m, 0.40m, 0.30m, 0.20m, 0.10m, 0.15m, 0.25m, 0.35m,
            0.45m, 0.55m, 0.65m, 0.75m, 0.85m, 0.95m, 1.05m, 1.15m,
            1.25m, 1.35m, 1.45m, 1.55m, 1.65m, 1.75m, 0.08m, 0.12m);
        // Hour 22 = 0.08 (cheapest in future), Hour 23 = 0.12

        // Tomorrow Feb 22
        var tomorrowStart = new DateTimeOffset(2026, 2, 22, 0, 0, 0, TimeSpan.Zero);
        var rawTomorrow = CreatePriceArray(tomorrowStart,
            0.09m, 0.70m, 0.60m, 0.50m, 0.40m, 0.30m, 0.25m, 0.35m,
            0.45m, 0.55m, 0.65m, 0.75m, 0.85m, 0.95m, 1.05m, 1.15m,
            1.25m, 1.35m, 1.45m, 1.55m, 1.65m, 1.75m, 1.85m, 1.95m);
        // Hour 0 tomorrow = 0.09, inside window [18:56, 00:56]

        // Act
        var result = ScheduleAlgorithm.GenerateFlexibleEco(
            rawToday: rawToday,
            rawTomorrow: rawTomorrow,
            lastEcoRun: lastEcoRun,
            intervalHours: intervalHours,
            flexibilityHours: flexibilityHours,
            nowOverride: now);

        // Assert: eco IS scheduled (window is open), cheapest future hour in window is 22:00 (0.08)
        Assert.NotNull(result.ScheduledHourUtc);
        Assert.Equal("scheduled", result.State);
        Assert.Equal(22, result.ScheduledHourUtc!.Value.Hour);
    }

    [Fact]
    public void GenerateFlexibleEco_17hInterval_3hFlex_AtNight_ShouldSchedule()
    {
        // Arrange: Very specific scenario from production
        // Eco ran at 04:00 today, interval=17h, flexibility=3h
        // Window: 04:00 + 14h = 18:00 → 04:00 + 20h = 00:00 next day
        // Now is 21:56 → inside window
        var lastEcoRun = new DateTimeOffset(2026, 2, 21, 4, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 2, 21, 21, 56, 0, TimeSpan.Zero);

        var todayStart = new DateTimeOffset(2026, 2, 21, 0, 0, 0, TimeSpan.Zero);
        var rawToday = CreatePriceArray(todayStart,
            0.50m, 0.40m, 0.30m, 0.20m, 0.10m, 0.15m, 0.25m, 0.35m,
            0.45m, 0.55m, 0.65m, 0.75m, 0.85m, 0.95m, 1.05m, 1.15m,
            1.25m, 1.35m, 1.45m, 1.55m, 1.65m, 1.75m, 0.05m, 0.12m);

        var tomorrowStart = new DateTimeOffset(2026, 2, 22, 0, 0, 0, TimeSpan.Zero);
        var rawTomorrow = CreatePriceArray(tomorrowStart,
            0.80m, 0.70m, 0.60m, 0.50m, 0.40m, 0.30m, 0.25m, 0.35m,
            0.45m, 0.55m, 0.65m, 0.75m, 0.85m, 0.95m, 1.05m, 1.15m,
            1.25m, 1.35m, 1.45m, 1.55m, 1.65m, 1.75m, 1.85m, 1.95m);

        // Act
        var result = ScheduleAlgorithm.GenerateFlexibleEco(
            rawToday: rawToday,
            rawTomorrow: rawTomorrow,
            lastEcoRun: lastEcoRun,
            intervalHours: 17,
            flexibilityHours: 3,
            nowOverride: now);

        // Assert: eco scheduled at hour 22 (0.05, cheapest in window)
        Assert.NotNull(result.ScheduledHourUtc);
        Assert.Equal("scheduled", result.State);
        Assert.Equal(22, result.ScheduledHourUtc!.Value.Hour);
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
    public void GenerateFlexibleEco_AllPricesInPast_ReturnsExpired()
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

        // Assert: window expired, all prices in the past → expired with no scheduled hour
        Assert.Null(result.ScheduledHourUtc);
        Assert.Equal("expired", result.State);
    }

    [Fact]
    public void GenerateFlexibleEco_FirstRunDefaultNow_WouldWait_ButBackdatedDefault_Schedules()
    {
        // Bug scenario: on first run, LastEcoRunUtc is null.
        // If defaulted to 'now', with interval=19h and flexibility=4h,
        // window opens at now + 15h — always in the future → perpetual "waiting".
        // Fix: backdate to now - intervalHours so window is already open.
        var now = new DateTimeOffset(2026, 3, 8, 10, 0, 0, TimeSpan.Zero);
        int intervalHours = 19;
        int flexibilityHours = 4;

        var todayStart = new DateTimeOffset(2026, 3, 8, 0, 0, 0, TimeSpan.Zero);
        var rawToday = CreatePriceArray(todayStart,
            0.50m, 0.40m, 0.30m, 0.20m, 0.10m, 0.15m, 0.25m, 0.35m,
            0.45m, 0.55m, 0.65m, 0.75m, 0.85m, 0.95m, 1.05m, 1.15m,
            1.25m, 1.35m, 1.45m, 1.55m, 1.65m, 1.75m, 1.85m, 1.95m);

        // Using lastEcoRun = now → window opens at now + 15h → waiting
        var resultBad = ScheduleAlgorithm.GenerateFlexibleEco(
            rawToday: rawToday,
            rawTomorrow: null,
            lastEcoRun: now,
            intervalHours: intervalHours,
            flexibilityHours: flexibilityHours,
            nowOverride: now);

        Assert.Equal("waiting", resultBad.State);

        // Using lastEcoRun = now - intervalHours → window is fully open → scheduled
        var backdated = now.AddHours(-intervalHours);
        var resultGood = ScheduleAlgorithm.GenerateFlexibleEco(
            rawToday: rawToday,
            rawTomorrow: null,
            lastEcoRun: backdated,
            intervalHours: intervalHours,
            flexibilityHours: flexibilityHours,
            nowOverride: now);

        Assert.Equal("scheduled", resultGood.State);
        Assert.NotNull(resultGood.ScheduledHourUtc);
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

    #region Expired Window

    [Fact]
    public void ExpiredWindow_WithFuturePrices_ForcesSchedule()
    {
        // Arrange: lastEcoRun far in the past so window has completely passed
        // lastEcoRun = Feb 19 10:00, interval=24h, flex=4h
        // windowStart = Feb 20 06:00, windowEnd = Feb 20 14:00
        // now = Feb 21 10:00 → well past windowEnd
        var lastEcoRun = new DateTimeOffset(2026, 2, 19, 10, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 2, 21, 10, 0, 0, TimeSpan.Zero);

        // Prices for today (Feb 21): cheapest future hour is 14:00 (0.03)
        var todayStart = new DateTimeOffset(2026, 2, 21, 0, 0, 0, TimeSpan.Zero);
        var rawToday = CreatePriceArray(todayStart,
            0.50m, 0.40m, 0.30m, 0.20m, 0.10m, 0.15m, 0.25m, 0.35m,
            0.45m, 0.55m, 0.65m, 0.75m, 0.85m, 0.95m, 0.03m, 1.15m,
            1.25m, 1.35m, 1.45m, 1.55m, 1.65m, 1.75m, 1.85m, 1.95m);

        // Act
        var result = ScheduleAlgorithm.GenerateFlexibleEco(
            rawToday: rawToday,
            rawTomorrow: null,
            lastEcoRun: lastEcoRun,
            intervalHours: 24,
            flexibilityHours: 4,
            nowOverride: now);

        // Assert: expired state, forced cheapest future hour (14:00)
        Assert.NotNull(result.ScheduledHourUtc);
        Assert.Equal("expired", result.State);
        Assert.Equal(14, result.ScheduledHourUtc!.Value.Hour);
        Assert.Contains("expired", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExpiredWindow_WithoutPrices_ReturnsExpired()
    {
        // Arrange: lastEcoRun far in the past, no price data at all
        var lastEcoRun = new DateTimeOffset(2026, 2, 19, 10, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 2, 21, 10, 0, 0, TimeSpan.Zero);

        // Act
        var result = ScheduleAlgorithm.GenerateFlexibleEco(
            rawToday: null,
            rawTomorrow: null,
            lastEcoRun: lastEcoRun,
            intervalHours: 24,
            flexibilityHours: 4,
            nowOverride: now);

        // Assert: expired with no scheduled hour
        Assert.Null(result.ScheduledHourUtc);
        Assert.Equal("expired", result.State);
        Assert.Contains("expired", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region NextScheduledEcoUtc — pending eco handling

    [Fact]
    public void GenerateFlexibleEco_PendingEcoInFuture_ReturnsAlreadyScheduled()
    {
        // Arrange: eco was scheduled for 14:00, batch runs again at 13:30.
        // The eco window is already in the past (expired), so re-optimization finds no candidates.
        // Expected: "already_scheduled" — pending eco preserved.
        // lastEcoRun = Feb 19 10:00, interval=24h, flex=4h
        // → windowStart = Feb 20 06:00, windowEnd = Feb 20 14:00 (both in the past)
        var lastEcoRun = new DateTimeOffset(2026, 2, 19, 10, 0, 0, TimeSpan.Zero);
        var nextScheduledEco = new DateTimeOffset(2026, 2, 21, 14, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 2, 21, 13, 30, 0, TimeSpan.Zero); // 30 min before eco

        // Today prices: 14:00 is affordable, nothing cheaper in the window (window expired)
        var todayStart = new DateTimeOffset(2026, 2, 21, 0, 0, 0, TimeSpan.Zero);
        var rawToday = CreatePriceArray(todayStart,
            0.50m, 0.40m, 0.30m, 0.20m, 0.10m, 0.15m, 0.25m, 0.35m,
            0.45m, 0.55m, 0.65m, 0.75m, 0.85m, 0.95m, 0.20m, 1.15m,
            1.25m, 1.35m, 1.45m, 1.55m, 1.65m, 1.75m, 1.85m, 1.95m);

        // Act
        var result = ScheduleAlgorithm.GenerateFlexibleEco(
            rawToday: rawToday,
            rawTomorrow: null,
            lastEcoRun: lastEcoRun,
            intervalHours: 24,
            flexibilityHours: 4,
            nextScheduledEcoUtc: nextScheduledEco,
            nowOverride: now);

        // Assert: already scheduled at 14:00, still cheapest
        Assert.Equal("already_scheduled", result.State);
        Assert.Equal(nextScheduledEco, result.ScheduledHourUtc);
        Assert.Contains("already scheduled", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GenerateFlexibleEco_PendingEcoInFuture_CheaperHourExists_ReturnsRescheduled()
    {
        // Arrange: eco window is OPEN (window includes hours around now and 14:00).
        // nextScheduledEco = 14:00, but 11:00 is cheaper and still in the window.
        // lastEcoRun = Feb 20 10:00, interval=24h, flex=8h
        // → windowStart = Feb 21 02:00, windowEnd = Feb 21 18:00
        // now = Feb 21 10:00 (inside window), nextScheduledEco = Feb 21 14:00 (also inside window)
        var lastEcoRun = new DateTimeOffset(2026, 2, 20, 10, 0, 0, TimeSpan.Zero);
        var nextScheduledEco = new DateTimeOffset(2026, 2, 21, 14, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 2, 21, 10, 0, 0, TimeSpan.Zero);

        // Feb 21 prices: 11:00 is very cheap (0.05), 14:00 is 0.20, both in window [10:00, 18:00)
        var todayStart = new DateTimeOffset(2026, 2, 21, 0, 0, 0, TimeSpan.Zero);
        var rawToday = CreatePriceArray(todayStart,
            0.50m, 0.40m, 0.30m, 0.20m, 0.10m, 0.15m, 0.25m, 0.35m,
            0.45m, 0.55m, 0.65m, 0.05m, 0.85m, 0.95m, 0.20m, 1.15m,
            1.25m, 1.35m, 1.45m, 1.55m, 1.65m, 1.75m, 1.85m, 1.95m);
        // Index 11 = 11:00 (0.05), Index 14 = 14:00 (0.20)

        // Act
        var result = ScheduleAlgorithm.GenerateFlexibleEco(
            rawToday: rawToday,
            rawTomorrow: null,
            lastEcoRun: lastEcoRun,
            intervalHours: 24,
            flexibilityHours: 8,
            nextScheduledEcoUtc: nextScheduledEco,
            nowOverride: now);

        // Assert: rescheduled to cheaper hour (11:00)
        Assert.Equal("rescheduled", result.State);
        Assert.NotNull(result.ScheduledHourUtc);
        Assert.Equal(11, result.ScheduledHourUtc!.Value.Hour);
        Assert.Contains("rescheduled", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GenerateFlexibleEco_PendingEcoInPast_ReturnsAlreadyRan()
    {
        // Arrange: eco was scheduled for 14:00, batch runs at 14:30 (eco has run)
        var lastEcoRun = new DateTimeOffset(2026, 2, 19, 10, 0, 0, TimeSpan.Zero);
        var nextScheduledEco = new DateTimeOffset(2026, 2, 21, 14, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 2, 21, 14, 30, 0, TimeSpan.Zero); // 30 min after eco

        var todayStart = new DateTimeOffset(2026, 2, 21, 0, 0, 0, TimeSpan.Zero);
        var rawToday = CreatePriceArray(todayStart,
            0.50m, 0.40m, 0.30m, 0.20m, 0.10m, 0.15m, 0.25m, 0.35m,
            0.45m, 0.55m, 0.65m, 0.75m, 0.85m, 0.95m, 0.20m, 1.15m,
            1.25m, 1.35m, 1.45m, 1.55m, 1.65m, 1.75m, 1.85m, 1.95m);

        // Act
        var result = ScheduleAlgorithm.GenerateFlexibleEco(
            rawToday: rawToday,
            rawTomorrow: null,
            lastEcoRun: lastEcoRun,
            intervalHours: 24,
            flexibilityHours: 4,
            nextScheduledEcoUtc: nextScheduledEco,
            nowOverride: now);

        // Assert: already_ran — eco time has passed
        Assert.Equal("already_ran", result.State);
        Assert.Equal(nextScheduledEco, result.ScheduledHourUtc);
        Assert.Contains("already ran", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GenerateFlexibleEco_PendingEcoExactlyNow_ReturnsAlreadyRan()
    {
        // Edge case: nextScheduledEcoUtc == now (boundary: <= now → already_ran)
        var lastEcoRun = new DateTimeOffset(2026, 2, 19, 10, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 2, 21, 14, 0, 0, TimeSpan.Zero);
        var nextScheduledEco = now; // exactly equal

        var todayStart = new DateTimeOffset(2026, 2, 21, 0, 0, 0, TimeSpan.Zero);
        var rawToday = CreatePriceArray(todayStart,
            0.50m, 0.40m, 0.30m, 0.20m, 0.10m, 0.15m, 0.25m, 0.35m,
            0.45m, 0.55m, 0.65m, 0.75m, 0.85m, 0.95m, 0.20m, 1.15m,
            1.25m, 1.35m, 1.45m, 1.55m, 1.65m, 1.75m, 1.85m, 1.95m);

        var result = ScheduleAlgorithm.GenerateFlexibleEco(
            rawToday: rawToday,
            rawTomorrow: null,
            lastEcoRun: lastEcoRun,
            intervalHours: 24,
            flexibilityHours: 4,
            nextScheduledEcoUtc: nextScheduledEco,
            nowOverride: now);

        Assert.Equal("already_ran", result.State);
        Assert.Equal(nextScheduledEco, result.ScheduledHourUtc);
    }

    [Fact]
    public void GenerateFlexibleEco_PendingEcoInFuture_NoCandidatesInWindow_KeepsPending()
    {
        // Arrange: eco scheduled for tomorrow at 10:00, batch runs today, no prices in next window
        // This tests that the algorithm keeps the pending eco when no cheaper candidates exist
        var lastEcoRun = new DateTimeOffset(2026, 2, 19, 10, 0, 0, TimeSpan.Zero);
        var nextScheduledEco = new DateTimeOffset(2026, 2, 21, 14, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 2, 21, 10, 0, 0, TimeSpan.Zero);

        // No prices available
        var result = ScheduleAlgorithm.GenerateFlexibleEco(
            rawToday: null,
            rawTomorrow: null,
            lastEcoRun: lastEcoRun,
            intervalHours: 24,
            flexibilityHours: 4,
            nextScheduledEcoUtc: nextScheduledEco,
            nowOverride: now);

        // When no prices are available for re-optimization, keep the current pending eco
        Assert.Equal("already_scheduled", result.State);
        Assert.Equal(nextScheduledEco, result.ScheduledHourUtc);
    }

    [Fact]
    public void GenerateFlexibleEco_NoPendingEco_NormalScheduling()
    {
        // Arrange: no pending eco, window is open, should schedule normally
        var lastEcoRun = new DateTimeOffset(2026, 2, 20, 10, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 2, 21, 10, 0, 0, TimeSpan.Zero);
        // Window: [20+16=Feb 22 02:00, 20+28=Feb 22 14:00] — wait, interval=24h, flex=4h
        // Window: [Feb 20+20h=Feb 21 06:00, Feb 20+28h=Feb 21 14:00]
        // now=10:00 is inside window [06:00, 14:00]

        var todayStart = new DateTimeOffset(2026, 2, 21, 0, 0, 0, TimeSpan.Zero);
        var rawToday = CreatePriceArray(todayStart,
            0.50m, 0.40m, 0.30m, 0.20m, 0.10m, 0.15m, 0.25m, 0.35m,
            0.45m, 0.55m, 0.65m, 0.75m, 0.85m, 0.95m, 0.03m, 1.15m,
            1.25m, 1.35m, 1.45m, 1.55m, 1.65m, 1.75m, 1.85m, 1.95m);
        // Cheapest future hour inside window [10:00, 14:00): index 10=10:00 (0.65), 11=11:00 (0.75),
        // 12=12:00 (0.85), 13=13:00 (0.95) → cheapest = 10:00 (0.65)

        var result = ScheduleAlgorithm.GenerateFlexibleEco(
            rawToday: rawToday,
            rawTomorrow: null,
            lastEcoRun: lastEcoRun,
            intervalHours: 24,
            flexibilityHours: 4,
            nextScheduledEcoUtc: null,
            nowOverride: now);

        // Assert: normal scheduling, cheapest future hour in window
        Assert.Equal("scheduled", result.State);
        Assert.NotNull(result.ScheduledHourUtc);
        Assert.Equal(10, result.ScheduledHourUtc!.Value.Hour); // 10:00 is cheapest in [10:00, 14:00)
    }

    #endregion
}
