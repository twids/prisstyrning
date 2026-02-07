using Microsoft.Extensions.Configuration;

internal static class UserSettingsService
{
    // Issue #53: Removed TurnOffMaxConsecutive - no longer needed with 2-mode system (comfort/turn_off only)
    internal sealed record UserScheduleSettings(int ComfortHours, double TurnOffPercentile, int MaxComfortGapHours);

    public static UserScheduleSettings LoadScheduleSettings(IConfiguration cfg, string? userId)
    {
        // Defaults from global config - use InvariantCulture for parsing
        int comfortHours = int.TryParse(cfg["Schedule:ComfortHours"], out var ch) ? Math.Clamp(ch, 1, 12) : 3;
        double turnOffPercentile = double.TryParse(cfg["Schedule:TurnOffPercentile"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var tp) ? Math.Clamp(tp, 0.5, 0.99) : 0.9;
        int maxComfortGapHours = int.TryParse(cfg["Schedule:MaxComfortGapHours"], out var mcgh) ? Math.Clamp(mcgh, 1, 72) : 28;

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
                        if (double.TryParse(node["TurnOffPercentile"]?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var tpUser)) turnOffPercentile = Math.Clamp(tpUser, 0.5, 0.99);
                        if (int.TryParse(node["MaxComfortGapHours"]?.ToString(), out var mcghUser)) maxComfortGapHours = Math.Clamp(mcghUser, 1, 72);
                        // Note: TurnOffMaxConsecutive removed - no longer used
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UserSettings] Failed to load schedule settings for {userId}: {ex.Message}");
            }
        }

        return new UserScheduleSettings(comfortHours, turnOffPercentile, maxComfortGapHours);
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

    public static async Task<bool> SetUserZoneAsync(IConfiguration? cfg, string? userId, string zone)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;
        if (!IsValidZone(zone)) return false;
        try
        {
            var dir = StoragePaths.GetUserTokenDir(userId, cfg);
            Directory.CreateDirectory(dir);
            var path = StoragePaths.GetUserJsonPath(cfg, userId);
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

    public static bool SetUserZone(IConfiguration? cfg, string? userId, string zone)
    {
        return SetUserZoneAsync(cfg, userId, zone).GetAwaiter().GetResult();
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
