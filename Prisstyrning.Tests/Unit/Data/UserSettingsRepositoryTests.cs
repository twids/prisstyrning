using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Data.Repositories;

namespace Prisstyrning.Tests.Unit.Data;

public class UserSettingsRepositoryTests : IDisposable
{
    private readonly PrisstyrningDbContext _db;
    private readonly IConfiguration _cfg;

    public UserSettingsRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<PrisstyrningDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PrisstyrningDbContext(options);
        _db.Database.EnsureCreated();

        _cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Schedule:ComfortHours"] = "4",
                ["Schedule:TurnOffPercentile"] = "0.85",
                ["Schedule:MaxComfortGapHours"] = "24",
                ["Price:Nordpool:DefaultZone"] = "SE3"
            })
            .Build();
    }

    public void Dispose() => _db.Dispose();

    private UserSettingsRepository CreateRepo() => new(_db, _cfg);

    #region LoadScheduleSettings

    [Fact]
    public async Task LoadScheduleSettingsAsync_MissingUser_ReturnsDefaults()
    {
        var repo = CreateRepo();
        var settings = await repo.LoadScheduleSettingsAsync("nonexistent-user");

        Assert.Equal(4, settings.ComfortHours);
        Assert.Equal(0.85, settings.TurnOffPercentile);
        Assert.Equal(24, settings.MaxComfortGapHours);
    }

    [Fact]
    public async Task LoadScheduleSettingsAsync_NullUserId_ReturnsDefaults()
    {
        var repo = CreateRepo();
        var settings = await repo.LoadScheduleSettingsAsync(null);

        Assert.Equal(4, settings.ComfortHours);
        Assert.Equal(0.85, settings.TurnOffPercentile);
        Assert.Equal(24, settings.MaxComfortGapHours);
    }

    [Fact]
    public async Task LoadScheduleSettingsAsync_EmptyUserId_ReturnsDefaults()
    {
        var repo = CreateRepo();
        var settings = await repo.LoadScheduleSettingsAsync("  ");

        Assert.Equal(4, settings.ComfortHours);
    }

    [Fact]
    public async Task LoadScheduleSettingsAsync_SavedValues_ReturnsUserValues()
    {
        _db.UserSettings.Add(new UserSettings
        {
            UserId = "user1",
            ComfortHours = 6,
            TurnOffPercentile = 0.75,
            MaxComfortGapHours = 36
        });
        await _db.SaveChangesAsync();

        var repo = CreateRepo();
        var settings = await repo.LoadScheduleSettingsAsync("user1");

        Assert.Equal(6, settings.ComfortHours);
        Assert.Equal(0.75, settings.TurnOffPercentile);
        Assert.Equal(36, settings.MaxComfortGapHours);
    }

    [Fact]
    public void LoadScheduleSettings_SyncWrapper_Works()
    {
        var repo = CreateRepo();
        var settings = repo.LoadScheduleSettings("nonexistent-user");

        Assert.Equal(4, settings.ComfortHours);
    }

    [Fact]
    public async Task LoadScheduleSettingsAsync_ClampsTooHighValues()
    {
        _db.UserSettings.Add(new UserSettings
        {
            UserId = "extreme-user",
            ComfortHours = 20,  // > 12
            TurnOffPercentile = 1.5,  // > 0.99
            MaxComfortGapHours = 100  // > 72
        });
        await _db.SaveChangesAsync();

        var repo = CreateRepo();
        var settings = await repo.LoadScheduleSettingsAsync("extreme-user");

        Assert.Equal(12, settings.ComfortHours);
        Assert.Equal(0.99, settings.TurnOffPercentile);
        Assert.Equal(72, settings.MaxComfortGapHours);
    }

    [Fact]
    public async Task LoadScheduleSettingsAsync_ClampsTooLowValues()
    {
        _db.UserSettings.Add(new UserSettings
        {
            UserId = "low-user",
            ComfortHours = 0,  // < 1
            TurnOffPercentile = 0.1,  // < 0.5
            MaxComfortGapHours = 0  // < 1
        });
        await _db.SaveChangesAsync();

        var repo = CreateRepo();
        var settings = await repo.LoadScheduleSettingsAsync("low-user");

        Assert.Equal(1, settings.ComfortHours);
        Assert.Equal(0.5, settings.TurnOffPercentile);
        Assert.Equal(1, settings.MaxComfortGapHours);
    }

    [Fact]
    public async Task LoadScheduleSettingsAsync_NoConfigDefaults_UsesHardcodedDefaults()
    {
        var emptyCfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var repo = new UserSettingsRepository(_db, emptyCfg);

        var settings = await repo.LoadScheduleSettingsAsync(null);

        Assert.Equal(3, settings.ComfortHours);
        Assert.Equal(0.9, settings.TurnOffPercentile);
        Assert.Equal(28, settings.MaxComfortGapHours);
    }

    #endregion

    #region GetUserZone

    [Fact]
    public async Task GetUserZoneAsync_MissingUser_ReturnsDefault()
    {
        var repo = CreateRepo();
        var zone = await repo.GetUserZoneAsync("nonexistent");

        Assert.Equal("SE3", zone);
    }

    [Fact]
    public async Task GetUserZoneAsync_NullUserId_ReturnsDefault()
    {
        var repo = CreateRepo();
        var zone = await repo.GetUserZoneAsync(null);

        Assert.Equal("SE3", zone);
    }

    [Fact]
    public async Task GetUserZoneAsync_SavedZone_ReturnsUserZone()
    {
        _db.UserSettings.Add(new UserSettings { UserId = "user1", Zone = "SE1" });
        await _db.SaveChangesAsync();

        var repo = CreateRepo();
        var zone = await repo.GetUserZoneAsync("user1");

        Assert.Equal("SE1", zone);
    }

    [Fact]
    public async Task GetUserZoneAsync_InvalidSavedZone_ReturnsDefault()
    {
        _db.UserSettings.Add(new UserSettings { UserId = "user1", Zone = "INVALID" });
        await _db.SaveChangesAsync();

        var repo = CreateRepo();
        var zone = await repo.GetUserZoneAsync("user1");

        Assert.Equal("SE3", zone);
    }

    [Fact]
    public void GetUserZone_SyncWrapper_Works()
    {
        var repo = CreateRepo();
        var zone = repo.GetUserZone("nonexistent");

        Assert.Equal("SE3", zone);
    }

    #endregion

    #region SetUserZone

    [Fact]
    public async Task SetUserZoneAsync_ValidZone_SavesAndRetrieves()
    {
        var repo = CreateRepo();
        var result = await repo.SetUserZoneAsync("user1", "SE1");

        Assert.True(result);
        var zone = await repo.GetUserZoneAsync("user1");
        Assert.Equal("SE1", zone);
    }

    [Fact]
    public async Task SetUserZoneAsync_UpdatesExisting()
    {
        _db.UserSettings.Add(new UserSettings { UserId = "user1", Zone = "SE1" });
        await _db.SaveChangesAsync();

        var repo = CreateRepo();
        var result = await repo.SetUserZoneAsync("user1", "SE4");

        Assert.True(result);
        var zone = await repo.GetUserZoneAsync("user1");
        Assert.Equal("SE4", zone);
    }

    [Fact]
    public async Task SetUserZoneAsync_InvalidZone_ReturnsFalse()
    {
        var repo = CreateRepo();
        var result = await repo.SetUserZoneAsync("user1", "INVALID");

        Assert.False(result);
    }

    [Fact]
    public async Task SetUserZoneAsync_NullUserId_ReturnsFalse()
    {
        var repo = CreateRepo();
        var result = await repo.SetUserZoneAsync(null, "SE1");

        Assert.False(result);
    }

    [Fact]
    public async Task SetUserZoneAsync_NormalizesToUpperCase()
    {
        var repo = CreateRepo();
        await repo.SetUserZoneAsync("user1", "se1");

        var zone = await repo.GetUserZoneAsync("user1");
        Assert.Equal("SE1", zone);
    }

    #endregion

    #region SaveSettings and GetOrCreate

    [Fact]
    public async Task SaveSettingsAsync_CreatesNewUser()
    {
        var repo = CreateRepo();
        await repo.SaveSettingsAsync("new-user", 5, 0.8, true, 30);

        var entity = await _db.UserSettings.FindAsync("new-user");
        Assert.NotNull(entity);
        Assert.Equal(5, entity.ComfortHours);
        Assert.Equal(0.8, entity.TurnOffPercentile);
        Assert.True(entity.AutoApplySchedule);
        Assert.Equal(30, entity.MaxComfortGapHours);
    }

    [Fact]
    public async Task SaveSettingsAsync_UpdatesExisting()
    {
        _db.UserSettings.Add(new UserSettings { UserId = "user1", ComfortHours = 3 });
        await _db.SaveChangesAsync();

        var repo = CreateRepo();
        await repo.SaveSettingsAsync("user1", 8, 0.75, true, 40);

        var entity = await _db.UserSettings.FindAsync("user1");
        Assert.NotNull(entity);
        Assert.Equal(8, entity.ComfortHours);
        Assert.Equal(0.75, entity.TurnOffPercentile);
        Assert.True(entity.AutoApplySchedule);
        Assert.Equal(40, entity.MaxComfortGapHours);
    }

    [Fact]
    public async Task GetOrCreateAsync_MissingUser_ReturnsDefaults()
    {
        var repo = CreateRepo();
        var entity = await repo.GetOrCreateAsync("missing-user");

        Assert.Equal("missing-user", entity.UserId);
        Assert.Equal(3, entity.ComfortHours);  // Entity default
        Assert.Equal(0.9, entity.TurnOffPercentile);
        Assert.False(entity.AutoApplySchedule);
    }

    [Fact]
    public async Task GetOrCreateAsync_ExistingUser_ReturnsStored()
    {
        _db.UserSettings.Add(new UserSettings 
        { 
            UserId = "user1", 
            ComfortHours = 7, 
            TurnOffPercentile = 0.65,
            AutoApplySchedule = true,
            MaxComfortGapHours = 48
        });
        await _db.SaveChangesAsync();

        var repo = CreateRepo();
        var entity = await repo.GetOrCreateAsync("user1");

        Assert.Equal(7, entity.ComfortHours);
        Assert.Equal(0.65, entity.TurnOffPercentile);
        Assert.True(entity.AutoApplySchedule);
        Assert.Equal(48, entity.MaxComfortGapHours);
    }

    #endregion

    #region GetAutoApplyUserIds

    [Fact]
    public async Task GetAutoApplyUserIdsAsync_ReturnsCorrectUsers()
    {
        _db.UserSettings.AddRange(
            new UserSettings { UserId = "auto-on-1", AutoApplySchedule = true },
            new UserSettings { UserId = "auto-off", AutoApplySchedule = false },
            new UserSettings { UserId = "auto-on-2", AutoApplySchedule = true }
        );
        await _db.SaveChangesAsync();

        var repo = CreateRepo();
        var ids = await repo.GetAutoApplyUserIdsAsync();

        Assert.Equal(2, ids.Count);
        Assert.Contains("auto-on-1", ids);
        Assert.Contains("auto-on-2", ids);
        Assert.DoesNotContain("auto-off", ids);
    }

    [Fact]
    public async Task GetAutoApplyUserIdsAsync_Empty_ReturnsEmptyList()
    {
        var repo = CreateRepo();
        var ids = await repo.GetAutoApplyUserIdsAsync();

        Assert.Empty(ids);
    }

    #endregion

    #region GetAllUserZones

    [Fact]
    public async Task GetAllUserZonesAsync_ReturnsDistinctZones()
    {
        _db.UserSettings.AddRange(
            new UserSettings { UserId = "user1", Zone = "SE1" },
            new UserSettings { UserId = "user2", Zone = "SE3" },
            new UserSettings { UserId = "user3", Zone = "SE1" }  // duplicate
        );
        await _db.SaveChangesAsync();

        var repo = CreateRepo();
        var zones = await repo.GetAllUserZonesAsync();

        Assert.Equal(2, zones.Count);
        Assert.Contains("SE1", zones);
        Assert.Contains("SE3", zones);
    }

    #endregion

    #region IsValidZone

    [Theory]
    [InlineData("SE1", true)]
    [InlineData("SE2", true)]
    [InlineData("SE3", true)]
    [InlineData("SE4", true)]
    [InlineData("NO1", true)]
    [InlineData("NO5", true)]
    [InlineData("DK1", true)]
    [InlineData("DK2", true)]
    [InlineData("FI", true)]
    [InlineData("EE", true)]
    [InlineData("LV", true)]
    [InlineData("LT", true)]
    [InlineData("se1", true)]  // case insensitive
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("  ", false)]
    [InlineData("INVALID", false)]
    [InlineData("SE5", false)]
    [InlineData("XX1", false)]
    [InlineData("SE0", false)]
    public void IsValidZone_ValidatesCorrectly(string? zone, bool expected)
    {
        Assert.Equal(expected, UserSettingsRepository.IsValidZone(zone));
    }

    #endregion

    #region DeleteAsync

    [Fact]
    public async Task DeleteAsync_RemovesUser()
    {
        _db.UserSettings.Add(new UserSettings { UserId = "to-delete", ComfortHours = 5 });
        await _db.SaveChangesAsync();

        var repo = CreateRepo();
        await repo.DeleteAsync("to-delete");

        var entity = await _db.UserSettings.FindAsync("to-delete");
        Assert.Null(entity);
    }

    [Fact]
    public async Task DeleteAsync_NonexistentUser_DoesNotThrow()
    {
        var repo = CreateRepo();
        await repo.DeleteAsync("nonexistent");
        // Should not throw
    }

    #endregion
}
