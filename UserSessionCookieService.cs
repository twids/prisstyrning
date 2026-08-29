using Microsoft.AspNetCore.DataProtection;

internal sealed class UserSessionCookieService
{
    public const string CookieName = "ps_user";
    public const string HttpContextItemKey = "ps_user.id";
    private const string ProtectedPrefix = "v1.";
    private readonly IDataProtector _protector;

    public UserSessionCookieService(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("Prisstyrning.UserSessionCookie.v1");
    }

    public string Protect(string userId)
    {
        if (!AdminService.IsValidUserId(userId))
            throw new ArgumentException("Invalid user identifier.", nameof(userId));
        return ProtectedPrefix + _protector.Protect(userId);
    }

    public bool TryUnprotect(string? value, out string userId)
    {
        userId = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
            return false;
        try
        {
            var candidate = _protector.Unprotect(value[ProtectedPrefix.Length..]);
            if (!AdminService.IsValidUserId(candidate)) return false;
            userId = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Append(HttpContext context, string userId, bool secure)
    {
        context.Response.Cookies.Append(
            CookieName,
            Protect(userId),
            new CookieOptions
            {
                HttpOnly = true,
                Secure = secure,
                SameSite = SameSiteMode.Lax,
                IsEssential = true,
                Path = "/",
                Expires = DateTimeOffset.UtcNow.AddYears(1)
            });
    }
}
