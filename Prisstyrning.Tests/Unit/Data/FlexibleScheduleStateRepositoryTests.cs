using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Data.Repositories;

namespace Prisstyrning.Tests.Unit.Data;

public class FlexibleScheduleStateRepositoryTests : IDisposable
{
    private readonly PrisstyrningDbContext _db;

    public FlexibleScheduleStateRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<PrisstyrningDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PrisstyrningDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() => _db.Dispose();

    private FlexibleScheduleStateRepository CreateRepo() => new(_db);

    #region GetOrCreateAsync

    [Fact]
    public async Task GetOrCreateAsync_NewUser_CreatesEntityWithNullTimestamps()
    {
        var repo = CreateRepo();
        var entity = await repo.GetOrCreateAsync("new-user");

        Assert.Equal("new-user", entity.UserId);
        Assert.Null(entity.LastEcoRunUtc);
        Assert.Null(entity.LastComfortRunUtc);
        Assert.Null(entity.NextScheduledComfortUtc);

        // Verify persisted
        var persisted = await _db.FlexibleScheduleStates.FindAsync("new-user");
        Assert.NotNull(persisted);
    }

    [Fact]
    public async Task GetOrCreateAsync_ExistingUser_ReturnsExistingEntity()
    {
        var now = DateTimeOffset.UtcNow;
        _db.FlexibleScheduleStates.Add(new FlexibleScheduleState
        {
            UserId = "existing-user",
            LastEcoRunUtc = now,
            LastComfortRunUtc = now.AddDays(-7)
        });
        await _db.SaveChangesAsync();

        var repo = CreateRepo();
        var entity = await repo.GetOrCreateAsync("existing-user");

        Assert.Equal("existing-user", entity.UserId);
        Assert.Equal(now, entity.LastEcoRunUtc);
        Assert.Equal(now.AddDays(-7), entity.LastComfortRunUtc);
    }

    #endregion

    #region UpdateEcoRunAsync

    [Fact]
    public async Task UpdateEcoRunAsync_SetsTimestamp()
    {
        var repo = CreateRepo();
        var runTime = new DateTimeOffset(2026, 2, 22, 10, 0, 0, TimeSpan.Zero);

        await repo.UpdateEcoRunAsync("user1", runTime);

        var entity = await _db.FlexibleScheduleStates.FindAsync("user1");
        Assert.NotNull(entity);
        Assert.Equal(runTime, entity.LastEcoRunUtc);
        Assert.Null(entity.LastComfortRunUtc);
    }

    [Fact]
    public async Task UpdateEcoRunAsync_UpdatesExistingTimestamp()
    {
        var initial = new DateTimeOffset(2026, 2, 20, 10, 0, 0, TimeSpan.Zero);
        _db.FlexibleScheduleStates.Add(new FlexibleScheduleState
        {
            UserId = "user1",
            LastEcoRunUtc = initial
        });
        await _db.SaveChangesAsync();

        var repo = CreateRepo();
        var newTime = new DateTimeOffset(2026, 2, 22, 14, 0, 0, TimeSpan.Zero);
        await repo.UpdateEcoRunAsync("user1", newTime);

        var entity = await _db.FlexibleScheduleStates.FindAsync("user1");
        Assert.NotNull(entity);
        Assert.Equal(newTime, entity.LastEcoRunUtc);
    }

    #endregion

    #region UpdateComfortRunAsync

    [Fact]
    public async Task UpdateComfortRunAsync_SetsTimestamp()
    {
        var repo = CreateRepo();
        var runTime = new DateTimeOffset(2026, 2, 22, 12, 0, 0, TimeSpan.Zero);

        await repo.UpdateComfortRunAsync("user1", runTime);

        var entity = await _db.FlexibleScheduleStates.FindAsync("user1");
        Assert.NotNull(entity);
        Assert.Equal(runTime, entity.LastComfortRunUtc);
        Assert.Null(entity.LastEcoRunUtc);
    }

    [Fact]
    public async Task UpdateComfortRunAsync_UpdatesExistingTimestamp()
    {
        var initial = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        _db.FlexibleScheduleStates.Add(new FlexibleScheduleState
        {
            UserId = "user1",
            LastComfortRunUtc = initial
        });
        await _db.SaveChangesAsync();

        var repo = CreateRepo();
        var newTime = new DateTimeOffset(2026, 2, 22, 18, 0, 0, TimeSpan.Zero);
        await repo.UpdateComfortRunAsync("user1", newTime);

        var entity = await _db.FlexibleScheduleStates.FindAsync("user1");
        Assert.NotNull(entity);
        Assert.Equal(newTime, entity.LastComfortRunUtc);
    }

    [Fact]
    public async Task UpdateComfortRunAsync_ClearsNextScheduledComfort()
    {
        var scheduled = new DateTimeOffset(2026, 2, 25, 3, 0, 0, TimeSpan.Zero);
        _db.FlexibleScheduleStates.Add(new FlexibleScheduleState
        {
            UserId = "user1",
            NextScheduledComfortUtc = scheduled
        });
        await _db.SaveChangesAsync();

        var repo = CreateRepo();
        var runTime = new DateTimeOffset(2026, 2, 22, 14, 0, 0, TimeSpan.Zero);
        await repo.UpdateComfortRunAsync("user1", runTime);

        var entity = await _db.FlexibleScheduleStates.FindAsync("user1");
        Assert.NotNull(entity);
        Assert.Equal(runTime, entity.LastComfortRunUtc);
        Assert.Null(entity.NextScheduledComfortUtc); // Cleared after actual run
    }

    #endregion

    #region ScheduleComfortRunAsync

    [Fact]
    public async Task ScheduleComfortRunAsync_SetsNextScheduledTime()
    {
        var repo = CreateRepo();
        var scheduled = new DateTimeOffset(2026, 2, 25, 3, 0, 0, TimeSpan.Zero);

        await repo.ScheduleComfortRunAsync("user1", scheduled);

        var entity = await _db.FlexibleScheduleStates.FindAsync("user1");
        Assert.NotNull(entity);
        Assert.Equal(scheduled, entity.NextScheduledComfortUtc);
    }

    [Fact]
    public async Task ScheduleComfortRunAsync_UpdatesExistingSchedule()
    {
        var initial = new DateTimeOffset(2026, 2, 25, 3, 0, 0, TimeSpan.Zero);
        _db.FlexibleScheduleStates.Add(new FlexibleScheduleState
        {
            UserId = "user1",
            NextScheduledComfortUtc = initial
        });
        await _db.SaveChangesAsync();

        var repo = CreateRepo();
        var cheaper = new DateTimeOffset(2026, 2, 26, 2, 0, 0, TimeSpan.Zero);
        await repo.ScheduleComfortRunAsync("user1", cheaper);

        var entity = await _db.FlexibleScheduleStates.FindAsync("user1");
        Assert.NotNull(entity);
        Assert.Equal(cheaper, entity.NextScheduledComfortUtc);
    }

    #endregion

    #region ClearScheduledComfortAsync

    [Fact]
    public async Task ClearScheduledComfortAsync_ClearsExistingSchedule()
    {
        var scheduled = new DateTimeOffset(2026, 2, 25, 3, 0, 0, TimeSpan.Zero);
        _db.FlexibleScheduleStates.Add(new FlexibleScheduleState
        {
            UserId = "user1",
            NextScheduledComfortUtc = scheduled
        });
        await _db.SaveChangesAsync();

        var repo = CreateRepo();
        await repo.ClearScheduledComfortAsync("user1");

        var entity = await _db.FlexibleScheduleStates.FindAsync("user1");
        Assert.NotNull(entity);
        Assert.Null(entity.NextScheduledComfortUtc);
    }

    [Fact]
    public async Task ClearScheduledComfortAsync_NoopWhenNoSchedule()
    {
        _db.FlexibleScheduleStates.Add(new FlexibleScheduleState
        {
            UserId = "user1"
        });
        await _db.SaveChangesAsync();

        var repo = CreateRepo();
        await repo.ClearScheduledComfortAsync("user1");

        var entity = await _db.FlexibleScheduleStates.FindAsync("user1");
        Assert.NotNull(entity);
        Assert.Null(entity.NextScheduledComfortUtc);
    }

    #endregion
}
