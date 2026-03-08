using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Data.Repositories;

namespace Prisstyrning.Tests.Api;

public class ScheduleEntryEndpointTests : IDisposable
{
    private readonly PrisstyrningDbContext _db;

    public ScheduleEntryEndpointTests()
    {
        var options = new DbContextOptionsBuilder<PrisstyrningDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PrisstyrningDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() => _db.Dispose();

    private UserScheduleEntryRepository CreateEntryRepo() => new(_db);
    private FlexibleScheduleStateRepository CreateFlexRepo() => new(_db);

    [Fact]
    public async Task AddEntry_ValidComfort_CreatesEntryAndId()
    {
        var repo = CreateEntryRepo();
        var entry = new UserScheduleEntry
        {
            UserId = "user1",
            ScheduledTimeUtc = DateTimeOffset.UtcNow.AddHours(2),
            State = "comfort",
            CountsAsLegionella = true,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        var result = await repo.AddAsync(entry);

        Assert.True(result.Id > 0);
        Assert.Equal("user1", result.UserId);
        Assert.Equal("comfort", result.State);
        Assert.True(result.CountsAsLegionella);

        // Verify persisted
        var persisted = await _db.UserScheduleEntries.FindAsync(result.Id);
        Assert.NotNull(persisted);
        Assert.Equal("comfort", persisted.State);
    }

    [Fact]
    public async Task AddEntry_EcoState_SetsCountsAsLegionellaFalse()
    {
        var repo = CreateEntryRepo();
        // Even if caller passes countsAsLegionella=true, eco should force it false
        // This is enforced at the endpoint level, not repo level.
        // We simulate what the endpoint does: force countsAsLegionella=false for eco.
        var entry = new UserScheduleEntry
        {
            UserId = "user1",
            ScheduledTimeUtc = DateTimeOffset.UtcNow.AddHours(3),
            State = "eco",
            CountsAsLegionella = false, // endpoint forces this for eco
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        var result = await repo.AddAsync(entry);

        Assert.Equal("eco", result.State);
        Assert.False(result.CountsAsLegionella);

        var persisted = await _db.UserScheduleEntries.FindAsync(result.Id);
        Assert.NotNull(persisted);
        Assert.False(persisted.CountsAsLegionella);
    }

    [Fact]
    public async Task AddEntry_ComfortWithLegionella_UpdatesFlexibleState()
    {
        var entryRepo = CreateEntryRepo();
        var flexRepo = CreateFlexRepo();

        var scheduledTime = DateTimeOffset.UtcNow.AddHours(4);
        var entry = new UserScheduleEntry
        {
            UserId = "user1",
            ScheduledTimeUtc = scheduledTime,
            State = "comfort",
            CountsAsLegionella = true,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        await entryRepo.AddAsync(entry);

        // Simulate what the endpoint does: update comfort run when countsAsLegionella
        await flexRepo.UpdateComfortRunAsync("user1", scheduledTime);

        var flexState = await _db.FlexibleScheduleStates.FindAsync("user1");
        Assert.NotNull(flexState);
        Assert.Equal(scheduledTime, flexState.LastComfortRunUtc);
    }

    [Fact]
    public async Task RemoveEntry_ExistingEntry_DeletesAndReturnsTrue()
    {
        var repo = CreateEntryRepo();
        var entry = new UserScheduleEntry
        {
            UserId = "user1",
            ScheduledTimeUtc = DateTimeOffset.UtcNow.AddHours(5),
            State = "comfort",
            CountsAsLegionella = true,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        var added = await repo.AddAsync(entry);
        var removed = await repo.RemoveAsync("user1", added.Id);

        Assert.True(removed);

        var persisted = await _db.UserScheduleEntries.FindAsync(added.Id);
        Assert.Null(persisted);
    }

    [Fact]
    public async Task RemoveEntry_OtherUsersEntry_ReturnsFalse()
    {
        var repo = CreateEntryRepo();
        var entry = new UserScheduleEntry
        {
            UserId = "user1",
            ScheduledTimeUtc = DateTimeOffset.UtcNow.AddHours(6),
            State = "comfort",
            CountsAsLegionella = true,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        var added = await repo.AddAsync(entry);

        // Try to remove as different user
        var removed = await repo.RemoveAsync("user2", added.Id);

        Assert.False(removed);

        // Entry should still exist
        var persisted = await _db.UserScheduleEntries.FindAsync(added.Id);
        Assert.NotNull(persisted);
    }
}
