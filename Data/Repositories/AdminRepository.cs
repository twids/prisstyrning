using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data.Entities;
using Microsoft.Extensions.Configuration;

namespace Prisstyrning.Data.Repositories;

public class AdminRepository
{
    private readonly PrisstyrningDbContext _db;
    private readonly IConfiguration _cfg;

    public AdminRepository(PrisstyrningDbContext db, IConfiguration cfg)
    {
        _db = db;
        _cfg = cfg;
    }

    public static bool IsValidUserId(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;
        if (userId.Length > 100) return false;
        return userId.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_');
    }

    public async Task<bool> IsAdminAsync(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;
        var role = await _db.AdminRoles.FindAsync(userId);
        return role?.IsAdmin ?? false;
    }

    public bool IsAdmin(string? userId)
        => IsAdminAsync(userId).GetAwaiter().GetResult();

    public async Task GrantAdminAsync(string userId)
    {
        var role = await _db.AdminRoles.FindAsync(userId);
        if (role == null)
        {
            role = new AdminRole { UserId = userId, IsAdmin = true };
            _db.AdminRoles.Add(role);
        }
        else
        {
            role.IsAdmin = true;
        }
        await _db.SaveChangesAsync();
    }

    public async Task RevokeAdminAsync(string userId)
    {
        var role = await _db.AdminRoles.FindAsync(userId);
        if (role != null)
        {
            role.IsAdmin = false;
            if (!role.HasHangfireAccess)
                _db.AdminRoles.Remove(role);
            await _db.SaveChangesAsync();
        }
    }

    public async Task<List<string>> GetAdminUserIdsAsync()
    {
        return await _db.AdminRoles
            .Where(r => r.IsAdmin)
            .Select(r => r.UserId)
            .ToListAsync();
    }

    public List<string> GetAdminUserIds()
        => GetAdminUserIdsAsync().GetAwaiter().GetResult();

    public async Task<bool> HasHangfireAccessAsync(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;
        var role = await _db.AdminRoles.FindAsync(userId);
        return role?.HasHangfireAccess ?? false;
    }

    public bool HasHangfireAccess(string? userId)
        => HasHangfireAccessAsync(userId).GetAwaiter().GetResult();

    public async Task GrantHangfireAccessAsync(string userId)
    {
        var role = await _db.AdminRoles.FindAsync(userId);
        if (role == null)
        {
            role = new AdminRole { UserId = userId, HasHangfireAccess = true };
            _db.AdminRoles.Add(role);
        }
        else
        {
            role.HasHangfireAccess = true;
        }
        await _db.SaveChangesAsync();
    }

    public async Task RevokeHangfireAccessAsync(string userId)
    {
        var role = await _db.AdminRoles.FindAsync(userId);
        if (role != null)
        {
            role.HasHangfireAccess = false;
            if (!role.IsAdmin)
                _db.AdminRoles.Remove(role);
            await _db.SaveChangesAsync();
        }
    }

    public async Task<List<string>> GetHangfireUserIdsAsync()
    {
        return await _db.AdminRoles
            .Where(r => r.HasHangfireAccess)
            .Select(r => r.UserId)
            .ToListAsync();
    }

    public List<string> GetHangfireUserIds()
        => GetHangfireUserIdsAsync().GetAwaiter().GetResult();

    public (bool isAdmin, string? reason) CheckAdminAccess(string? userId, string? passwordHeader)
    {
        if (!string.IsNullOrEmpty(userId) && IsAdmin(userId))
            return (true, null);

        var configuredPassword = _cfg["Admin:Password"];
        if (string.IsNullOrEmpty(configuredPassword))
            return (false, "No admin password configured");

        if (!string.IsNullOrEmpty(passwordHeader) && passwordHeader == configuredPassword)
            return (true, null);

        return (false, "Unauthorized");
    }

    public async Task DeleteAsync(string userId)
    {
        var role = await _db.AdminRoles.FindAsync(userId);
        if (role != null)
        {
            _db.AdminRoles.Remove(role);
            await _db.SaveChangesAsync();
        }
    }
}
