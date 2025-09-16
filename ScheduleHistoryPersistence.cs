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
        var history = new List<JsonObject>();
        if (File.Exists(file))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                if (!string.IsNullOrWhiteSpace(json) && JsonNode.Parse(json) is JsonArray existingArr)
                {
                    foreach (var n in existingArr)
                        if (n is JsonObject o) history.Add(o);
                }
            }
            catch { /* ignore corrupt history file; start fresh */ }
        }
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
        var outArr = new JsonArray();
        foreach (var h in history) outArr.Add(h);
        await File.WriteAllTextAsync(file, outArr.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
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
