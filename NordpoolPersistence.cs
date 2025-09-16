using System.Text.Json;
using System.Text.Json.Nodes;

internal static class NordpoolPersistence
{
    public static async Task SaveAsync(string zone, JsonArray today, JsonArray tomorrow, string? baseDir = null)
    {
        try
        {
            zone = zone.Trim().ToUpperInvariant();
            var cfg = new ConfigurationBuilder().AddJsonFile("appsettings.json", optional: true).Build();
            var dir = Path.Combine(StoragePaths.GetNordpoolDir(cfg), zone);
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"prices-{DateTimeOffset.UtcNow:yyyyMMdd}-{zone}.json");
            if (File.Exists(file))
            {
                // Filen finns redan, ingen ny skrivning behövs
                Console.WriteLine($"[Persist] cache hit: {file}");
                return;
            }
            var snapshot = new JsonObject
            {
                ["zone"] = zone,
                ["savedAt"] = DateTimeOffset.UtcNow.ToString("o"),
                ["today"] = today.DeepClone() as JsonArray ?? new JsonArray(),
                ["tomorrow"] = tomorrow.DeepClone() as JsonArray ?? new JsonArray()
            };
            var json = snapshot.ToJsonString(new JsonSerializerOptions{ WriteIndented = true });
            await File.WriteAllTextAsync(file, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NordpoolPersistence] save failed zone={zone}: {ex.Message}");
        }
    }

    public static void Save(string zone, JsonArray today, JsonArray tomorrow, string? baseDir = null)
    {
        SaveAsync(zone, today, tomorrow, baseDir).GetAwaiter().GetResult();
    }

    public static string? GetLatestFile(string zone, string? baseDir = null)
    {
        zone = zone.Trim().ToUpperInvariant();
    var cfg = new ConfigurationBuilder().AddJsonFile("appsettings.json", optional: true).Build();
    var dir = Path.Combine(StoragePaths.GetNordpoolDir(cfg), zone);
        if (!Directory.Exists(dir)) return null;
        // Endast filer med formatet prices-YYYYMMDD-ZON.json (ignorera tid)
        var files = Directory.GetFiles(dir, $"prices-???????.json");
        // Extra filter: matchar exakt prices-YYYYMMDD-ZON.json
        var validFiles = files.Where(f => {
            var name = Path.GetFileName(f);
            return name == $"prices-{DateTime.UtcNow:yyyyMMdd}-{zone}.json" ||
                System.Text.RegularExpressions.Regex.IsMatch(name, $"^prices-\\d{{8}}-{zone}\\.json$");
        }).OrderByDescending(f => f).ToList();
        return validFiles.FirstOrDefault();
    }
}
