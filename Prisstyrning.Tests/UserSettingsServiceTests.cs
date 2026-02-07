using Microsoft.Extensions.Configuration;
using Prisstyrning.Tests.Fixtures;
using Xunit;

namespace Prisstyrning.Tests;

/// <summary>
/// Tests for UserSettingsService - manages user-specific schedule settings and zone preferences.
/// Tests cover default fallbacks, user overrides, validation, and corruption handling.
/// </summary>
public class UserSettingsServiceTests
{
    /// <summary>
    /// When a user has no custom settings, should return global defaults from configuration.
    /// </summary>
    [Fact]
    public void LoadScheduleSettings_WithDefaults_ReturnsGlobalConfig()
    {
        // Arrange
        using var fs = new TempFileSystem();
        var config = fs.GetTestConfig(new Dictionary<string, string?>
        {
            ["Schedule:ComfortHours"] = "4",
            ["Schedule:TurnOffPercentile"] = "0.85",
            ["Schedule:MaxComfortGapHours"] = "24"
        });
        var userId = "user-with-no-settings";

        // Act
        var settings = UserSettingsService.LoadScheduleSettings(config, userId);

        // Assert
        Assert.Equal(4, settings.ComfortHours);
        Assert.Equal(0.85, settings.TurnOffPercentile);
        Assert.Equal(24, settings.MaxComfortGapHours);
    }

    /// <summary>
    /// When a user has custom settings in user.json, those should override global config.
    /// </summary>
    [Fact]
    public void LoadScheduleSettings_WithUserOverrides_ReturnsUserConfig()
    {
        // Arrange
        using var fs = new TempFileSystem();
        var config = fs.GetTestConfig();
        var userId = "user-with-overrides";
        
        // Create user.json with custom settings
        fs.CreateUserSettings(userId, comfortHours: 5, turnOffPercentile: 0.95, 
            maxComfortGapHours: 36);

        // Act
        var settings = UserSettingsService.LoadScheduleSettings(config, userId);

        // Assert
        Assert.Equal(5, settings.ComfortHours);
        Assert.Equal(0.95, settings.TurnOffPercentile);
        Assert.Equal(36, settings.MaxComfortGapHours);
    }

    /// <summary>
    /// Invalid values should be clamped to valid ranges to prevent broken schedules.
    /// </summary>
    [Fact]
    public void LoadScheduleSettings_WithInvalidValues_ClampsToValidRange()
    {
        // Arrange
        using var fs = new TempFileSystem();
        var config = fs.GetTestConfig(new Dictionary<string, string?>
        {
            ["Schedule:ComfortHours"] = "999", // Too high, should clamp to 12
            ["Schedule:TurnOffPercentile"] = "1.5", // Too high, should clamp to 0.99
            ["Schedule:MaxComfortGapHours"] = "-10" // Negative, should clamp to 1
        });

        // Act
        var settings = UserSettingsService.LoadScheduleSettings(config, null);

        // Assert
        Assert.Equal(12, settings.ComfortHours); // Clamped from 999
        Assert.Equal(0.99, settings.TurnOffPercentile); // Clamped from 1.5
        Assert.Equal(1, settings.MaxComfortGapHours); // Clamped from -10
    }

    /// <summary>
    /// When user has no zone file, should return the default zone from global config.
    /// </summary>
    [Fact]
    public void GetUserZone_WithNoUserFile_ReturnsDefaultZone()
    {
        // Arrange
        using var fs = new TempFileSystem();
        var config = fs.GetTestConfig(new Dictionary<string, string?>
        {
            ["Price:Nordpool:DefaultZone"] = "NO2"
        });
        var userId = "user-no-zone";

        // Act
        var zone = UserSettingsService.GetUserZone(config, userId);

        // Assert
        Assert.Equal("NO2", zone);
    }

    /// <summary>
    /// Setting a valid zone should persist to user.json correctly.
    /// </summary>
    [Fact]
    public async Task SetUserZone_WithValidZone_PersistsCorrectly()
    {
        // Arrange
        using var fs = new TempFileSystem();
        var config = fs.GetTestConfig();
        var userId = "user-set-zone";

        // Act
        var result = await UserSettingsService.SetUserZoneAsync(config, userId, "NO5");

        // Assert
        Assert.True(result);
        
        // Verify it was saved
        var zone = UserSettingsService.GetUserZone(config, userId);
        Assert.Equal("NO5", zone);
        
        // Verify file exists
        var userFile = fs.GetUserJsonPath(userId);
        Assert.True(File.Exists(userFile));
    }

    /// <summary>
    /// Setting an invalid zone should return false and not create/modify files.
    /// </summary>
    [Fact]
    public async Task SetUserZone_WithInvalidZone_ReturnsFalse()
    {
        // Arrange
        using var fs = new TempFileSystem();
        var userId = "user-invalid-zone";

        // Act
        var result1 = await UserSettingsService.SetUserZoneAsync(null, userId, "INVALID");
        var result2 = await UserSettingsService.SetUserZoneAsync(null, userId, "SE99"); // Non-existent zone
        var result3 = await UserSettingsService.SetUserZoneAsync(null, userId, ""); // Empty string

        // Assert
        Assert.False(result1);
        Assert.False(result2);
        Assert.False(result3);
    }

    /// <summary>
    /// All valid Nordic zones should be accepted by validation.
    /// </summary>
    [Fact]
    public void IsValidZone_WithAllNordicZones_ReturnsTrue()
    {
        // Arrange & Act & Assert
        // Swedish zones
        Assert.True(UserSettingsService.IsValidZone("SE1"));
        Assert.True(UserSettingsService.IsValidZone("SE2"));
        Assert.True(UserSettingsService.IsValidZone("SE3"));
        Assert.True(UserSettingsService.IsValidZone("SE4"));
        
        // Norwegian zones
        Assert.True(UserSettingsService.IsValidZone("NO1"));
        Assert.True(UserSettingsService.IsValidZone("NO2"));
        Assert.True(UserSettingsService.IsValidZone("NO3"));
        Assert.True(UserSettingsService.IsValidZone("NO4"));
        Assert.True(UserSettingsService.IsValidZone("NO5"));
        
        // Danish zones
        Assert.True(UserSettingsService.IsValidZone("DK1"));
        Assert.True(UserSettingsService.IsValidZone("DK2"));
        
        // Baltic zones
        Assert.True(UserSettingsService.IsValidZone("FI"));
        Assert.True(UserSettingsService.IsValidZone("EE"));
        Assert.True(UserSettingsService.IsValidZone("LV"));
        Assert.True(UserSettingsService.IsValidZone("LT"));
        
        // Case insensitive
        Assert.True(UserSettingsService.IsValidZone("se3"));
        Assert.True(UserSettingsService.IsValidZone("No2"));
        
        // Invalid zones
        Assert.False(UserSettingsService.IsValidZone("SE5")); // Doesn't exist
        Assert.False(UserSettingsService.IsValidZone("UK1")); // Wrong country
        Assert.False(UserSettingsService.IsValidZone(null));
        Assert.False(UserSettingsService.IsValidZone(""));
        Assert.False(UserSettingsService.IsValidZone("   "));
    }

    /// <summary>
    /// If user.json is corrupted, should fall back to defaults without crashing.
    /// </summary>
    [Fact]
    public void LoadScheduleSettings_WithCorruptUserJson_FallsBackToDefaults()
    {
        // Arrange
        using var fs = new TempFileSystem();
        var config = fs.GetTestConfig();
        var userId = "user-corrupt-json";
        
        // Create corrupt user.json
        var userDir = Path.Combine(fs.TokensDir, userId);
        Directory.CreateDirectory(userDir);
        var userFile = Path.Combine(userDir, "user.json");
        File.WriteAllText(userFile, "{ this is not valid json at all [[[");

        // Act - Should not crash
        var settings = UserSettingsService.LoadScheduleSettings(config, userId);

        // Assert - Should return defaults from config
        Assert.Equal(3, settings.ComfortHours); // Default from test config
        Assert.Equal(0.9, settings.TurnOffPercentile);
        Assert.Equal(28, settings.MaxComfortGapHours);
    }

    /// <summary>
    /// Partial user settings should merge with global defaults (only override specified values).
    /// </summary>
    [Fact]
    public void LoadScheduleSettings_WithPartialUserSettings_MergesWithDefaults()
    {
        // Arrange
        using var fs = new TempFileSystem();
        var config = fs.GetTestConfig();
        var userId = "user-partial";
        
        // Create user.json with only ComfortHours set
        fs.CreateUserSettings(userId, comfortHours: 6); // Only override comfort hours

        // Act
        var settings = UserSettingsService.LoadScheduleSettings(config, userId);

        // Assert
        Assert.Equal(6, settings.ComfortHours); // User override
        Assert.Equal(0.9, settings.TurnOffPercentile); // Global default
        Assert.Equal(28, settings.MaxComfortGapHours); // Global default
    }

    /// <summary>
    /// GetUserZone should handle null userId gracefully.
    /// </summary>
    [Fact]
    public void GetUserZone_WithNullUserId_ReturnsDefault()
    {
        // Arrange
        using var fs = new TempFileSystem();
        var config = fs.GetTestConfig(new Dictionary<string, string?>
        {
            ["Price:Nordpool:DefaultZone"] = "SE1"
        });

        // Act
        var zone = UserSettingsService.GetUserZone(config, null);

        // Assert
        Assert.Equal("SE1", zone);
    }

    /// <summary>
    /// SetUserZone should return false for null userId.
    /// </summary>
    [Fact]
    public async Task SetUserZone_WithNullUserId_ReturnsFalse()
    {
        // Arrange & Act
        var result = await UserSettingsService.SetUserZoneAsync(null, null, "SE3");

        // Assert
        Assert.False(result);
    }
}
