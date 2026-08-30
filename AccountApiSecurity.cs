using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.RateLimiting;

namespace Prisstyrning.Security;

/// <summary>
/// Shared account boundary for production and isolated HTTP tests. Registration
/// does not start the application, database migrations, jobs or control workers.
/// </summary>
public static class AccountApiSecurity
{
    internal static bool IsAccountApiPath(PathString path) =>
        path.StartsWithSegments("/api") || path.StartsWithSegments("/auth/daikin");

    public static IServiceCollection AddAccountSessions(this IServiceCollection services, bool isDevelopment = false)
    {
        services.AddScoped<AccountSessionService>();
        services.AddScoped<AccountCookieEvents>();
        services.AddAuthentication(AccountAuthentication.Scheme)
            .AddCookie(AccountAuthentication.Scheme, options =>
            {
                options.Cookie.Name = "__Host-prisstyrning-session";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = isDevelopment
                    ? CookieSecurePolicy.SameAsRequest
                    : CookieSecurePolicy.Always;
                options.Cookie.Path = "/";
                options.ExpireTimeSpan = AccountAuthentication.InactivityTimeout;
                options.SlidingExpiration = true;
                options.EventsType = typeof(AccountCookieEvents);
            });
        services.AddAuthorization();
        return services;
    }

    public static IServiceCollection AddAdminLoginRateLimiting(this IServiceCollection services)
    {
        // Rate limiting for admin login endpoint (partitioned per remote IP)
        services.AddRateLimiter(options =>
        {
            options.AddPolicy("admin-login", httpContext =>
            {
                var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: remoteIp,
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    });
            });
            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.HttpContext.Response.WriteAsJsonAsync(
                    new { error = "Too many attempts. Please try again later." }, cancellationToken);
            };
        });
        return services;
    }

    public static IApplicationBuilder UseAccountApiSecurity(this IApplicationBuilder app)
    {
        // Fail closed for account APIs. Static login assets and the OAuth entry/callback
        // remain public; no anonymous browser identity is manufactured.
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path;
            var publicApi = path.Equals("/api/session", StringComparison.OrdinalIgnoreCase);
            var publicOAuth = path.Equals("/auth/daikin/start", StringComparison.OrdinalIgnoreCase) ||
                              path.Equals("/auth/daikin/callback", StringComparison.OrdinalIgnoreCase);
            if ((path.StartsWithSegments("/api") && !publicApi || path.StartsWithSegments("/auth/daikin") && !publicOAuth) &&
                ctx.User.Identity?.IsAuthenticated != true)
            {
                await Results.Json(new { error = "Authentication required." }, statusCode: StatusCodes.Status401Unauthorized)
                    .ExecuteAsync(ctx);
                return;
            }
            await next();
        });

        // Double-submit antiforgery protection for every authenticated state-changing API call.
        app.Use(async (ctx, next) =>
        {
            if (ctx.User.Identity?.IsAuthenticated == true &&
                (HttpMethods.IsPost(ctx.Request.Method) || HttpMethods.IsPut(ctx.Request.Method) ||
                 HttpMethods.IsPatch(ctx.Request.Method) || HttpMethods.IsDelete(ctx.Request.Method)))
            {
                if (IsAccountApiPath(ctx.Request.Path))
                {
                    try
                    {
                        await ctx.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(ctx);
                    }
                    catch (AntiforgeryValidationException)
                    {
                        await Results.Json(new { error = "Invalid or missing CSRF token." }, statusCode: StatusCodes.Status400BadRequest)
                            .ExecuteAsync(ctx);
                        return;
                    }
                }
            }
            await next();
        });
        return app;
    }

    public static IEndpointRouteBuilder MapAccountSessionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/session", (HttpContext context, IAntiforgery antiforgery, IConfiguration configuration) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            var userId = AccountAuthentication.UserId(context.User);
            return Results.Ok(new
            {
                authenticated = userId is not null,
                userId,
                isAdmin = userId is not null && AdminService.IsAdmin(configuration, userId),
                csrfToken = tokens.RequestToken
            });
        });

        endpoints.MapPost("/api/session/logout", async (
            HttpContext context,
            AccountSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            await sessions.SignOutAsync(context, cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization();
        return endpoints;
    }
}
