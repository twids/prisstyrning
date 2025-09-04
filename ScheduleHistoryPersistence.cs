using System.Text.Json.Nodes;
using System.Text.Json;

internal static class ScheduleHistoryPersistence
{
    // Save a schedule change for a user, keeping only last X days
    public static void Save(string userId, JsonObject schedulePayload, DateTimeOffset timestamp, int retentionDays = 7, string? baseDir = null)
    {
        baseDir ??= "data";
        var dir = Path.Combine(baseDir, "schedule_history", userId);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"history.json");
        List<JsonObject> history;
        if (File.Exists(file))
        {
            var json = File.ReadAllText(file);
            history = JsonNode.Parse(json)?.AsArray()?.Select(n => n as JsonObject).Where(n => n != null).ToList() ?? new List<JsonObject>();
        }
        else history = new List<JsonObject>();
        // Add new entry
        var entry = new JsonObject
        {
            ["timestamp"] = timestamp.ToString("o"),
            ["schedule"] = JsonNode.Parse(schedulePayload.ToJsonString()) as JsonObject,
        };
        history.Add(entry);
        // Remove entries older than retentionDays
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);
        history = history.Where(e => DateTimeOffset.TryParse(e["timestamp"]?.ToString(), out var t) && t >= cutoff).ToList();
        var arr = new JsonArray(history.ToArray());
        File.WriteAllText(file, arr.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    // Load schedule history for a user
    public static JsonArray Load(string userId, string? baseDir = null)
    {
        baseDir ??= "data";
        var file = Path.Combine(baseDir, "schedule_history", userId, "history.json");
        if (!File.Exists(file)) return new JsonArray();
        var json = File.ReadAllText(file);
        return JsonNode.Parse(json) as JsonArray ?? new JsonArray();
    }
}
