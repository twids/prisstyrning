using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Data.Repositories;
using Xunit;

namespace Prisstyrning.Tests.Unit;

public class UserSettingsTimezoneTests
{
    private static readonly string[] ValidTimezones = new[]
    {
        "auto",
        "Europe/Stockholm",
        "Europe/Oslo",
        "Europe/Copenhagen",
        "Europe/Helsinki"
    };

    [Fact]
    public void NewUserSettings_HasTimezoneDefaultAuto()
    {
        var settings = new UserSettings();
        Assert.Equal("auto", settings.Timezone);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("Europe/Stockholm")]
    [InlineData("Europe/Oslo")]
    [InlineData("Europe/Copenhagen")]
    [InlineData("Europe/Helsinki")]
    public async Task SaveSettings_AcceptsValidTimezone(string timezone)
    {
        var options = new DbContextOptionsBuilder<PrisstyrningDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        using var db = new PrisstyrningDbContext(options);
        db.Database.EnsureCreated();

        var cfg = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var repo = new UserSettingsRepository(db, cfg);

        await repo.SaveSettingsAsync("tz-test", 3, 0.9, false, 28,
            timezone: timezone);

        var entity = await repo.GetOrCreateAsync("tz-test");
        Assert.Equal(timezone, entity.Timezone);
    }

    [Fact]
    public async Task SaveSettings_WithoutTimezone_KeepsDefault()
    {
        var options = new DbContextOptionsBuilder<PrisstyrningDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        using var db = new PrisstyrningDbContext(options);
        db.Database.EnsureCreated();

        var cfg = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var repo = new UserSettingsRepository(db, cfg);

        // Save without specifying timezone
        await repo.SaveSettingsAsync("tz-default", 3, 0.9, false, 28);

        var entity = await repo.GetOrCreateAsync("tz-default");
        Assert.Equal("auto", entity.Timezone);
    }

    [Fact]
    public async Task GetOrCreate_ReturnsTimezoneInEntity()
    {
        var options = new DbContextOptionsBuilder<PrisstyrningDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        using var db = new PrisstyrningDbContext(options);
        db.Database.EnsureCreated();

        db.UserSettings.Add(new UserSettings
        {
            UserId = "tz-get",
            Timezone = "Europe/Helsinki"
        });
        await db.SaveChangesAsync();

        var cfg = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var repo = new UserSettingsRepository(db, cfg);

        var entity = await repo.GetOrCreateAsync("tz-get");
        Assert.Equal("Europe/Helsinki", entity.Timezone);
    }

    [Theory]
    [InlineData("US/Eastern")]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("America/New_York")]
    [InlineData(null)]
    public void InvalidTimezone_IsNotInValidSet(string? timezone)
    {
        Assert.DoesNotContain(timezone, ValidTimezones);
    }
}
