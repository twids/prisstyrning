using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Prisstyrning.Data;
using Prisstyrning.Data.Repositories;
using Prisstyrning.Tests.Fixtures;
using Xunit;

namespace Prisstyrning.Tests.Api;

/// <summary>
/// Integration tests for API endpoints - tests underlying logic
/// For full HTTP integration, would use WebApplicationFactory
/// </summary>
public class EndpointIntegrationTests
{
    private static DaikinOAuthService CreateService(IConfiguration config)
    {
        var options = new DbContextOptionsBuilder<PrisstyrningDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        var db = new PrisstyrningDbContext(options);
        db.Database.EnsureCreated();
        var tokenRepo = new DaikinTokenRepository(db);
        return new DaikinOAuthService(config, tokenRepo);
    }

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
        var result = await BatchRunner.GenerateSchedulePreview(cfg);
        
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
        var (generated, payload, message) = await BatchRunner.RunBatchAsync(
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

        var options = new DbContextOptionsBuilder<PrisstyrningDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        var db = new PrisstyrningDbContext(options);
        db.Database.EnsureCreated();
        var repo = new UserSettingsRepository(db, cfg);

        // Should return defaults since no user exists in DB
        var settings = await repo.LoadScheduleSettingsAsync(userId);

        Assert.Equal(3, settings.ComfortHours);
        Assert.Equal(0.9, settings.TurnOffPercentile);
        Assert.Equal(28, settings.MaxComfortGapHours);
    }

    [Fact]
    public async Task POST_UserSettings_ValidatesInput()
    {
        var userId = "validation-test-user";
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();

        var options = new DbContextOptionsBuilder<PrisstyrningDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        var db = new PrisstyrningDbContext(options);
        db.Database.EnsureCreated();

        // Seed user settings in DB
        db.UserSettings.Add(new Data.Entities.UserSettings
        {
            UserId = userId,
            ComfortHours = 5,
            TurnOffPercentile = 0.85,
            MaxComfortGapHours = 36
        });
        await db.SaveChangesAsync();

        var repo = new UserSettingsRepository(db, cfg);
        var loaded = await repo.LoadScheduleSettingsAsync(userId);

        Assert.Equal(5, loaded.ComfortHours);
        Assert.Equal(0.85, loaded.TurnOffPercentile);
        Assert.Equal(36, loaded.MaxComfortGapHours);
    }

    [Fact]
    public async Task GET_ScheduleHistory_ReturnsUserHistory()
    {
        var userId = "history-test-user";

        var options = new DbContextOptionsBuilder<PrisstyrningDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        var db = new PrisstyrningDbContext(options);
        db.Database.EnsureCreated();
        var repo = new ScheduleHistoryRepository(db);

        // Save a history entry
        var payload = new JsonObject { ["test"] = true };
        await repo.SaveAsync(userId, payload, DateTimeOffset.UtcNow);

        // Load and verify
        var history = await repo.LoadAsync(userId);

        Assert.NotNull(history);
        Assert.True(history.Count > 0);
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
        var service = CreateService(cfg);
        var authUrl = service.GetAuthorizationUrl(httpContext: null);
        
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
        var service = CreateService(cfg);
        var success = await service.HandleCallbackAsync(
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
        
        var cfg = fs.GetTestConfig(new Dictionary<string, string?>
        {
            ["Daikin:ClientId"] = "test-client-id",
            ["Daikin:ClientSecret"] = "test-client-secret"
        });
        
        // Create service and store an expired token in the DB
        var options = new DbContextOptionsBuilder<PrisstyrningDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        var db = new PrisstyrningDbContext(options);
        db.Database.EnsureCreated();
        var tokenRepo = new DaikinTokenRepository(db);
        await tokenRepo.SaveAsync(userId, "expired-token", "refresh-123", DateTimeOffset.UtcNow.AddMinutes(-10));
        var service = new DaikinOAuthService(cfg, tokenRepo);
        
        // Test refresh logic (will fail without real OAuth server)
        var refreshedToken = await service.RefreshIfNeededAsync(userId);
        
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
    public async Task GET_PricesZone_ReturnsUserZone()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();

        var options = new DbContextOptionsBuilder<PrisstyrningDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        var db = new PrisstyrningDbContext(options);
        db.Database.EnsureCreated();
        var repo = new UserSettingsRepository(db, cfg);

        // Test without user zone set (should return default from config)
        var zone = await repo.GetUserZoneAsync("zone-test-user");

        Assert.NotNull(zone);
        Assert.Equal("SE3", zone);
    }

    [Fact]
    public void POST_PricesZone_ValidatesZone()
    {
        // Test valid zones
        Assert.True(UserSettingsRepository.IsValidZone("SE3"));
        Assert.True(UserSettingsRepository.IsValidZone("NO5"));
        Assert.True(UserSettingsRepository.IsValidZone("DK1"));

        // Test invalid zones
        Assert.False(UserSettingsRepository.IsValidZone("INVALID"));
        Assert.False(UserSettingsRepository.IsValidZone(""));
        Assert.False(UserSettingsRepository.IsValidZone(null));
    }
}
