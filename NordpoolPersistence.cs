using System.Text.Json;
using System.Text.Json.Nodes;

internal static class NordpoolPersistence
{
    public static void Save(string zone, JsonArray today, JsonArray tomorrow, string? baseDir = null)
    {
        try
        {
            zone = zone.Trim().ToUpperInvariant();
            baseDir ??= "data";
            var dir = Path.Combine(baseDir, "nordpool", zone);
            Directory.CreateDirectory(dir);
            var snapshot = new JsonObject
            {
                ["zone"] = zone,
                ["savedAt"] = DateTimeOffset.UtcNow.ToString("o"),
                ["today"] = today.DeepClone() as JsonArray ?? new JsonArray(),
                ["tomorrow"] = tomorrow.DeepClone() as JsonArray ?? new JsonArray()
            };
            var json = snapshot.ToJsonString(new JsonSerializerOptions{ WriteIndented = true });
            var file = Path.Combine(dir, $"prices-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(file, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NordpoolPersistence] save failed zone={zone}: {ex.Message}");
        }
    }

    public static string? GetLatestFile(string zone, string? baseDir = null)
    {
        zone = zone.Trim().ToUpperInvariant();
        baseDir ??= "data";
        var dir = Path.Combine(baseDir, "nordpool", zone);
        if (!Directory.Exists(dir)) return null;
        return Directory.GetFiles(dir, "prices-*.json").OrderByDescending(f => f).FirstOrDefault();
    }
}
