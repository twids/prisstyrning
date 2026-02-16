using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Data.Repositories;
using System.Text.Json.Nodes;

namespace Prisstyrning.Tests.Unit.Data;

public class ScheduleHistoryRepositoryTests : IDisposable
{
    private readonly PrisstyrningDbContext _db;

    public ScheduleHistoryRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<PrisstyrningDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PrisstyrningDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() => _db.Dispose();

    private ScheduleHistoryRepository CreateRepo() => new(_db);

    private static JsonObject CreatePayload() => new() { ["test"] = "data", ["hours"] = 3 };

    #region SaveAsync

    [Fact]
    public async Task SaveAsync_CreatesEntry()
    {
        var repo = CreateRepo();
        var payload = CreatePayload();
        var timestamp = DateTimeOffset.UtcNow;

        await repo.SaveAsync("user1", payload, timestamp);

        var entry = await _db.ScheduleHistory.SingleAsync();
        Assert.Equal("user1", entry.UserId);
        Assert.Equal(timestamp, entry.Timestamp);
        Assert.Contains("\"test\"", entry.SchedulePayloadJson);
        Assert.Contains("\"hours\"", entry.SchedulePayloadJson);
    }

    [Fact]
    public async Task SaveAsync_MultipleSaves_CreatesMultipleEntries()
    {
        var repo = CreateRepo();
        var now = DateTimeOffset.UtcNow;

        await repo.SaveAsync("user1", CreatePayload(), now);
        await repo.SaveAsync("user1", CreatePayload(), now.AddHours(1));
        await repo.SaveAsync("user1", CreatePayload(), now.AddHours(2));

        var count = await _db.ScheduleHistory.CountAsync();
        Assert.Equal(3, count);
    }

    [Fact]
    public async Task SaveAsync_TrimsUserId()
    {
        var repo = CreateRepo();

        await repo.SaveAsync(" user1 ", CreatePayload(), DateTimeOffset.UtcNow);

        var entry = await _db.ScheduleHistory.SingleAsync();
        Assert.Equal("user1", entry.UserId);
    }

    #endregion

    #region LoadAsync

    [Fact]
    public async Task LoadAsync_ReturnsEntriesNewestFirst()
    {
        var repo = CreateRepo();
        var baseTime = new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

        await repo.SaveAsync("user1", CreatePayload(), baseTime);
        await repo.SaveAsync("user1", CreatePayload(), baseTime.AddHours(2));
        await repo.SaveAsync("user1", CreatePayload(), baseTime.AddHours(1));

        var entries = await repo.LoadAsync("user1");

        Assert.Equal(3, entries.Count);
        Assert.Equal(baseTime.AddHours(2), entries[0].Timestamp);
        Assert.Equal(baseTime.AddHours(1), entries[1].Timestamp);
        Assert.Equal(baseTime, entries[2].Timestamp);
    }

    [Fact]
    public async Task LoadAsync_NoEntries_ReturnsEmptyList()
    {
        var repo = CreateRepo();

        var entries = await repo.LoadAsync("nonexistent");

        Assert.Empty(entries);
    }

    [Fact]
    public async Task LoadAsync_IsolatesUsers()
    {
        var repo = CreateRepo();
        var now = DateTimeOffset.UtcNow;

        await repo.SaveAsync("user1", CreatePayload(), now);
        await repo.SaveAsync("user1", CreatePayload(), now.AddHours(1));
        await repo.SaveAsync("user2", CreatePayload(), now.AddHours(2));

        var user1Entries = await repo.LoadAsync("user1");
        var user2Entries = await repo.LoadAsync("user2");

        Assert.Equal(2, user1Entries.Count);
        Assert.All(user1Entries, e => Assert.Equal("user1", e.UserId));
        Assert.Single(user2Entries);
        Assert.Equal("user2", user2Entries[0].UserId);
    }

    #endregion

    #region LoadByDateRangeAsync

    [Fact]
    public async Task LoadByDateRangeAsync_ReturnsOnlyInRange()
    {
        var repo = CreateRepo();
        var baseTime = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero);

        await repo.SaveAsync("user1", CreatePayload(), baseTime);           // June 1
        await repo.SaveAsync("user1", CreatePayload(), baseTime.AddDays(1)); // June 2
        await repo.SaveAsync("user1", CreatePayload(), baseTime.AddDays(2)); // June 3
        await repo.SaveAsync("user1", CreatePayload(), baseTime.AddDays(5)); // June 6

        var from = baseTime.AddDays(1);  // June 2
        var to = baseTime.AddDays(3);    // June 4
        var entries = await repo.LoadByDateRangeAsync("user1", from, to);

        Assert.Equal(2, entries.Count);
        Assert.All(entries, e =>
        {
            Assert.True(e.Timestamp >= from);
            Assert.True(e.Timestamp <= to);
        });
    }

    [Fact]
    public async Task LoadByDateRangeAsync_Empty_ReturnsEmptyList()
    {
        var repo = CreateRepo();
        var baseTime = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero);

        await repo.SaveAsync("user1", CreatePayload(), baseTime);

        var from = baseTime.AddDays(10);
        var to = baseTime.AddDays(20);
        var entries = await repo.LoadByDateRangeAsync("user1", from, to);

        Assert.Empty(entries);
    }

    #endregion

    #region DeleteOlderThanAsync

    [Fact]
    public async Task DeleteOlderThanAsync_RemovesOld_KeepsNew()
    {
        var repo = CreateRepo();
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddDays(-3);

        // Old entries (should be deleted)
        await repo.SaveAsync("user1", CreatePayload(), now.AddDays(-5));
        await repo.SaveAsync("user1", CreatePayload(), now.AddDays(-4));
        // New entries (should be kept)
        await repo.SaveAsync("user1", CreatePayload(), now.AddDays(-2));
        await repo.SaveAsync("user1", CreatePayload(), now.AddDays(-1));

        var deleted = await repo.DeleteOlderThanAsync("user1", cutoff);

        Assert.Equal(2, deleted);
        var remaining = await _db.ScheduleHistory.CountAsync();
        Assert.Equal(2, remaining);
    }

    [Fact]
    public async Task DeleteOlderThanAsync_WrongUser_ReturnsZero()
    {
        var repo = CreateRepo();
        var now = DateTimeOffset.UtcNow;

        await repo.SaveAsync("user1", CreatePayload(), now.AddDays(-10));
        await repo.SaveAsync("user1", CreatePayload(), now.AddDays(-5));

        var deleted = await repo.DeleteOlderThanAsync("user2", now);

        Assert.Equal(0, deleted);
        var remaining = await _db.ScheduleHistory.CountAsync();
        Assert.Equal(2, remaining);
    }

    #endregion

    #region DeleteAllOlderThanAsync

    [Fact]
    public async Task DeleteAllOlderThanAsync_RemovesAcrossAllUsers()
    {
        var repo = CreateRepo();
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddDays(-3);

        // Old entries for multiple users
        await repo.SaveAsync("user1", CreatePayload(), now.AddDays(-5));
        await repo.SaveAsync("user2", CreatePayload(), now.AddDays(-4));
        // New entries
        await repo.SaveAsync("user1", CreatePayload(), now.AddDays(-1));
        await repo.SaveAsync("user2", CreatePayload(), now.AddDays(-1));

        var deleted = await repo.DeleteAllOlderThanAsync(cutoff);

        Assert.Equal(2, deleted);
        var remaining = await _db.ScheduleHistory.CountAsync();
        Assert.Equal(2, remaining);
    }

    #endregion

    #region CountAsync

    [Fact]
    public async Task CountAsync_ReturnsCorrectCount()
    {
        var repo = CreateRepo();
        var now = DateTimeOffset.UtcNow;

        await repo.SaveAsync("user1", CreatePayload(), now);
        await repo.SaveAsync("user1", CreatePayload(), now.AddHours(1));
        await repo.SaveAsync("user2", CreatePayload(), now.AddHours(2));

        var count = await repo.CountAsync("user1");
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task CountAsync_NoEntries_ReturnsZero()
    {
        var repo = CreateRepo();

        var count = await repo.CountAsync("nonexistent");
        Assert.Equal(0, count);
    }

    #endregion
}
