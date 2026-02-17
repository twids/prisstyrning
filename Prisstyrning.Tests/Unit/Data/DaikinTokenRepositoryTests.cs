using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Data.Repositories;

namespace Prisstyrning.Tests.Unit.Data;

public class DaikinTokenRepositoryTests : IDisposable
{
    private readonly PrisstyrningDbContext _db;

    public DaikinTokenRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<PrisstyrningDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PrisstyrningDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() => _db.Dispose();

    private DaikinTokenRepository CreateRepo() => new(_db);

    #region SaveAsync

    [Fact]
    public async Task SaveAsync_NewEntry_CreatesRecord()
    {
        var repo = CreateRepo();
        var expires = DateTimeOffset.UtcNow.AddHours(1);

        await repo.SaveAsync("user1", "access-token-1", "refresh-token-1", expires);

        var token = await _db.DaikinTokens.SingleAsync();
        Assert.Equal("user1", token.UserId);
        Assert.Equal("access-token-1", token.AccessToken);
        Assert.Equal("refresh-token-1", token.RefreshToken);
        Assert.Equal(expires, token.ExpiresAtUtc);
    }

    [Fact]
    public async Task SaveAsync_ExistingEntry_UpdatesRecord()
    {
        var repo = CreateRepo();
        var expires1 = DateTimeOffset.UtcNow.AddHours(1);
        await repo.SaveAsync("user1", "access-1", "refresh-1", expires1);

        var expires2 = DateTimeOffset.UtcNow.AddHours(2);
        await repo.SaveAsync("user1", "access-2", "refresh-2", expires2);

        var count = await _db.DaikinTokens.CountAsync();
        Assert.Equal(1, count);
        var token = await _db.DaikinTokens.SingleAsync();
        Assert.Equal("access-2", token.AccessToken);
        Assert.Equal("refresh-2", token.RefreshToken);
        Assert.Equal(expires2, token.ExpiresAtUtc);
    }

    #endregion

    #region LoadAsync

    [Fact]
    public async Task LoadAsync_Existing_ReturnsToken()
    {
        var repo = CreateRepo();
        var expires = DateTimeOffset.UtcNow.AddHours(1);
        await repo.SaveAsync("user1", "access-tok", "refresh-tok", expires);

        var token = await repo.LoadAsync("user1");

        Assert.NotNull(token);
        Assert.Equal("user1", token.UserId);
        Assert.Equal("access-tok", token.AccessToken);
        Assert.Equal("refresh-tok", token.RefreshToken);
        Assert.Equal(expires, token.ExpiresAtUtc);
    }

    [Fact]
    public async Task LoadAsync_NonExisting_ReturnsNull()
    {
        var repo = CreateRepo();

        var token = await repo.LoadAsync("nonexistent-user");

        Assert.Null(token);
    }

    #endregion

    #region DeleteAsync

    [Fact]
    public async Task DeleteAsync_ExistingEntry_RemovesRecord()
    {
        var repo = CreateRepo();
        await repo.SaveAsync("user1", "access", "refresh", DateTimeOffset.UtcNow.AddHours(1));

        await repo.DeleteAsync("user1");

        var count = await _db.DaikinTokens.CountAsync();
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task DeleteAsync_NonExisting_DoesNotThrow()
    {
        var repo = CreateRepo();

        var exception = await Record.ExceptionAsync(() => repo.DeleteAsync("nonexistent-user"));

        Assert.Null(exception);
    }

    #endregion

    #region GetAllUserIdsAsync

    [Fact]
    public async Task GetAllUserIdsAsync_ReturnsAllUserIds()
    {
        var repo = CreateRepo();
        await repo.SaveAsync("user1", "a1", "r1", DateTimeOffset.UtcNow.AddHours(1));
        await repo.SaveAsync("user2", "a2", "r2", DateTimeOffset.UtcNow.AddHours(1));
        await repo.SaveAsync("user3", "a3", "r3", DateTimeOffset.UtcNow.AddHours(1));

        var userIds = await repo.GetAllUserIdsAsync();

        Assert.Equal(3, userIds.Count);
        Assert.Contains("user1", userIds);
        Assert.Contains("user2", userIds);
        Assert.Contains("user3", userIds);
    }

    [Fact]
    public async Task GetAllUserIdsAsync_EmptyDb_ReturnsEmptyList()
    {
        var repo = CreateRepo();

        var userIds = await repo.GetAllUserIdsAsync();

        Assert.Empty(userIds);
    }

    #endregion
}
