using Microsoft.Extensions.Configuration;

internal static class StoragePaths
{
    public static string GetUserTokenDir(string userId, IConfiguration? config)
    {
        var cfg = config ?? new ConfigurationBuilder().Build();
        return Path.Combine(GetTokensDir(cfg), userId);
    }

    public static string GetUserJsonPath(IConfiguration? config, string userId)
    {
        var dir = GetUserTokenDir(userId, config);
        return Path.Combine(dir, "user.json");
    }

    public static string GetBaseDir(IConfiguration config) => config["Storage:Directory"] ?? "data";
    public static string GetTokensDir(IConfiguration config) => Path.Combine(GetBaseDir(config), "tokens");
    public static string GetScheduleHistoryDir(IConfiguration config) => Path.Combine(GetBaseDir(config), "schedule_history");
    public static string GetNordpoolDir(IConfiguration config) => Path.Combine(GetBaseDir(config), "nordpool");
}
