using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;

namespace Prisstyrning.Security;

public static class AccountAuthentication
{
    public const string Scheme = "PrisstyrningSession";
    public const string SessionIdClaim = "prisstyrning:session-id";
    public static readonly TimeSpan InactivityTimeout = TimeSpan.FromDays(30);

    public static string? UserId(ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.NameIdentifier) is { } value && AdminService.IsValidUserId(value)
            ? value
            : null;
}

public sealed class AccountSessionService
{
    private readonly PrisstyrningDbContext _db;
    private readonly IConfiguration _configuration;

    public AccountSessionService(PrisstyrningDbContext db, IConfiguration configuration)
    {
        _db = db;
        _configuration = configuration;
    }

    public async Task SignInAsync(HttpContext context, string userId, string subject, CancellationToken cancellationToken = default)
    {
        if (!AdminService.IsValidUserId(userId) || string.IsNullOrWhiteSpace(subject))
            throw new ArgumentException("A verified account identity is required.");

        var now = DateTimeOffset.UtcNow;
        var subjectHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(subject))).ToLowerInvariant();
        var account = await _db.UserAccounts.SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        if (account is null)
        {
            account = new UserAccount
            {
                UserId = userId,
                DaikinSubjectHash = subjectHash,
                CreatedAtUtc = now,
                LastLoginUtc = now
            };
            _db.UserAccounts.Add(account);
        }
        else
        {
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(account.DaikinSubjectHash),
                    Encoding.UTF8.GetBytes(subjectHash)))
                throw new InvalidOperationException("The verified Daikin identity does not match the local account.");
            if (account.Disabled) throw new InvalidOperationException("The account is disabled.");
            account.LastLoginUtc = now;
        }

        var session = new UserSession
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CreatedAtUtc = now,
            LastSeenUtc = now,
            ExpiresAtUtc = now.Add(AccountAuthentication.InactivityTimeout),
            UserAgentHash = HashUserAgent(context.Request.Headers.UserAgent.ToString())
        };
        _db.UserSessions.Add(session);
        await _db.SaveChangesAsync(cancellationToken);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(AccountAuthentication.SessionIdClaim, session.Id.ToString("D"))
        };
        if (AdminService.IsAdmin(_configuration, userId)) claims.Add(new Claim(ClaimTypes.Role, "Administrator"));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, AccountAuthentication.Scheme));
        await context.SignInAsync(AccountAuthentication.Scheme, principal, new AuthenticationProperties
        {
            IsPersistent = true,
            AllowRefresh = true,
            IssuedUtc = now,
            ExpiresUtc = session.ExpiresAtUtc
        });
    }

    public async Task SignOutAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        if (Guid.TryParse(context.User.FindFirstValue(AccountAuthentication.SessionIdClaim), out var sessionId))
        {
            var session = await _db.UserSessions.SingleOrDefaultAsync(x => x.Id == sessionId, cancellationToken);
            if (session is not null && session.RevokedAtUtc is null)
            {
                session.RevokedAtUtc = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(cancellationToken);
            }
        }
        await context.SignOutAsync(AccountAuthentication.Scheme);
    }

    private static string? HashUserAgent(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed class AccountCookieEvents : CookieAuthenticationEvents
{
    private readonly PrisstyrningDbContext _db;

    public AccountCookieEvents(PrisstyrningDbContext db) => _db = db;

    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var userId = AccountAuthentication.UserId(context.Principal!);
        if (userId is null || !Guid.TryParse(context.Principal?.FindFirstValue(AccountAuthentication.SessionIdClaim), out var sessionId))
        {
            await RejectAsync(context);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var session = await _db.UserSessions.SingleOrDefaultAsync(x => x.Id == sessionId && x.UserId == userId, context.HttpContext.RequestAborted);
        var account = await _db.UserAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId, context.HttpContext.RequestAborted);
        if (session is null || session.RevokedAtUtc is not null || session.ExpiresAtUtc <= now || account is null || account.Disabled)
        {
            await RejectAsync(context);
            return;
        }

        if (now - session.LastSeenUtc >= TimeSpan.FromMinutes(5))
        {
            session.LastSeenUtc = now;
            session.ExpiresAtUtc = now.Add(AccountAuthentication.InactivityTimeout);
            await _db.SaveChangesAsync(context.HttpContext.RequestAborted);
        }
    }

    private static async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(AccountAuthentication.Scheme);
    }
}
