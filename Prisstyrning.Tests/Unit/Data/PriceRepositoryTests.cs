using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Data.Repositories;
using System.Text.Json.Nodes;

namespace Prisstyrning.Tests.Unit.Data;

public class PriceRepositoryTests : IDisposable
{
    private readonly PrisstyrningDbContext _db;

    public PriceRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<PrisstyrningDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PrisstyrningDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() => _db.Dispose();

    private PriceRepository CreateRepo() => new(_db);

    #region SaveSnapshotAsync

    [Fact]
    public async Task SaveSnapshotAsync_NewEntry_CreatesRecord()
    {
        var repo = CreateRepo();
        var today = new JsonArray(new JsonObject { ["value"] = 100 });
        var tomorrow = new JsonArray(new JsonObject { ["value"] = 200 });

        await repo.SaveSnapshotAsync("SE3", new DateOnly(2025, 6, 15), today, tomorrow);

        var snapshot = await _db.PriceSnapshots.SingleAsync();
        Assert.Equal("SE3", snapshot.Zone);
        Assert.Equal(new DateOnly(2025, 6, 15), snapshot.Date);
        Assert.Contains("100", snapshot.TodayPricesJson);
        Assert.Contains("200", snapshot.TomorrowPricesJson);
        Assert.True(snapshot.SavedAtUtc <= DateTimeOffset.UtcNow);
        Assert.True(snapshot.SavedAtUtc > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task SaveSnapshotAsync_ExistingEntry_Updates()
    {
        var repo = CreateRepo();
        var today1 = new JsonArray(new JsonObject { ["value"] = 100 });
        var tomorrow1 = new JsonArray(new JsonObject { ["value"] = 200 });
        await repo.SaveSnapshotAsync("SE3", new DateOnly(2025, 6, 15), today1, tomorrow1);

        var today2 = new JsonArray(new JsonObject { ["value"] = 300 });
        var tomorrow2 = new JsonArray(new JsonObject { ["value"] = 400 });
        await repo.SaveSnapshotAsync("SE3", new DateOnly(2025, 6, 15), today2, tomorrow2);

        var count = await _db.PriceSnapshots.CountAsync();
        Assert.Equal(1, count);
        var snapshot = await _db.PriceSnapshots.SingleAsync();
        Assert.Contains("300", snapshot.TodayPricesJson);
        Assert.Contains("400", snapshot.TomorrowPricesJson);
    }

    [Fact]
    public async Task SaveSnapshotAsync_NormalizesZone()
    {
        var repo = CreateRepo();
        var today = new JsonArray(new JsonObject { ["value"] = 1 });
        var tomorrow = new JsonArray();

        await repo.SaveSnapshotAsync("se3", new DateOnly(2025, 6, 15), today, tomorrow);
        await repo.SaveSnapshotAsync(" SE3 ", new DateOnly(2025, 6, 15), today, tomorrow);

        var count = await _db.PriceSnapshots.CountAsync();
        Assert.Equal(1, count);
        var snapshot = await _db.PriceSnapshots.SingleAsync();
        Assert.Equal("SE3", snapshot.Zone);
    }

    [Fact]
    public async Task SaveSnapshotAsync_DifferentZones_IsolatesData()
    {
        var repo = CreateRepo();
        var today3 = new JsonArray(new JsonObject { ["value"] = 10 });
        var today4 = new JsonArray(new JsonObject { ["value"] = 20 });
        var empty = new JsonArray();

        await repo.SaveSnapshotAsync("SE3", new DateOnly(2025, 6, 15), today3, empty);
        await repo.SaveSnapshotAsync("SE4", new DateOnly(2025, 6, 15), today4, empty);

        var count = await _db.PriceSnapshots.CountAsync();
        Assert.Equal(2, count);
        var se3 = await _db.PriceSnapshots.SingleAsync(s => s.Zone == "SE3");
        var se4 = await _db.PriceSnapshots.SingleAsync(s => s.Zone == "SE4");
        Assert.Contains("10", se3.TodayPricesJson);
        Assert.Contains("20", se4.TodayPricesJson);
    }

    #endregion

    #region GetLatestAsync

    [Fact]
    public async Task GetLatestAsync_ReturnsNewestByDate()
    {
        _db.PriceSnapshots.AddRange(
            new PriceSnapshot { Zone = "SE3", Date = new DateOnly(2025, 6, 14), SavedAtUtc = DateTimeOffset.UtcNow, TodayPricesJson = "[{\"value\":1}]" },
            new PriceSnapshot { Zone = "SE3", Date = new DateOnly(2025, 6, 16), SavedAtUtc = DateTimeOffset.UtcNow, TodayPricesJson = "[{\"value\":3}]" },
            new PriceSnapshot { Zone = "SE3", Date = new DateOnly(2025, 6, 15), SavedAtUtc = DateTimeOffset.UtcNow, TodayPricesJson = "[{\"value\":2}]" }
        );
        await _db.SaveChangesAsync();

        var repo = CreateRepo();
        var latest = await repo.GetLatestAsync("SE3");

        Assert.NotNull(latest);
        Assert.Equal(new DateOnly(2025, 6, 16), latest.Date);
        Assert.Contains("3", latest.TodayPricesJson);
    }

    [Fact]
    public async Task GetLatestAsync_NoData_ReturnsNull()
    {
        var repo = CreateRepo();
        var latest = await repo.GetLatestAsync("SE3");
        Assert.Null(latest);
    }

    #endregion

    #region GetByDateAsync

    [Fact]
    public async Task GetByDateAsync_Found_ReturnsSnapshot()
    {
        _db.PriceSnapshots.Add(new PriceSnapshot
        {
            Zone = "SE3",
            Date = new DateOnly(2025, 6, 15),
            SavedAtUtc = DateTimeOffset.UtcNow,
            TodayPricesJson = "[{\"value\":42}]"
        });
        await _db.SaveChangesAsync();

        var repo = CreateRepo();
        var snapshot = await repo.GetByDateAsync("SE3", new DateOnly(2025, 6, 15));

        Assert.NotNull(snapshot);
        Assert.Contains("42", snapshot.TodayPricesJson);
    }

    [Fact]
    public async Task GetByDateAsync_NotFound_ReturnsNull()
    {
        var repo = CreateRepo();
        var snapshot = await repo.GetByDateAsync("SE3", new DateOnly(2025, 6, 15));
        Assert.Null(snapshot);
    }

    [Fact]
    public async Task GetByDateAsync_WrongZone_ReturnsNull()
    {
        _db.PriceSnapshots.Add(new PriceSnapshot
        {
            Zone = "SE3",
            Date = new DateOnly(2025, 6, 15),
            SavedAtUtc = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync();

        var repo = CreateRepo();
        var snapshot = await repo.GetByDateAsync("SE4", new DateOnly(2025, 6, 15));
        Assert.Null(snapshot);
    }

    #endregion

    #region GetByDateRangeAsync

    [Fact]
    public async Task GetByDateRangeAsync_ReturnsOnlyInRange()
    {
        _db.PriceSnapshots.AddRange(
            new PriceSnapshot { Zone = "SE3", Date = new DateOnly(2025, 6, 10), SavedAtUtc = DateTimeOffset.UtcNow },
            new PriceSnapshot { Zone = "SE3", Date = new DateOnly(2025, 6, 15), SavedAtUtc = DateTimeOffset.UtcNow },
            new PriceSnapshot { Zone = "SE3", Date = new DateOnly(2025, 6, 20), SavedAtUtc = DateTimeOffset.UtcNow },
            new PriceSnapshot { Zone = "SE3", Date = new DateOnly(2025, 6, 25), SavedAtUtc = DateTimeOffset.UtcNow }
        );
        await _db.SaveChangesAsync();

        var repo = CreateRepo();
        var results = await repo.GetByDateRangeAsync("SE3", new DateOnly(2025, 6, 14), new DateOnly(2025, 6, 21));

        Assert.Equal(2, results.Count);
        Assert.All(results, s => Assert.True(s.Date >= new DateOnly(2025, 6, 14) && s.Date <= new DateOnly(2025, 6, 21)));
    }

    [Fact]
    public async Task GetByDateRangeAsync_Empty_ReturnsEmptyList()
    {
        var repo = CreateRepo();
        var results = await repo.GetByDateRangeAsync("SE3", new DateOnly(2025, 6, 1), new DateOnly(2025, 6, 30));
        Assert.Empty(results);
    }

    #endregion

    #region DeleteOlderThanAsync

    [Fact]
    public async Task DeleteOlderThanAsync_RemovesOld_KeepsNew()
    {
        _db.PriceSnapshots.AddRange(
            new PriceSnapshot { Zone = "SE3", Date = new DateOnly(2025, 6, 1), SavedAtUtc = DateTimeOffset.UtcNow },
            new PriceSnapshot { Zone = "SE3", Date = new DateOnly(2025, 6, 5), SavedAtUtc = DateTimeOffset.UtcNow },
            new PriceSnapshot { Zone = "SE3", Date = new DateOnly(2025, 6, 10), SavedAtUtc = DateTimeOffset.UtcNow },
            new PriceSnapshot { Zone = "SE3", Date = new DateOnly(2025, 6, 15), SavedAtUtc = DateTimeOffset.UtcNow }
        );
        await _db.SaveChangesAsync();

        var repo = CreateRepo();
        var deleted = await repo.DeleteOlderThanAsync(new DateOnly(2025, 6, 10));

        Assert.Equal(2, deleted);
        var remaining = await _db.PriceSnapshots.ToListAsync();
        Assert.Equal(2, remaining.Count);
        Assert.All(remaining, s => Assert.True(s.Date >= new DateOnly(2025, 6, 10)));
    }

    [Fact]
    public async Task DeleteOlderThanAsync_NothingToDelete_ReturnsZero()
    {
        _db.PriceSnapshots.Add(new PriceSnapshot
        {
            Zone = "SE3",
            Date = new DateOnly(2025, 6, 15),
            SavedAtUtc = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync();

        var repo = CreateRepo();
        var deleted = await repo.DeleteOlderThanAsync(new DateOnly(2025, 6, 1));

        Assert.Equal(0, deleted);
        Assert.Equal(1, await _db.PriceSnapshots.CountAsync());
    }

    #endregion
}
