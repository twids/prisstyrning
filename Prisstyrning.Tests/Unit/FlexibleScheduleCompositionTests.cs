using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Prisstyrning.Tests.Unit;

/// <summary>
/// Unit tests for ComposeFlexibleSchedule that combines eco and comfort results
/// into a Daikin-compatible JSON schedule payload.
/// </summary>
public class FlexibleScheduleCompositionTests
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

    /// <summary>
    /// Extracts the actions object for a given weekday key from the Daikin schedule payload.
    /// Returns null if not found.
    /// </summary>
    private static JsonObject? GetDayActions(JsonNode? schedulePayload, string weekday)
    {
        return schedulePayload?["0"]?["actions"]?[weekday]?.AsObject();
    }

    /// <summary>
    /// Extracts the domesticHotWaterTemperature value at a specific time key from a day's actions.
    /// </summary>
    private static string? GetStateAtTime(JsonObject? dayActions, string timeKey)
    {
        return dayActions?[timeKey]?["domesticHotWaterTemperature"]?.ToString();
    }

    #endregion

    [Fact]
    public void ComposeFlexibleSchedule_EcoOnly_HasEcoActions()
    {
        // Arrange: eco scheduled at 03:00 on a Wednesday, no comfort
        var now = new DateTimeOffset(2026, 2, 25, 10, 0, 0, TimeSpan.Zero); // Wednesday
        var ecoResult = new ScheduleAlgorithm.FlexibleEcoResult(
            ScheduledHourUtc: new DateTimeOffset(2026, 2, 25, 3, 0, 0, TimeSpan.Zero),
            State: "scheduled",
            Message: "Eco scheduled at 03:00");

        // Act
        var (payload, message) = ScheduleAlgorithm.ComposeFlexibleSchedule(ecoResult, null, now);

        // Assert
        Assert.NotNull(payload);
        var todayActions = GetDayActions(payload, "wednesday");
        Assert.NotNull(todayActions);

        // Should have turn_off at 00:00, eco at 03:00, turn_off at 04:00
        Assert.Equal("turn_off", GetStateAtTime(todayActions, "00:00:00"));
        Assert.Equal("eco", GetStateAtTime(todayActions, "03:00:00"));
        Assert.Equal("turn_off", GetStateAtTime(todayActions, "04:00:00"));
        Assert.Contains("eco", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ComposeFlexibleSchedule_ComfortOnly_HasComfortActions()
    {
        // Arrange: comfort scheduled at 14:00 on a Monday, no eco
        var now = new DateTimeOffset(2026, 2, 23, 10, 0, 0, TimeSpan.Zero); // Monday
        var comfortResult = new ScheduleAlgorithm.FlexibleComfortResult(
            ScheduledHourUtc: new DateTimeOffset(2026, 2, 23, 14, 0, 0, TimeSpan.Zero),
            State: "scheduled",
            WindowProgress: 0.5,
            EffectiveThreshold: 0.50m,
            Message: "Comfort scheduled at 14:00");

        // Act
        var (payload, message) = ScheduleAlgorithm.ComposeFlexibleSchedule(null, comfortResult, now);

        // Assert
        Assert.NotNull(payload);
        var todayActions = GetDayActions(payload, "monday");
        Assert.NotNull(todayActions);

        Assert.Equal("turn_off", GetStateAtTime(todayActions, "00:00:00"));
        Assert.Equal("comfort", GetStateAtTime(todayActions, "14:00:00"));
        Assert.Equal("turn_off", GetStateAtTime(todayActions, "15:00:00"));
        Assert.Contains("comfort", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ComposeFlexibleSchedule_Combined_HasBothActions()
    {
        // Arrange: eco at 03:00 today (Thursday), comfort at 14:00 today
        var now = new DateTimeOffset(2026, 2, 26, 10, 0, 0, TimeSpan.Zero); // Thursday
        var ecoResult = new ScheduleAlgorithm.FlexibleEcoResult(
            ScheduledHourUtc: new DateTimeOffset(2026, 2, 26, 3, 0, 0, TimeSpan.Zero),
            State: "scheduled",
            Message: "Eco scheduled at 03:00");
        var comfortResult = new ScheduleAlgorithm.FlexibleComfortResult(
            ScheduledHourUtc: new DateTimeOffset(2026, 2, 26, 14, 0, 0, TimeSpan.Zero),
            State: "scheduled",
            WindowProgress: 0.5,
            EffectiveThreshold: 0.50m,
            Message: "Comfort scheduled at 14:00");

        // Act
        var (payload, message) = ScheduleAlgorithm.ComposeFlexibleSchedule(ecoResult, comfortResult, now);

        // Assert
        Assert.NotNull(payload);
        var todayActions = GetDayActions(payload, "thursday");
        Assert.NotNull(todayActions);

        // Should have both eco and comfort transitions
        Assert.Equal("turn_off", GetStateAtTime(todayActions, "00:00:00"));
        Assert.Equal("eco", GetStateAtTime(todayActions, "03:00:00"));
        Assert.Equal("turn_off", GetStateAtTime(todayActions, "04:00:00"));
        Assert.Equal("comfort", GetStateAtTime(todayActions, "14:00:00"));
        Assert.Equal("turn_off", GetStateAtTime(todayActions, "15:00:00"));
        Assert.Contains("eco", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("comfort", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ComposeFlexibleSchedule_NeitherScheduled_HasTurnOffOnly()
    {
        // Arrange: both eco and comfort have null scheduled hour (waiting states)
        var now = new DateTimeOffset(2026, 2, 25, 10, 0, 0, TimeSpan.Zero); // Wednesday
        var ecoResult = new ScheduleAlgorithm.FlexibleEcoResult(
            ScheduledHourUtc: null,
            State: "waiting",
            Message: "Eco window opens in 5 hours");
        var comfortResult = new ScheduleAlgorithm.FlexibleComfortResult(
            ScheduledHourUtc: null,
            State: "waiting_for_cheaper",
            WindowProgress: 0.3,
            EffectiveThreshold: 0.40m,
            Message: "Waiting for cheaper prices");

        // Act
        var (payload, message) = ScheduleAlgorithm.ComposeFlexibleSchedule(ecoResult, comfortResult, now);

        // Assert
        Assert.NotNull(payload);
        var todayActions = GetDayActions(payload, "wednesday");
        Assert.NotNull(todayActions);

        // Only turn_off at 00:00
        Assert.Equal("turn_off", GetStateAtTime(todayActions, "00:00:00"));
        Assert.Single(todayActions); // Only one action: default turn_off
    }

    [Fact]
    public void ComposeFlexibleSchedule_CorrectWeekdayKeys()
    {
        // Arrange: Friday now → expect "friday" and "saturday"
        var now = new DateTimeOffset(2026, 2, 27, 10, 0, 0, TimeSpan.Zero); // Friday
        var ecoResult = new ScheduleAlgorithm.FlexibleEcoResult(
            ScheduledHourUtc: new DateTimeOffset(2026, 2, 27, 5, 0, 0, TimeSpan.Zero),
            State: "scheduled",
            Message: "Eco at 05:00");

        // Act
        var (payload, _) = ScheduleAlgorithm.ComposeFlexibleSchedule(ecoResult, null, now);

        // Assert
        Assert.NotNull(payload);
        var actions = payload?["0"]?["actions"]?.AsObject();
        Assert.NotNull(actions);
        Assert.True(actions!.ContainsKey("friday"), "Should have friday key");
        Assert.True(actions.ContainsKey("saturday"), "Should have saturday key (tomorrow)");
    }

    [Fact]
    public void ComposeFlexibleSchedule_CorrectDaikinJsonFormat()
    {
        // Arrange: Sunday → expect "sunday" and "monday"
        var now = new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.Zero); // Sunday
        var ecoResult = new ScheduleAlgorithm.FlexibleEcoResult(
            ScheduledHourUtc: new DateTimeOffset(2026, 3, 2, 6, 0, 0, TimeSpan.Zero), // Monday 06:00
            State: "scheduled",
            Message: "Eco scheduled for tomorrow");

        // Act
        var (payload, _) = ScheduleAlgorithm.ComposeFlexibleSchedule(ecoResult, null, now);

        // Assert: verify the full nested structure { "0": { "actions": { "<weekday>": { "<time>": { "domesticHotWaterTemperature": "<state>" } } } } }
        Assert.NotNull(payload);
        var root = payload!.AsObject();
        Assert.True(root.ContainsKey("0"), "Root should have key '0'");

        var schedule = root["0"]!.AsObject();
        Assert.True(schedule.ContainsKey("actions"), "Schedule should have 'actions'");

        var actions = schedule["actions"]!.AsObject();
        Assert.True(actions.ContainsKey("sunday"), "Should have today (sunday)");
        Assert.True(actions.ContainsKey("monday"), "Should have tomorrow (monday)");

        // Monday should have eco at 06:00  
        var monday = actions["monday"]!.AsObject();
        Assert.True(monday.ContainsKey("06:00:00"), "Monday should have 06:00:00 action");
        var ecoAction = monday["06:00:00"]!.AsObject();
        Assert.True(ecoAction.ContainsKey("domesticHotWaterTemperature"));
        Assert.Equal("eco", ecoAction["domesticHotWaterTemperature"]!.ToString());

        // Verify JSON can be serialized/deserialized correctly (round-trip test)
        var json = payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var parsed = JsonNode.Parse(json);
        Assert.NotNull(parsed);
        Assert.Equal("eco", parsed!["0"]!["actions"]!["monday"]!["06:00:00"]!["domesticHotWaterTemperature"]!.ToString());
    }
}
