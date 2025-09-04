using System.Text.Json.Nodes;
using System.Text.Json;

internal static class ScheduleHistoryPersistence
{
    // Async save (preferred)
    public static async Task SaveAsync(string userId, JsonObject schedulePayload, DateTimeOffset timestamp, int retentionDays = 7, string? baseDir = null)
    {
        baseDir ??= "data";
        var dir = Path.Combine(baseDir, "schedule_history", userId);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"history.json");
        List<JsonObject> history;
        if (File.Exists(file))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var node = JsonNode.Parse(json);
                var existingArray = node?.AsArray();
                var objects = existingArray?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>();
                history = objects.ToList();
            }
            catch
            {
                history = new List<JsonObject>();
            }
        }
        else history = new List<JsonObject>();
        // Add new entry
        var entry = new JsonObject
        {
            ["timestamp"] = timestamp.ToString("o"),
            ["schedule"] = schedulePayload.DeepClone() as JsonObject ?? new JsonObject(),
        };
        history.Add(entry);
        // Remove entries older than retentionDays
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);
        history = history.Where(e => DateTimeOffset.TryParse(e["timestamp"]?.ToString(), out var t) && t >= cutoff).ToList();
        var resultArray = new JsonArray();
        foreach (var item in history) resultArray.Add(item);
        await File.WriteAllTextAsync(file, resultArray.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public static async Task<JsonArray> LoadAsync(string userId, string? baseDir = null)
    {
        baseDir ??= "data";
        var file = Path.Combine(baseDir, "schedule_history", userId, "history.json");
        if (!File.Exists(file)) return new JsonArray();
        try
        {
            var json = await File.ReadAllTextAsync(file);
            return JsonNode.Parse(json) as JsonArray ?? new JsonArray();
        }
        catch { return new JsonArray(); }
    }

    // Synchronous wrappers
    public static JsonArray Load(string userId, string? baseDir = null) => LoadAsync(userId, baseDir).GetAwaiter().GetResult();
    public static void Save(string userId, JsonObject schedulePayload, DateTimeOffset timestamp, int retentionDays = 7, string? baseDir = null)
        => SaveAsync(userId, schedulePayload, timestamp, retentionDays, baseDir).GetAwaiter().GetResult();
}
