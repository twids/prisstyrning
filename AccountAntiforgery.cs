using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Prisstyrning.Security;

public static class AccountAntiforgery
{
    public const string CookieName = "__Host-prisstyrning-csrf";
    public const string HeaderName = "X-CSRF-TOKEN";

    public static IServiceCollection AddAccountAntiforgery(
        this IServiceCollection services,
        bool isDevelopment = false)
    {
        services.AddAntiforgery(options =>
        {
            options.Cookie.Name = CookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = isDevelopment
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;
            options.HeaderName = HeaderName;
        });
        return services;
    }
}
