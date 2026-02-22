using System.Text.Json.Nodes;
using Xunit;

namespace Prisstyrning.Tests.Unit;

/// <summary>
/// Unit tests for the flexible comfort scheduling algorithm in ScheduleAlgorithm.
/// </summary>
public class FlexibleComfortAlgorithmTests
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

    #region GenerateFlexibleComfort

    [Fact]
    public void GenerateFlexibleComfort_WindowNotYetOpen_ReturnsWaiting()
    {
        // Arrange: lastComfortRun at Feb 20 10:00, interval=7 days, flexibility=2 days
        // windowStart = Feb 20 10:00 + (7-2) days = Feb 25 10:00
        // windowEnd   = Feb 20 10:00 + (7+2) days = Feb 29 10:00  (2026 is not a leap year → Mar 1)
        // Now is Feb 22 12:00 → window hasn't opened yet
        var lastComfortRun = new DateTimeOffset(2026, 2, 20, 10, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 2, 22, 12, 0, 0, TimeSpan.Zero);

        var todayStart = new DateTimeOffset(2026, 2, 22, 0, 0, 0, TimeSpan.Zero);
        var rawToday = CreatePriceArray(todayStart,
            0.50m, 0.40m, 0.30m, 0.20m, 0.10m, 0.15m, 0.25m, 0.35m,
            0.45m, 0.55m, 0.65m, 0.75m, 0.85m, 0.95m, 1.05m, 1.15m,
            1.25m, 1.35m, 1.45m, 1.55m, 1.65m, 1.75m, 1.85m, 1.95m);

        // Act
        var result = ScheduleAlgorithm.GenerateFlexibleComfort(
            rawToday: rawToday,
            rawTomorrow: null,
            lastComfortRun: lastComfortRun,
            intervalDays: 7,
            flexibilityDays: 2,
            historicalBaseThreshold: 0.50m,
            historicalMaxPrice: 2.00m,
            nowOverride: now);

        // Assert
        Assert.Null(result.ScheduledHourUtc);
        Assert.Equal("waiting", result.State);
        Assert.Equal(0.0, result.WindowProgress);
        Assert.Contains("opens", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GenerateFlexibleComfort_NoPrices_ReturnsNoPrices()
    {
        // Arrange: window is open but no price data
        var lastComfortRun = new DateTimeOffset(2026, 2, 15, 10, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 2, 22, 12, 0, 0, TimeSpan.Zero);
        // windowStart = Feb 15 + 5 days = Feb 20 10:00 (interval=7, flex=2)
        // windowEnd   = Feb 15 + 9 days = Feb 24 10:00
        // Now Feb 22 is inside the window

        // Act
        var result = ScheduleAlgorithm.GenerateFlexibleComfort(
            rawToday: null,
            rawTomorrow: null,
            lastComfortRun: lastComfortRun,
            intervalDays: 7,
            flexibilityDays: 2,
            historicalBaseThreshold: 0.50m,
            historicalMaxPrice: 2.00m,
            nowOverride: now);

        // Assert
        Assert.Null(result.ScheduledHourUtc);
        Assert.Equal("no_prices", result.State);
    }

    [Fact]
    public void GenerateFlexibleComfort_EarlyInWindow_CheapPrice_Schedules()
    {
        // Arrange: lastComfortRun at Feb 15 00:00, interval=7, flexibility=2
        // windowStart = Feb 15 + 5 days = Feb 20 00:00
        // windowEnd   = Feb 15 + 9 days = Feb 24 00:00
        // Window span = 4 days = 96 hours
        // Now = Feb 20 10:00 → progress = 10h / 96h ≈ 0.104
        // baseThreshold=0.50, maxPrice=2.00
        // effectiveThreshold at 0.104 ≈ 0.50 + (2.00-0.50)*0.104 = 0.50 + 0.156 = 0.656
        // Provide a price of 0.10 which is well below threshold → should schedule
        var lastComfortRun = new DateTimeOffset(2026, 2, 15, 0, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 2, 20, 10, 0, 0, TimeSpan.Zero);

        var todayStart = new DateTimeOffset(2026, 2, 20, 0, 0, 0, TimeSpan.Zero);
        var rawToday = CreatePriceArray(todayStart,
            0.80m, 0.70m, 0.60m, 0.50m, 0.40m, 0.30m, 0.20m, 0.15m,
            0.10m, 0.12m, 0.25m, 0.35m, 0.50m, 0.65m, 0.80m, 0.95m,
            1.10m, 1.25m, 1.40m, 1.55m, 1.70m, 1.85m, 2.00m, 2.10m);

        // Act
        var result = ScheduleAlgorithm.GenerateFlexibleComfort(
            rawToday: rawToday,
            rawTomorrow: null,
            lastComfortRun: lastComfortRun,
            intervalDays: 7,
            flexibilityDays: 2,
            historicalBaseThreshold: 0.50m,
            historicalMaxPrice: 2.00m,
            nowOverride: now);

        // Assert: cheapest future hour is hour 10 with 0.10 at index 8 but hour 8 is in the past
        // at now=10:00, future hours are 10,11,... cheapest is hour 10 (0.25m)? No wait...
        // Future means Start >= now. now = Feb 20 10:00
        // Hour 10 starts at 10:00 → >= now → included. Price = 0.25m
        // Actually hour 8 starts at 08:00 which is < 10:00, so it's excluded.
        // Cheapest future hour: hour 10 = 0.25m? No wait, let me recount:
        // Index 0 = 00:00 (0.80), 1 = 01:00 (0.70), ... 8 = 08:00 (0.10), 9 = 09:00 (0.12)
        // Future from 10:00: index 10 = 10:00 (0.25), 11 = 11:00 (0.35), ...
        // Cheapest future = 0.25 at hour 10. Threshold ≈ 0.656.  0.25 < 0.656 → scheduled
        Assert.NotNull(result.ScheduledHourUtc);
        Assert.Equal("scheduled", result.State);
        Assert.Equal(10, result.ScheduledHourUtc!.Value.Hour);
        Assert.True(result.WindowProgress > 0.0 && result.WindowProgress < 0.2);
    }

    [Fact]
    public void GenerateFlexibleComfort_EarlyInWindow_ExpensivePrice_Waits()
    {
        // Arrange: same window as above but all future prices are very expensive (above threshold)
        // windowStart = Feb 20 00:00, windowEnd = Feb 24 00:00
        // Now = Feb 20 10:00 → progress ≈ 0.104
        // effectiveThreshold ≈ 0.656
        // All future prices > 0.656 → waiting_for_cheaper
        var lastComfortRun = new DateTimeOffset(2026, 2, 15, 0, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 2, 20, 10, 0, 0, TimeSpan.Zero);

        var todayStart = new DateTimeOffset(2026, 2, 20, 0, 0, 0, TimeSpan.Zero);
        var rawToday = CreatePriceArray(todayStart,
            2.50m, 2.40m, 2.30m, 2.20m, 2.10m, 2.05m, 2.00m, 1.95m,
            1.90m, 1.85m, 1.80m, 1.75m, 1.70m, 1.65m, 1.60m, 1.55m,
            1.50m, 1.45m, 1.40m, 1.35m, 1.30m, 1.25m, 1.20m, 1.15m);

        // Act
        var result = ScheduleAlgorithm.GenerateFlexibleComfort(
            rawToday: rawToday,
            rawTomorrow: null,
            lastComfortRun: lastComfortRun,
            intervalDays: 7,
            flexibilityDays: 2,
            historicalBaseThreshold: 0.50m,
            historicalMaxPrice: 2.00m,
            nowOverride: now);

        // Assert: cheapest future price is 1.15 at hour 23, threshold ≈ 0.656 → 1.15 > 0.656
        Assert.Null(result.ScheduledHourUtc);
        Assert.Equal("waiting_for_cheaper", result.State);
        Assert.True(result.WindowProgress > 0.0 && result.WindowProgress < 0.2);
    }

    [Fact]
    public void GenerateFlexibleComfort_MidWindow_ModeratePrice_Schedules()
    {
        // Arrange: lastComfortRun at Feb 15 00:00, interval=7, flexibility=2
        // windowStart = Feb 20 00:00, windowEnd = Feb 24 00:00 (96h window)
        // Now = Feb 22 00:00 → progress = 48/96 = 0.5
        // effectiveThreshold at 0.5 = 0.50 + (2.00-0.50)*0.5 = 0.50 + 0.75 = 1.25
        // Provide a moderate price (1.00) which is below 1.25 → should schedule
        var lastComfortRun = new DateTimeOffset(2026, 2, 15, 0, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 2, 22, 0, 0, 0, TimeSpan.Zero);

        var todayStart = new DateTimeOffset(2026, 2, 22, 0, 0, 0, TimeSpan.Zero);
        var rawToday = CreatePriceArray(todayStart,
            1.50m, 1.45m, 1.40m, 1.35m, 1.30m, 1.25m, 1.20m, 1.15m,
            1.10m, 1.05m, 1.00m, 1.05m, 1.10m, 1.15m, 1.20m, 1.25m,
            1.30m, 1.35m, 1.40m, 1.45m, 1.50m, 1.55m, 1.60m, 1.65m);

        // Act
        var result = ScheduleAlgorithm.GenerateFlexibleComfort(
            rawToday: rawToday,
            rawTomorrow: null,
            lastComfortRun: lastComfortRun,
            intervalDays: 7,
            flexibilityDays: 2,
            historicalBaseThreshold: 0.50m,
            historicalMaxPrice: 2.00m,
            nowOverride: now);

        // Assert: cheapest future = 1.00 at hour 10, threshold = 1.25 → 1.00 ≤ 1.25 → scheduled
        Assert.NotNull(result.ScheduledHourUtc);
        Assert.Equal("scheduled", result.State);
        Assert.Equal(10, result.ScheduledHourUtc!.Value.Hour);
        Assert.InRange(result.WindowProgress, 0.45, 0.55);
    }

    [Fact]
    public void GenerateFlexibleComfort_NearDeadline_ForcesSchedule()
    {
        // Arrange: lastComfortRun at Feb 15 00:00, interval=7, flexibility=2
        // windowStart = Feb 20 00:00, windowEnd = Feb 24 00:00 (96h)
        // Now = Feb 23 20:00 → progress = 92/96 ≈ 0.9583 (≥ 0.95)
        // Should force cheapest regardless of threshold
        var lastComfortRun = new DateTimeOffset(2026, 2, 15, 0, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 2, 23, 20, 0, 0, TimeSpan.Zero);

        var todayStart = new DateTimeOffset(2026, 2, 23, 0, 0, 0, TimeSpan.Zero);
        var rawToday = CreatePriceArray(todayStart,
            5.00m, 5.10m, 5.20m, 5.30m, 5.40m, 5.50m, 5.60m, 5.70m,
            5.80m, 5.90m, 6.00m, 6.10m, 6.20m, 6.30m, 6.40m, 6.50m,
            6.60m, 6.70m, 6.80m, 6.90m, 7.00m, 4.50m, 4.80m, 4.90m);

        // Act
        var result = ScheduleAlgorithm.GenerateFlexibleComfort(
            rawToday: rawToday,
            rawTomorrow: null,
            lastComfortRun: lastComfortRun,
            intervalDays: 7,
            flexibilityDays: 2,
            historicalBaseThreshold: 0.50m,
            historicalMaxPrice: 2.00m,
            nowOverride: now);

        // Assert: Future hours from 20:00 are 20(7.00), 21(4.50), 22(4.80), 23(4.90)
        // Cheapest = 4.50 at hour 21, forced because near deadline
        Assert.NotNull(result.ScheduledHourUtc);
        Assert.Equal("scheduled", result.State);
        Assert.Equal(21, result.ScheduledHourUtc!.Value.Hour);
        Assert.True(result.WindowProgress >= 0.95);
    }

    [Fact]
    public void GenerateFlexibleComfort_NoHistoricalData_ForcesAtDeadline()
    {
        // Arrange: null thresholds, near deadline → should force schedule
        var lastComfortRun = new DateTimeOffset(2026, 2, 15, 0, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 2, 23, 22, 0, 0, TimeSpan.Zero);
        // windowStart = Feb 20, windowEnd = Feb 24, progress = 94/96 ≈ 0.979

        var todayStart = new DateTimeOffset(2026, 2, 23, 0, 0, 0, TimeSpan.Zero);
        var rawToday = CreatePriceArray(todayStart,
            3.00m, 3.10m, 3.20m, 3.30m, 3.40m, 3.50m, 3.60m, 3.70m,
            3.80m, 3.90m, 4.00m, 4.10m, 4.20m, 4.30m, 4.40m, 4.50m,
            4.60m, 4.70m, 4.80m, 4.90m, 5.00m, 5.10m, 2.50m, 2.60m);

        // Act
        var result = ScheduleAlgorithm.GenerateFlexibleComfort(
            rawToday: rawToday,
            rawTomorrow: null,
            lastComfortRun: lastComfortRun,
            intervalDays: 7,
            flexibilityDays: 2,
            historicalBaseThreshold: null,
            historicalMaxPrice: null,
            nowOverride: now);

        // Assert: future hours from 22:00: hour 22(2.50), 23(2.60). Cheapest = 2.50 at hour 22
        Assert.NotNull(result.ScheduledHourUtc);
        Assert.Equal("scheduled", result.State);
        Assert.Equal(22, result.ScheduledHourUtc!.Value.Hour);
        Assert.Null(result.EffectiveThreshold); // no historical data
    }

    [Fact]
    public void GenerateFlexibleComfort_AlreadyScheduledFuture_CheaperFound_Reschedules()
    {
        // Arrange: comfort scheduled at hour 18 (expensive), but cheaper hour found at hour 14
        var lastComfortRun = new DateTimeOffset(2026, 2, 15, 0, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 2, 22, 10, 0, 0, TimeSpan.Zero);
        // windowStart = Feb 20, windowEnd = Feb 24

        var todayStart = new DateTimeOffset(2026, 2, 22, 0, 0, 0, TimeSpan.Zero);
        var rawToday = CreatePriceArray(todayStart,
            1.50m, 1.40m, 1.30m, 1.20m, 1.10m, 1.00m, 0.90m, 0.80m,
            0.70m, 0.60m, 0.50m, 0.45m, 0.40m, 0.35m, 0.30m, 0.35m,
            0.40m, 0.50m, 1.80m, 1.90m, 2.00m, 2.10m, 2.20m, 2.30m);

        var scheduledAt = new DateTimeOffset(2026, 2, 22, 18, 0, 0, TimeSpan.Zero); // price 1.80

        // Act
        var result = ScheduleAlgorithm.GenerateFlexibleComfort(
            rawToday: rawToday,
            rawTomorrow: null,
            lastComfortRun: lastComfortRun,
            intervalDays: 7,
            flexibilityDays: 2,
            historicalBaseThreshold: 0.50m,
            historicalMaxPrice: 2.00m,
            nextScheduledComfortUtc: scheduledAt,
            nowOverride: now);

        // Assert: cheapest future = 0.30 at hour 14. 0.30 < 1.80 → rescheduled
        Assert.NotNull(result.ScheduledHourUtc);
        Assert.Equal("rescheduled", result.State);
        Assert.Equal(14, result.ScheduledHourUtc!.Value.Hour);
    }

    [Fact]
    public void GenerateFlexibleComfort_AlreadyScheduledFuture_NoCheaper_KeepsExisting()
    {
        // Arrange: comfort scheduled at hour 14 (cheapest), no cheaper hour available
        var lastComfortRun = new DateTimeOffset(2026, 2, 15, 0, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 2, 22, 10, 0, 0, TimeSpan.Zero);
        // windowStart = Feb 20, windowEnd = Feb 24

        var todayStart = new DateTimeOffset(2026, 2, 22, 0, 0, 0, TimeSpan.Zero);
        var rawToday = CreatePriceArray(todayStart,
            1.50m, 1.40m, 1.30m, 1.20m, 1.10m, 1.00m, 0.90m, 0.80m,
            0.70m, 0.60m, 0.50m, 0.45m, 0.40m, 0.35m, 0.30m, 0.35m,
            0.40m, 0.50m, 0.60m, 0.70m, 0.80m, 0.90m, 1.00m, 1.10m);

        var scheduledAt = new DateTimeOffset(2026, 2, 22, 14, 0, 0, TimeSpan.Zero); // price 0.30 (cheapest)

        // Act
        var result = ScheduleAlgorithm.GenerateFlexibleComfort(
            rawToday: rawToday,
            rawTomorrow: null,
            lastComfortRun: lastComfortRun,
            intervalDays: 7,
            flexibilityDays: 2,
            historicalBaseThreshold: 0.50m,
            historicalMaxPrice: 2.00m,
            nextScheduledComfortUtc: scheduledAt,
            nowOverride: now);

        // Assert: cheapest future = 0.30 at hour 14, same as scheduled → already_scheduled
        Assert.NotNull(result.ScheduledHourUtc);
        Assert.Equal("already_scheduled", result.State);
        Assert.Equal(14, result.ScheduledHourUtc!.Value.Hour);
    }

    [Fact]
    public void GenerateFlexibleComfort_AlreadyScheduledPast_ReturnsAlreadyRan()
    {
        // Arrange: nextScheduledComfortUtc is in the past
        var lastComfortRun = new DateTimeOffset(2026, 2, 15, 0, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 2, 22, 14, 0, 0, TimeSpan.Zero);
        // windowStart = Feb 20, windowEnd = Feb 24

        var todayStart = new DateTimeOffset(2026, 2, 22, 0, 0, 0, TimeSpan.Zero);
        var rawToday = CreatePriceArray(todayStart,
            0.50m, 0.40m, 0.30m, 0.20m, 0.10m, 0.15m, 0.25m, 0.35m,
            0.45m, 0.55m, 0.65m, 0.75m, 0.85m, 0.95m, 1.05m, 1.15m,
            1.25m, 1.35m, 1.45m, 1.55m, 1.65m, 1.75m, 1.85m, 1.95m);

        var scheduledAt = new DateTimeOffset(2026, 2, 22, 10, 0, 0, TimeSpan.Zero); // in the past

        // Act
        var result = ScheduleAlgorithm.GenerateFlexibleComfort(
            rawToday: rawToday,
            rawTomorrow: null,
            lastComfortRun: lastComfortRun,
            intervalDays: 7,
            flexibilityDays: 2,
            historicalBaseThreshold: 0.50m,
            historicalMaxPrice: 2.00m,
            nextScheduledComfortUtc: scheduledAt,
            nowOverride: now);

        // Assert
        Assert.Equal("already_ran", result.State);
        Assert.Equal(scheduledAt, result.ScheduledHourUtc);
    }

    [Fact]
    public void GenerateFlexibleComfort_SlidingThreshold_ProgressZero_Strict()
    {
        // Arrange: exactly at windowStart → progress = 0.0
        // effectiveThreshold should equal baseThreshold
        var lastComfortRun = new DateTimeOffset(2026, 2, 15, 0, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 2, 20, 0, 0, 0, TimeSpan.Zero);
        // windowStart = Feb 20 00:00 (interval=7, flex=2 → +5 days)
        // windowEnd = Feb 24 00:00

        var todayStart = new DateTimeOffset(2026, 2, 20, 0, 0, 0, TimeSpan.Zero);
        // Provide expensive prices (all above base threshold of 0.30)
        var rawToday = CreatePriceArray(todayStart,
            0.50m, 0.55m, 0.60m, 0.65m, 0.70m, 0.75m, 0.80m, 0.85m,
            0.90m, 0.95m, 1.00m, 1.05m, 1.10m, 1.15m, 1.20m, 1.25m,
            1.30m, 1.35m, 1.40m, 1.45m, 1.50m, 1.55m, 1.60m, 1.65m);

        // Act
        var result = ScheduleAlgorithm.GenerateFlexibleComfort(
            rawToday: rawToday,
            rawTomorrow: null,
            lastComfortRun: lastComfortRun,
            intervalDays: 7,
            flexibilityDays: 2,
            historicalBaseThreshold: 0.30m,
            historicalMaxPrice: 2.00m,
            nowOverride: now);

        // Assert: at progress=0, threshold = baseThreshold = 0.30
        // Cheapest future price = 0.50 at hour 0, which is > 0.30 → waiting_for_cheaper
        Assert.Equal(0.30m, result.EffectiveThreshold);
        Assert.Equal(0.0, result.WindowProgress);
        Assert.Equal("waiting_for_cheaper", result.State);
    }

    [Fact]
    public void GenerateFlexibleComfort_SlidingThreshold_ProgressOne_Lenient()
    {
        // Arrange: exactly at windowEnd → progress = 1.0 (clamped)
        // effectiveThreshold should equal maxPrice
        // This means progress ≥ 0.95 → forced schedule
        var lastComfortRun = new DateTimeOffset(2026, 2, 15, 0, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 2, 24, 0, 0, 0, TimeSpan.Zero);
        // windowStart = Feb 20 00:00
        // windowEnd   = Feb 24 00:00 → progress = 96/96 = 1.0

        var todayStart = new DateTimeOffset(2026, 2, 24, 0, 0, 0, TimeSpan.Zero);
        var rawToday = CreatePriceArray(todayStart,
            3.00m, 2.50m, 2.80m, 3.10m, 3.20m, 3.30m, 3.40m, 3.50m,
            3.60m, 3.70m, 3.80m, 3.90m, 4.00m, 4.10m, 4.20m, 4.30m,
            4.40m, 4.50m, 4.60m, 4.70m, 4.80m, 4.90m, 5.00m, 5.10m);

        // Act
        var result = ScheduleAlgorithm.GenerateFlexibleComfort(
            rawToday: rawToday,
            rawTomorrow: null,
            lastComfortRun: lastComfortRun,
            intervalDays: 7,
            flexibilityDays: 2,
            historicalBaseThreshold: 0.30m,
            historicalMaxPrice: 2.00m,
            nowOverride: now);

        // Assert: threshold at progress=1.0 = maxPrice = 2.00
        // Progress ≥ 0.95 → forced, picks cheapest (2.50 at hour 1)
        Assert.Equal(2.00m, result.EffectiveThreshold);
        Assert.Equal(1.0, result.WindowProgress);
        Assert.Equal("scheduled", result.State);
        Assert.NotNull(result.ScheduledHourUtc);
        Assert.Equal(1, result.ScheduledHourUtc!.Value.Hour);
    }

    #endregion
}
