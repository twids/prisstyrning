using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using Prisstyrning.Data;
using Prisstyrning.Data.Repositories;
using Prisstyrning.Tests.Fixtures;

namespace Prisstyrning.Tests.Unit;

/// <summary>
/// Tests for OAuth state handling in DaikinOAuthService.
/// Verifies that valid state is accepted, unknown state is rejected, state values can only be used once,
/// expired states are rejected, and EvictExpiredStates removes stale entries.
/// </summary>
public class OAuthStateTtlTests
{
    private static (DaikinOAuthService service, DaikinTokenRepository tokenRepo, PrisstyrningDbContext db) CreateService(IConfiguration config, TimeProvider? timeProvider = null)
    {
        var options = new DbContextOptionsBuilder<PrisstyrningDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        var db = new PrisstyrningDbContext(options);
        db.Database.EnsureCreated();
        var tokenRepo = new DaikinTokenRepository(db, TestSecretProtector.Instance);
        return (new DaikinOAuthService(config, tokenRepo, MockServiceFactory.CreateMockHttpClientFactory(), timeProvider), tokenRepo, db);
    }

    private static IConfiguration CreateConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Daikin:ClientId"] = "test-client-id",
                ["Daikin:ClientSecret"] = "test-secret",
                ["Daikin:RedirectUri"] = "http://localhost:5000/callback"
            })
            .Build();

    [Fact]
    public async Task HandleCallback_WithValidState_Succeeds()
    {
        // Arrange: fresh state should work fine
        var config = CreateConfig();
        var (service, tokenRepo, db) = CreateService(config);
        using (db)
        {
            var authUrl = service.GetAuthorizationUrl();
            var state = ExtractParameter(authUrl, "state");

            var mockHandler = new MockHttpMessageHandler();
            mockHandler.AddRoute("idp.onecta.daikineurope.com/v1/oidc/token",
                HttpStatusCode.OK,
                JsonSerializer.Serialize(new
                {
                    access_token = "test-access-token",
                    refresh_token = "test-refresh-token",
                    expires_in = 3600,
                    token_type = "Bearer"
                }));
            MockServiceFactory.AddSuccessfulDaikinIdentity(mockHandler, "state-test-subject");

            var mockHttpClient = new HttpClient(mockHandler);
            var result = await service.HandleCallbackAsync("auth-code-123", state, userId: "test-user", mockHttpClient);

            Assert.True(result, "Token exchange should succeed with fresh state");
        }
    }

    [Fact]
    public void BrowserCorrelation_BindsCallbackStateToTheBrowserThatStartedLogin()
    {
        var (service, _, db) = CreateService(CreateConfig());
        using (db)
        {
            var startContext = new DefaultHttpContext();
            var authUrl = service.GetAuthorizationUrl(startContext);
            var state = ExtractParameter(authUrl, "state");
            Assert.Contains(DaikinOAuthService.CorrelationCookieName, startContext.Response.Headers.SetCookie.ToString());

            var callbackContext = new DefaultHttpContext();
            callbackContext.Request.Headers.Cookie = $"{DaikinOAuthService.CorrelationCookieName}={state}";
            Assert.True(DaikinOAuthService.ValidateBrowserCorrelation(callbackContext, state));

            var unrelatedBrowser = new DefaultHttpContext();
            unrelatedBrowser.Request.Headers.Cookie = $"{DaikinOAuthService.CorrelationCookieName}={new string('0', 32)}";
            Assert.False(DaikinOAuthService.ValidateBrowserCorrelation(unrelatedBrowser, state));
        }
    }

    [Fact]
    public async Task HandleCallback_WithInvalidState_Fails()
    {
        // Arrange: using a completely wrong state should fail
        var config = CreateConfig();
        var (service, _, db) = CreateService(config);
        using (db)
        {
            _ = service.GetAuthorizationUrl();

            var result = await service.HandleCallbackAsync("auth-code", "nonexistent-state", userId: "test-user");

            Assert.False(result, "Should fail with unknown state");
        }
    }

    [Fact]
    public async Task HandleCallback_StateCanOnlyBeUsedOnce()
    {
        // Arrange: state should be consumed on first use (TryRemove)
        var config = CreateConfig();
        var (service, _, db) = CreateService(config);
        using (db)
        {
            var authUrl = service.GetAuthorizationUrl();
            var state = ExtractParameter(authUrl, "state");

            var mockHandler = new MockHttpMessageHandler();
            mockHandler.AddRoute("idp.onecta.daikineurope.com/v1/oidc/token",
                HttpStatusCode.OK,
                JsonSerializer.Serialize(new
                {
                    access_token = "test-access-token",
                    refresh_token = "test-refresh-token",
                    expires_in = 3600,
                    token_type = "Bearer"
                }));
            MockServiceFactory.AddSuccessfulDaikinIdentity(mockHandler, "single-use-subject");

            var mockHttpClient = new HttpClient(mockHandler);
            var firstResult = await service.HandleCallbackAsync("auth-code-123", state, userId: "test-user", mockHttpClient);
            Assert.True(firstResult, "First use should succeed");

            // Second use of the same state should fail
            var secondResult = await service.HandleCallbackAsync("auth-code-123", state, userId: "test-user", mockHttpClient);
            Assert.False(secondResult, "Second use of same state should fail (state consumed)");
        }
    }

    [Fact]
    public async Task HandleCallback_WithExpiredState_Fails()
    {
        // Arrange: create state, then advance time past the TTL
        var config = CreateConfig();
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (service, _, db) = CreateService(config, fakeTime);
        using (db)
        {
            var authUrl = service.GetAuthorizationUrl();
            var state = ExtractParameter(authUrl, "state");

            // Advance time past the 10-minute TTL
            fakeTime.Advance(DaikinOAuthService.StateTtl + TimeSpan.FromSeconds(1));

            var mockHandler = new MockHttpMessageHandler();
            mockHandler.AddRoute("idp.onecta.daikineurope.com/v1/oidc/token",
                HttpStatusCode.OK,
                JsonSerializer.Serialize(new
                {
                    access_token = "test-access-token",
                    refresh_token = "test-refresh-token",
                    expires_in = 3600,
                    token_type = "Bearer"
                }));
            MockServiceFactory.AddSuccessfulDaikinIdentity(mockHandler, "expired-state-subject");

            var mockHttpClient = new HttpClient(mockHandler);
            var result = await service.HandleCallbackAsync("auth-code-123", state, userId: "test-user", mockHttpClient);

            Assert.False(result, "Should fail with expired state (past TTL)");
        }
    }

    [Fact]
    public async Task EvictExpiredStates_RemovesStaleEntries()
    {
        // Arrange: create multiple states, then advance time to expire them
        var config = CreateConfig();
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (service, _, db) = CreateService(config, fakeTime);
        using (db)
        {
            // Create states at current time
            var authUrl1 = service.GetAuthorizationUrl();
            var state1 = ExtractParameter(authUrl1, "state");
            var authUrl2 = service.GetAuthorizationUrl();
            var state2 = ExtractParameter(authUrl2, "state");

            // Advance time past TTL
            fakeTime.Advance(DaikinOAuthService.StateTtl + TimeSpan.FromMinutes(1));

            // Create a fresh state (should survive eviction)
            var authUrl3 = service.GetAuthorizationUrl();
            var state3 = ExtractParameter(authUrl3, "state");

            // Evict should have been called during GetAuthorizationUrl, but call explicitly too
            DaikinOAuthService.EvictExpiredStates(fakeTime);

            // Stale states should have been evicted -- trying to use them should fail
            var result1 = await service.HandleCallbackAsync("code", state1, userId: "u");
            var result2 = await service.HandleCallbackAsync("code", state2, userId: "u");
            Assert.False(result1, "Expired state1 should be evicted");
            Assert.False(result2, "Expired state2 should be evicted");

            // Fresh state3 should still be usable — verify it wasn't evicted by using it with a mock token endpoint
            var mockHandler = new MockHttpMessageHandler();
            mockHandler.AddRoute("idp.onecta.daikineurope.com/v1/oidc/token",
                HttpStatusCode.OK,
                JsonSerializer.Serialize(new
                {
                    access_token = "test-access-token",
                    refresh_token = "test-refresh-token",
                    expires_in = 3600,
                    token_type = "Bearer"
                }));
            MockServiceFactory.AddSuccessfulDaikinIdentity(mockHandler, "fresh-state-subject");
            var mockHttpClient = new HttpClient(mockHandler);
            var result3 = await service.HandleCallbackAsync("code", state3, userId: "u", mockHttpClient);
            Assert.True(result3, "Fresh state3 should survive eviction and succeed");
        }
    }

    private static string ExtractParameter(string url, string paramName)
    {
        var match = Regex.Match(url, $@"{paramName}=([^&]+)");
        Assert.True(match.Success, $"URL should contain {paramName} parameter");
        return Uri.UnescapeDataString(match.Groups[1].Value);
    }
}

/// <summary>
/// A fake TimeProvider for deterministic TTL testing.
/// </summary>
internal class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public FakeTimeProvider(DateTimeOffset startTime)
    {
        _utcNow = startTime;
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan duration)
    {
        _utcNow += duration;
    }
}
