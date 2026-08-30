using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data.Entities;
using Prisstyrning.Security;
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Prisstyrning.Data.Repositories;

public class DaikinTokenRepository
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> AccountLocks = new(StringComparer.Ordinal);
    private readonly PrisstyrningDbContext _db;
    private readonly IAccountSecretProtector _protector;
    private readonly bool _preserveLegacyPlaintext;

    public DaikinTokenRepository(
        PrisstyrningDbContext db,
        IAccountSecretProtector protector,
        IOptions<CredentialEncryptionOptions> options)
    {
        _db = db;
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _preserveLegacyPlaintext = options.Value.PreserveLegacyDaikinTokenColumns;
    }

    internal DaikinTokenRepository(PrisstyrningDbContext db, IAccountSecretProtector protector)
        : this(db, protector, Options.Create(new CredentialEncryptionOptions
        {
            PreserveLegacyDaikinTokenColumns = false
        }))
    {
    }

    /// <summary>
    /// Upsert a Daikin token by userId.
    /// </summary>
    public async Task SaveAsync(string userId, string accessToken, string refreshToken, DateTimeOffset expiresAtUtc, string? daikinSubject = null)
    {
        if (!AdminService.IsValidUserId(userId)) throw new ArgumentException("Invalid account identifier.", nameof(userId));
        if (!_protector.IsConfigured)
            throw new InvalidOperationException("Credential encryption is required before Daikin credentials can be saved.");
        var accountLock = AccountLocks.GetOrAdd(userId, static _ => new SemaphoreSlim(1, 1));
        await accountLock.WaitAsync();
        try
        {
            var existing = await _db.DaikinTokens.FindAsync(userId);
            var accessCiphertext = _protector.Protect(accessToken, userId, "daikin-access");
            var refreshCiphertext = _protector.Protect(refreshToken, userId, "daikin-refresh");

            if (existing != null)
            {
                existing.AccessToken = _preserveLegacyPlaintext ? accessToken : string.Empty;
                existing.RefreshToken = _preserveLegacyPlaintext ? refreshToken : string.Empty;
                existing.AccessTokenCiphertext = accessCiphertext;
                existing.RefreshTokenCiphertext = refreshCiphertext;
                existing.EncryptionVersion = 1;
                existing.ExpiresAtUtc = expiresAtUtc;
                existing.ConcurrencyStamp = Guid.NewGuid();
                if (daikinSubject != null) existing.DaikinSubject = daikinSubject;
            }
            else
            {
                _db.DaikinTokens.Add(new DaikinToken
                {
                    UserId = userId,
                    AccessToken = _preserveLegacyPlaintext ? accessToken : string.Empty,
                    RefreshToken = _preserveLegacyPlaintext ? refreshToken : string.Empty,
                    AccessTokenCiphertext = accessCiphertext,
                    RefreshTokenCiphertext = refreshCiphertext,
                    EncryptionVersion = 1,
                    ExpiresAtUtc = expiresAtUtc,
                    DaikinSubject = daikinSubject,
                    ConcurrencyStamp = Guid.NewGuid()
                });
            }

            await _db.SaveChangesAsync();
        }
        finally
        {
            accountLock.Release();
        }
    }

    /// <summary>
    /// Load a token by userId, or null if not found.
    /// </summary>
    public async Task<DaikinToken?> LoadAsync(string userId)
    {
        var stored = await _db.DaikinTokens.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId);
        return Materialize(stored);
    }

    /// <summary>
    /// Load a token by Daikin OIDC subject, or null if not found.
    /// </summary>
    public async Task<DaikinToken?> FindByDaikinSubjectAsync(string daikinSubject)
    {
        if (daikinSubject is null)
        {
            throw new ArgumentNullException(nameof(daikinSubject));
        }

        if (string.IsNullOrWhiteSpace(daikinSubject))
        {
            throw new ArgumentException("daikinSubject must not be empty or whitespace.", nameof(daikinSubject));
        }

        var stored = await _db.DaikinTokens.AsNoTracking()
            .FirstOrDefaultAsync(t => t.DaikinSubject == daikinSubject);
        return Materialize(stored);
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

    /// <summary>
    /// Encrypts pre-upgrade credentials idempotently. During the deployment
    /// canary, plaintext columns may be retained so the previous image remains
    /// a viable rollback. Turning the compatibility option off clears them.
    /// </summary>
    public async Task<CredentialStorageReconciliationResult> ReconcileCredentialStorageAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_protector.IsConfigured)
            throw new InvalidOperationException("Credential encryption must be configured before the application starts.");

        var legacy = await _db.DaikinTokens.AsNoTracking()
            .Where(x => x.EncryptionVersion != 1)
            .Select(x => new
            {
                x.UserId,
                x.AccessToken,
                x.RefreshToken,
                x.ExpiresAtUtc,
                x.DaikinSubject
            })
            .ToListAsync(cancellationToken);
        foreach (var token in legacy)
        {
            if (string.IsNullOrWhiteSpace(token.AccessToken) || string.IsNullOrWhiteSpace(token.RefreshToken))
                throw new InvalidOperationException($"Legacy Daikin credentials for account '{token.UserId}' are incomplete and cannot be encrypted.");
            await SaveAsync(token.UserId, token.AccessToken, token.RefreshToken, token.ExpiresAtUtc, token.DaikinSubject);
        }

        var cleared = 0;
        if (!_preserveLegacyPlaintext)
        {
            var encryptedWithPlaintext = await _db.DaikinTokens
                .Where(x => x.EncryptionVersion == 1 && (x.AccessToken != string.Empty || x.RefreshToken != string.Empty))
                .ToListAsync(cancellationToken);
            foreach (var token in encryptedWithPlaintext)
            {
                token.AccessToken = string.Empty;
                token.RefreshToken = string.Empty;
                token.ConcurrencyStamp = Guid.NewGuid();
            }
            if (encryptedWithPlaintext.Count > 0)
            {
                await _db.SaveChangesAsync(cancellationToken);
                cleared = encryptedWithPlaintext.Count;
            }
        }

        return new CredentialStorageReconciliationResult(legacy.Count, cleared, _preserveLegacyPlaintext);
    }

    private DaikinToken? Materialize(DaikinToken? stored)
    {
        if (stored is null) return null;
        if (stored.EncryptionVersion != 1) return stored; // read-only compatibility for one-time migration
        if (!_protector.IsConfigured || string.IsNullOrWhiteSpace(stored.AccessTokenCiphertext) || string.IsNullOrWhiteSpace(stored.RefreshTokenCiphertext))
            throw new InvalidOperationException("Encrypted Daikin credentials cannot be opened because the credential key is unavailable.");
        stored.AccessToken = _protector.Unprotect(stored.AccessTokenCiphertext, stored.UserId, "daikin-access");
        stored.RefreshToken = _protector.Unprotect(stored.RefreshTokenCiphertext, stored.UserId, "daikin-refresh");
        return stored;
    }
}

public sealed record CredentialStorageReconciliationResult(
    int EncryptedCount,
    int PlaintextClearedCount,
    bool LegacyPlaintextPreserved);
