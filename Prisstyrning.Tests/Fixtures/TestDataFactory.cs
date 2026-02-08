using System.Text.Json.Nodes;

namespace Prisstyrning.Tests.Fixtures;

/// <summary>
/// Factory for creating realistic test data that matches production data structures.
/// </summary>
public static class TestDataFactory
{
    /// <summary>
    /// Creates a valid Daikin DHW schedule payload with the specified actions.
    /// Format matches the actual Daikin API structure used in production.
    /// </summary>
    public static JsonObject CreateValidSchedulePayload(params (string day, int hour, string mode)[] actions)
    {
        var schedule = new JsonObject
        {
            ["scheduleType"] = "dhw",
            ["actions"] = new JsonArray()
        };

        var actionsArray = schedule["actions"] as JsonArray;
        foreach (var (day, hour, mode) in actions)
        {
            actionsArray!.Add(new JsonObject
            {
                ["day"] = day,
                ["hour"] = hour,
                ["mode"] = mode
            });
        }

        return schedule;
    }

    /// <summary>
    /// Creates a complete schedule payload with comfort/eco/off classification.
    /// Includes both the schedule and metadata used by the frontend.
    /// </summary>
    public static JsonObject CreateCompleteSchedulePayload(DateTime generatedAt, string[]? comfortHours = null, string[]? ecoHours = null, string[]? offHours = null)
    {
        comfortHours ??= new[] { "monday:8", "monday:9", "monday:18" };
        ecoHours ??= new[] { "tuesday:10", "tuesday:11" };
        offHours ??= new[] { "wednesday:2", "wednesday:3" };

        var payload = new JsonObject
        {
            ["scheduleType"] = "dhw",
            ["generatedAt"] = generatedAt.ToString("o"),
            ["comfortHours"] = new JsonArray(comfortHours.Select(h => JsonValue.Create(h)).ToArray()),
            ["ecoHours"] = new JsonArray(ecoHours.Select(h => JsonValue.Create(h)).ToArray()),
            ["offHours"] = new JsonArray(offHours.Select(h => JsonValue.Create(h)).ToArray()),
            ["actions"] = new JsonArray()
        };

        // Convert hour strings to actions
        var actionsArray = payload["actions"] as JsonArray;
        foreach (var hour in comfortHours)
        {
            var parts = hour.Split(':');
            actionsArray!.Add(new JsonObject { ["day"] = parts[0], ["hour"] = int.Parse(parts[1]), ["mode"] = "comfort" });
        }

        return payload;
    }

    /// <summary>
    /// Creates Nordpool price data for testing. Returns 24 hours of price data.
    /// </summary>
    public static JsonArray CreatePriceData(DateTime date, decimal basePrice = 0.5m, decimal variation = 0.3m)
    {
        var prices = new JsonArray();
        var random = new Random(date.GetHashCode()); // Deterministic for testing

        for (int hour = 0; hour < 24; hour++)
        {
            var timestamp = new DateTime(date.Year, date.Month, date.Day, hour, 0, 0, DateTimeKind.Utc);
            var price = basePrice + ((decimal)random.NextDouble() * variation * 2) - variation;
            
            prices.Add(new JsonObject
            {
                ["timestamp"] = timestamp.ToString("o"),
                ["price"] = Math.Round(price, 4)
            });
        }

        return prices;
    }

    /// <summary>
    /// Creates 48 hours of price data (today + tomorrow).
    /// </summary>
    public static (JsonArray today, JsonArray tomorrow) CreateTwoDayPriceData(DateTime today, decimal basePrice = 0.5m)
    {
        var todayPrices = CreatePriceData(today, basePrice);
        var tomorrowPrices = CreatePriceData(today.AddDays(1), basePrice + 0.1m);
        return (todayPrices, tomorrowPrices);
    }

    /// <summary>
    /// Creates price data with specific expensive and cheap hours for testing percentile logic.
    /// </summary>
    public static JsonArray CreatePriceDataWithExtremes(DateTime date, decimal cheapPrice = 0.2m, decimal normalPrice = 0.5m, decimal expensivePrice = 1.5m)
    {
        var prices = new JsonArray();

        for (int hour = 0; hour < 24; hour++)
        {
            var timestamp = new DateTime(date.Year, date.Month, date.Day, hour, 0, 0, DateTimeKind.Utc);
            
            // Make hours 2-4 cheap, 14-16 expensive, rest normal
            decimal price = normalPrice;
            if (hour >= 2 && hour <= 4) price = cheapPrice;
            else if (hour >= 14 && hour <= 16) price = expensivePrice;
            
            prices.Add(new JsonObject
            {
                ["timestamp"] = timestamp.ToString("o"),
                ["price"] = price
            });
        }

        return prices;
    }

    /// <summary>
    /// Creates a user settings object for user.json files.
    /// Issue #53: Removed turnOffMaxConsecutive - no longer needed with 2-mode system.
    /// </summary>
    public static JsonObject CreateUserSettings(int comfortHours = 3, double turnOffPercentile = 0.9, 
        int maxComfortGapHours = 28, string zone = "SE3")
    {
        return new JsonObject
        {
            ["ComfortHours"] = comfortHours,
            ["TurnOffPercentile"] = turnOffPercentile,
            ["MaxComfortGapHours"] = maxComfortGapHours,
            ["zone"] = zone
        };
    }

    /// <summary>
    /// Creates a schedule history entry with timestamp.
    /// </summary>
    public static JsonObject CreateHistoryEntry(DateTimeOffset timestamp, JsonObject schedulePayload)
    {
        return new JsonObject
        {
            ["timestamp"] = timestamp.ToString("o"),
            ["schedule"] = JsonNode.Parse(schedulePayload.ToJsonString())
        };
    }
}
