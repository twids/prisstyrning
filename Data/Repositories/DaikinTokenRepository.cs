using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data.Entities;

namespace Prisstyrning.Data.Repositories;

public class DaikinTokenRepository
{
    private readonly PrisstyrningDbContext _db;

    public DaikinTokenRepository(PrisstyrningDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Upsert a Daikin token by userId.
    /// </summary>
    public async Task SaveAsync(string userId, string accessToken, string refreshToken, DateTimeOffset expiresAtUtc, string? daikinSubject = null)
    {
        var existing = await _db.DaikinTokens.FindAsync(userId);

        if (existing != null)
        {
            existing.AccessToken = accessToken;
            existing.RefreshToken = refreshToken;
            existing.ExpiresAtUtc = expiresAtUtc;
            if (daikinSubject != null)
                existing.DaikinSubject = daikinSubject;
        }
        else
        {
            _db.DaikinTokens.Add(new DaikinToken
            {
                UserId = userId,
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                ExpiresAtUtc = expiresAtUtc,
                DaikinSubject = daikinSubject
            });
        }

        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Load a token by userId, or null if not found.
    /// </summary>
    public async Task<DaikinToken?> LoadAsync(string userId)
    {
        return await _db.DaikinTokens.FindAsync(userId);
    }

    /// <summary>
    /// Load a token by Daikin OIDC subject, or null if not found.
    /// </summary>
    public async Task<DaikinToken?> FindByDaikinSubjectAsync(string daikinSubject)
    {
        return await _db.DaikinTokens
            .FirstOrDefaultAsync(t => t.DaikinSubject == daikinSubject);
    }

    /// <summary>
    /// Delete a token by userId. Does not throw if not found.
    /// </summary>
    public async Task DeleteAsync(string userId)
    {
        var existing = await _db.DaikinTokens.FindAsync(userId);
        if (existing != null)
        {
            _db.DaikinTokens.Remove(existing);
            await _db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Get all user IDs that have stored tokens (for the refresh job).
    /// </summary>
    public async Task<List<string>> GetAllUserIdsAsync()
    {
        return await _db.DaikinTokens
            .Select(t => t.UserId)
            .ToListAsync();
    }
}
