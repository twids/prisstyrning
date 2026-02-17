using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Data.Repositories;

namespace Prisstyrning.Tests.Unit.Data;

public class AdminRepositoryTests : IDisposable
{
    private readonly PrisstyrningDbContext _db;
    private readonly IConfiguration _cfg;

    public AdminRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<PrisstyrningDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new PrisstyrningDbContext(options);
        _db.Database.EnsureCreated();

        _cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Admin:Password"] = "test-admin-pass"
            })
            .Build();
    }

    public void Dispose() => _db.Dispose();

    private AdminRepository CreateRepo() => new(_db, _cfg);

    #region IsValidUserId

    [Theory]
    [InlineData("valid-user", true)]
    [InlineData("user_123", true)]
    [InlineData("abc", true)]
    [InlineData("a-b-c", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("   ", false)]
    [InlineData("user with spaces", false)]
    [InlineData("user@email", false)]
    public void IsValidUserId_ValidatesCorrectly(string? userId, bool expected)
    {
        Assert.Equal(expected, AdminRepository.IsValidUserId(userId));
    }

    [Fact]
    public void IsValidUserId_TooLong_ReturnsFalse()
    {
        var longId = new string('a', 101);
        Assert.False(AdminRepository.IsValidUserId(longId));
    }

    [Fact]
    public void IsValidUserId_ExactMaxLength_ReturnsTrue()
    {
        var maxId = new string('a', 100);
        Assert.True(AdminRepository.IsValidUserId(maxId));
    }

    #endregion

    #region IsAdmin

    [Fact]
    public async Task IsAdminAsync_MissingUser_ReturnsFalse()
    {
        var repo = CreateRepo();
        Assert.False(await repo.IsAdminAsync("nonexistent"));
    }

    [Fact]
    public async Task IsAdminAsync_NullUserId_ReturnsFalse()
    {
        var repo = CreateRepo();
        Assert.False(await repo.IsAdminAsync(null));
    }

    [Fact]
    public async Task IsAdminAsync_EmptyUserId_ReturnsFalse()
    {
        var repo = CreateRepo();
        Assert.False(await repo.IsAdminAsync(""));
    }

    [Fact]
    public void IsAdmin_SyncWrapper_Works()
    {
        var repo = CreateRepo();
        Assert.False(repo.IsAdmin("nonexistent"));
    }

    #endregion

    #region GrantAdmin and RevokeAdmin

    [Fact]
    public async Task GrantAdminAsync_NewUser_CreatesRole()
    {
        var repo = CreateRepo();
        await repo.GrantAdminAsync("user1");

        Assert.True(await repo.IsAdminAsync("user1"));
    }

    [Fact]
    public async Task GrantAdminAsync_ExistingUser_UpdatesRole()
    {
        _db.AdminRoles.Add(new AdminRole { UserId = "user1", IsAdmin = false });
        await _db.SaveChangesAsync();

        var repo = CreateRepo();
        await repo.GrantAdminAsync("user1");

        Assert.True(await repo.IsAdminAsync("user1"));
    }

    [Fact]
    public async Task GrantAdminAsync_AlreadyAdmin_DoesNotDuplicate()
    {
        var repo = CreateRepo();
        await repo.GrantAdminAsync("user1");
        await repo.GrantAdminAsync("user1");

        var count = await _db.AdminRoles.CountAsync(r => r.UserId == "user1");
        Assert.Equal(1, count);
        Assert.True(await repo.IsAdminAsync("user1"));
    }

    [Fact]
    public async Task RevokeAdminAsync_RemovesAdmin()
    {
        var repo = CreateRepo();
        await repo.GrantAdminAsync("user1");
        await repo.RevokeAdminAsync("user1");

        Assert.False(await repo.IsAdminAsync("user1"));
    }

    [Fact]
    public async Task RevokeAdminAsync_WithHangfireAccess_KeepsRole()
    {
        _db.AdminRoles.Add(new AdminRole { UserId = "user1", IsAdmin = true, HasHangfireAccess = true });
        await _db.SaveChangesAsync();

        var repo = CreateRepo();
        await repo.RevokeAdminAsync("user1");

        Assert.False(await repo.IsAdminAsync("user1"));
        // Role entry should still exist because HasHangfireAccess is true
        var role = await _db.AdminRoles.FindAsync("user1");
        Assert.NotNull(role);
        Assert.True(role.HasHangfireAccess);
    }

    [Fact]
    public async Task RevokeAdminAsync_NoOtherRoles_RemovesEntity()
    {
        var repo = CreateRepo();
        await repo.GrantAdminAsync("user1");
        await repo.RevokeAdminAsync("user1");

        var role = await _db.AdminRoles.FindAsync("user1");
        Assert.Null(role);
    }

    [Fact]
    public async Task RevokeAdminAsync_NonexistentUser_DoesNotThrow()
    {
        var repo = CreateRepo();
        await repo.RevokeAdminAsync("nonexistent");
        // Should not throw
    }

    #endregion

    #region GetAdminUserIds

    [Fact]
    public async Task GetAdminUserIdsAsync_ReturnsAllAdmins()
    {
        _db.AdminRoles.AddRange(
            new AdminRole { UserId = "admin1", IsAdmin = true },
            new AdminRole { UserId = "not-admin", IsAdmin = false },
            new AdminRole { UserId = "admin2", IsAdmin = true }
        );
        await _db.SaveChangesAsync();

        var repo = CreateRepo();
        var ids = await repo.GetAdminUserIdsAsync();

        Assert.Equal(2, ids.Count);
        Assert.Contains("admin1", ids);
        Assert.Contains("admin2", ids);
    }

    [Fact]
    public void GetAdminUserIds_SyncWrapper_Works()
    {
        var repo = CreateRepo();
        var ids = repo.GetAdminUserIds();
        Assert.Empty(ids);
    }

    #endregion

    #region HangfireAccess

    [Fact]
    public async Task HasHangfireAccessAsync_MissingUser_ReturnsFalse()
    {
        var repo = CreateRepo();
        Assert.False(await repo.HasHangfireAccessAsync("nonexistent"));
    }

    [Fact]
    public async Task HasHangfireAccessAsync_NullUserId_ReturnsFalse()
    {
        var repo = CreateRepo();
        Assert.False(await repo.HasHangfireAccessAsync(null));
    }

    [Fact]
    public async Task GrantHangfireAccessAsync_NewUser_CreatesRole()
    {
        var repo = CreateRepo();
        await repo.GrantHangfireAccessAsync("user1");

        Assert.True(await repo.HasHangfireAccessAsync("user1"));
    }

    [Fact]
    public async Task GrantHangfireAccessAsync_ExistingUser_UpdatesRole()
    {
        _db.AdminRoles.Add(new AdminRole { UserId = "user1", HasHangfireAccess = false });
        await _db.SaveChangesAsync();

        var repo = CreateRepo();
        await repo.GrantHangfireAccessAsync("user1");

        Assert.True(await repo.HasHangfireAccessAsync("user1"));
    }

    [Fact]
    public async Task RevokeHangfireAccessAsync_Revokes()
    {
        var repo = CreateRepo();
        await repo.GrantHangfireAccessAsync("user1");
        await repo.RevokeHangfireAccessAsync("user1");

        Assert.False(await repo.HasHangfireAccessAsync("user1"));
    }

    [Fact]
    public async Task RevokeHangfireAccessAsync_WithAdmin_KeepsRole()
    {
        _db.AdminRoles.Add(new AdminRole { UserId = "user1", IsAdmin = true, HasHangfireAccess = true });
        await _db.SaveChangesAsync();

        var repo = CreateRepo();
        await repo.RevokeHangfireAccessAsync("user1");

        Assert.False(await repo.HasHangfireAccessAsync("user1"));
        var role = await _db.AdminRoles.FindAsync("user1");
        Assert.NotNull(role);
        Assert.True(role.IsAdmin);
    }

    [Fact]
    public async Task RevokeHangfireAccessAsync_NoOtherRoles_RemovesEntity()
    {
        var repo = CreateRepo();
        await repo.GrantHangfireAccessAsync("user1");
        await repo.RevokeHangfireAccessAsync("user1");

        var role = await _db.AdminRoles.FindAsync("user1");
        Assert.Null(role);
    }

    [Fact]
    public void HasHangfireAccess_SyncWrapper_Works()
    {
        var repo = CreateRepo();
        Assert.False(repo.HasHangfireAccess("nonexistent"));
    }

    [Fact]
    public async Task GetHangfireUserIdsAsync_ReturnsCorrectUsers()
    {
        _db.AdminRoles.AddRange(
            new AdminRole { UserId = "hf1", HasHangfireAccess = true },
            new AdminRole { UserId = "nohf", HasHangfireAccess = false },
            new AdminRole { UserId = "hf2", HasHangfireAccess = true }
        );
        await _db.SaveChangesAsync();

        var repo = CreateRepo();
        var ids = await repo.GetHangfireUserIdsAsync();

        Assert.Equal(2, ids.Count);
        Assert.Contains("hf1", ids);
        Assert.Contains("hf2", ids);
    }

    [Fact]
    public void GetHangfireUserIds_SyncWrapper_Works()
    {
        var repo = CreateRepo();
        var ids = repo.GetHangfireUserIds();
        Assert.Empty(ids);
    }

    #endregion

    #region CheckAdminAccess

    [Fact]
    public async Task CheckAdminAccess_DbAdmin_Succeeds()
    {
        var repo = CreateRepo();
        await repo.GrantAdminAsync("user1");

        var (isAdmin, reason) = repo.CheckAdminAccess("user1", null);

        Assert.True(isAdmin);
        Assert.Null(reason);
    }

    [Fact]
    public void CheckAdminAccess_CorrectPassword_Succeeds()
    {
        var repo = CreateRepo();
        var (isAdmin, reason) = repo.CheckAdminAccess(null, "test-admin-pass");

        Assert.True(isAdmin);
        Assert.Null(reason);
    }

    [Fact]
    public void CheckAdminAccess_WrongPassword_Fails()
    {
        var repo = CreateRepo();
        var (isAdmin, reason) = repo.CheckAdminAccess(null, "wrong-password");

        Assert.False(isAdmin);
        Assert.Equal("Unauthorized", reason);
    }

    [Fact]
    public void CheckAdminAccess_NoPasswordConfigured_Fails()
    {
        var emptyCfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var repo = new AdminRepository(_db, emptyCfg);

        var (isAdmin, reason) = repo.CheckAdminAccess(null, "any-password");

        Assert.False(isAdmin);
        Assert.Equal("No admin password configured", reason);
    }

    [Fact]
    public void CheckAdminAccess_NullUserAndNullPassword_Fails()
    {
        var repo = CreateRepo();
        var (isAdmin, _) = repo.CheckAdminAccess(null, null);

        Assert.False(isAdmin);
    }

    #endregion

    #region DeleteAsync

    [Fact]
    public async Task DeleteAsync_RemovesRole()
    {
        _db.AdminRoles.Add(new AdminRole { UserId = "to-delete", IsAdmin = true, HasHangfireAccess = true });
        await _db.SaveChangesAsync();

        var repo = CreateRepo();
        await repo.DeleteAsync("to-delete");

        var role = await _db.AdminRoles.FindAsync("to-delete");
        Assert.Null(role);
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
