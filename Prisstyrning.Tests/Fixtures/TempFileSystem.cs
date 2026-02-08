using Microsoft.Extensions.Configuration;

namespace Prisstyrning.Tests.Fixtures;

/// <summary>
/// Provides an isolated temporary file system for testing with automatic cleanup.
/// Each test gets a unique directory structure that mimics the production layout.
/// </summary>
public class TempFileSystem : IDisposable
{
    public string BaseDir { get; }
    public string TokensDir => Path.Combine(BaseDir, "tokens");
    public string HistoryDir => Path.Combine(BaseDir, "schedule_history");
    public string NordpoolDir => Path.Combine(BaseDir, "nordpool");
    
    private bool _disposed = false;

    public TempFileSystem()
    {
        BaseDir = Path.Combine(Path.GetTempPath(), $"prisstyrning-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(BaseDir);
        Directory.CreateDirectory(TokensDir);
        Directory.CreateDirectory(HistoryDir);
        Directory.CreateDirectory(NordpoolDir);
    }

    /// <summary>
    /// Creates a test configuration with this temporary file system as Storage:Directory.
    /// </summary>
    public IConfiguration GetTestConfig(Dictionary<string, string?>? additionalSettings = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Storage:Directory"] = BaseDir,
            ["Schedule:ComfortHours"] = "3",
            ["Schedule:TurnOffPercentile"] = "0.9",
            ["Schedule:MaxComfortGapHours"] = "28",
            ["Schedule:MaxActivationsPerDay"] = "4",
            ["Schedule:HistoryRetentionDays"] = "30",
            ["Price:Nordpool:DefaultZone"] = "SE3",
            ["Price:Nordpool:Currency"] = "SEK",
        };

        if (additionalSettings != null)
        {
            foreach (var kvp in additionalSettings)
            {
                settings[kvp.Key] = kvp.Value;
            }
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
    }

    /// <summary>
    /// Creates a user.json file for a specific user with the given settings.
    /// Issue #53: Removed turnOffMaxConsecutive - no longer needed with 2-mode system.
    /// </summary>
    public void CreateUserSettings(string userId, int? comfortHours = null, double? turnOffPercentile = null, 
        int? maxComfortGapHours = null, string? zone = null)
    {
        var userDir = Path.Combine(TokensDir, userId);
        Directory.CreateDirectory(userDir);
        var userFile = Path.Combine(userDir, "user.json");

        var userSettings = new System.Text.Json.Nodes.JsonObject();
        if (comfortHours.HasValue) userSettings["ComfortHours"] = comfortHours.Value;
        if (turnOffPercentile.HasValue) userSettings["TurnOffPercentile"] = turnOffPercentile.Value;
        if (maxComfortGapHours.HasValue) userSettings["MaxComfortGapHours"] = maxComfortGapHours.Value;
        if (zone != null) userSettings["zone"] = zone;

        File.WriteAllText(userFile, userSettings.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Gets the path to a user's history file.
    /// </summary>
    public string GetHistoryPath(string userId) => Path.Combine(HistoryDir, userId, "history.json");

    /// <summary>
    /// Gets the path to a user's user.json file.
    /// </summary>
    public string GetUserJsonPath(string userId) => Path.Combine(TokensDir, userId, "user.json");

    public void Dispose()
    {
        if (_disposed) return;
        
        try
        {
            if (Directory.Exists(BaseDir))
            {
                Directory.Delete(BaseDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup - don't throw from Dispose
        }
        
        _disposed = true;
    }
}
