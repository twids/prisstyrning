using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Prisstyrning.Tests;

/// <summary>
/// Comprehensive unit tests for Daikin schedule schema creation, validation, and manipulation.
/// These tests ensure that all schema generation scenarios work correctly, including updates,
/// additions, and removals of schedule actions.
/// </summary>
public class SchemaCreationTests
{
    private readonly IConfiguration _testConfig;
    
    // Shared constants for test data
    private const decimal BasePrice = 0.1m;
    private const decimal HourlyIncrement = 0.05m;
    private static readonly HashSet<string> ValidWeekdays = new(StringComparer.OrdinalIgnoreCase)
    {
        "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday"
    };

    public SchemaCreationTests()
    {
        var configDict = new Dictionary<string, string?>
        {
            { "Schedule:TurnOffSpikeDeltaPct", "10" },
            { "Schedule:TurnOffNeighborWindow", "2" },
            { "Schedule:ComfortNextHourMaxIncreasePct", "25" },
            { "Schedule:MaxActivationsPerDay", "4" }
        };
        _testConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(configDict)
            .Build();
    }

    #region Schema Structure Validation Tests

    [Fact]
    public void GeneratedSchema_HasCorrectRootStructure()
    {
        // Arrange
        var today = DateTimeOffset.Now.Date;
        var rawToday = CreatePriceData(today, new[] {
            (0, 0.30m), (1, 0.25m), (2, 0.20m), (3, 0.35m),
            (4, 0.40m), (5, 0.50m)
        });

        // Act
        var result = ScheduleAlgorithm.Generate(
            rawToday, null, 3, 0.9, 2, 4, _testConfig,
            nowOverride: today, ScheduleAlgorithm.LogicType.PerDayOriginal);

        // Assert
        Assert.NotNull(result.schedulePayload);
        var schema = result.schedulePayload;
        
        // Root should have schedule ID "0"
        Assert.True(schema is JsonObject);
        Assert.True(schema["0"] != null, "Schema should contain schedule ID '0'");
        
        // Schedule 0 should have actions
        var schedule0 = schema["0"] as JsonObject;
        Assert.NotNull(schedule0);
        Assert.True(schedule0!["actions"] != null, "Schedule 0 should have 'actions' property");
    }

    [Fact]
    public void GeneratedSchema_ActionsContainValidWeekdayNames()
    {
        // Arrange
        var today = DateTimeOffset.Now.Date;
        var tomorrow = today.AddDays(1);
        var rawToday = CreatePriceData(today, new[] {
            (0, 0.30m), (1, 0.25m), (2, 0.20m)
        });
        var rawTomorrow = CreatePriceData(tomorrow, new[] {
            (0, 0.35m), (1, 0.30m), (2, 0.25m)
        });

        // Act
        var result = ScheduleAlgorithm.Generate(
            rawToday, rawTomorrow, 2, 0.9, 2, 4, _testConfig,
            nowOverride: today, ScheduleAlgorithm.LogicType.PerDayOriginal);

        // Assert
        Assert.NotNull(result.schedulePayload);
        var actions = result.schedulePayload["0"]?["actions"] as JsonObject;
        Assert.NotNull(actions);
        
        // Verify all keys are valid weekday names
        foreach (var prop in actions!)
        {
            Assert.True(ValidWeekdays.Contains(prop.Key.ToLower()),
                $"Action period '{prop.Key}' should be a valid weekday name");
        }
    }

    [Fact]
    public void GeneratedSchema_TimestampsAreValidTimeSpanFormat()
    {
        // Arrange
        var today = DateTimeOffset.Now.Date;
        var rawToday = CreatePriceData(today, new[] {
            (0, 0.50m), (1, 0.40m), (2, 0.30m), (3, 0.45m),
            (4, 0.35m), (5, 0.60m)
        });

        // Act
        var result = ScheduleAlgorithm.Generate(
            rawToday, null, 3, 0.9, 2, 4, _testConfig,
            nowOverride: today, ScheduleAlgorithm.LogicType.PerDayOriginal);

        // Assert
        Assert.NotNull(result.schedulePayload);
        var actions = result.schedulePayload["0"]?["actions"] as JsonObject;
        Assert.NotNull(actions);
        
        // Check each action period
        foreach (var dayProp in actions!)
        {
            var dayActions = dayProp.Value as JsonObject;
            Assert.NotNull(dayActions);
            
            // Each timestamp should be parseable as TimeSpan with robust error handling
            foreach (var timeProp in dayActions!)
            {
                var parseSuccess = TimeSpan.TryParse(timeProp.Key, out var timeSpan);
                Assert.True(parseSuccess,
                    $"Timestamp '{timeProp.Key}' should be a valid TimeSpan format");
                
                // Verify timestamp is within 0-24 hours range
                if (parseSuccess)
                {
                    Assert.True(timeSpan >= TimeSpan.Zero && timeSpan < TimeSpan.FromHours(24),
                        $"Timestamp '{timeProp.Key}' should be within 0-24 hour range");
                }
            }
        }
    }

    [Fact]
    public void GeneratedSchema_ActionsContainValidTemperatureValues()
    {
        // Arrange
        var today = DateTimeOffset.Now.Date;
        var rawToday = CreatePriceData(today, new[] {
            (0, 0.30m), (1, 0.25m), (2, 0.20m), (3, 1.50m),
            (4, 1.60m), (5, 0.40m), (6, 0.35m)
        });

        // Act
        var result = ScheduleAlgorithm.Generate(
            rawToday, null, 2, 0.8, 2, 4, _testConfig,
            nowOverride: today, ScheduleAlgorithm.LogicType.PerDayOriginal);

        // Assert
        Assert.NotNull(result.schedulePayload);
        var actions = result.schedulePayload["0"]?["actions"] as JsonObject;
        Assert.NotNull(actions);
        
        var validValues = new HashSet<string> { "comfort", "eco", "turn_off" };
        
        foreach (var dayProp in actions!)
        {
            var dayActions = dayProp.Value as JsonObject;
            Assert.NotNull(dayActions);
            
            foreach (var timeProp in dayActions!)
            {
                var action = timeProp.Value as JsonObject;
                Assert.NotNull(action);
                
                var tempValue = action!["domesticHotWaterTemperature"]?.ToString();
                Assert.NotNull(tempValue);
                Assert.True(validValues.Contains(tempValue!),
                    $"Temperature value '{tempValue}' should be one of: {string.Join(", ", validValues)}");
            }
        }
    }

    #endregion

    #region Schema Serialization Tests

    [Fact]
    public void GeneratedSchema_CanBeSerializedToJson()
    {
        // Arrange
        var today = DateTimeOffset.Now.Date;
        var rawToday = CreatePriceData(today, new[] {
            (0, 0.30m), (1, 0.25m), (2, 0.20m)
        });

        // Act
        var result = ScheduleAlgorithm.Generate(
            rawToday, null, 2, 0.9, 2, 4, _testConfig,
            nowOverride: today, ScheduleAlgorithm.LogicType.PerDayOriginal);

        // Assert
        Assert.NotNull(result.schedulePayload);
        
        // Should serialize without exceptions
        var json = result.schedulePayload.ToJsonString();
        Assert.False(string.IsNullOrWhiteSpace(json));
        
        // Should be valid JSON
        var parsed = JsonNode.Parse(json);
        Assert.NotNull(parsed);
    }

    [Fact]
    public void GeneratedSchema_SerializationIsStable()
    {
        // Arrange
        var today = DateTimeOffset.Now.Date;
        var rawToday = CreatePriceData(today, new[] {
            (0, 0.30m), (1, 0.25m), (2, 0.20m)
        });

        // Act - generate twice with same input
        var result1 = ScheduleAlgorithm.Generate(
            rawToday, null, 2, 0.9, 2, 4, _testConfig,
            nowOverride: today, ScheduleAlgorithm.LogicType.PerDayOriginal);
        
        var result2 = ScheduleAlgorithm.Generate(
            rawToday, null, 2, 0.9, 2, 4, _testConfig,
            nowOverride: today, ScheduleAlgorithm.LogicType.PerDayOriginal);

        // Assert - should produce identical JSON
        Assert.NotNull(result1.schedulePayload);
        Assert.NotNull(result2.schedulePayload);
        
        var json1 = result1.schedulePayload.ToJsonString();
        var json2 = result2.schedulePayload.ToJsonString();
        
        Assert.Equal(json1, json2);
    }

    #endregion

    #region Schema Modification Tests

    [Fact]
    public void SchemaUpdate_CanModifyExistingAction()
    {
        // Arrange
        var today = DateTimeOffset.Now.Date;
        var rawToday = CreatePriceData(today, new[] {
            (0, 0.30m), (1, 0.25m), (2, 0.20m)
        });
        var result = ScheduleAlgorithm.Generate(
            rawToday, null, 2, 0.9, 2, 4, _testConfig,
            nowOverride: today, ScheduleAlgorithm.LogicType.PerDayOriginal);
        
        Assert.NotNull(result.schedulePayload);
        var schema = result.schedulePayload as JsonObject;
        var actions = schema!["0"]!["actions"] as JsonObject;
        
        // Act - modify an existing action
        var weekdayName = today.DayOfWeek.ToString().ToLower();
        if (actions!.ContainsKey(weekdayName))
        {
            var dayActions = actions[weekdayName] as JsonObject;
            if (dayActions != null && dayActions.Count > 0)
            {
                var firstTime = dayActions.First().Key;
                dayActions[firstTime] = new JsonObject
                {
                    ["domesticHotWaterTemperature"] = "turn_off"
                };
                
                // Assert - modification should be persisted
                var modifiedAction = dayActions[firstTime] as JsonObject;
                Assert.NotNull(modifiedAction);
                Assert.Equal("turn_off", modifiedAction!["domesticHotWaterTemperature"]?.ToString());
            }
        }
    }

    [Fact]
    public void SchemaAddition_CanAddNewActionToExistingDay()
    {
        // Arrange
        var today = DateTimeOffset.Now.Date;
        var rawToday = CreatePriceData(today, new[] {
            (0, 0.30m), (1, 0.25m), (2, 0.20m)
        });
        var result = ScheduleAlgorithm.Generate(
            rawToday, null, 2, 0.9, 2, 4, _testConfig,
            nowOverride: today, ScheduleAlgorithm.LogicType.PerDayOriginal);
        
        Assert.NotNull(result.schedulePayload);
        var schema = result.schedulePayload as JsonObject;
        var actions = schema!["0"]!["actions"] as JsonObject;
        
        // Act - add a new action to existing day
        var weekdayName = today.DayOfWeek.ToString().ToLower();
        if (actions!.ContainsKey(weekdayName))
        {
            var dayActions = actions[weekdayName] as JsonObject;
            var initialCount = dayActions!.Count;
            
            // Add new action at 23:00:00
            dayActions["23:00:00"] = new JsonObject
            {
                ["domesticHotWaterTemperature"] = "eco"
            };
            
            // Assert - action should be added
            Assert.Equal(initialCount + 1, dayActions.Count);
            Assert.True(dayActions.ContainsKey("23:00:00"));
            var newAction = dayActions["23:00:00"] as JsonObject;
            Assert.Equal("eco", newAction!["domesticHotWaterTemperature"]?.ToString());
        }
    }

    [Fact]
    public void SchemaAddition_CanAddNewDayToSchedule()
    {
        // Arrange
        var today = DateTimeOffset.Now.Date;
        var rawToday = CreatePriceData(today, new[] {
            (0, 0.30m), (1, 0.25m), (2, 0.20m)
        });
        var result = ScheduleAlgorithm.Generate(
            rawToday, null, 2, 0.9, 2, 4, _testConfig,
            nowOverride: today, ScheduleAlgorithm.LogicType.PerDayOriginal);
        
        Assert.NotNull(result.schedulePayload);
        var schema = result.schedulePayload as JsonObject;
        var actions = schema!["0"]!["actions"] as JsonObject;
        var initialCount = actions!.Count;
        
        // Act - add a new day (e.g., "friday") if not present
        if (!actions.ContainsKey("friday"))
        {
            actions["friday"] = new JsonObject
            {
                ["08:00:00"] = new JsonObject
                {
                    ["domesticHotWaterTemperature"] = "comfort"
                }
            };
            
            // Assert
            Assert.Equal(initialCount + 1, actions.Count);
            Assert.True(actions.ContainsKey("friday"));
            var fridayActions = actions["friday"] as JsonObject;
            Assert.NotNull(fridayActions);
            Assert.True(fridayActions!.ContainsKey("08:00:00"));
        }
    }

    [Fact]
    public void SchemaRemoval_CanRemoveActionFromDay()
    {
        // Arrange
        var today = DateTimeOffset.Now.Date;
        var rawToday = CreatePriceData(today, new[] {
            (0, 0.30m), (1, 0.25m), (2, 0.20m), (3, 0.35m),
            (4, 0.40m), (5, 0.50m)
        });
        var result = ScheduleAlgorithm.Generate(
            rawToday, null, 3, 0.9, 2, 4, _testConfig,
            nowOverride: today, ScheduleAlgorithm.LogicType.PerDayOriginal);
        
        Assert.NotNull(result.schedulePayload);
        var schema = result.schedulePayload as JsonObject;
        var actions = schema!["0"]!["actions"] as JsonObject;
        
        // Act - remove an action if multiple exist
        var weekdayName = today.DayOfWeek.ToString().ToLower();
        if (actions!.ContainsKey(weekdayName))
        {
            var dayActions = actions[weekdayName] as JsonObject;
            var initialCount = dayActions!.Count;
            
            if (initialCount > 1)
            {
                var firstTime = dayActions.First().Key;
                dayActions.Remove(firstTime);
                
                // Assert
                Assert.Equal(initialCount - 1, dayActions.Count);
                Assert.False(dayActions.ContainsKey(firstTime));
            }
        }
    }

    [Fact]
    public void SchemaRemoval_CanRemoveEntireDay()
    {
        // Arrange
        var today = DateTimeOffset.Now.Date;
        var tomorrow = today.AddDays(1);
        var rawToday = CreatePriceData(today, new[] {
            (0, 0.30m), (1, 0.25m), (2, 0.20m)
        });
        var rawTomorrow = CreatePriceData(tomorrow, new[] {
            (0, 0.35m), (1, 0.30m), (2, 0.25m)
        });
        var result = ScheduleAlgorithm.Generate(
            rawToday, rawTomorrow, 2, 0.9, 2, 4, _testConfig,
            nowOverride: today, ScheduleAlgorithm.LogicType.PerDayOriginal);
        
        Assert.NotNull(result.schedulePayload);
        var schema = result.schedulePayload as JsonObject;
        var actions = schema!["0"]!["actions"] as JsonObject;
        var initialCount = actions!.Count;
        
        // Act - remove a day if multiple exist
        if (initialCount > 1)
        {
            var firstDay = actions.First().Key;
            actions.Remove(firstDay);
            
            // Assert
            Assert.Equal(initialCount - 1, actions.Count);
            Assert.False(actions.ContainsKey(firstDay));
        }
    }

    #endregion

    #region Schema Edge Cases

    [Fact]
    public void Schema_EmptyActions_HandledGracefully()
    {
        // Arrange - create empty schema structure
        var schema = new JsonObject
        {
            ["0"] = new JsonObject
            {
                ["actions"] = new JsonObject()
            }
        };

        // Act - serialize and validate
        var json = schema.ToJsonString();
        var parsed = JsonNode.Parse(json);

        // Assert - should be valid but empty
        Assert.NotNull(parsed);
        var actions = parsed!["0"]?["actions"] as JsonObject;
        Assert.NotNull(actions);
        Assert.Empty(actions!);
    }

    [Fact]
    public void Schema_SingleAction_CreatesValidStructure()
    {
        // Arrange
        var schema = new JsonObject
        {
            ["0"] = new JsonObject
            {
                ["actions"] = new JsonObject
                {
                    ["monday"] = new JsonObject
                    {
                        ["08:00:00"] = new JsonObject
                        {
                            ["domesticHotWaterTemperature"] = "comfort"
                        }
                    }
                }
            }
        };

        // Act
        var json = schema.ToJsonString();
        var parsed = JsonNode.Parse(json);

        // Assert
        Assert.NotNull(parsed);
        var mondayActions = parsed!["0"]?["actions"]?["monday"] as JsonObject;
        Assert.NotNull(mondayActions);
        Assert.Single(mondayActions!);
        Assert.Equal("comfort", 
            mondayActions["08:00:00"]!["domesticHotWaterTemperature"]?.ToString());
    }

    [Fact]
    public void Schema_MaxActionsPerDay_Validated()
    {
        // Arrange
        var today = DateTimeOffset.Now.Date;
        var rawToday = CreatePriceData(today, Enumerable.Range(0, 24)
            .Select(h => (h, BasePrice + (decimal)h * HourlyIncrement))
            .ToArray());

        // Act - with activation limit of 4
        var result = ScheduleAlgorithm.Generate(
            rawToday, null, 3, 0.8, 2, 4, _testConfig,
            nowOverride: today, ScheduleAlgorithm.LogicType.PerDayOriginal);

        // Assert
        Assert.NotNull(result.schedulePayload);
        var actions = result.schedulePayload["0"]?["actions"] as JsonObject;
        Assert.NotNull(actions);
        
        // Verify each day has at most 4 actions
        foreach (var dayProp in actions!)
        {
            var dayActions = dayProp.Value as JsonObject;
            Assert.NotNull(dayActions);
            Assert.True(dayActions!.Count <= 4,
                $"Day {dayProp.Key} should have at most 4 actions, but has {dayActions.Count}");
        }
    }

    [Fact]
    public void Schema_MultipleScheduleIds_CanBeSupported()
    {
        // Arrange - test that schema structure supports multiple schedule IDs
        var schema = new JsonObject
        {
            ["0"] = new JsonObject
            {
                ["actions"] = new JsonObject
                {
                    ["monday"] = new JsonObject
                    {
                        ["08:00:00"] = new JsonObject
                        {
                            ["domesticHotWaterTemperature"] = "comfort"
                        }
                    }
                }
            },
            ["1"] = new JsonObject
            {
                ["actions"] = new JsonObject
                {
                    ["tuesday"] = new JsonObject
                    {
                        ["09:00:00"] = new JsonObject
                        {
                            ["domesticHotWaterTemperature"] = "eco"
                        }
                    }
                }
            }
        };

        // Act
        var json = schema.ToJsonString();
        var parsed = JsonNode.Parse(json);

        // Assert - both schedule IDs should exist
        Assert.NotNull(parsed);
        Assert.NotNull(parsed!["0"]);
        Assert.NotNull(parsed["1"]);
        Assert.NotNull(parsed["0"]!["actions"]);
        Assert.NotNull(parsed["1"]!["actions"]);
    }

    #endregion

    #region Cross-Day Logic Tests

    [Fact]
    public void CrossDayLogic_GeneratesValidSchema()
    {
        // Arrange
        var today = DateTimeOffset.Now.Date;
        var tomorrow = today.AddDays(1);
        var rawToday = CreatePriceData(today, new[] {
            (0, 0.80m), (1, 0.70m), (2, 0.60m)
        });
        var rawTomorrow = CreatePriceData(tomorrow, new[] {
            (0, 0.20m), (1, 0.10m), (2, 0.30m)
        });

        // Act
        var result = ScheduleAlgorithm.Generate(
            rawToday, rawTomorrow, 3, 0.9, 2, 4, _testConfig,
            nowOverride: today, ScheduleAlgorithm.LogicType.CrossDayCheapestLimited);

        // Assert
        Assert.NotNull(result.schedulePayload);
        // Verify the message indicates cross-day logic was used (checking for substring)
        Assert.True(result.message.Contains("cross-day", StringComparison.OrdinalIgnoreCase),
            "Message should indicate cross-day logic was applied");
        
        // Verify schema structure
        var schema = result.schedulePayload as JsonObject;
        Assert.NotNull(schema);
        Assert.NotNull(schema!["0"]);
        Assert.NotNull(schema["0"]!["actions"]);
    }

    [Fact]
    public void CrossDayLogic_RespectsActivationLimit()
    {
        // Arrange
        var today = DateTimeOffset.Now.Date;
        var tomorrow = today.AddDays(1);
        var rawToday = CreatePriceData(today, Enumerable.Range(0, 12)
            .Select(h => (h, BasePrice + (decimal)h * HourlyIncrement))
            .ToArray());
        var rawTomorrow = CreatePriceData(tomorrow, Enumerable.Range(0, 12)
            .Select(h => (h, BasePrice + (decimal)h * HourlyIncrement))
            .ToArray());

        // Act
        var result = ScheduleAlgorithm.Generate(
            rawToday, rawTomorrow, 3, 0.9, 2, 3, _testConfig,
            nowOverride: today, ScheduleAlgorithm.LogicType.CrossDayCheapestLimited);

        // Assert
        Assert.NotNull(result.schedulePayload);
        var actions = result.schedulePayload["0"]?["actions"] as JsonObject;
        Assert.NotNull(actions);
        
        // Each day should respect the activation limit
        foreach (var dayProp in actions!)
        {
            var dayActions = dayProp.Value as JsonObject;
            if (dayActions != null && dayActions.Count > 0)
            {
                Assert.True(dayActions.Count <= 3,
                    $"Day {dayProp.Key} should have at most 3 actions with activation limit 3");
            }
        }
    }

    #endregion

    #region Integration Tests

    [Fact]
    public void Schema_GeneratedFromRealPriceData_IsValid()
    {
        // Arrange - simulate realistic price data with variations
        var today = DateTimeOffset.Now.Date;
        var rawToday = CreatePriceData(today, new[] {
            (0, 0.45m), (1, 0.42m), (2, 0.38m), (3, 0.35m),
            (4, 0.33m), (5, 0.40m), (6, 0.55m), (7, 0.75m),
            (8, 1.20m), (9, 1.45m), (10, 1.35m), (11, 1.15m),
            (12, 1.00m), (13, 0.95m), (14, 0.90m), (15, 0.85m),
            (16, 1.10m), (17, 1.55m), (18, 1.80m), (19, 1.65m),
            (20, 1.30m), (21, 0.95m), (22, 0.70m), (23, 0.55m)
        });

        // Act
        var result = ScheduleAlgorithm.Generate(
            rawToday, null, 3, 0.9, 2, 4, _testConfig,
            nowOverride: today, ScheduleAlgorithm.LogicType.PerDayOriginal);

        // Assert - comprehensive validation
        Assert.NotNull(result.schedulePayload);
        var schema = result.schedulePayload as JsonObject;
        Assert.NotNull(schema);
        
        // Verify schedule structure
        Assert.NotNull(schema!["0"]);
        var schedule0 = schema["0"] as JsonObject;
        Assert.NotNull(schedule0);
        Assert.NotNull(schedule0!["actions"]);
        
        var actions = schedule0["actions"] as JsonObject;
        Assert.NotNull(actions);
        Assert.NotEmpty(actions!);
        
        // Verify at least one day has comfort hours
        bool hasComfort = false;
        foreach (var dayProp in actions)
        {
            var dayActions = dayProp.Value as JsonObject;
            if (dayActions != null)
            {
                foreach (var timeProp in dayActions)
                {
                    var action = timeProp.Value as JsonObject;
                    var temp = action?["domesticHotWaterTemperature"]?.ToString();
                    if (temp == "comfort")
                    {
                        hasComfort = true;
                        break;
                    }
                }
            }
        }
        Assert.True(hasComfort, "Schedule should have at least one comfort period");
    }

    [Fact]
    public void Schema_WithBothDays_HasCorrectWeekdayNames()
    {
        // Arrange - Use relative date to get Monday of current week
        var now = DateTimeOffset.Now.Date;
        var daysToMonday = ((int)now.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        var today = now.AddDays(-daysToMonday); // Monday
        var tomorrow = today.AddDays(1); // Tuesday
        
        var rawToday = CreatePriceData(today, new[] {
            (0, 0.30m), (1, 0.25m), (2, 0.20m)
        });
        var rawTomorrow = CreatePriceData(tomorrow, new[] {
            (0, 0.35m), (1, 0.30m), (2, 0.25m)
        });

        // Act
        var result = ScheduleAlgorithm.Generate(
            rawToday, rawTomorrow, 2, 0.9, 2, 4, _testConfig,
            nowOverride: today, ScheduleAlgorithm.LogicType.PerDayOriginal);

        // Assert
        Assert.NotNull(result.schedulePayload);
        var actions = result.schedulePayload["0"]?["actions"] as JsonObject;
        Assert.NotNull(actions);
        
        // Should have Monday and/or Tuesday
        var hasMonday = actions!.ContainsKey("monday");
        var hasTuesday = actions.ContainsKey("tuesday");
        
        Assert.True(hasMonday || hasTuesday, 
            "Schema should contain either Monday or Tuesday actions");
    }

    [Fact]
    public void Schema_ConsecutiveTimestamps_AreSorted()
    {
        // Arrange
        var today = DateTimeOffset.Now.Date;
        var rawToday = CreatePriceData(today, new[] {
            (0, 0.30m), (1, 0.25m), (2, 0.20m), (3, 1.50m),
            (4, 1.60m), (5, 0.35m), (6, 0.40m)
        });

        // Act
        var result = ScheduleAlgorithm.Generate(
            rawToday, null, 2, 0.8, 2, 4, _testConfig,
            nowOverride: today, ScheduleAlgorithm.LogicType.PerDayOriginal);

        // Assert
        Assert.NotNull(result.schedulePayload);
        var actions = result.schedulePayload["0"]?["actions"] as JsonObject;
        Assert.NotNull(actions);
        
        // Check each day's actions are in chronological order
        foreach (var dayProp in actions!)
        {
            var dayActions = dayProp.Value as JsonObject;
            if (dayActions != null && dayActions.Count > 1)
            {
                var times = new List<TimeSpan>();
                foreach (var kv in dayActions)
                {
                    if (TimeSpan.TryParse(kv.Key, out var ts))
                    {
                        times.Add(ts);
                    }
                    else
                    {
                        Assert.Fail($"Failed to parse timestamp '{kv.Key}' as TimeSpan");
                    }
                }
                var sortedTimes = times.OrderBy(t => t).ToList();
                
                Assert.True(times.SequenceEqual(sortedTimes),
                    $"Timestamps for {dayProp.Key} should be in chronological order");
            }
        }
    }

    #endregion

    #region Helper Methods

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

    #endregion
}
