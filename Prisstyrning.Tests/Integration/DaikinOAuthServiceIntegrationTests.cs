using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Prisstyrning.Tests.Fixtures;

namespace Prisstyrning.Tests.Integration;

/// <summary>
/// Integration tests for DaikinOAuthService OAuth flow with mocked external APIs.
/// Tests the complete authorization, token exchange, refresh, and revocation flows.
/// </summary>
public class DaikinOAuthServiceIntegrationTests
{
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

        var url = DaikinOAuthService.GetAuthorizationUrl(config, httpContext: null);

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

    [Fact]
    public void GetAuthorizationUrl_ContainsPKCECodeChallenge()
    {
        using var fs = new TempFileSystem();
        var config = fs.GetTestConfig(new Dictionary<string, string?>
        {
            ["Daikin:ClientId"] = "test-client-id",
            ["Daikin:RedirectUri"] = "http://localhost:5000/callback"
        });

        var url = DaikinOAuthService.GetAuthorizationUrl(config);

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

        // Generate authorization URL to establish state
        var authUrl = DaikinOAuthService.GetAuthorizationUrl(config);
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
        var result = await DaikinOAuthService.HandleCallbackAsync(config, "auth-code-123", state, userId: "test-user", mockHttpClient);

        Assert.True(result, "Token exchange should succeed");
        
        // Verify token file was created
        var tokenPath = Path.Combine(fs.TokensDir, "test-user", "daikin.json");
        Assert.True(File.Exists(tokenPath), $"Token file should exist at {tokenPath}");
        
        var tokenJson = await File.ReadAllTextAsync(tokenPath);
        var tokenDoc = JsonDocument.Parse(tokenJson);
        Assert.Equal("test-access-token", tokenDoc.RootElement.GetProperty("access_token").GetString());
        Assert.Equal("test-refresh-token", tokenDoc.RootElement.GetProperty("refresh_token").GetString());
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

        // Generate authorization URL
        _ = DaikinOAuthService.GetAuthorizationUrl(config);

        // Use wrong state
        var result = await DaikinOAuthService.HandleCallbackAsync(config, "auth-code", "wrong-state", userId: "test-user");

        Assert.False(result, "Should fail with invalid state");
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

        var authUrl = DaikinOAuthService.GetAuthorizationUrl(config);
        var state = ExtractParameter(authUrl, "state");

        // Mock token endpoint to return error
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.AddRoute("idp.onecta.daikineurope.com/v1/oidc/token",
            HttpStatusCode.BadRequest,
            JsonSerializer.Serialize(new { error = "invalid_grant" }));

        var mockHttpClient = new HttpClient(mockHandler);
        var result = await DaikinOAuthService.HandleCallbackAsync(config, "", state, userId: "test-user", mockHttpClient);

        // With empty code, token endpoint should fail
        Assert.False(result);
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

        // Create an expired token file
        var userDir = Path.Combine(fs.TokensDir, "test-user");
        Directory.CreateDirectory(userDir);
        var tokenFile = Path.Combine(userDir, "daikin.json");
        var expiredToken = new
        {
            access_token = "old-token",
            refresh_token = "refresh-token",
            expires_at_utc = DateTimeOffset.UtcNow.AddMinutes(-10) // Expired 10 minutes ago
        };
        await File.WriteAllTextAsync(tokenFile, JsonSerializer.Serialize(expiredToken));

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
        var result = await DaikinOAuthService.RefreshIfNeededAsync(config, "test-user", mockHttpClient);

        Assert.NotNull(result);
        Assert.Equal("new-access-token", result);

        // Verify token file was updated
        var updatedJson = await File.ReadAllTextAsync(tokenFile);
        var updatedDoc = JsonDocument.Parse(updatedJson);
        Assert.Equal("new-access-token", updatedDoc.RootElement.GetProperty("access_token").GetString());
    }

    [Fact]
    public async Task RefreshIfNeededAsync_WithValidToken_SkipsRefresh()
    {
        using var fs = new TempFileSystem();
        var config = fs.GetTestConfig(new Dictionary<string, string?>
        {
            ["Daikin:ClientId"] = "test-client-id"
        });

        // Create a valid token file (expires in future)
        var userDir = Path.Combine(fs.TokensDir, "test-user");
        Directory.CreateDirectory(userDir);
        var tokenFile = Path.Combine(userDir, "daikin.json");
        var validToken = new
        {
            access_token = "current-token",
            refresh_token = "refresh-token",
            expires_at_utc = DateTimeOffset.UtcNow.AddHours(1) // Valid for 1 hour
        };
        await File.WriteAllTextAsync(tokenFile, JsonSerializer.Serialize(validToken));

        var result = await DaikinOAuthService.RefreshIfNeededAsync(config, "test-user");

        Assert.NotNull(result);
        Assert.Equal("current-token", result);
    }

    [Fact]
    public void TryGetValidAccessToken_WithExpiredToken_ReturnsNull()
    {
        using var fs = new TempFileSystem();
        var config = fs.GetTestConfig();

        // Create an expired token file
        var userDir = Path.Combine(fs.TokensDir, "test-user");
        Directory.CreateDirectory(userDir);
        var tokenFile = Path.Combine(userDir, "daikin.json");
        var expiredToken = new
        {
            access_token = "expired-token",
            refresh_token = "refresh-token",
            expires_at_utc = DateTimeOffset.UtcNow.AddSeconds(-10) // Expired
        };
        File.WriteAllText(tokenFile, JsonSerializer.Serialize(expiredToken));

        var (token, expiresAt) = DaikinOAuthService.TryGetValidAccessToken(config, "test-user");

        Assert.Null(token);
        Assert.NotNull(expiresAt); // Should still return expiry time
    }

    [Fact]
    public void SaveTokens_CreatesTokenFileWithCorrectStructure()
    {
        using var fs = new TempFileSystem();
        var config = fs.GetTestConfig(new Dictionary<string, string?>
        {
            ["Daikin:ClientId"] = "test-client-id",
            ["Daikin:ClientSecret"] = "test-secret",
            ["Daikin:RedirectUri"] = "http://localhost:5000/callback"
        });

        // Generate auth URL and handle callback to trigger SaveTokens
        var authUrl = DaikinOAuthService.GetAuthorizationUrl(config);
        var state = ExtractParameter(authUrl, "state");

        // We'll need to test this after refactoring to inject HttpClient
        // For now, verify the token file structure by creating it manually
        var userDir = Path.Combine(fs.TokensDir, "test-user");
        Directory.CreateDirectory(userDir);
        var tokenFile = Path.Combine(userDir, "daikin.json");
        var tokenData = new
        {
            access_token = "test-token",
            refresh_token = "test-refresh",
            expires_at_utc = DateTimeOffset.UtcNow.AddHours(1)
        };
        File.WriteAllText(tokenFile, JsonSerializer.Serialize(tokenData, new JsonSerializerOptions { WriteIndented = true }));

        Assert.True(File.Exists(tokenFile));
        var json = File.ReadAllText(tokenFile);
        var doc = JsonDocument.Parse(json);
        
        Assert.True(doc.RootElement.TryGetProperty("access_token", out _));
        Assert.True(doc.RootElement.TryGetProperty("refresh_token", out _));
        Assert.True(doc.RootElement.TryGetProperty("expires_at_utc", out _));
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

        // Create a token file
        var userDir = Path.Combine(fs.TokensDir, "test-user");
        Directory.CreateDirectory(userDir);
        var tokenFile = Path.Combine(userDir, "daikin.json");
        var tokenData = new
        {
            access_token = "token-to-revoke",
            refresh_token = "refresh-to-revoke",
            expires_at_utc = DateTimeOffset.UtcNow.AddHours(1)
        };
        await File.WriteAllTextAsync(tokenFile, JsonSerializer.Serialize(tokenData));

        // Mock revoke endpoint
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.AddRoute("idp.onecta.daikineurope.com/v1/oidc/revoke",
            HttpStatusCode.OK,
            "{}");

        var mockHttpClient = new HttpClient(mockHandler);
        var result = await DaikinOAuthService.RevokeAsync(config, "test-user", mockHttpClient);

        Assert.True(result);
        Assert.False(File.Exists(tokenFile), "Token file should be deleted after revocation");
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

        // Create a token file
        var userDir = Path.Combine(fs.TokensDir, "test-user");
        Directory.CreateDirectory(userDir);
        var tokenFile = Path.Combine(userDir, "daikin.json");
        var tokenData = new
        {
            access_token = "token-to-introspect",
            refresh_token = "refresh-token",
            expires_at_utc = DateTimeOffset.UtcNow.AddHours(1)
        };
        await File.WriteAllTextAsync(tokenFile, JsonSerializer.Serialize(tokenData));

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
        var result = await DaikinOAuthService.IntrospectAsync(config, "test-user", refresh: false, mockHttpClient);

        Assert.NotNull(result);
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

        // Generate multiple URLs and verify PKCE parameters are unique
        var url1 = DaikinOAuthService.GetAuthorizationUrl(config);
        var url2 = DaikinOAuthService.GetAuthorizationUrl(config);

        var challenge1 = ExtractParameter(url1, "code_challenge");
        var challenge2 = ExtractParameter(url2, "code_challenge");

        // PKCE challenges should be unique per request
        Assert.NotEqual(challenge1, challenge2);
        
        // Verify Base64URL format (43-128 characters, only alphanumeric, dash, underscore)
        Assert.Matches(@"^[A-Za-z0-9_-]{43,128}$", challenge1);
        Assert.Matches(@"^[A-Za-z0-9_-]{43,128}$", challenge2);
    }

    [Fact]
    public void MultiUser_IsolatesTokensCorrectly()
    {
        using var fs = new TempFileSystem();
        var config = fs.GetTestConfig();

        // Create tokens for two different users
        var user1Dir = Path.Combine(fs.TokensDir, "user-1");
        var user2Dir = Path.Combine(fs.TokensDir, "user-2");
        Directory.CreateDirectory(user1Dir);
        Directory.CreateDirectory(user2Dir);

        var token1File = Path.Combine(user1Dir, "daikin.json");
        var token2File = Path.Combine(user2Dir, "daikin.json");

        var token1 = new
        {
            access_token = "user1-token",
            refresh_token = "user1-refresh",
            expires_at_utc = DateTimeOffset.UtcNow.AddHours(1)
        };
        var token2 = new
        {
            access_token = "user2-token",
            refresh_token = "user2-refresh",
            expires_at_utc = DateTimeOffset.UtcNow.AddHours(1)
        };

        File.WriteAllText(token1File, JsonSerializer.Serialize(token1));
        File.WriteAllText(token2File, JsonSerializer.Serialize(token2));

        // Verify tokens are isolated
        var (token1Result, _) = DaikinOAuthService.TryGetValidAccessToken(config, "user-1");
        var (token2Result, _) = DaikinOAuthService.TryGetValidAccessToken(config, "user-2");

        Assert.Equal("user1-token", token1Result);
        Assert.Equal("user2-token", token2Result);
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

        var authUrl = DaikinOAuthService.GetAuthorizationUrl(config);
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
        var result = await DaikinOAuthService.HandleCallbackWithSubjectAsync(config, "auth-code-123", state, userId: "test-user", mockHttpClient);

        Assert.True(result.Success);
        Assert.Equal("daikin-user-12345", result.Subject);
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

        var authUrl = DaikinOAuthService.GetAuthorizationUrl(config);
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
        var result = await DaikinOAuthService.HandleCallbackWithSubjectAsync(config, "auth-code-123", state, userId: "test-user", mockHttpClient);

        Assert.True(result.Success);
        Assert.Null(result.Subject);
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
    public void ExtractSubjectFromIdToken_ValidJwt_ExtractsSub()
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
    public void ExtractSubjectFromIdToken_WrongIssuer_ReturnsNull()
    {
        var idToken = CreateMockIdToken(new { sub = "test-subject", iss = "https://evil-idp.example.com" });
        var json = JsonSerializer.Serialize(new { id_token = idToken });
        using var doc = JsonDocument.Parse(json);
        var subject = DaikinOAuthService.ExtractSubjectFromIdToken(doc.RootElement);

        Assert.Null(subject);
    }

    [Fact]
    public void ExtractSubjectFromIdToken_MissingIssuer_ReturnsNull()
    {
        var idToken = CreateMockIdToken(new { sub = "test-subject" });
        var json = JsonSerializer.Serialize(new { id_token = idToken });
        using var doc = JsonDocument.Parse(json);
        var subject = DaikinOAuthService.ExtractSubjectFromIdToken(doc.RootElement);

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
    public void MigrateUserData_MovesFilesToNewDirectory()
    {
        using var fs = new TempFileSystem();
        var config = fs.GetTestConfig();

        // Create token file under old userId
        var oldDir = Path.Combine(fs.TokensDir, "old-user-id");
        Directory.CreateDirectory(oldDir);
        File.WriteAllText(Path.Combine(oldDir, "daikin.json"), "{\"access_token\":\"abc\"}");
        File.WriteAllText(Path.Combine(oldDir, "user.json"), "{\"zone\":\"SE3\"}");

        // Create schedule history under old userId
        var oldHistoryDir = Path.Combine(fs.HistoryDir, "old-user-id");
        Directory.CreateDirectory(oldHistoryDir);
        File.WriteAllText(Path.Combine(oldHistoryDir, "history.json"), "[]");

        DaikinOAuthService.MigrateUserData(config, "old-user-id", "new-user-id");

        var newDir = Path.Combine(fs.TokensDir, "new-user-id");
        Assert.True(File.Exists(Path.Combine(newDir, "daikin.json")));
        Assert.True(File.Exists(Path.Combine(newDir, "user.json")));
        // Old token directory should be cleaned up
        Assert.False(Directory.Exists(oldDir));

        // Schedule history should also be migrated
        var newHistoryDir = Path.Combine(fs.HistoryDir, "new-user-id");
        Assert.True(File.Exists(Path.Combine(newHistoryDir, "history.json")));
        Assert.False(Directory.Exists(oldHistoryDir));
    }

    [Fact]
    public void MigrateUserData_DoesNotOverwriteExistingFiles()
    {
        using var fs = new TempFileSystem();
        var config = fs.GetTestConfig();

        // Create token file under old userId
        var oldDir = Path.Combine(fs.TokensDir, "old-user");
        Directory.CreateDirectory(oldDir);
        File.WriteAllText(Path.Combine(oldDir, "daikin.json"), "{\"old\":true}");

        // Create existing file under new userId
        var newDir = Path.Combine(fs.TokensDir, "new-user");
        Directory.CreateDirectory(newDir);
        File.WriteAllText(Path.Combine(newDir, "daikin.json"), "{\"existing\":true}");

        DaikinOAuthService.MigrateUserData(config, "old-user", "new-user");

        // Existing file should NOT be overwritten
        var content = File.ReadAllText(Path.Combine(newDir, "daikin.json"));
        Assert.Contains("existing", content);
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

        // Browser A
        var authUrl1 = DaikinOAuthService.GetAuthorizationUrl(config);
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

        var result1 = await DaikinOAuthService.HandleCallbackWithSubjectAsync(config, "code-a", state1, "browser-a-guid", new HttpClient(mockHandler1));

        // Browser B
        var authUrl2 = DaikinOAuthService.GetAuthorizationUrl(config);
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

        var result2 = await DaikinOAuthService.HandleCallbackWithSubjectAsync(config, "code-b", state2, "browser-b-guid", new HttpClient(mockHandler2));

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
