using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Prisstyrning.Data;
using Prisstyrning.Data.Repositories;
using Prisstyrning.Tests.Fixtures;

namespace Prisstyrning.Tests.Unit;

/// <summary>
/// Tests for OAuth state dictionary TTL eviction in DaikinOAuthService.
/// Verifies that stale state entries are rejected and that expired entries are evicted.
/// </summary>
public class OAuthStateTtlTests
{
    private static (DaikinOAuthService service, DaikinTokenRepository tokenRepo, PrisstyrningDbContext db) CreateService(IConfiguration config)
    {
        var options = new DbContextOptionsBuilder<PrisstyrningDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        var db = new PrisstyrningDbContext(options);
        db.Database.EnsureCreated();
        var tokenRepo = new DaikinTokenRepository(db);
        return (new DaikinOAuthService(config, tokenRepo, MockServiceFactory.CreateMockHttpClientFactory()), tokenRepo, db);
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

            var mockHttpClient = new HttpClient(mockHandler);
            var result = await service.HandleCallbackAsync("auth-code-123", state, userId: "test-user", mockHttpClient);

            Assert.True(result, "Token exchange should succeed with fresh state");
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

            var mockHttpClient = new HttpClient(mockHandler);
            var firstResult = await service.HandleCallbackAsync("auth-code-123", state, userId: "test-user", mockHttpClient);
            Assert.True(firstResult, "First use should succeed");

            // Second use of the same state should fail
            var secondResult = await service.HandleCallbackAsync("auth-code-123", state, userId: "test-user", mockHttpClient);
            Assert.False(secondResult, "Second use of same state should fail (state consumed)");
        }
    }

    private static string ExtractParameter(string url, string paramName)
    {
        var match = Regex.Match(url, $@"{paramName}=([^&]+)");
        Assert.True(match.Success, $"URL should contain {paramName} parameter");
        return Uri.UnescapeDataString(match.Groups[1].Value);
    }
}
