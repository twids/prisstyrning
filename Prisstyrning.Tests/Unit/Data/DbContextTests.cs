using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;

namespace Prisstyrning.Tests.Unit.Data;

public class DbContextTests : IDisposable
{
    private readonly PrisstyrningDbContext _db;

    public DbContextTests()
    {
        var options = new DbContextOptionsBuilder<PrisstyrningDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PrisstyrningDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public void DbContext_CanBeCreated()
    {
        Assert.NotNull(_db);
    }

    [Fact]
    public void DbSets_AreAccessible()
    {
        Assert.NotNull(_db.UserSettings);
        Assert.NotNull(_db.AdminRoles);
        Assert.NotNull(_db.PriceSnapshots);
        Assert.NotNull(_db.ScheduleHistory);
        Assert.NotNull(_db.DaikinTokens);
    }

    [Fact]
    public async Task UserSettings_CanAddAndRetrieve()
    {
        var settings = new UserSettings
        {
            UserId = "test-user",
            ComfortHours = 5,
            TurnOffPercentile = 0.8,
            Zone = "SE1"
        };

        _db.UserSettings.Add(settings);
        await _db.SaveChangesAsync();

        var retrieved = await _db.UserSettings.FindAsync("test-user");
        Assert.NotNull(retrieved);
        Assert.Equal(5, retrieved.ComfortHours);
        Assert.Equal(0.8, retrieved.TurnOffPercentile);
        Assert.Equal("SE1", retrieved.Zone);
    }

    [Fact]
    public async Task AdminRole_CanAddAndRetrieve()
    {
        var role = new AdminRole
        {
            UserId = "admin-user",
            IsAdmin = true,
            HasHangfireAccess = true
        };

        _db.AdminRoles.Add(role);
        await _db.SaveChangesAsync();

        var retrieved = await _db.AdminRoles.FindAsync("admin-user");
        Assert.NotNull(retrieved);
        Assert.True(retrieved.IsAdmin);
        Assert.True(retrieved.HasHangfireAccess);
    }

    [Fact]
    public async Task PriceSnapshot_CanAddAndRetrieve()
    {
        var snapshot = new PriceSnapshot
        {
            Zone = "SE3",
            Date = new DateOnly(2025, 8, 24),
            SavedAtUtc = DateTimeOffset.UtcNow,
            TodayPricesJson = "[1.0, 2.0]",
            TomorrowPricesJson = "[3.0, 4.0]"
        };

        _db.PriceSnapshots.Add(snapshot);
        await _db.SaveChangesAsync();

        var retrieved = await _db.PriceSnapshots.FirstAsync(p => p.Zone == "SE3");
        Assert.Equal(new DateOnly(2025, 8, 24), retrieved.Date);
        Assert.Equal("[1.0, 2.0]", retrieved.TodayPricesJson);
    }

    [Fact]
    public async Task ScheduleHistoryEntry_CanAddAndRetrieve()
    {
        var entry = new ScheduleHistoryEntry
        {
            UserId = "test-user",
            Timestamp = DateTimeOffset.UtcNow,
            SchedulePayloadJson = "{\"hours\": [1,2,3]}"
        };

        _db.ScheduleHistory.Add(entry);
        await _db.SaveChangesAsync();

        var retrieved = await _db.ScheduleHistory.FirstAsync(e => e.UserId == "test-user");
        Assert.Equal("{\"hours\": [1,2,3]}", retrieved.SchedulePayloadJson);
    }

    [Fact]
    public async Task DaikinToken_CanAddAndRetrieve()
    {
        var token = new DaikinToken
        {
            UserId = "daikin-user",
            AccessToken = "access123",
            RefreshToken = "refresh456",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1)
        };

        _db.DaikinTokens.Add(token);
        await _db.SaveChangesAsync();

        var retrieved = await _db.DaikinTokens.FindAsync("daikin-user");
        Assert.NotNull(retrieved);
        Assert.Equal("access123", retrieved.AccessToken);
        Assert.Equal("refresh456", retrieved.RefreshToken);
    }

    [Fact]
    public async Task UserSettings_DuplicatePK_Throws()
    {
        _db.UserSettings.Add(new UserSettings { UserId = "dup-user" });
        await _db.SaveChangesAsync();

        Assert.Throws<InvalidOperationException>(() =>
            _db.UserSettings.Add(new UserSettings { UserId = "dup-user" }));
    }

    [Fact]
    public async Task PriceSnapshot_AutoIncrementId()
    {
        _db.PriceSnapshots.Add(new PriceSnapshot { Zone = "SE3", Date = new DateOnly(2025, 1, 1), SavedAtUtc = DateTimeOffset.UtcNow });
        _db.PriceSnapshots.Add(new PriceSnapshot { Zone = "SE3", Date = new DateOnly(2025, 1, 2), SavedAtUtc = DateTimeOffset.UtcNow });
        await _db.SaveChangesAsync();

        var snapshots = await _db.PriceSnapshots.OrderBy(s => s.Id).ToListAsync();
        Assert.Equal(2, snapshots.Count);
        Assert.True(snapshots[1].Id > snapshots[0].Id);
    }
}
