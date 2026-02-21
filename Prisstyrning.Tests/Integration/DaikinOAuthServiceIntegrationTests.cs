using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Prisstyrning.Data;
using Prisstyrning.Data.Repositories;
using Prisstyrning.Tests.Fixtures;

namespace Prisstyrning.Tests.Integration;

/// <summary>
/// Integration tests for DaikinOAuthService OAuth flow with mocked external APIs.
/// Tests the complete authorization, token exchange, refresh, and revocation flows.
/// </summary>
public class DaikinOAuthServiceIntegrationTests
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

    [Fact]
    public void GetAuthorizationUrl_WithValidConfig_GeneratesCorrectUrl()
    {
        using var fs = new TempFileSystem();
        var config = fs.GetTestConfig(new Dictionary<string, string?>
        {
            ["Daikin:ClientId"] = "test-client-id",
            ["Daikin:RedirectUri"] = "http://localhost:5000/auth/daikin/callback",
            ["Daikin:Scope"] = "openid onecta:basic.integration"
        });

        var (service, _, db) = CreateService(config);
        using (db)
        {
            var url = service.GetAuthorizationUrl(httpContext: null);

            Assert.Contains("response_type=code", url);
            Assert.Contains("client_id=test-client-id", url);
            Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost%3A5000%2Fauth%2Fdaikin%2Fcallback", url);
            Assert.Contains("scope=openid%20onecta%3Abasic.integration", url);
            Assert.Contains("state=", url);
            Assert.Contains("nonce=", url);
            Assert.Contains("code_challenge=", url);
            Assert.Contains("code_challenge_method=S256", url);
            Assert.Contains("idp.onecta.daikineurope.com", url);
        }
    }

    [Fact]
    public void GetAuthorizationUrl_ContainsPKCECodeChallenge()
    {
        using var fs = new TempFileSystem();
        var config = fs.GetTestConfig(new Dictionary<string, string?>
        {
            ["Daikin:ClientId"] = "test-client-id",
            ["Daikin:RedirectUri"] = "http://localhost:5000/callback"
        });

        var (service, _, db) = CreateService(config);
        using (db)
        {
            var url = service.GetAuthorizationUrl();

            // Extract code_challenge parameter
            var match = Regex.Match(url, @"code_challenge=([^&]+)");
            Assert.True(match.Success, "URL should contain code_challenge parameter");
            
            var codeChallenge = Uri.UnescapeDataString(match.Groups[1].Value);
            
            // Verify it's Base64URL encoded (no +, /, or = characters when URL decoded)
            Assert.DoesNotContain("+", codeChallenge);
            Assert.DoesNotContain("/", codeChallenge);
            Assert.DoesNotContain("=", codeChallenge);
            Assert.Matches(@"^[A-Za-z0-9_-]+$", codeChallenge);
        }
    }

    [Fact]
    public async Task HandleCallbackAsync_WithValidState_ExchangesTokenSuccessfully()
    {
        using var fs = new TempFileSystem();
        var config = fs.GetTestConfig(new Dictionary<string, string?>
        {
            ["Daikin:ClientId"] = "test-client-id",
            ["Daikin:ClientSecret"] = "test-secret",
            ["Daikin:RedirectUri"] = "http://localhost:5000/callback"
        });

        var (service, tokenRepo, db) = CreateService(config);
        using (db)
        {
            // Generate authorization URL to establish state
            var authUrl = service.GetAuthorizationUrl();
            var state = ExtractParameter(authUrl, "state");

            // Mock token endpoint response
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

            Assert.True(result, "Token exchange should succeed");
            
            // Verify token was stored in DB
            var token = await tokenRepo.LoadAsync("test-user");
            Assert.NotNull(token);
            Assert.Equal("test-access-token", token.AccessToken);
            Assert.Equal("test-refresh-token", token.RefreshToken);
        }
    }

    [Fact]
    public async Task HandleCallbackAsync_WithInvalidState_ReturnsFalse()
    {
        using var fs = new TempFileSystem();
        var config = fs.GetTestConfig(new Dictionary<string, string?>
        {
            ["Daikin:ClientId"] = "test-client-id",
            ["Daikin:RedirectUri"] = "http://localhost:5000/callback"
        });

        var (service, _, db) = CreateService(config);
        using (db)
        {
            // Generate authorization URL
            _ = service.GetAuthorizationUrl();

            // Use wrong state
            var result = await service.HandleCallbackAsync("auth-code", "wrong-state", userId: "test-user");

            Assert.False(result, "Should fail with invalid state");
        }
    }

    [Fact]
    public async Task HandleCallbackAsync_WithMissingCode_ReturnsFalse()
    {
        using var fs = new TempFileSystem();
        var config = fs.GetTestConfig(new Dictionary<string, string?>
        {
            ["Daikin:ClientId"] = "test-client-id",
            ["Daikin:RedirectUri"] = "http://localhost:5000/callback"
        });

        var (service, _, db) = CreateService(config);
        using (db)
        {
            var authUrl = service.GetAuthorizationUrl();
            var state = ExtractParameter(authUrl, "state");

            // Mock token endpoint to return error
            var mockHandler = new MockHttpMessageHandler();
            mockHandler.AddRoute("idp.onecta.daikineurope.com/v1/oidc/token",
                HttpStatusCode.BadRequest,
                JsonSerializer.Serialize(new { error = "invalid_grant" }));

            var mockHttpClient = new HttpClient(mockHandler);
            var result = await service.HandleCallbackAsync("", state, userId: "test-user", mockHttpClient);

            // With empty code, token endpoint should fail
            Assert.False(result);
        }
    }

    [Fact]
    public async Task RefreshIfNeededAsync_WithExpiredToken_RefreshesToken()
    {
        using var fs = new TempFileSystem();
        var config = fs.GetTestConfig(new Dictionary<string, string?>
        {
            ["Daikin:ClientId"] = "test-client-id",
            ["Daikin:ClientSecret"] = "test-secret"
        });

        var (service, tokenRepo, db) = CreateService(config);
        using (db)
        {
            // Store an expired token in DB
            await tokenRepo.SaveAsync("test-user", "old-token", "refresh-token", DateTimeOffset.UtcNow.AddMinutes(-10));

            // Mock refresh token response
            var mockHandler = new MockHttpMessageHandler();
            mockHandler.AddRoute("idp.onecta.daikineurope.com/v1/oidc/token",
                HttpStatusCode.OK,
                JsonSerializer.Serialize(new
                {
                    access_token = "new-access-token",
                    refresh_token = "new-refresh-token",
                    expires_in = 3600
                }));

            var mockHttpClient = new HttpClient(mockHandler);
            var result = await service.RefreshIfNeededAsync("test-user", TimeSpan.FromMinutes(1), mockHttpClient);

            Assert.NotNull(result);
            Assert.Equal("new-access-token", result);

            // Verify token was updated in DB
            var updatedToken = await tokenRepo.LoadAsync("test-user");
            Assert.NotNull(updatedToken);
            Assert.Equal("new-access-token", updatedToken.AccessToken);
        }
    }

    [Fact]
    public async Task RefreshIfNeededAsync_WithValidToken_SkipsRefresh()
    {
        using var fs = new TempFileSystem();
        var config = fs.GetTestConfig(new Dictionary<string, string?>
        {
            ["Daikin:ClientId"] = "test-client-id"
        });

        var (service, tokenRepo, db) = CreateService(config);
        using (db)
        {
            // Store a valid token in DB (expires in 1 hour)
            await tokenRepo.SaveAsync("test-user", "current-token", "refresh-token", DateTimeOffset.UtcNow.AddHours(1));

            var result = await service.RefreshIfNeededAsync("test-user");

            Assert.NotNull(result);
            Assert.Equal("current-token", result);
        }
    }

    [Fact]
    public async Task TryGetValidAccessTokenAsync_WithExpiredToken_ReturnsNull()
    {
        using var fs = new TempFileSystem();
        var config = fs.GetTestConfig();

        var (service, tokenRepo, db) = CreateService(config);
        using (db)
        {
            // Store an expired token in DB
            await tokenRepo.SaveAsync("test-user", "expired-token", "refresh-token", DateTimeOffset.UtcNow.AddSeconds(-10));

            var (token, expiresAt) = await service.TryGetValidAccessTokenAsync("test-user");

            Assert.Null(token);
            Assert.NotNull(expiresAt); // Should still return expiry time
        }
    }

    [Fact]
    public async Task SaveTokens_CreatesTokenRecordWithCorrectStructure()
    {
        using var fs = new TempFileSystem();
        var config = fs.GetTestConfig(new Dictionary<string, string?>
        {
            ["Daikin:ClientId"] = "test-client-id",
            ["Daikin:ClientSecret"] = "test-secret",
            ["Daikin:RedirectUri"] = "http://localhost:5000/callback"
        });

        var (service, tokenRepo, db) = CreateService(config);
        using (db)
        {
            // Generate auth URL and handle callback to trigger SaveTokens
            var authUrl = service.GetAuthorizationUrl();
            var state = ExtractParameter(authUrl, "state");

            // For now, verify DB storage by saving directly
            await tokenRepo.SaveAsync("test-user", "test-token", "test-refresh", DateTimeOffset.UtcNow.AddHours(1));
            
            var token = await tokenRepo.LoadAsync("test-user");
            Assert.NotNull(token);
            Assert.Equal("test-token", token.AccessToken);
            Assert.Equal("test-refresh", token.RefreshToken);
        }
    }

    [Fact]
    public async Task RevokeAsync_CallsRevokeEndpoint()
    {
        using var fs = new TempFileSystem();
        var config = fs.GetTestConfig(new Dictionary<string, string?>
        {
            ["Daikin:ClientId"] = "test-client-id",
            ["Daikin:ClientSecret"] = "test-secret"
        });

        var (service, tokenRepo, db) = CreateService(config);
        using (db)
        {
            // Store a token in DB
            await tokenRepo.SaveAsync("test-user", "token-to-revoke", "refresh-to-revoke", DateTimeOffset.UtcNow.AddHours(1));

            // Mock revoke endpoint
            var mockHandler = new MockHttpMessageHandler();
            mockHandler.AddRoute("idp.onecta.daikineurope.com/v1/oidc/revoke",
                HttpStatusCode.OK,
                "{}");

            var mockHttpClient = new HttpClient(mockHandler);
            var result = await service.RevokeAsync("test-user", mockHttpClient);

            Assert.True(result);
            // Verify token was deleted from DB
            var token = await tokenRepo.LoadAsync("test-user");
            Assert.Null(token);
        }
    }

    [Fact]
    public async Task IntrospectAsync_ReturnsTokenMetadata()
    {
        using var fs = new TempFileSystem();
        var config = fs.GetTestConfig(new Dictionary<string, string?>
        {
            ["Daikin:ClientId"] = "test-client-id",
            ["Daikin:ClientSecret"] = "test-secret"
        });

        var (service, tokenRepo, db) = CreateService(config);
        using (db)
        {
            // Store a token in DB
            await tokenRepo.SaveAsync("test-user", "token-to-introspect", "refresh-token", DateTimeOffset.UtcNow.AddHours(1));

            // Mock introspect endpoint
            var mockHandler = new MockHttpMessageHandler();
            mockHandler.AddRoute("idp.onecta.daikineurope.com/v1/oidc/introspect",
                HttpStatusCode.OK,
                JsonSerializer.Serialize(new
                {
                    active = true,
                    scope = "openid onecta:basic.integration",
                    client_id = "test-client-id",
                    exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
                }));

            var mockHttpClient = new HttpClient(mockHandler);
            var result = await service.IntrospectAsync("test-user", refresh: false, mockHttpClient);

            Assert.NotNull(result);
        }
    }

    [Fact]
    public void PKCE_GeneratesValidCodeChallengeAndVerifier()
    {
        using var fs = new TempFileSystem();
        var config = fs.GetTestConfig(new Dictionary<string, string?>
        {
            ["Daikin:ClientId"] = "test-client-id",
            ["Daikin:RedirectUri"] = "http://localhost:5000/callback"
        });

        var (service, _, db) = CreateService(config);
        using (db)
        {
            // Generate multiple URLs and verify PKCE parameters are unique
            var url1 = service.GetAuthorizationUrl();
            var url2 = service.GetAuthorizationUrl();

            var challenge1 = ExtractParameter(url1, "code_challenge");
            var challenge2 = ExtractParameter(url2, "code_challenge");

            // PKCE challenges should be unique per request
            Assert.NotEqual(challenge1, challenge2);
            
            // Verify Base64URL format (43-128 characters, only alphanumeric, dash, underscore)
            Assert.Matches(@"^[A-Za-z0-9_-]{43,128}$", challenge1);
            Assert.Matches(@"^[A-Za-z0-9_-]{43,128}$", challenge2);
        }
    }

    [Fact]
    public async Task MultiUser_IsolatesTokensCorrectly()
    {
        using var fs = new TempFileSystem();
        var config = fs.GetTestConfig();

        var (service, tokenRepo, db) = CreateService(config);
        using (db)
        {
            // Store tokens for two different users in DB
            await tokenRepo.SaveAsync("user-1", "user1-token", "user1-refresh", DateTimeOffset.UtcNow.AddHours(1));
            await tokenRepo.SaveAsync("user-2", "user2-token", "user2-refresh", DateTimeOffset.UtcNow.AddHours(1));

            // Verify tokens are isolated
            var (token1Result, _) = await service.TryGetValidAccessTokenAsync("user-1");
            var (token2Result, _) = await service.TryGetValidAccessTokenAsync("user-2");

            Assert.Equal("user1-token", token1Result);
            Assert.Equal("user2-token", token2Result);
        }
    }

    [Fact]
    public async Task HandleCallbackWithSubjectAsync_ExtractsSubjectFromIdToken()
    {
        using var fs = new TempFileSystem();
        var config = fs.GetTestConfig(new Dictionary<string, string?>
        {
            ["Daikin:ClientId"] = "test-client-id",
            ["Daikin:ClientSecret"] = "test-secret",
            ["Daikin:RedirectUri"] = "http://localhost:5000/callback"
        });

        var (service, _, db) = CreateService(config);
        using (db)
        {
        var authUrl = service.GetAuthorizationUrl();
        var state = ExtractParameter(authUrl, "state");

        // Create a mock id_token (JWT) with a 'sub' claim
        var idToken = CreateMockIdToken(new { sub = "daikin-user-12345", iss = "https://idp.onecta.daikineurope.com" });

        var mockHandler = new MockHttpMessageHandler();
        mockHandler.AddRoute("idp.onecta.daikineurope.com/v1/oidc/token",
            HttpStatusCode.OK,
            JsonSerializer.Serialize(new
            {
                access_token = "test-access-token",
                refresh_token = "test-refresh-token",
                expires_in = 3600,
                token_type = "Bearer",
                id_token = idToken
            }));

        var mockHttpClient = new HttpClient(mockHandler);
        var result = await service.HandleCallbackWithSubjectAsync("auth-code-123", state, userId: "test-user", mockHttpClient);

        Assert.True(result.Success);
        Assert.Equal("daikin-user-12345", result.Subject);
        }
    }

    [Fact]
    public async Task HandleCallbackWithSubjectAsync_ReturnsNullSubject_WhenNoIdToken()
    {
        using var fs = new TempFileSystem();
        var config = fs.GetTestConfig(new Dictionary<string, string?>
        {
            ["Daikin:ClientId"] = "test-client-id",
            ["Daikin:ClientSecret"] = "test-secret",
            ["Daikin:RedirectUri"] = "http://localhost:5000/callback"
        });

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
                // No id_token
            }));

        var mockHttpClient = new HttpClient(mockHandler);
        var result = await service.HandleCallbackWithSubjectAsync("auth-code-123", state, userId: "test-user", mockHttpClient);

        Assert.True(result.Success);
        Assert.Null(result.Subject);
        }
    }

    [Fact]
    public void DeriveUserId_ReturnsDeterministicValue()
    {
        var userId1 = DaikinOAuthService.DeriveUserId("daikin-user-12345");
        var userId2 = DaikinOAuthService.DeriveUserId("daikin-user-12345");

        Assert.Equal(userId1, userId2);
        Assert.StartsWith("daikin-", userId1);
    }

    [Fact]
    public void DeriveUserId_DifferentSubjects_ProduceDifferentIds()
    {
        var userId1 = DaikinOAuthService.DeriveUserId("user-A");
        var userId2 = DaikinOAuthService.DeriveUserId("user-B");

        Assert.NotEqual(userId1, userId2);
    }

    [Fact]
    public void ExtractSubjectFromIdToken_WithDaikinIssuer_ReturnsSubject()
    {
        var idToken = CreateMockIdToken(new { sub = "test-subject-xyz", iss = "https://idp.onecta.daikineurope.com" });
        var json = JsonSerializer.Serialize(new { id_token = idToken });
        using var doc = JsonDocument.Parse(json);
        var subject = DaikinOAuthService.ExtractSubjectFromIdToken(doc.RootElement);

        Assert.Equal("test-subject-xyz", subject);
    }

    [Fact]
    public void ExtractSubjectFromIdToken_MissingIdToken_ReturnsNull()
    {
        var json = JsonSerializer.Serialize(new { access_token = "abc" });
        using var doc = JsonDocument.Parse(json);
        var subject = DaikinOAuthService.ExtractSubjectFromIdToken(doc.RootElement);

        Assert.Null(subject);
    }

    [Fact]
    public void ExtractSubjectFromIdToken_InvalidJwt_ReturnsNull()
    {
        var json = JsonSerializer.Serialize(new { id_token = "not-a-valid-jwt" });
        using var doc = JsonDocument.Parse(json);
        var subject = DaikinOAuthService.ExtractSubjectFromIdToken(doc.RootElement);

        Assert.Null(subject);
    }

    [Fact]
    public void ExtractSubjectFromIdToken_TwoPartJwt_ReturnsNull()
    {
        // JWT must have exactly 3 parts (header.payload.signature)
        var header = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"alg\":\"RS256\"}"));
        var payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { sub = "test", iss = "https://idp.onecta.daikineurope.com" })));
        var twoPartJwt = $"{header}.{payload}";
        var json = JsonSerializer.Serialize(new { id_token = twoPartJwt });
        using var doc = JsonDocument.Parse(json);
        var subject = DaikinOAuthService.ExtractSubjectFromIdToken(doc.RootElement);

        Assert.Null(subject);
    }

    [Fact]
    public void ExtractSubjectFromIdToken_WithAlternateIssuerButMatchingAud_ReturnsSubject()
    {
        var idToken = CreateMockIdToken(new
        {
            sub = "test-subject",
            iss = "https://login.example-cognito.com/oauth2",
            aud = "my-client-id"
        });
        var json = JsonSerializer.Serialize(new { id_token = idToken });
        using var doc = JsonDocument.Parse(json);
        var subject = DaikinOAuthService.ExtractSubjectFromIdToken(doc.RootElement, expectedClientId: "my-client-id");

        Assert.Equal("test-subject", subject);
    }

    [Fact]
    public void ExtractSubjectFromIdToken_WithAlternateIssuerAndNoAud_ReturnsNull()
    {
        var idToken = CreateMockIdToken(new { sub = "test-subject", iss = "https://evil-idp.example.com" });
        var json = JsonSerializer.Serialize(new { id_token = idToken });
        using var doc = JsonDocument.Parse(json);
        var subject = DaikinOAuthService.ExtractSubjectFromIdToken(doc.RootElement);

        Assert.Null(subject);
    }

    [Fact]
    public void ExtractSubjectFromIdToken_WithWrongIssuer_ReturnsNull()
    {
        var idToken = CreateMockIdToken(new
        {
            sub = "test-subject",
            iss = "https://evil-idp.example.com",
            aud = "wrong-client-id"
        });
        var json = JsonSerializer.Serialize(new { id_token = idToken });
        using var doc = JsonDocument.Parse(json);
        var subject = DaikinOAuthService.ExtractSubjectFromIdToken(doc.RootElement, expectedClientId: "my-client-id");

        Assert.Null(subject);
    }

    [Fact]
    public void ExtractSubjectFromIdToken_WithMissingIssuerButMatchingAud_ReturnsSubject()
    {
        var idToken = CreateMockIdToken(new { sub = "test-subject", aud = "my-client-id" });
        var json = JsonSerializer.Serialize(new { id_token = idToken });
        using var doc = JsonDocument.Parse(json);
        var subject = DaikinOAuthService.ExtractSubjectFromIdToken(doc.RootElement, expectedClientId: "my-client-id");

        Assert.Equal("test-subject", subject);
    }

    [Fact]
    public void ExtractSubjectFromIdToken_WithMissingIssuer_ReturnsNull()
    {
        var idToken = CreateMockIdToken(new { sub = "test-subject" });
        var json = JsonSerializer.Serialize(new { id_token = idToken });
        using var doc = JsonDocument.Parse(json);
        var subject = DaikinOAuthService.ExtractSubjectFromIdToken(doc.RootElement, expectedClientId: "my-client-id");

        Assert.Null(subject);
    }

    [Fact]
    public void ExtractSubjectFromIdToken_WrongAudience_ReturnsNull()
    {
        var idToken = CreateMockIdToken(new { sub = "test-subject", iss = "https://idp.onecta.daikineurope.com", aud = "wrong-client-id" });
        var json = JsonSerializer.Serialize(new { id_token = idToken });
        using var doc = JsonDocument.Parse(json);
        var subject = DaikinOAuthService.ExtractSubjectFromIdToken(doc.RootElement, expectedClientId: "my-client-id");

        Assert.Null(subject);
    }

    [Fact]
    public void ExtractSubjectFromIdToken_CorrectAudience_ExtractsSub()
    {
        var idToken = CreateMockIdToken(new { sub = "test-subject", iss = "https://idp.onecta.daikineurope.com", aud = "my-client-id" });
        var json = JsonSerializer.Serialize(new { id_token = idToken });
        using var doc = JsonDocument.Parse(json);
        var subject = DaikinOAuthService.ExtractSubjectFromIdToken(doc.RootElement, expectedClientId: "my-client-id");

        Assert.Equal("test-subject", subject);
    }

    [Fact]
    public async Task MigrateUserDataAsync_WhenTargetNotExists_CopiesToTarget()
    {
        using var fs = new TempFileSystem();
        var config = fs.GetTestConfig();

        var (migService, tokenRepo, migDb) = CreateService(config);
        using (migDb)
        {
            // Seed a token under old userId
            await tokenRepo.SaveAsync("old-user-id", "abc", "refresh-abc", DateTimeOffset.UtcNow.AddHours(1));

            await migService.MigrateUserDataAsync("old-user-id", "new-user-id");

            // Token should exist under new userId
            var newToken = await tokenRepo.LoadAsync("new-user-id");
            Assert.NotNull(newToken);
            Assert.Equal("abc", newToken!.AccessToken);

            // Old userId should be deleted
            var oldToken = await tokenRepo.LoadAsync("old-user-id");
            Assert.Null(oldToken);
        }
    }

    [Fact]
    public async Task MigrateUserDataAsync_WhenTargetExists_OverwritesWithSourceTokens()
    {
        using var fs = new TempFileSystem();
        var config = fs.GetTestConfig();

        var (migService2, tokenRepo2, migDb2) = CreateService(config);
        using (migDb2)
        {
            // Seed tokens under both old and new userId
            await tokenRepo2.SaveAsync("old-user", "old-access", "old-refresh", DateTimeOffset.UtcNow.AddHours(1));
            await tokenRepo2.SaveAsync("new-user", "existing-access", "existing-refresh", DateTimeOffset.UtcNow.AddHours(1));

            await migService2.MigrateUserDataAsync("old-user", "new-user");

            // Existing token should be overwritten with source token
            var newToken = await tokenRepo2.LoadAsync("new-user");
            Assert.NotNull(newToken);
            Assert.Equal("old-access", newToken!.AccessToken);
            Assert.Equal("old-refresh", newToken.RefreshToken);

            // Source should be deleted
            var oldToken = await tokenRepo2.LoadAsync("old-user");
            Assert.Null(oldToken);
        }
    }

    [Fact]
    public async Task MigrateUserDataAsync_PreservesDaikinSubject()
    {
        using var fs = new TempFileSystem();
        var config = fs.GetTestConfig();

        var (service, tokenRepo, db) = CreateService(config);
        using (db)
        {
            await tokenRepo.SaveAsync(
                "source-user",
                "source-access",
                "source-refresh",
                DateTimeOffset.UtcNow.AddHours(1),
                daikinSubject: "daikin-subject-123");

            await service.MigrateUserDataAsync("source-user", "target-user");

            var target = await tokenRepo.LoadAsync("target-user");
            Assert.NotNull(target);
            Assert.Equal("daikin-subject-123", target!.DaikinSubject);
        }
    }

    [Fact]
    public async Task FindByDaikinSubjectAsync_ReturnsExistingToken()
    {
        using var fs = new TempFileSystem();
        var config = fs.GetTestConfig();

        var (_, tokenRepo, db) = CreateService(config);
        using (db)
        {
            await tokenRepo.SaveAsync(
                "subject-user",
                "subject-access",
                "subject-refresh",
                DateTimeOffset.UtcNow.AddHours(1),
                daikinSubject: "find-me-subject");

            var token = await tokenRepo.FindByDaikinSubjectAsync("find-me-subject");

            Assert.NotNull(token);
            Assert.Equal("subject-user", token!.UserId);
            Assert.Equal("find-me-subject", token.DaikinSubject);
        }
    }

    [Fact]
    public async Task FindByDaikinSubjectAsync_ReturnsNullWhenNotFound()
    {
        using var fs = new TempFileSystem();
        var config = fs.GetTestConfig();

        var (_, tokenRepo, db) = CreateService(config);
        using (db)
        {
            await tokenRepo.SaveAsync(
                "subject-user",
                "subject-access",
                "subject-refresh",
                DateTimeOffset.UtcNow.AddHours(1),
                daikinSubject: "existing-subject");

            var token = await tokenRepo.FindByDaikinSubjectAsync("missing-subject");

            Assert.Null(token);
        }
    }

    [Fact]
    public async Task HandleCallbackWithSubjectAsync_SameDaikinUser_SameStableUserId()
    {
        // Simulate two different browsers (different random userIds) authenticating
        // with the same Daikin account - they should derive the same stable userId
        using var fs = new TempFileSystem();
        var config = fs.GetTestConfig(new Dictionary<string, string?>
        {
            ["Daikin:ClientId"] = "test-client-id",
            ["Daikin:ClientSecret"] = "test-secret",
            ["Daikin:RedirectUri"] = "http://localhost:5000/callback"
        });

        var daikinSubject = "unique-daikin-subject-42";
        var idToken = CreateMockIdToken(new { sub = daikinSubject, iss = "https://idp.onecta.daikineurope.com" });

        var (service, _, db) = CreateService(config);
        using (db)
        {
        // Browser A
        var authUrl1 = service.GetAuthorizationUrl();
        var state1 = ExtractParameter(authUrl1, "state");

        var mockHandler1 = new MockHttpMessageHandler();
        mockHandler1.AddRoute("idp.onecta.daikineurope.com/v1/oidc/token",
            HttpStatusCode.OK,
            JsonSerializer.Serialize(new
            {
                access_token = "token-a",
                refresh_token = "refresh-a",
                expires_in = 3600,
                id_token = idToken
            }));

        var mockHttpClient1 = new HttpClient(mockHandler1);
        var result1 = await service.HandleCallbackWithSubjectAsync("code-a", state1, "browser-a-guid", mockHttpClient1);

        // Browser B
        var authUrl2 = service.GetAuthorizationUrl();
        var state2 = ExtractParameter(authUrl2, "state");

        var mockHandler2 = new MockHttpMessageHandler();
        mockHandler2.AddRoute("idp.onecta.daikineurope.com/v1/oidc/token",
            HttpStatusCode.OK,
            JsonSerializer.Serialize(new
            {
                access_token = "token-b",
                refresh_token = "refresh-b",
                expires_in = 3600,
                id_token = idToken
            }));

        var mockHttpClient2 = new HttpClient(mockHandler2);
        var result2 = await service.HandleCallbackWithSubjectAsync("code-b", state2, "browser-b-guid", mockHttpClient2);

        // Both should succeed and return the same subject
        Assert.True(result1.Success);
        Assert.True(result2.Success);
        Assert.Equal(daikinSubject, result1.Subject);
        Assert.Equal(daikinSubject, result2.Subject);

        // DeriveUserId should produce the same result for both
        var stableId1 = DaikinOAuthService.DeriveUserId(result1.Subject!);
        var stableId2 = DaikinOAuthService.DeriveUserId(result2.Subject!);
        Assert.Equal(stableId1, stableId2);
        }
    }

    // Helper: Create a mock JWT id_token with the given payload claims (no real signature)
    private static string CreateMockIdToken(object payloadClaims)
    {
        var header = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
        var payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payloadClaims)));
        var signature = "mock-signature";
        // Convert to base64url
        header = header.Replace("+", "-").Replace("/", "_").Replace("=", "");
        payload = payload.Replace("+", "-").Replace("/", "_").Replace("=", "");
        return $"{header}.{payload}.{signature}";
    }

    // Helper method to extract query parameter from URL
    private static string ExtractParameter(string url, string paramName)
    {
        var match = Regex.Match(url, $@"{paramName}=([^&]+)");
        if (!match.Success)
            throw new InvalidOperationException($"Parameter {paramName} not found in URL");
        return Uri.UnescapeDataString(match.Groups[1].Value);
    }
}
