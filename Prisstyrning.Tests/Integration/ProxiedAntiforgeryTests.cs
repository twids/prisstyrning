using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using Prisstyrning.Security;

namespace Prisstyrning.Tests.Integration;

/// <summary>
/// Runs the real forwarding middleware and antiforgery service with the production
/// registrations. Keys, users and requests are test-only and kept in memory;
/// Program's database migrations and heating workers are deliberately not started.
/// </summary>
public sealed class ProxiedAntiforgeryTests
{
    [Theory]
    [InlineData("172.19.0.8")]
    [InlineData("::ffff:172.19.0.8")]
    public async Task TrustedHttpsProxy_IssuesSecureCookieAndAcceptsMatchingToken(string proxyIp)
    {
        using var services = Services();
        var issued = await IssueTokensAsync(services, proxyIp);
        var cookie = SetCookieHeaderValue.Parse(issued.Context.Response.Headers.SetCookie.ToString());

        Assert.Equal("https", issued.Context.Request.Scheme);
        Assert.Equal(IPAddress.Parse("198.51.100.27"), issued.Context.Connection.RemoteIpAddress);
        Assert.Equal(AccountAntiforgery.CookieName, cookie.Name.ToString());
        Assert.True(cookie.Secure);
        Assert.True(cookie.HttpOnly);
        Assert.Equal("Strict", cookie.SameSite.ToString());
        Assert.Equal("/", cookie.Path.ToString());
        Assert.False(cookie.Domain.HasValue);

        var post = Request(services, proxyIp, "account-a");
        post.Request.Method = "POST";
        post.Request.Headers.Cookie = issued.CookieHeader;
        post.Request.Headers[AccountAntiforgery.HeaderName] = issued.RequestToken;

        await Pipeline(services, context => services.GetRequiredService<IAntiforgery>()
            .ValidateRequestAsync(context))(post);
    }

    [Theory]
    [InlineData("192.0.2.8", "172.19.0.0/16")]
    [InlineData("172.19.0.8", null)]
    public async Task UntrustedHttpsHeader_CannotTurnPlainHttpIntoSecureSession(
        string proxyIp,
        string? trustedNetworks)
    {
        using var services = Services(trustedNetworks);
        var context = Request(services, proxyIp, "account-a");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Pipeline(services, current =>
            {
                services.GetRequiredService<IAntiforgery>().GetAndStoreTokens(current);
                return Task.CompletedTask;
            })(context));

        Assert.Equal("http", context.Request.Scheme);
        Assert.Equal(IPAddress.Parse(proxyIp), context.Connection.RemoteIpAddress);
        Assert.Empty(context.Response.Headers.SetCookie);
    }

    [Fact]
    public async Task ProxyChain_ConsumesOnlyNearestHopAndNeverForwardsHost()
    {
        using var services = Services();
        var context = Request(services, "172.19.0.8", "account-a");
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.5, 198.51.100.27";
        context.Request.Headers["X-Forwarded-Proto"] = "http, https";
        context.Request.Headers["X-Forwarded-Host"] = "untrusted.example";

        await Pipeline(services, current =>
        {
            services.GetRequiredService<IAntiforgery>().GetAndStoreTokens(current);
            return Task.CompletedTask;
        })(context);

        Assert.Equal("https", context.Request.Scheme);
        Assert.Equal(IPAddress.Parse("198.51.100.27"), context.Connection.RemoteIpAddress);
        Assert.Equal("daikin.example.test", context.Request.Host.Value);
        Assert.Equal("203.0.113.5", context.Request.Headers["X-Forwarded-For"].ToString());
        Assert.Equal("http", context.Request.Headers["X-Forwarded-Proto"].ToString());
    }

    [Fact]
    public async Task DirectHttps_IgnoresSpoofedHeadersFromUntrustedPeer()
    {
        using var services = Services();
        var context = Request(services, "192.0.2.8", "account-a");
        context.Request.Scheme = "https";
        context.Request.Headers["X-Forwarded-Proto"] = "http";

        await Pipeline(services, current =>
        {
            services.GetRequiredService<IAntiforgery>().GetAndStoreTokens(current);
            return Task.CompletedTask;
        })(context);

        Assert.Equal("https", context.Request.Scheme);
        Assert.Equal(IPAddress.Parse("192.0.2.8"), context.Connection.RemoteIpAddress);
        Assert.True(SetCookieHeaderValue.Parse(context.Response.Headers.SetCookie.ToString()).Secure);
    }

    [Theory]
    [InlineData("missing-header")]
    [InlineData("missing-cookie")]
    [InlineData("invalid-token")]
    [InlineData("different-account")]
    public async Task TrustedProxy_DoesNotBypassAntiforgeryValidation(string failure)
    {
        using var services = Services();
        var issued = await IssueTokensAsync(services, "172.19.0.8");
        var post = Request(services, "172.19.0.8", failure == "different-account" ? "account-b" : "account-a");
        post.Request.Method = "POST";
        if (failure != "missing-cookie") post.Request.Headers.Cookie = issued.CookieHeader;
        if (failure != "missing-header")
            post.Request.Headers[AccountAntiforgery.HeaderName] = failure == "invalid-token"
                ? "not-a-valid-request-token"
                : issued.RequestToken;

        await Assert.ThrowsAsync<AntiforgeryValidationException>(() =>
            Pipeline(services, context => services.GetRequiredService<IAntiforgery>()
                .ValidateRequestAsync(context))(post));
    }

    [Fact]
    public async Task Development_RetainsExistingLocalHttpCookiePolicy()
    {
        using var services = Services(isDevelopment: true);
        var context = Request(services, "127.0.0.1", "account-a");
        context.Request.Headers.Clear();

        await Pipeline(services, current =>
        {
            services.GetRequiredService<IAntiforgery>().GetAndStoreTokens(current);
            return Task.CompletedTask;
        })(context);

        Assert.False(SetCookieHeaderValue.Parse(context.Response.Headers.SetCookie.ToString()).Secure);
    }

    private static ServiceProvider Services(string? trustedNetworks = "172.19.0.0/16", bool isDevelopment = false)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [TrustedProxyForwarding.ConfigurationKey] = trustedNetworks }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddTrustedProxyForwarding(configuration);
        services.AddAccountAntiforgery(isDevelopment);
        return services.BuildServiceProvider();
    }

    private static DefaultHttpContext Request(IServiceProvider services, string proxyIp, string userId)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Connection.RemoteIpAddress = IPAddress.Parse(proxyIp);
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("daikin.example.test");
        context.Request.Path = "/api/session";
        context.Request.Method = "GET";
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.27";
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, AccountAuthentication.Scheme));
        return context;
    }

    private static RequestDelegate Pipeline(IServiceProvider services, RequestDelegate handler)
    {
        var builder = new ApplicationBuilder(services);
        builder.UseForwardedHeaders();
        builder.Run(handler);
        return builder.Build();
    }

    private static async Task<IssuedTokens> IssueTokensAsync(IServiceProvider services, string proxyIp)
    {
        var context = Request(services, proxyIp, "account-a");
        string? requestToken = null;
        await Pipeline(services, current =>
        {
            requestToken = services.GetRequiredService<IAntiforgery>().GetAndStoreTokens(current).RequestToken;
            return Task.CompletedTask;
        })(context);
        Assert.NotNull(requestToken);
        var cookie = SetCookieHeaderValue.Parse(context.Response.Headers.SetCookie.ToString());
        return new IssuedTokens(context, $"{cookie.Name}={cookie.Value}", requestToken!);
    }

    private sealed record IssuedTokens(DefaultHttpContext Context, string CookieHeader, string RequestToken);
}
