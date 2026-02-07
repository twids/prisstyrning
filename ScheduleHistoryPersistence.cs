using System.Text.Json.Nodes;
using System.Text.Json;
using System.Collections.Concurrent;

internal static class ScheduleHistoryPersistence
{
    // File locking mechanism to prevent concurrent write conflicts
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();
    
    // Async save (preferred)
    public static async Task SaveAsync(string userId, JsonObject schedulePayload, DateTimeOffset timestamp, int retentionDays = 7, string? baseDir = null)
    {
        baseDir ??= "data";
        
        // Sanitize userId to prevent path traversal attacks
        userId = SanitizeUserId(userId);
        
        var dir = Path.Combine(baseDir, "schedule_history", userId);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"history.json");
        
        // Get or create a semaphore for this file
        var lockKey = file.ToLowerInvariant();
        var fileLock = _fileLocks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));
        
        await fileLock.WaitAsync();
        try
        {
            var history = new List<JsonObject>();
            if (File.Exists(file))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file);
                    if (!string.IsNullOrWhiteSpace(json) && JsonNode.Parse(json) is JsonArray existingArr)
                    {
                        // CRITICAL FIX: Clone existing entries to avoid "node already has a parent" errors
                        foreach (var n in existingArr)
                        {
                            if (n is JsonObject o)
                            {
                                var cloned = JsonNode.Parse(o.ToJsonString()) as JsonObject;
                                if (cloned != null) history.Add(cloned);
                            }
                        }
                    }
                }
                catch { /* ignore corrupt history file; start fresh */ }
            }
            // Add new entry
            // Create a proper deep clone by serializing and deserializing to avoid "node already has a parent" errors
            var scheduleClone = JsonNode.Parse(schedulePayload.ToJsonString()) as JsonObject ?? new JsonObject();
            var entry = new JsonObject
            {
                ["timestamp"] = timestamp.ToString("o"),
                ["schedule"] = scheduleClone,
            };
            history.Add(entry);
            // Remove entries older than retentionDays
            var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);
            history = history.Where(e => DateTimeOffset.TryParse(e["timestamp"]?.ToString(), out var t) && t >= cutoff).ToList();
            var outArr = new JsonArray();
            foreach (var h in history) outArr.Add(h);
            await File.WriteAllTextAsync(file, outArr.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            fileLock.Release();
        }
    }
    
    /// <summary>
    /// Sanitizes userId to prevent path traversal attacks by removing directory separators and special characters.
    /// </summary>
    private static string SanitizeUserId(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return "anonymous";
        
        // Get only the filename part to prevent any path traversal
        // This removes all directory separators and parent directory references
        var sanitized = Path.GetFileName(userId);
        
        // Additional safety: remove any remaining problematic characters
        sanitized = sanitized
            .Replace("..", "")
            .Replace(":", "")
            .Trim();
        
        // If nothing is left, use a fallback
        return string.IsNullOrWhiteSpace(sanitized) ? "anonymous" : sanitized;
    }

    public static async Task<JsonArray> LoadAsync(string userId, string? baseDir = null)
    {
        baseDir ??= "data";
        
        // Sanitize userId to prevent path traversal attacks
        userId = SanitizeUserId(userId);
        
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
