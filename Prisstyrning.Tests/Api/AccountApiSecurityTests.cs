using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Net.Http.Headers;
using Prisstyrning.Data;
using Prisstyrning.Security;
using Prisstyrning.Tests.Fixtures;

namespace Prisstyrning.Tests.Api;

public sealed class AccountApiSecurityTests
{
    [Fact]
    public async Task AnonymousSession_IssuesSecureCsrfCookieWithoutCreatingAnAccount()
    {
        await using var host = await AccountApiTestHost.CreateAsync();
        using var browser = host.CreateBrowser();

        using var response = await browser.Client.GetAsync("/api/session");
        var session = await response.Content.ReadFromJsonAsync<TestSessionStatus>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(session);
        Assert.False(session!.Authenticated);
        Assert.Null(session.UserId);
        Assert.False(session.IsAdmin);
        Assert.False(string.IsNullOrWhiteSpace(session.CsrfToken));
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie").Select(value => SetCookieHeaderValue.Parse(value)));
        Assert.Equal(AccountAntiforgery.CookieName, cookie.Name.ToString());
        Assert.True(cookie.Secure);
        Assert.True(cookie.HttpOnly);
        Assert.Equal("Strict", cookie.SameSite.ToString());
        Assert.Equal("/", cookie.Path.ToString());
        Assert.True(response.Headers.CacheControl?.NoStore);
        await host.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<PrisstyrningDbContext>();
            Assert.Equal(0, await db.UserAccounts.CountAsync());
            Assert.Equal(0, await db.UserSessions.CountAsync());
        });
        Assert.Null(host.Services.GetService<BatchRunner>());
        Assert.DoesNotContain(host.Services.GetServices<IHostedService>(), service =>
            service.GetType().Namespace?.StartsWith("Prisstyrning", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData("GET", "/api/thermal/status")]
    [InlineData("GET", "/api/home-assistant/config")]
    [InlineData("GET", "/api/user/settings")]
    [InlineData("GET", "/auth/daikin/status")]
    [InlineData("GET", "/auth/daikin/start/extra")]
    [InlineData("POST", "/api/session/logout")]
    public async Task ProtectedPaths_RejectAnonymousRequestsWithoutLoginRedirect(string method, string path)
    {
        await using var host = await AccountApiTestHost.CreateAsync();
        using var browser = host.CreateBrowser();

        using var response = await browser.MutateAsync(new HttpMethod(method), path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal(0, host.MutationCount);
    }

    [Theory]
    [InlineData("/auth/daikin/start")]
    [InlineData("/auth/daikin/callback")]
    public async Task OAuthEntryAndCallback_RemainPublic(string path)
    {
        await using var host = await AccountApiTestHost.CreateAsync();
        using var browser = host.CreateBrowser();

        using var response = await browser.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task SignedSession_UsesItsVerifiedIdentityAndIgnoresCallerSuppliedAccountIds()
    {
        await using var host = await AccountApiTestHost.CreateAsync();
        using var browser = host.CreateBrowser();
        await browser.SignInAsync("account-a");
        browser.Client.DefaultRequestHeaders.Add("X-User-ID", "account-b");

        using var response = await browser.Client.GetAsync("/api/session?userId=account-b");
        var session = await response.Content.ReadFromJsonAsync<TestSessionStatus>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(session!.Authenticated);
        Assert.Equal("account-a", session.UserId);
        Assert.False(session.IsAdmin);
        Assert.Contains("__Host-prisstyrning-session=", browser.CookieHeader);
    }

    [Theory]
    [InlineData("user_id=account-a")]
    [InlineData("__Host-prisstyrning-session=unsigned-ticket; user_id=account-a")]
    public async Task UnsignedCookiesAndIdentityHeaders_DoNotAuthenticate(string cookie)
    {
        await using var host = await AccountApiTestHost.CreateAsync();
        using var browser = host.CreateBrowser();
        browser.Client.DefaultRequestHeaders.Add("Cookie", cookie);
        browser.Client.DefaultRequestHeaders.Add("X-User-ID", "account-a");

        using var response = await browser.Client.GetAsync("/api/test/account");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("expired")]
    [InlineData("disabled-account")]
    [InlineData("missing-account")]
    [InlineData("missing-session")]
    [InlineData("mismatched-account")]
    public async Task DatabaseSessionValidation_RejectsNoLongerValidSignedCookie(string reason)
    {
        await using var host = await AccountApiTestHost.CreateAsync();
        using var browser = host.CreateBrowser();
        await browser.SignInAsync();
        await host.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<PrisstyrningDbContext>();
            var session = await db.UserSessions.SingleAsync();
            var account = await db.UserAccounts.SingleAsync();
            switch (reason)
            {
                case "revoked": session.RevokedAtUtc = DateTimeOffset.UtcNow; break;
                case "expired": session.ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1); break;
                case "disabled-account": account.Disabled = true; break;
                case "missing-account": db.UserAccounts.Remove(account); break;
                case "missing-session": db.UserSessions.Remove(session); break;
                case "mismatched-account": session.UserId = "account-b"; break;
            }
            await db.SaveChangesAsync();
        });

        var current = await browser.ReadSessionAsync();
        using var response = await browser.Client.GetAsync("/api/test/account");

        Assert.False(current.Authenticated);
        Assert.Null(current.UserId);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("__Host-prisstyrning-session=", browser.CookieHeader);
    }

    [Theory]
    [InlineData("POST", "/api/test/mutation")]
    [InlineData("PUT", "/api/test/mutation")]
    [InlineData("PATCH", "/api/test/mutation")]
    [InlineData("DELETE", "/api/test/mutation")]
    [InlineData("POST", "/auth/daikin/test-action")]
    [InlineData("PUT", "/auth/daikin/test-action")]
    [InlineData("PATCH", "/auth/daikin/test-action")]
    [InlineData("DELETE", "/auth/daikin/test-action")]
    public async Task EveryMutationMethod_RequiresMatchingCsrfBeforeHandlerRuns(string method, string path)
    {
        await using var host = await AccountApiTestHost.CreateAsync();
        using var browser = host.CreateBrowser();
        await browser.SignInAsync();

        using var missing = await browser.MutateAsync(new HttpMethod(method), path);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(0, host.MutationCount);

        using var accepted = await browser.MutateAsync(new HttpMethod(method), path, browser.CsrfToken);
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        Assert.Equal(1, host.MutationCount);
    }

    [Fact]
    public async Task CsrfToken_FromAnonymousOrAnotherAccount_CannotAuthorizeSignedMutation()
    {
        await using var host = await AccountApiTestHost.CreateAsync();
        using var browser = host.CreateBrowser();
        var anonymous = await browser.ReadSessionAsync();
        await browser.SignInAsync("account-a");
        using var other = host.CreateBrowser();
        await other.SignInAsync("account-b");

        using var anonymousReplay = await browser.MutateAsync(HttpMethod.Post, "/api/test/mutation", anonymous.CsrfToken);
        using var otherAccount = await browser.MutateAsync(HttpMethod.Post, "/api/test/mutation", other.CsrfToken);

        Assert.Equal(HttpStatusCode.BadRequest, anonymousReplay.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, otherAccount.StatusCode);
        Assert.Equal(0, host.MutationCount);
    }

    [Fact]
    public async Task Logout_RequiresCsrfRevokesPersistentSessionAndRejectsCookieReplay()
    {
        await using var host = await AccountApiTestHost.CreateAsync();
        using var browser = host.CreateBrowser();
        await browser.SignInAsync();
        var oldCookie = browser.CookieHeader;

        using var rejected = await browser.MutateAsync(HttpMethod.Post, "/api/session/logout");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.True((await browser.ReadSessionAsync()).Authenticated);

        using var accepted = await browser.MutateAsync(HttpMethod.Post, "/api/session/logout", browser.CsrfToken);
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        Assert.False((await browser.ReadSessionAsync()).Authenticated);
        await host.WithServicesAsync(async services =>
            Assert.NotNull((await services.GetRequiredService<PrisstyrningDbContext>().UserSessions.SingleAsync()).RevokedAtUtc));

        using var replay = host.CreateBrowser();
        replay.Client.DefaultRequestHeaders.Add("Cookie", oldCookie);
        using var response = await replay.Client.GetAsync("/api/test/account");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AdminLogin_RequiresSessionCsrfAndPasswordBeforePersistingPermission()
    {
        await using var host = await AccountApiTestHost.CreateAsync(new() { ["Admin:Password"] = "test-admin-password" });
        using var browser = host.CreateBrowser();
        await browser.SignInAsync();

        using var notAdmin = await browser.Client.GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.Unauthorized, notAdmin.StatusCode);
        using var noCsrf = await browser.MutateAsync(HttpMethod.Post, "/api/admin/login", adminPassword: "test-admin-password");
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        Assert.False(AdminService.IsAdmin(host.Configuration, "account-a"));
        using var wrong = await browser.MutateAsync(HttpMethod.Post, "/api/admin/login", browser.CsrfToken, "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.False(AdminService.IsAdmin(host.Configuration, "account-a"));

        using var accepted = await browser.MutateAsync(HttpMethod.Post, "/api/admin/login", browser.CsrfToken, "test-admin-password");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.True((await browser.ReadSessionAsync()).IsAdmin);
        using var allowed = await browser.Client.GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task RoleProtectedApi_Returns403InsteadOfRedirectingAnAuthenticatedNonAdmin()
    {
        await using var host = await AccountApiTestHost.CreateAsync();
        using var browser = host.CreateBrowser();
        await browser.SignInAsync();

        using var response = await browser.Client.GetAsync("/api/test/administrator");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.True((await browser.ReadSessionAsync()).Authenticated);
    }

    [Fact]
    public async Task AdminLogin_EnforcesExistingFiveAttemptsPerMinuteLimit()
    {
        await using var host = await AccountApiTestHost.CreateAsync(new() { ["Admin:Password"] = "test-admin-password" });
        using var browser = host.CreateBrowser();
        await browser.SignInAsync();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var response = await browser.MutateAsync(HttpMethod.Post, "/api/admin/login", browser.CsrfToken, "wrong");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using var limited = await browser.MutateAsync(HttpMethod.Post, "/api/admin/login", browser.CsrfToken, "test-admin-password");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.False(AdminService.IsAdmin(host.Configuration, "account-a"));
    }
}
