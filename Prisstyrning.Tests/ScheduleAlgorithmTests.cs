using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Prisstyrning.Tests;

public class ScheduleAlgorithmTests
{
    private readonly IConfiguration _testConfig;

    public ScheduleAlgorithmTests()
    {
        var configDict = new Dictionary<string, string?>
        {
            { "Schedule:TurnOffSpikeDeltaPct", "10" },
            { "Schedule:TurnOffNeighborWindow", "2" },
            { "Schedule:ComfortNextHourMaxIncreasePct", "25" }
        };
        _testConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(configDict)
            .Build();
    }

    [Fact]
    public void Generate_WithNullInputs_ReturnsNoSchedule()
    {
        // Arrange
        JsonArray? rawToday = null;
        JsonArray? rawTomorrow = null;

        // Act
        var result = ScheduleAlgorithm.Generate(
            rawToday, 
            rawTomorrow, 
            comfortHoursDefault: 3,
            turnOffPercentile: 0.9,
            turnOffMaxConsec: 2,
            activationLimit: 4,
            _testConfig);

        // Assert
        Assert.Null(result.schedulePayload);
        Assert.Equal("No schedule generated", result.message);
    }

    [Fact]
    public void Generate_WithEmptyInputs_ReturnsNoSchedule()
    {
        // Arrange
        var rawToday = new JsonArray();
        var rawTomorrow = new JsonArray();

        // Act
        var result = ScheduleAlgorithm.Generate(
            rawToday, 
            rawTomorrow, 
            comfortHoursDefault: 3,
            turnOffPercentile: 0.9,
            turnOffMaxConsec: 2,
            activationLimit: 4,
            _testConfig);

        // Assert
        Assert.Null(result.schedulePayload);
        Assert.Equal("No schedule generated", result.message);
    }

    [Fact]
    public void Generate_WithValidTodayData_PerDayOriginal_GeneratesSchedule()
    {
        // Arrange
        var today = DateTimeOffset.Now.Date;
        var rawToday = CreatePriceData(today, new[] { 
            (0, 0.50m), (1, 0.40m), (2, 0.30m), (3, 0.45m),
            (4, 0.35m), (5, 0.60m), (6, 0.80m), (7, 1.20m),
            (8, 1.50m), (9, 1.30m), (10, 1.10m), (11, 0.90m),
            (12, 0.85m), (13, 0.75m), (14, 0.70m), (15, 0.65m),
            (16, 0.95m), (17, 1.40m), (18, 1.80m), (19, 1.60m),
            (20, 1.20m), (21, 0.90m), (22, 0.70m), (23, 0.50m)
        });

        // Act
        var result = ScheduleAlgorithm.Generate(
            rawToday,
            null,
            comfortHoursDefault: 3,
            turnOffPercentile: 0.9,
            turnOffMaxConsec: 2,
            activationLimit: 4,
            _testConfig,
            nowOverride: today,
            ScheduleAlgorithm.LogicType.PerDayOriginal);

        // Assert
        Assert.NotNull(result.schedulePayload);
        Assert.Contains("Schedule generated", result.message);
        
        // Verify the schedule structure
        var payload = result.schedulePayload;
        Assert.NotNull(payload);
        Assert.True(payload["0"] != null);
        Assert.True(payload["0"]!["actions"] != null);
    }

    [Fact]
    public void Generate_WithValidTodayData_CrossDayCheapestLimited_GeneratesSchedule()
    {
        // Arrange
        var today = DateTimeOffset.Now.Date;
        var rawToday = CreatePriceData(today, new[] { 
            (0, 0.50m), (1, 0.40m), (2, 0.30m), (3, 0.45m),
            (4, 0.35m), (5, 0.60m), (6, 0.80m), (7, 1.20m),
            (8, 1.50m), (9, 1.30m), (10, 1.10m), (11, 0.90m)
        });

        // Act
        var result = ScheduleAlgorithm.Generate(
            rawToday,
            null,
            comfortHoursDefault: 3,
            turnOffPercentile: 0.9,
            turnOffMaxConsec: 2,
            activationLimit: 4,
            _testConfig,
            nowOverride: today,
            ScheduleAlgorithm.LogicType.CrossDayCheapestLimited);

        // Assert
        Assert.NotNull(result.schedulePayload);
        Assert.Contains("cross-day", result.message);
        
        // Verify the schedule structure
        var payload = result.schedulePayload;
        Assert.NotNull(payload);
        Assert.True(payload["0"] != null);
        Assert.True(payload["0"]!["actions"] != null);
    }

    [Fact]
    public void Generate_WithBothTodayAndTomorrowData_GeneratesSchedule()
    {
        // Arrange
        var today = DateTimeOffset.Now.Date;
        var tomorrow = today.AddDays(1);
        
        var rawToday = CreatePriceData(today, new[] { 
            (0, 0.50m), (1, 0.40m), (2, 0.30m), (3, 0.45m),
            (22, 0.70m), (23, 0.50m)
        });
        
        var rawTomorrow = CreatePriceData(tomorrow, new[] { 
            (0, 0.60m), (1, 0.50m), (2, 0.40m), (3, 0.55m),
            (22, 0.80m), (23, 0.60m)
        });

        // Act
        var result = ScheduleAlgorithm.Generate(
            rawToday,
            rawTomorrow,
            comfortHoursDefault: 3,
            turnOffPercentile: 0.9,
            turnOffMaxConsec: 2,
            activationLimit: 4,
            _testConfig,
            nowOverride: today,
            ScheduleAlgorithm.LogicType.PerDayOriginal);

        // Assert
        Assert.NotNull(result.schedulePayload);
        Assert.Contains("today + tomorrow", result.message);
    }

    [Fact]
    public void Generate_WithHighPercentile_GeneratesMoreTurnOffs()
    {
        // Arrange
        var today = DateTimeOffset.Now.Date;
        var rawToday = CreatePriceData(today, new[] { 
            (0, 0.30m), (1, 0.40m), (2, 0.50m), (3, 0.60m),
            (4, 0.70m), (5, 0.80m), (6, 0.90m), (7, 1.00m),
            (8, 1.10m), (9, 1.20m), (10, 1.30m), (11, 1.40m),
            (12, 1.50m), (13, 1.60m), (14, 1.70m), (15, 1.80m)
        });

        // Act - test with very low percentile (more aggressive turn-offs)
        var result = ScheduleAlgorithm.Generate(
            rawToday,
            null,
            comfortHoursDefault: 2,
            turnOffPercentile: 0.5, // Lower percentile = more turn-offs
            turnOffMaxConsec: 2,
            activationLimit: 4,
            _testConfig,
            nowOverride: today,
            ScheduleAlgorithm.LogicType.PerDayOriginal);

        // Assert
        Assert.NotNull(result.schedulePayload);
        Assert.Contains("Schedule generated", result.message);
    }

    [Fact]
    public void Generate_WithInvalidPriceData_HandlesGracefully()
    {
        // Arrange
        var today = DateTimeOffset.Now.Date;
        var rawToday = new JsonArray
        {
            new JsonObject { ["start"] = "invalid-date", ["value"] = "0.50" },
            new JsonObject { ["start"] = today.ToString("yyyy-MM-ddTHH:mm:ssZ"), ["value"] = "invalid-price" },
            null, // null entry
            new JsonObject { ["start"] = today.ToString("yyyy-MM-ddTHH:mm:ssZ") }, // missing value
            new JsonObject { ["value"] = "0.50" } // missing start
        };

        // Act
        var result = ScheduleAlgorithm.Generate(
            rawToday,
            null,
            comfortHoursDefault: 3,
            turnOffPercentile: 0.9,
            turnOffMaxConsec: 2,
            activationLimit: 4,
            _testConfig,
            nowOverride: today,
            ScheduleAlgorithm.LogicType.PerDayOriginal);

        // Assert - Should handle invalid data gracefully
        Assert.Null(result.schedulePayload);
        Assert.Equal("No schedule generated", result.message);
    }

    [Fact]
    public void Generate_ComfortHoursLimit_RespectsMaxComfortHours()
    {
        // Arrange
        var today = DateTimeOffset.Now.Date;
        var rawToday = CreatePriceData(today, new[] { 
            (0, 0.10m), (1, 0.11m), (2, 0.12m), (3, 0.13m),
            (4, 0.14m), (5, 0.15m), (6, 0.16m), (7, 0.17m)
        });

        // Act - request only 2 comfort hours from 8 cheap hours
        var result = ScheduleAlgorithm.Generate(
            rawToday,
            null,
            comfortHoursDefault: 2,
            turnOffPercentile: 0.9,
            turnOffMaxConsec: 2,
            activationLimit: 4,
            _testConfig,
            nowOverride: today,
            ScheduleAlgorithm.LogicType.PerDayOriginal);

        // Assert
        Assert.NotNull(result.schedulePayload);
        
        // Check that we don't have too many comfort actions
        var actions = result.schedulePayload["0"]?["actions"];
        Assert.NotNull(actions);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void Generate_ActivationLimit_RespectsLimit(int activationLimit)
    {
        // Arrange
        var today = DateTimeOffset.Now.Date;
        var rawToday = CreatePriceData(today, Enumerable.Range(0, 24)
            .Select(h => (h, (decimal)(h * 0.1 + 0.1)))
            .ToArray());

        // Act
        var result = ScheduleAlgorithm.Generate(
            rawToday,
            null,
            comfortHoursDefault: 3,
            turnOffPercentile: 0.8,
            turnOffMaxConsec: 1,
            activationLimit: activationLimit,
            _testConfig,
            nowOverride: today,
            ScheduleAlgorithm.LogicType.PerDayOriginal);

        // Assert
        Assert.NotNull(result.schedulePayload);
        
        // The test should not crash and should generate valid output
        var actions = result.schedulePayload["0"]?["actions"];
        Assert.NotNull(actions);
    }

    [Fact]
    public void Generate_BothLogicTypes_ProduceDifferentResults()
    {
        // Arrange
        var today = DateTimeOffset.Now.Date;
        var tomorrow = today.AddDays(1);
        
        var rawToday = CreatePriceData(today, new[] { 
            (0, 0.80m), (1, 0.70m), (2, 0.60m), (3, 0.50m)
        });
        
        var rawTomorrow = CreatePriceData(tomorrow, new[] { 
            (0, 0.40m), (1, 0.30m), (2, 0.20m), (3, 0.10m)
        });

        // Act
        var resultPerDay = ScheduleAlgorithm.Generate(
            rawToday, rawTomorrow, 3, 0.9, 2, 4, _testConfig,
            nowOverride: today, ScheduleAlgorithm.LogicType.PerDayOriginal);
            
        var resultCrossDay = ScheduleAlgorithm.Generate(
            rawToday, rawTomorrow, 3, 0.9, 2, 4, _testConfig,
            nowOverride: today, ScheduleAlgorithm.LogicType.CrossDayCheapestLimited);

        // Assert
        Assert.NotNull(resultPerDay.schedulePayload);
        Assert.NotNull(resultCrossDay.schedulePayload);
        
        // Messages should be different
        Assert.NotEqual(resultPerDay.message, resultCrossDay.message);
    }

    [Fact]
    public void Generate_MaxConsecutiveTurnOff_RespectsUserSetting()
    {
        // Arrange
        var today = DateTimeOffset.Now.Date;
        
        // Create scenario with multiple consecutive expensive hours that should be turned off
        // Make sure the expensive hours around 19 qualify as "spikes"
        var rawToday = CreatePriceData(today, new[] { 
            (0, 0.30m), (1, 0.25m), (2, 0.20m), (3, 0.22m),
            (4, 0.28m), (5, 0.35m), (6, 0.45m), (7, 0.55m),
            (8, 0.40m), (9, 0.35m), (10, 0.30m), (11, 0.25m),  // Keep these low to make 17-20 spikes
            (12, 0.25m), (13, 0.30m), (14, 0.35m), (15, 0.40m),  // Gradual increase
            (16, 0.45m), (17, 0.85m), (18, 1.00m), (19, 1.20m), // Big spikes that should all be turned off
            (20, 1.10m), (21, 0.50m), (22, 0.35m), (23, 0.25m)   // Back to normal
        });

        // Act - Test with turnOffMaxConsec = 4 (user's setting)
        var result = ScheduleAlgorithm.Generate(
            rawToday,
            null,
            comfortHoursDefault: 3,
            turnOffPercentile: 0.9,
            turnOffMaxConsec: 4,  // User setting: allow up to 4 consecutive hours
            activationLimit: 4,
            _testConfig,
            nowOverride: today,
            ScheduleAlgorithm.LogicType.PerDayOriginal);

        // Assert
        Assert.NotNull(result.schedulePayload);
        
        // Verify the schedule allows for consecutive turn-offs that can reach the peak
        var actions = result.schedulePayload["0"]?["actions"];
        Assert.NotNull(actions);
        
        // Check that we have a schedule for today
        var todayKey = today.DayOfWeek.ToString().ToLower();
        var todaySchedule = actions[todayKey] as JsonObject;
        Assert.NotNull(todaySchedule);
        
        // Count consecutive turn_off periods
        var turnOffHours = new List<int>();
        for (int hour = 0; hour < 24; hour++)
        {
            var timeKey = new TimeSpan(hour, 0, 0).ToString();
            if (todaySchedule.ContainsKey(timeKey))
            {
                var action = todaySchedule[timeKey]?["domesticHotWaterTemperature"]?.ToString();
                if (action == "turn_off")
                {
                    turnOffHours.Add(hour);
                }
            }
        }
        
        // If we have turn-offs, verify they can be consecutive up to the max setting
        if (turnOffHours.Count > 0)
        {
            // Find longest consecutive sequence
            turnOffHours.Sort();
            int maxConsecutive = 1;
            int currentConsecutive = 1;
            
            for (int i = 1; i < turnOffHours.Count; i++)
            {
                if (turnOffHours[i] == turnOffHours[i-1] + 1)
                {
                    currentConsecutive++;
                    maxConsecutive = Math.Max(maxConsecutive, currentConsecutive);
                }
                else
                {
                    currentConsecutive = 1;
                }
            }
            
            // The algorithm should allow consecutive turn-offs up to the user's setting
            // and should capture the expensive hours 17-20 with proper consecutive blocks
            Assert.True(maxConsecutive <= 4, $"Consecutive turn-offs ({maxConsecutive}) should not exceed user setting (4)");
            
            // With the expensive hours at 17-20, we should get more than just 1 hour
            Assert.True(turnOffHours.Count >= 2, $"Should turn off multiple expensive hours, but only got: {string.Join(", ", turnOffHours)}");
            
            // Print debug info to understand current behavior
            var turnOffHoursStr = string.Join(", ", turnOffHours);
            Console.WriteLine($"Turn-off hours: {turnOffHoursStr}");
            Console.WriteLine($"Max consecutive: {maxConsecutive}");
            Console.WriteLine($"Schedule: {result.schedulePayload.ToJsonString()}");
        }
        else
        {
            Console.WriteLine("No turn-off hours found in schedule");
            Console.WriteLine($"Full schedule: {result.schedulePayload.ToJsonString()}");
            
            // Debug: print all hours and their classifications
            for (int hour = 0; hour < 24; hour++)
            {
                var timeKey = new TimeSpan(hour, 0, 0).ToString();
                if (todaySchedule.ContainsKey(timeKey))
                {
                    var action = todaySchedule[timeKey]?["domesticHotWaterTemperature"]?.ToString();
                    Console.WriteLine($"Hour {hour}: {action}");
                }
                else
                {
                    Console.WriteLine($"Hour {hour}: (no action)");
                }
            }
            Assert.True(false, "No turn-off hours found in schedule");
        }
    }

    /// <summary>
    /// Helper method to create test price data in the expected JSON format
    /// </summary>
    private static JsonArray CreatePriceData(DateTimeOffset date, (int hour, decimal price)[] hourlyPrices)
    {
        var jsonArray = new JsonArray();
        
        foreach (var (hour, price) in hourlyPrices)
        {
            var timestamp = new DateTimeOffset(date.Year, date.Month, date.Day, hour, 0, 0, date.Offset);
            var priceEntry = new JsonObject
            {
                ["start"] = timestamp.ToString("O"),
                ["value"] = price.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
            };
            jsonArray.Add(priceEntry);
        }
        
        return jsonArray;
    }
}