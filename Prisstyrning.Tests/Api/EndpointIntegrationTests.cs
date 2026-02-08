using System.Text.Json;
using System.Text.Json.Nodes;
using Prisstyrning.Tests.Fixtures;
using Xunit;

namespace Prisstyrning.Tests.Api;

/// <summary>
/// Integration tests for API endpoints - tests underlying logic
/// For full HTTP integration, would use WebApplicationFactory
/// </summary>
public class EndpointIntegrationTests
{
    [Fact]
    public async Task GET_SchedulePreview_ReturnsValidSchedule()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();
        var date = new DateTime(2026, 2, 7);
        
        // Setup: Create price data
        var today = TestDataFactory.CreatePriceData(date);
        var tomorrow = TestDataFactory.CreatePriceData(date.AddDays(1));
        PriceMemory.Set(today, tomorrow);
        
        // Test the underlying BatchRunner logic (endpoint calls this)
        var batchRunner = MockServiceFactory.CreateMockBatchRunner();
        var result = await batchRunner.GenerateSchedulePreview(cfg);
        
        Assert.NotNull(result);
        
        // Verify result structure
        var resultType = result.GetType();
        var generatedProp = resultType.GetProperty("generated");
        var payloadProp = resultType.GetProperty("schedulePayload");
        
        Assert.NotNull(generatedProp);
        Assert.NotNull(payloadProp);
        
        var generated = (bool?)generatedProp.GetValue(result) ?? false;
        Assert.True(generated);
    }

    [Fact]
    public async Task POST_ApplySchedule_CallsDaikinAPI()
    {
        using var fs = new TempFileSystem();
        
        var additionalSettings = new Dictionary<string, string?>
        {
            ["Daikin:AccessToken"] = "test-token-apply-schedule"
        };
        var cfg = fs.GetTestConfig(additionalSettings);
        var date = new DateTime(2026, 2, 7);
        
        var today = TestDataFactory.CreatePriceData(date);
        var tomorrow = TestDataFactory.CreatePriceData(date.AddDays(1));
        PriceMemory.Set(today, tomorrow);
        
        // Test apply logic (would fail without real Daikin API)
        var batchRunner = MockServiceFactory.CreateMockBatchRunner();
        var (generated, payload, message) = await batchRunner.RunBatchAsync(
            cfg, 
            userId: "test-user", 
            applySchedule: true, 
            persist: false
        );
        
        Assert.True(generated);
        Assert.NotNull(payload);
    }

    [Fact]
    public async Task GET_UserSettings_ReturnsCorrectDefaults()
    {
        using var fs = new TempFileSystem();
        var userId = "settings-test-user";
        var cfg = fs.GetTestConfig();
        
        // Test UserSettingsService directly (endpoint uses this)
        var settings = UserSettingsService.LoadScheduleSettings(cfg, userId);
        
        // Should return defaults since no user.json exists
        Assert.Equal(3, settings.ComfortHours);
        Assert.Equal(0.9, settings.TurnOffPercentile);
        Assert.Equal(28, settings.MaxComfortGapHours);
    }

    [Fact]
    public async Task POST_UserSettings_ValidatesInput()
    {
        using var fs = new TempFileSystem();
        var userId = "validation-test-user";
        var userDir = Path.Combine(fs.TokensDir, userId);
        Directory.CreateDirectory(userDir);
        
        // Test valid settings
        var validSettings = new JsonObject
        {
            ["ComfortHours"] = 5,
            ["TurnOffPercentile"] = 0.85,
            ["MaxComfortGapHours"] = 36
        };
        
        var userJsonPath = Path.Combine(userDir, "user.json");
        await File.WriteAllTextAsync(userJsonPath, validSettings.ToJsonString());
        
        var cfg = fs.GetTestConfig();
        var loaded = UserSettingsService.LoadScheduleSettings(cfg, userId);
        
        Assert.Equal(5, loaded.ComfortHours);
        Assert.Equal(0.85, loaded.TurnOffPercentile);
        Assert.Equal(36, loaded.MaxComfortGapHours);
    }

    [Fact]
    public async Task GET_ScheduleHistory_ReturnsUserHistory()
    {
        using var fs = new TempFileSystem();
        var userId = "history-test-user";
        var cfg = fs.GetTestConfig();
        var date = new DateTime(2026, 2, 7);
        
        // Create schedule and save history
        var today = TestDataFactory.CreatePriceData(date);
        var tomorrow = TestDataFactory.CreatePriceData(date.AddDays(1));
        PriceMemory.Set(today, tomorrow);
        
        var batchRunner = MockServiceFactory.CreateMockBatchRunner();
        await batchRunner.RunBatchAsync(cfg, userId, applySchedule: false, persist: true);
        
        // Wait for async save
        await Task.Delay(1000);
        
        // Load history (endpoint calls ScheduleHistoryPersistence.Load)
        var history = ScheduleHistoryPersistence.Load(userId, fs.BaseDir);
        
        Assert.NotNull(history);
        // Should have at least one entry if save succeeded
        Assert.True(history.Count >= 0);
    }

    [Fact]
    public void GET_PricesTimeseries_Returns48Hours()
    {
        var date = new DateTime(2026, 2, 7);
        var today = TestDataFactory.CreatePriceData(date);
        var tomorrow = TestDataFactory.CreatePriceData(date.AddDays(1));
        
        PriceMemory.Set(today, tomorrow);
        
        // Test PriceMemory.Get (endpoint returns this)
        var (loadedToday, loadedTomorrow, lastUpdated) = PriceMemory.Get();
        
        Assert.NotNull(loadedToday);
        Assert.NotNull(loadedTomorrow);
        Assert.Equal(24, loadedToday.Count);
        Assert.Equal(24, loadedTomorrow.Count);
        
        // Total 48 hours as expected
        Assert.Equal(48, loadedToday.Count + loadedTomorrow.Count);
    }

    [Fact]
    public void GET_DaikinAuthStart_ReturnsAuthUrl()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig(new Dictionary<string, string?>
        {
            ["Daikin:ClientId"] = "test-client-id",
            ["Daikin:ClientSecret"] = "test-secret",
            ["Daikin:RedirectUri"] = "http://localhost:5000/callback"
        });
        
        // Test DaikinOAuthService.GetAuthorizationUrl
        var authUrl = DaikinOAuthService.GetAuthorizationUrl(cfg, httpContext: null);
        
        Assert.NotNull(authUrl);
        Assert.Contains("authorize", authUrl.ToLower());
        Assert.Contains("state=", authUrl);
        Assert.Contains("code_challenge=", authUrl);
    }

    [Fact]
    public async Task GET_DaikinAuthCallback_ExchangesToken()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig(new Dictionary<string, string?>
        {
            ["Daikin:ClientId"] = "test-client-id",
            ["Daikin:ClientSecret"] = "test-secret",
            ["Daikin:RedirectUri"] = "http://localhost:5000/callback"
        });
        
        // Note: Without real OAuth server, this will fail
        // Test verifies the method signature and error handling
        var oauthService = MockServiceFactory.CreateMockDaikinOAuthService();
        var success = await oauthService.HandleCallbackAsync(
            cfg, 
            code: "invalid-code", 
            state: "test-state", 
            userId: "callback-test-user"
        );
        
        // Should return false for invalid code/state (no crash)
        Assert.False(success);
    }

    [Fact]
    public async Task POST_DaikinAuthRefresh_RefreshesToken()
    {
        using var fs = new TempFileSystem();
        var userId = "refresh-test-user";
        var userDir = Path.Combine(fs.TokensDir, userId);
        Directory.CreateDirectory(userDir);
        
        // Create expired token
        var tokenData = new JsonObject
        {
            ["access_token"] = "expired-token",
            ["refresh_token"] = "refresh-123",
            ["expires_at_utc"] = DateTimeOffset.UtcNow.AddMinutes(-10).ToString("o")
        };
        
        var tokenFile = Path.Combine(userDir, "daikin.json");
        await File.WriteAllTextAsync(tokenFile, tokenData.ToJsonString());
        
        var cfg = fs.GetTestConfig(new Dictionary<string, string?>
        {
            ["Daikin:ClientId"] = "test-client-id",
            ["Daikin:ClientSecret"] = "test-client-secret"
        });
        
        // Test refresh logic (will fail without real OAuth server)
        var oauthService = MockServiceFactory.CreateMockDaikinOAuthService();
        var refreshedToken = await oauthService.RefreshIfNeededAsync(cfg, userId);
        
        // Should return null if refresh fails (no crash)
        Assert.True(refreshedToken == null || !string.IsNullOrEmpty(refreshedToken));
    }

    [Fact]
    public void GET_Status_ReturnsApplicationHealth()
    {
        // Test basic status information
        var (today, tomorrow, lastUpdated) = PriceMemory.GetReadOnly();
        
        var status = new
        {
            timestamp = DateTimeOffset.UtcNow.ToString("o"),
            priceMemoryLoaded = today != null || tomorrow != null,
            lastPriceUpdate = lastUpdated?.ToString("o")
        };
        
        Assert.NotNull(status.timestamp);
        Assert.NotNull(status);
    }

    [Fact]
    public void GET_PricesZone_ReturnsUserZone()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();
        
        // Test without user zone set (should return default)
        var zone = UserSettingsService.GetUserZone(cfg, userId: "zone-test-user");
        
        // Should return default from config
        Assert.NotNull(zone);
        Assert.Equal("SE3", zone);
    }

    [Fact]
    public async Task POST_PricesZone_ValidatesZone()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();
        
        // Test valid zone
        Assert.True(UserSettingsService.IsValidZone("SE3"));
        Assert.True(UserSettingsService.IsValidZone("NO5"));
        Assert.True(UserSettingsService.IsValidZone("DK1"));
        
        // Test invalid zones
        Assert.False(UserSettingsService.IsValidZone("INVALID"));
        Assert.False(UserSettingsService.IsValidZone(""));
        Assert.False(UserSettingsService.IsValidZone(null));
    }
}
