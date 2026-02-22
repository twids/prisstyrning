using System.Text.Json.Nodes;
using Xunit;

namespace Prisstyrning.Tests.Unit;

/// <summary>
/// Unit tests for ScheduleAlgorithm.ComposeManualComfortSchedule which creates
/// a Daikin-compatible schedule JSON for manual comfort runs.
/// </summary>
public class ManualComfortScheduleTests
{
    #region Helpers

    private static string? GetStateAtTime(JsonNode? schedulePayload, string weekday, string timeKey)
    {
        return schedulePayload?["0"]?["actions"]?[weekday]?[timeKey]?["domesticHotWaterTemperature"]?.ToString();
    }

    private static JsonObject? GetDayActions(JsonNode? schedulePayload, string weekday)
    {
        return schedulePayload?["0"]?["actions"]?[weekday]?.AsObject();
    }

    #endregion

    [Fact]
    public void ComposeManualComfortSchedule_SetsComfortAtSpecifiedHour()
    {
        // Arrange: comfort at 14:00 today
        var now = DateTimeOffset.UtcNow;
        var comfortTime = new DateTimeOffset(now.Year, now.Month, now.Day, 14, 0, 0, TimeSpan.Zero);

        // Act
        var schedule = ScheduleAlgorithm.ComposeManualComfortSchedule(comfortTime);

        // Assert: comfort at 14:00 on the correct day
        var dayName = comfortTime.DayOfWeek.ToString().ToLowerInvariant();
        Assert.Equal("comfort", GetStateAtTime(schedule, dayName, "14:00:00"));
    }

    [Fact]
    public void ComposeManualComfortSchedule_SetsComfortOnCorrectDay()
    {
        // Arrange: comfort tomorrow at 10:00
        var tomorrow = DateTimeOffset.UtcNow.Date.AddDays(1);
        var comfortTime = new DateTimeOffset(tomorrow.Year, tomorrow.Month, tomorrow.Day, 10, 0, 0, TimeSpan.Zero);

        // Act
        var schedule = ScheduleAlgorithm.ComposeManualComfortSchedule(comfortTime);

        // Assert: comfort is on tomorrow's weekday
        var tomorrowDay = tomorrow.DayOfWeek.ToString().ToLowerInvariant();
        Assert.Equal("comfort", GetStateAtTime(schedule, tomorrowDay, "10:00:00"));

        // Assert: today has no comfort
        var todayDay = DateTimeOffset.UtcNow.Date.DayOfWeek.ToString().ToLowerInvariant();
        if (todayDay != tomorrowDay)
        {
            var todayActions = GetDayActions(schedule, todayDay);
            Assert.NotNull(todayActions);
            // Today should only have turn_off at 00:00:00
            Assert.Equal("turn_off", GetStateAtTime(schedule, todayDay, "00:00:00"));
            Assert.Null(todayActions![tomorrowDay]); // No cross-reference
        }
    }

    [Fact]
    public void ComposeManualComfortSchedule_HasTurnOffAfterComfort()
    {
        // Arrange: comfort at 14:00 today
        var now = DateTimeOffset.UtcNow;
        var comfortTime = new DateTimeOffset(now.Year, now.Month, now.Day, 14, 0, 0, TimeSpan.Zero);

        // Act
        var schedule = ScheduleAlgorithm.ComposeManualComfortSchedule(comfortTime);

        // Assert: turn_off at 15:00 (one hour after comfort)
        var dayName = comfortTime.DayOfWeek.ToString().ToLowerInvariant();
        Assert.Equal("turn_off", GetStateAtTime(schedule, dayName, "15:00:00"));
    }

    [Fact]
    public void ComposeManualComfortSchedule_BothDaysPresent()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var comfortTime = new DateTimeOffset(now.Year, now.Month, now.Day, 12, 0, 0, TimeSpan.Zero);

        // Act
        var schedule = ScheduleAlgorithm.ComposeManualComfortSchedule(comfortTime);

        // Assert: both today and tomorrow are present
        var todayName = now.Date.DayOfWeek.ToString().ToLowerInvariant();
        var tomorrowName = now.Date.AddDays(1).DayOfWeek.ToString().ToLowerInvariant();

        Assert.NotNull(GetDayActions(schedule, todayName));
        Assert.NotNull(GetDayActions(schedule, tomorrowName));
    }

    [Fact]
    public void ComposeManualComfortSchedule_StartsWithTurnOff()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var comfortTime = new DateTimeOffset(now.Year, now.Month, now.Day, 18, 0, 0, TimeSpan.Zero);

        // Act
        var schedule = ScheduleAlgorithm.ComposeManualComfortSchedule(comfortTime);

        // Assert: each day starts with turn_off at 00:00
        var todayName = now.Date.DayOfWeek.ToString().ToLowerInvariant();
        var tomorrowName = now.Date.AddDays(1).DayOfWeek.ToString().ToLowerInvariant();

        Assert.Equal("turn_off", GetStateAtTime(schedule, todayName, "00:00:00"));
        Assert.Equal("turn_off", GetStateAtTime(schedule, tomorrowName, "00:00:00"));
    }

    [Fact]
    public void ComposeManualComfortSchedule_Hour23_NoTurnOffAfter()
    {
        // Arrange: comfort at 23:00 — end would be 24:00 which is invalid
        var now = DateTimeOffset.UtcNow;
        var comfortTime = new DateTimeOffset(now.Year, now.Month, now.Day, 23, 0, 0, TimeSpan.Zero);

        // Act
        var schedule = ScheduleAlgorithm.ComposeManualComfortSchedule(comfortTime);

        // Assert: comfort at 23:00, no 24:00 key
        var dayName = comfortTime.DayOfWeek.ToString().ToLowerInvariant();
        var dayActions = GetDayActions(schedule, dayName);
        Assert.NotNull(dayActions);
        Assert.Equal("comfort", GetStateAtTime(schedule, dayName, "23:00:00"));
        // There should be no "24:00:00" key
        Assert.Null(dayActions!["24:00:00"]);
    }
}
