using Microsoft.Extensions.Configuration;

internal static class UserSettingsService
{
    internal sealed record UserScheduleSettings(int ComfortHours, double TurnOffPercentile, int TurnOffMaxConsecutive);

    public static UserScheduleSettings LoadScheduleSettings(IConfiguration cfg, string? userId)
    {
        // Defaults from global config
        int comfortHours = int.TryParse(cfg["Schedule:ComfortHours"], out var ch) ? Math.Clamp(ch, 1, 12) : 3;
        double turnOffPercentile = double.TryParse(cfg["Schedule:TurnOffPercentile"], out var tp) ? Math.Clamp(tp, 0.5, 0.99) : 0.9;
        int turnOffMaxConsecutive = int.TryParse(cfg["Schedule:TurnOffMaxConsecutive"], out var mc) ? Math.Clamp(mc, 1, 6) : 2;

        if (!string.IsNullOrWhiteSpace(userId))
        {
            try
            {
                var path = StoragePaths.GetUserJsonPath(cfg, userId);
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var node = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonObject;
                    if (node != null)
                    {
                        if (int.TryParse(node["ComfortHours"]?.ToString(), out var chUser)) comfortHours = Math.Clamp(chUser, 1, 12);
                        if (double.TryParse(node["TurnOffPercentile"]?.ToString(), out var tpUser)) turnOffPercentile = Math.Clamp(tpUser, 0.5, 0.99);
                        if (int.TryParse(node["TurnOffMaxConsecutive"]?.ToString(), out var mcUser)) turnOffMaxConsecutive = Math.Clamp(mcUser, 1, 6);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UserSettings] Failed to load schedule settings for {userId}: {ex.Message}");
            }
        }

        return new UserScheduleSettings(comfortHours, turnOffPercentile, turnOffMaxConsecutive);
    }
    public static async Task<string> GetUserZoneAsync(IConfiguration cfg, string? userId)
    {
        var def = cfg["Price:Nordpool:DefaultZone"] ?? "SE3";
        if (string.IsNullOrWhiteSpace(userId)) return def;
        try
        {
            var path = StoragePaths.GetUserJsonPath(cfg, userId);
            if (File.Exists(path))
            {
                var json = await File.ReadAllTextAsync(path);
                var node = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonObject;
                var z = node?["zone"]?.ToString()?.Trim();
                if (IsValidZone(z)) return z!;
            }
        }
        catch (Exception ex) 
        { 
            Console.WriteLine($"[UserSettings] Failed to load user zone for {userId}: {ex.Message}");
        }
        return def;
    }

    public static string GetUserZone(IConfiguration cfg, string? userId)
    {
        return GetUserZoneAsync(cfg, userId).GetAwaiter().GetResult();
    }

    public static async Task<bool> SetUserZoneAsync(string? userId, string zone)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;
        if (!IsValidZone(zone)) return false;
        try
        {
            var dir = StoragePaths.GetUserTokenDir(userId, null); // null config uses default
            Directory.CreateDirectory(dir);
            var path = StoragePaths.GetUserJsonPath(null, userId); // null config uses default
            System.Text.Json.Nodes.JsonObject node;
            if (File.Exists(path))
            {
                var json = await File.ReadAllTextAsync(path);
                node = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
            }
            else node = new System.Text.Json.Nodes.JsonObject();
            node["zone"] = zone.Trim().ToUpperInvariant();
            await File.WriteAllTextAsync(path, node.ToJsonString(new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
            return true;
        }
        catch (Exception ex) 
        { 
            Console.WriteLine($"[UserSettings] Failed to set user zone for {userId}: {ex.Message}");
            return false; 
        }
    }

    public static bool SetUserZone(string? userId, string zone)
    {
        return SetUserZoneAsync(userId, zone).GetAwaiter().GetResult();
    }

    public static bool IsValidZone(string? z)
    {
        if (string.IsNullOrWhiteSpace(z)) return false;
        z = z.Trim().ToUpperInvariant();
        // Simple pattern check (Nordpool typical: SE1-SE4, NO1-NO5, DK1, DK2, FI, EE, LV, LT, etc.)
        if (System.Text.RegularExpressions.Regex.IsMatch(z, "^(SE[1-4]|NO[1-9]|DK[12]|FI|EE|LV|LT)$")) return true;
        return false;
    }
}
