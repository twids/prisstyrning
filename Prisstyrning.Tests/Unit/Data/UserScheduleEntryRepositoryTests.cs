using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Data.Repositories;

namespace Prisstyrning.Tests.Unit.Data;

public class UserScheduleEntryRepositoryTests : IDisposable
{
    private readonly PrisstyrningDbContext _db;

    public UserScheduleEntryRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<PrisstyrningDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PrisstyrningDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() => _db.Dispose();

    private UserScheduleEntryRepository CreateRepo() => new(_db);

    #region AddAsync

    [Fact]
    public async Task AddAsync_ValidEntry_SavesAndReturnsWithId()
    {
        var repo = CreateRepo();
        var entry = new UserScheduleEntry
        {
            UserId = "user1",
            ScheduledTimeUtc = DateTimeOffset.UtcNow.AddHours(5),
            State = "comfort",
            CountsAsLegionella = true
        };

        var result = await repo.AddAsync(entry);

        Assert.True(result.Id > 0);
        Assert.Equal("user1", result.UserId);
        Assert.Equal("comfort", result.State);

        var persisted = await _db.UserScheduleEntries.FindAsync(result.Id);
        Assert.NotNull(persisted);
    }

    [Fact]
    public async Task AddAsync_ExceedsLimit_ThrowsInvalidOperationException()
    {
        var repo = CreateRepo();
        for (int i = 0; i < 10; i++)
        {
            _db.UserScheduleEntries.Add(new UserScheduleEntry
            {
                UserId = "user1",
                ScheduledTimeUtc = DateTimeOffset.UtcNow.AddHours(i + 1),
                State = "comfort"
            });
        }
        await _db.SaveChangesAsync();

        var entry = new UserScheduleEntry
        {
            UserId = "user1",
            ScheduledTimeUtc = DateTimeOffset.UtcNow.AddHours(20),
            State = "eco"
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.AddAsync(entry));
    }

    #endregion

    #region GetFutureEntriesAsync

    [Fact]
    public async Task GetFutureEntriesAsync_ReturnsFutureOnly_OrderedByTime()
    {
        var now = DateTimeOffset.UtcNow;
        _db.UserScheduleEntries.AddRange(
            new UserScheduleEntry { UserId = "user1", ScheduledTimeUtc = now.AddHours(3), State = "eco" },
            new UserScheduleEntry { UserId = "user1", ScheduledTimeUtc = now.AddHours(1), State = "comfort" },
            new UserScheduleEntry { UserId = "user1", ScheduledTimeUtc = now.AddHours(-1), State = "comfort" }
        );
        await _db.SaveChangesAsync();

        var repo = CreateRepo();
        var results = await repo.GetFutureEntriesAsync("user1");

        Assert.Equal(2, results.Count);
        Assert.Equal("comfort", results[0].State); // +1h first
        Assert.Equal("eco", results[1].State);       // +3h second
    }

    [Fact]
    public async Task GetFutureEntriesAsync_FiltersToUserId()
    {
        var now = DateTimeOffset.UtcNow;
        _db.UserScheduleEntries.AddRange(
            new UserScheduleEntry { UserId = "user1", ScheduledTimeUtc = now.AddHours(1), State = "comfort" },
            new UserScheduleEntry { UserId = "user2", ScheduledTimeUtc = now.AddHours(2), State = "eco" }
        );
        await _db.SaveChangesAsync();

        var repo = CreateRepo();
        var results = await repo.GetFutureEntriesAsync("user1");

        Assert.Single(results);
        Assert.Equal("user1", results[0].UserId);
    }

    #endregion

    #region RemoveAsync

    [Fact]
    public async Task RemoveAsync_OwnEntry_ReturnsTrue()
    {
        var entry = new UserScheduleEntry
        {
            UserId = "user1",
            ScheduledTimeUtc = DateTimeOffset.UtcNow.AddHours(1),
            State = "comfort"
        };
        _db.UserScheduleEntries.Add(entry);
        await _db.SaveChangesAsync();

        var repo = CreateRepo();
        var result = await repo.RemoveAsync("user1", entry.Id);

        Assert.True(result);
        Assert.Null(await _db.UserScheduleEntries.FindAsync(entry.Id));
    }

    [Fact]
    public async Task RemoveAsync_OtherUsersEntry_ReturnsFalse()
    {
        var entry = new UserScheduleEntry
        {
            UserId = "user2",
            ScheduledTimeUtc = DateTimeOffset.UtcNow.AddHours(1),
            State = "comfort"
        };
        _db.UserScheduleEntries.Add(entry);
        await _db.SaveChangesAsync();

        var repo = CreateRepo();
        var result = await repo.RemoveAsync("user1", entry.Id);

        Assert.False(result);
        Assert.NotNull(await _db.UserScheduleEntries.FindAsync(entry.Id));
    }

    [Fact]
    public async Task RemoveAsync_NonExistentId_ReturnsFalse()
    {
        var repo = CreateRepo();
        var result = await repo.RemoveAsync("user1", 999);

        Assert.False(result);
    }

    #endregion

    #region CleanupPastEntriesAsync

    [Fact]
    public async Task CleanupPastEntriesAsync_RemovesPastEntries_KeepsFuture()
    {
        var now = DateTimeOffset.UtcNow;
        _db.UserScheduleEntries.AddRange(
            new UserScheduleEntry { UserId = "user1", ScheduledTimeUtc = now.AddHours(-2), State = "comfort" },
            new UserScheduleEntry { UserId = "user1", ScheduledTimeUtc = now.AddHours(-1), State = "eco" },
            new UserScheduleEntry { UserId = "user1", ScheduledTimeUtc = now.AddHours(5), State = "comfort" }
        );
        await _db.SaveChangesAsync();

        var repo = CreateRepo();
        await repo.CleanupPastEntriesAsync("user1");

        var remaining = await _db.UserScheduleEntries.Where(e => e.UserId == "user1").ToListAsync();
        Assert.Single(remaining);
        Assert.True(remaining[0].ScheduledTimeUtc > now);
    }

    [Fact]
    public async Task CleanupThenFetch_RemovesPastKeepsFuture()
    {
        var repo = CreateRepo();
        var past = new UserScheduleEntry
        {
            UserId = "user1",
            ScheduledTimeUtc = DateTimeOffset.UtcNow.AddHours(-2),
            State = "comfort",
            CreatedAtUtc = DateTimeOffset.UtcNow.AddHours(-3)
        };
        var future = new UserScheduleEntry
        {
            UserId = "user1",
            ScheduledTimeUtc = DateTimeOffset.UtcNow.AddHours(5),
            State = "eco",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await repo.AddAsync(past);
        await repo.AddAsync(future);

        await repo.CleanupPastEntriesAsync("user1");
        var entries = await repo.GetFutureEntriesAsync("user1");

        Assert.Single(entries);
        Assert.Equal("eco", entries[0].State);
    }

    #endregion
}
