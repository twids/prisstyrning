using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

internal static class AdminService
{
    private static readonly SemaphoreSlim _lock = new(1, 1);

    private const int MaxUserIdLength = 100;

    /// <summary>
    /// Validates that a userId is well-formed: non-empty, within length limit, and contains only safe characters.
    /// </summary>
    public static bool IsValidUserId(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;
        if (userId.Length > MaxUserIdLength) return false;
        return userId.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_');
    }

    public static bool IsAdmin(IConfiguration cfg, string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;
        var admins = GetAdminUserIds(cfg);
        return admins.Contains(userId);
    }

    public static async Task GrantAdmin(IConfiguration cfg, string userId)
    {
        await _lock.WaitAsync();
        try
        {
            var data = LoadAdminJson(cfg);
            var admins = GetListFromJson(data, "adminUserIds");
            if (!admins.Contains(userId))
            {
                admins.Add(userId);
                SetListInJson(data, "adminUserIds", admins);
                await SaveAdminJson(cfg, data);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public static async Task RevokeAdmin(IConfiguration cfg, string userId)
    {
        await _lock.WaitAsync();
        try
        {
            var data = LoadAdminJson(cfg);
            var admins = GetListFromJson(data, "adminUserIds");
            if (admins.Remove(userId))
            {
                SetListInJson(data, "adminUserIds", admins);
                await SaveAdminJson(cfg, data);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public static List<string> GetAdminUserIds(IConfiguration cfg)
    {
        var data = LoadAdminJson(cfg);
        return GetListFromJson(data, "adminUserIds");
    }

    public static bool HasHangfireAccess(IConfiguration cfg, string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;
        var hangfireUsers = GetHangfireUserIds(cfg);
        return hangfireUsers.Contains(userId);
    }

    public static async Task GrantHangfireAccess(IConfiguration cfg, string userId)
    {
        await _lock.WaitAsync();
        try
        {
            var data = LoadAdminJson(cfg);
            var hangfireUsers = GetListFromJson(data, "hangfireUserIds");
            if (!hangfireUsers.Contains(userId))
            {
                hangfireUsers.Add(userId);
                SetListInJson(data, "hangfireUserIds", hangfireUsers);
                await SaveAdminJson(cfg, data);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public static async Task RevokeHangfireAccess(IConfiguration cfg, string userId)
    {
        await _lock.WaitAsync();
        try
        {
            var data = LoadAdminJson(cfg);
            var hangfireUsers = GetListFromJson(data, "hangfireUserIds");
            if (hangfireUsers.Remove(userId))
            {
                SetListInJson(data, "hangfireUserIds", hangfireUsers);
                await SaveAdminJson(cfg, data);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public static List<string> GetHangfireUserIds(IConfiguration cfg)
    {
        var data = LoadAdminJson(cfg);
        return GetListFromJson(data, "hangfireUserIds");
    }

    /// <summary>
    /// Combined admin access check: returns true if userId is in admin.json OR X-Admin-Password header matches configured password.
    /// </summary>
    public static (bool isAdmin, string? reason) CheckAdminAccess(IConfiguration cfg, string? userId, string? passwordHeader)
    {
        // Check persisted admin via userId
        if (!string.IsNullOrEmpty(userId) && IsAdmin(cfg, userId))
            return (true, null);

        // Check password header
        var configuredPassword = cfg["Admin:Password"];
        if (string.IsNullOrEmpty(configuredPassword))
            return (false, "No admin password configured");

        if (!string.IsNullOrEmpty(passwordHeader) && SecureCompare(passwordHeader, configuredPassword))
            return (true, null);

        return (false, "Unauthorized");
    }

    /// <summary>
    /// Constant-time string comparison using fixed-length hashes to prevent timing attacks on password checks.
    /// </summary>
    public static bool SecureCompare(string? a, string? b)
    {
        if (a == null || b == null) return false;

        // Compare fixed-length SHA-256 hashes of the UTF-8 bytes to avoid length-based timing leaks
        var aBytes = System.Text.Encoding.UTF8.GetBytes(a);
        var bBytes = System.Text.Encoding.UTF8.GetBytes(b);

        Span<byte> aHash = stackalloc byte[32];
        Span<byte> bHash = stackalloc byte[32];
        SHA256.HashData(aBytes, aHash);
        SHA256.HashData(bBytes, bHash);

        return CryptographicOperations.FixedTimeEquals(aHash, bHash);
    }

    /// <summary>
    /// Returns a validated storage directory path from configuration.
    /// Rejects values containing path traversal sequences.
    /// </summary>
    private static string GetSafeStorageDirectory(IConfiguration cfg)
    {
        var baseDir = cfg["Storage:Directory"] ?? "data";

        // Reject path traversal sequences in the configured value
        if (baseDir.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException("Storage directory must not contain '..' path traversal sequences");

        return Path.GetFullPath(baseDir);
    }

    private static string GetAdminJsonPath(IConfiguration cfg)
    {
        var safeDir = GetSafeStorageDirectory(cfg);
        return Path.Combine(safeDir, "admin.json");
    }

    private static JsonObject LoadAdminJson(IConfiguration cfg)
    {
        var safeDir = GetSafeStorageDirectory(cfg);
        var safePath = Path.Combine(safeDir, "admin.json");
        if (!File.Exists(safePath)) return new JsonObject();

        try
        {
            var json = File.ReadAllText(safePath);
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AdminService] Failed to load admin data: {ex.Message}");
            return new JsonObject();
        }
    }

    private static List<string> GetListFromJson(JsonObject data, string key)
    {
        var arr = data[key] as JsonArray;
        if (arr == null) return new List<string>();
        return arr.Select(n => n?.ToString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
    }

    private static void SetListInJson(JsonObject data, string key, List<string> values)
    {
        data[key] = new JsonArray(values.Select(id => (JsonNode)JsonValue.Create(id)!).ToArray());
    }

    private static async Task SaveAdminJson(IConfiguration cfg, JsonObject data)
    {
        var safeDir = GetSafeStorageDirectory(cfg);
        var safePath = Path.Combine(safeDir, "admin.json");
        if (!Directory.Exists(safeDir)) Directory.CreateDirectory(safeDir);

        await File.WriteAllTextAsync(safePath, data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
