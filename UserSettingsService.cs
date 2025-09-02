using Microsoft.Extensions.Configuration;

internal static class UserSettingsService
{
    public static string GetUserZone(IConfiguration cfg, string? userId)
    {
        var def = cfg["Price:Nordpool:DefaultZone"] ?? "SE3";
        if (string.IsNullOrWhiteSpace(userId)) return def;
        try
        {
            var path = Path.Combine("tokens", userId, "user.json");
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var node = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonObject;
                var z = node?["zone"]?.ToString()?.Trim();
                if (IsValidZone(z)) return z;
            }
        }
        catch { }
        return def;
    }

    public static bool SetUserZone(string? userId, string zone)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;
        if (!IsValidZone(zone)) return false;
        try
        {
            var dir = Path.Combine("tokens", userId);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "user.json");
            System.Text.Json.Nodes.JsonObject node;
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                node = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
            }
            else node = new System.Text.Json.Nodes.JsonObject();
            node["zone"] = zone.Trim().ToUpperInvariant();
            File.WriteAllText(path, node.ToJsonString(new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
            return true;
        }
        catch { return false; }
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
