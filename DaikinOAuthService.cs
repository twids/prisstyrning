using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;

internal static class DaikinOAuthService
{
    private static readonly object _lock = new();
    private static readonly Dictionary<string,string> _stateToVerifier = new();

    private const string DefaultAuthEndpoint = "https://idp.onecta.daikineurope.com/v1/oidc/authorize"; // OIDC authorize
    private const string DefaultTokenEndpoint = "https://idp.onecta.daikineurope.com/v1/oidc/token";   // token
    private const string DefaultRevokeEndpoint = "https://idp.onecta.daikineurope.com/v1/oidc/revoke"; // revoke
    private const string DefaultIntrospectEndpoint = "https://idp.onecta.daikineurope.com/v1/oidc/introspect"; // introspect

    private record TokenFile(string access_token, string refresh_token, DateTimeOffset expires_at_utc);

    /// <summary>Result of the OAuth callback token exchange, including the OIDC subject if available.</summary>
    public record CallbackResult(bool Success, string? Subject);

    // Helper to derive a user specific token file path segment (sanitized)
    private static string SanitizeUser(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return string.Empty; // global (legacy)
        // keep only hex and dash to avoid path traversal
        var clean = new string(userId.Where(c => char.IsLetterOrDigit(c) || c=='-' ).ToArray());
        return clean.Length == 0 ? string.Empty : clean;
    }

    public static string GetAuthorizationUrl(IConfiguration cfg, HttpContext? httpContext = null)
    {
        var clientId = cfg["Daikin:ClientId"] ?? throw new InvalidOperationException("Daikin:ClientId saknas");
        var redirectUri = ResolveRedirectUri(cfg, httpContext);
        var includeOffline = (cfg["Daikin:IncludeOfflineAccess"] ?? "false").Equals("true", StringComparison.OrdinalIgnoreCase);
        var scopeCfg = cfg["Daikin:Scope"];
        var scope = string.IsNullOrWhiteSpace(scopeCfg) ? "openid onecta:basic.integration" : scopeCfg!;
        if (includeOffline && !scope.Split(' ',StringSplitOptions.RemoveEmptyEntries).Contains("offline_access"))
            scope += " offline_access";
        var authEndpoint = cfg["Daikin:AuthEndpoint"] ?? DefaultAuthEndpoint;
        var state = Guid.NewGuid().ToString("N");
        var nonce = Guid.NewGuid().ToString("N");
        var (codeChallenge, verifier) = CreatePkcePair();
        lock(_lock) _stateToVerifier[state] = verifier;
        var url = $"{authEndpoint}?response_type=code&client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(redirectUri)}&scope={Uri.EscapeDataString(scope)}&state={state}&nonce={nonce}&code_challenge={codeChallenge}&code_challenge_method=S256";
        Console.WriteLine($"[DaikinOAuth] Built authorize URL (host only) authHost={new Uri(authEndpoint).Host} redirect={redirectUri} scope='{scope}' state={state.Substring(0,8)}... offline={(includeOffline?"yes":"no")}");
        return url;
    }

    // Minimal variant utan state/PKCE/nonce – ENDAST för felsökning av 403.
    public static string GetMinimalAuthorizationUrl(IConfiguration cfg, HttpContext? httpContext = null)
    {
        var clientId = cfg["Daikin:ClientId"] ?? throw new InvalidOperationException("Daikin:ClientId saknas");
        var redirectUri = ResolveRedirectUri(cfg, httpContext);
        var scopeCfg = cfg["Daikin:Scope"];
        var scope = string.IsNullOrWhiteSpace(scopeCfg) ? "openid onecta:basic.integration" : scopeCfg!;
        // OBS: inget state, ingen PKCE – använd ej i produktion.
        var authEndpoint = cfg["Daikin:AuthEndpoint"] ?? DefaultAuthEndpoint;
        var url = $"{authEndpoint}?response_type=code&client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(redirectUri)}&scope={Uri.EscapeDataString(scope)}";
        Console.WriteLine($"[DaikinOAuth][MIN] URL => {url}");
        return url;
    }

    public static async Task<bool> HandleCallbackAsync(IConfiguration cfg, string code, string state, HttpClient? httpClient = null) => (await HandleCallbackWithSubjectAsync(cfg, code, state, userId: null, httpClient)).Success;
    public static async Task<bool> HandleCallbackAsync(IConfiguration cfg, string code, string state, string? userId, HttpClient? httpClient = null) => (await HandleCallbackWithSubjectAsync(cfg, code, state, userId, httpClient)).Success;
    public static async Task<CallbackResult> HandleCallbackWithSubjectAsync(IConfiguration cfg, string code, string state, string? userId, HttpClient? httpClient = null)
    {
        string? verifier;
        lock(_lock)
        {
            if(!_stateToVerifier.TryGetValue(state, out verifier)) return new CallbackResult(false, null);
            _stateToVerifier.Remove(state);
        }
    var clientId = cfg["Daikin:ClientId"] ?? throw new InvalidOperationException("Daikin:ClientId saknas");
    var redirectUri = ResolveRedirectUri(cfg, null);
    var clientSecret = cfg["Daikin:ClientSecret"];
    var tokenEndpoint = cfg["Daikin:TokenEndpoint"] ?? DefaultTokenEndpoint;
        var form = new Dictionary<string,string>{
            ["grant_type"]="authorization_code",
            ["code"] = code,
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier!
        };
        if(!string.IsNullOrWhiteSpace(clientSecret))
            form["client_secret"] = clientSecret;
        var http = httpClient ?? new HttpClient();
        try
        {
            Console.WriteLine($"[DaikinOAuth] Exchanging code for tokens at {tokenEndpoint}");
            using var resp = await http.PostAsync(tokenEndpoint, new FormUrlEncodedContent(form));
            if(!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                Console.WriteLine($"[DaikinOAuth][Error] Token exchange failed {(int)resp.StatusCode} {resp.StatusCode}");
                return new CallbackResult(false, null);
            }
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var access = root.GetProperty("access_token").GetString()!;
            var refresh = root.GetProperty("refresh_token").GetString()!;
            var expiresIn = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
            var expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 30);
            var subject = ExtractSubjectFromIdToken(root, clientId);
            SaveTokens(cfg, new TokenFile(access, refresh, expiresAt), userId);
            Console.WriteLine($"[DaikinOAuth] Token exchange OK expiresAt={expiresAt:O} refresh={(refresh?.Length>8?refresh[..8]+"...":"(none)")} hasSubject={subject != null}");
            return new CallbackResult(true, subject);
        }
        finally
        {
            if (httpClient == null) http.Dispose();
        }
    }

    public static (string? accessToken, DateTimeOffset? expiresAt) TryGetValidAccessToken(IConfiguration cfg) => TryGetValidAccessToken(cfg, null);
    public static (string? accessToken, DateTimeOffset? expiresAt) TryGetValidAccessToken(IConfiguration cfg, string? userId)
    {
        var tf = LoadTokens(cfg, userId);
        if(tf == null) return (null,null);
        if(tf.expires_at_utc > DateTimeOffset.UtcNow.AddMinutes(1)) return (tf.access_token, tf.expires_at_utc);
        return (null, tf.expires_at_utc);
    }

    public static async Task<string?> RefreshIfNeededAsync(IConfiguration cfg, HttpClient? httpClient = null) => await RefreshIfNeededAsync(cfg, null, httpClient);
    public static async Task<string?> RefreshIfNeededAsync(IConfiguration cfg, string? userId, HttpClient? httpClient = null) => await RefreshIfNeededAsync(cfg, userId, TimeSpan.FromMinutes(1), httpClient);

    /// <summary>
    /// Refresh the access token if it will expire within the provided window.
    /// Default window in existing calls is 1 minute; callers can request a larger proactive window.
    /// </summary>
    public static async Task<string?> RefreshIfNeededAsync(IConfiguration cfg, string? userId, TimeSpan window, HttpClient? httpClient = null)
    {
        var existing = LoadTokens(cfg, userId);
        if (existing == null) return null;
        if (existing.expires_at_utc > DateTimeOffset.UtcNow.Add(window)) return existing.access_token; // still valid enough
        var clientId = cfg["Daikin:ClientId"] ?? throw new InvalidOperationException("Daikin:ClientId saknas");
        var clientSecret = cfg["Daikin:ClientSecret"];
        var tokenEndpoint = cfg["Daikin:TokenEndpoint"] ?? DefaultTokenEndpoint;
        var form = new Dictionary<string,string>{
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = existing.refresh_token,
            ["client_id"] = clientId
        };
        if(!string.IsNullOrWhiteSpace(clientSecret)) form["client_secret"] = clientSecret;
        
        var http = httpClient ?? new HttpClient();
        try
        {
            Console.WriteLine($"[DaikinOAuth] Refreshing token at {tokenEndpoint} (window={window})");
            var resp = await http.PostAsync(tokenEndpoint, new FormUrlEncodedContent(form));
            if(!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                Console.WriteLine($"[DaikinOAuth][Error] refresh failed {(int)resp.StatusCode} {resp.StatusCode}");
                return null;
            }
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var access = root.GetProperty("access_token").GetString()!;
            var refresh = root.TryGetProperty("refresh_token", out var rEl)? rEl.GetString() ?? existing.refresh_token : existing.refresh_token;
            var expiresIn = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
            var expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 30);
            SaveTokens(cfg, new TokenFile(access, refresh!, expiresAt), userId);
            Console.WriteLine($"[DaikinOAuth] Refresh OK newExpiry={expiresAt:O}");
            return access;
        }
        finally
        {
            if (httpClient == null) http.Dispose();
        }
    }

    public static object Status(IConfiguration cfg) => Status(cfg, null);
    public static object Status(IConfiguration cfg, string? userId)
    {
        var t = LoadTokens(cfg, userId);
        if(t == null) return new { authorized = false };
        return new {
            authorized = true,
            expiresAtUtc = t.expires_at_utc,
            expiresInSeconds = (int)Math.Max(0,(t.expires_at_utc - DateTimeOffset.UtcNow).TotalSeconds)
        };
    }

    public static async Task<bool> RevokeAsync(IConfiguration cfg, HttpClient? httpClient = null) => await RevokeAsync(cfg, null, httpClient);
    public static async Task<bool> RevokeAsync(IConfiguration cfg, string? userId, HttpClient? httpClient = null)
    {
        var t = LoadTokens(cfg, userId);
        if (t == null) return false;
        var clientId = cfg["Daikin:ClientId"] ?? throw new InvalidOperationException("Daikin:ClientId saknas");
        var clientSecret = cfg["Daikin:ClientSecret"];
        var revokeEndpoint = cfg["Daikin:RevokeEndpoint"] ?? DefaultRevokeEndpoint;
        
        var http = httpClient ?? new HttpClient();
        try
        {
            var okAccess = await RevokeToken(http, revokeEndpoint, clientId, clientSecret, t.access_token, "access_token");
            var okRefresh = await RevokeToken(http, revokeEndpoint, clientId, clientSecret, t.refresh_token, "refresh_token");
            if(okAccess || okRefresh)
            {
                try 
                { 
                    File.Delete(TokenFilePath(cfg, userId)); 
                } 
                catch (Exception ex)
                {
                    Console.WriteLine($"[DaikinOAuth] Failed to delete token file: {ex.Message}");
                }
            }
            return okAccess && okRefresh;
        }
        finally
        {
            if (httpClient == null) http.Dispose();
        } // true only if both succeeded
    }

    private static async Task<bool> RevokeToken(HttpClient http, string endpoint, string clientId, string? secret, string token, string hint)
    {
        var form = new Dictionary<string,string>{
            ["token"] = token,
            ["client_id"] = clientId,
            ["token_type_hint"] = hint
        };
        if(!string.IsNullOrWhiteSpace(secret)) form["client_secret"] = secret!;
        var resp = await http.PostAsync(endpoint, new FormUrlEncodedContent(form));
        Console.WriteLine($"[DaikinOAuth] Revoke {hint} => {(int)resp.StatusCode} {resp.StatusCode}");
        return resp.IsSuccessStatusCode;
    }

    public static async Task<object?> IntrospectAsync(IConfiguration cfg, bool refresh=false, HttpClient? httpClient = null) => await IntrospectAsync(cfg, null, refresh, httpClient);
    public static async Task<object?> IntrospectAsync(IConfiguration cfg, string? userId, bool refresh=false, HttpClient? httpClient = null)
    {
        var t = LoadTokens(cfg, userId); if(t==null) return null;
        var clientId = cfg["Daikin:ClientId"] ?? throw new InvalidOperationException("Daikin:ClientId saknas");
        var clientSecret = cfg["Daikin:ClientSecret"] ?? throw new InvalidOperationException("ClientSecret krävs för introspection");
        var introspectEndpoint = cfg["Daikin:IntrospectEndpoint"] ?? DefaultIntrospectEndpoint;
        var token = refresh ? t.refresh_token : t.access_token;
        
        var http = httpClient ?? new HttpClient();
        try
        {
            var bytes = Encoding.ASCII.GetBytes(clientId+":"+clientSecret);
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(bytes));
            var form = new Dictionary<string,string>{{"token", token}};
            var resp = await http.PostAsync(introspectEndpoint, new FormUrlEncodedContent(form));
            var body = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"[DaikinOAuth] Introspect {(refresh?"refresh":"access")} => {(int)resp.StatusCode}");
            try 
            { 
                return JsonSerializer.Deserialize<JsonElement>(body); 
            } 
            catch 
            { 
                // Don't log potentially sensitive OAuth response body
                return new { error = "Failed to parse introspect response", statusCode = (int)resp.StatusCode }; 
            }
        }
        finally
        {
            if (httpClient == null) http.Dispose();
        }
    }

    public static string? GetAccessTokenUnsafe(IConfiguration cfg) => GetAccessTokenUnsafe(cfg, null);
    public static string? GetAccessTokenUnsafe(IConfiguration cfg, string? userId) => LoadTokens(cfg, userId)?.access_token;

    /// <summary>
    /// Derives a deterministic, browser-agnostic userId from the OIDC subject claim.
    /// Returns a sanitized string like "daikin-{hex}" suitable for use as a directory name and cookie value.
    /// </summary>
    public static string DeriveUserId(string subject)
    {
        // Use a hash to keep the id short and filesystem-safe while still being deterministic
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(subject));
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return $"daikin-{hex[..32]}"; // 32 hex chars = 128 bits to keep collision probability negligible
    }

    /// <summary>
    /// Extracts the 'sub' (subject) claim from the OIDC id_token in the token response.
    /// The id_token is a JWT whose payload is base64url-encoded JSON.
    /// We validate the JWT has the expected 3-part structure and that iss matches the
    /// Daikin IDP. Full signature verification is not performed because the token was
    /// just received over TLS from the trusted IDP during the authorization code exchange.
    /// </summary>
    internal static string? ExtractSubjectFromIdToken(JsonElement tokenResponseRoot, string? expectedClientId = null)
    {
        try
        {
            if (!tokenResponseRoot.TryGetProperty("id_token", out var idTokenEl)) return null;
            var idToken = idTokenEl.GetString();
            if (string.IsNullOrWhiteSpace(idToken)) return null;

            // JWT must have exactly 3 parts: header.payload.signature
            var parts = idToken.Split('.');
            if (parts.Length != 3) return null;

            // Decode the payload (second part)
            var payload = parts[1];
            // Add padding for base64
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }
            payload = payload.Replace('-', '+').Replace('_', '/');
            var bytes = Convert.FromBase64String(payload);
            using var payloadDoc = JsonDocument.Parse(bytes);
            var root = payloadDoc.RootElement;

            // Validate issuer - must be the Daikin OIDC IDP
            if (root.TryGetProperty("iss", out var issEl))
            {
                var iss = issEl.GetString();
                if (string.IsNullOrEmpty(iss) || !iss.Contains("daikineurope.com", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"[DaikinOAuth] id_token issuer rejected");
                    return null;
                }
            }
            else
            {
                Console.WriteLine($"[DaikinOAuth] id_token missing 'iss' claim");
                return null;
            }

            // Validate audience matches our client_id (if we know it)
            if (!string.IsNullOrEmpty(expectedClientId) && root.TryGetProperty("aud", out var audEl))
            {
                var aud = audEl.ValueKind == JsonValueKind.String ? audEl.GetString() : null;
                if (aud != null && aud != expectedClientId)
                {
                    Console.WriteLine($"[DaikinOAuth] id_token audience mismatch");
                    return null;
                }
            }

            if (root.TryGetProperty("sub", out var subEl))
            {
                var sub = subEl.GetString();
                if (!string.IsNullOrWhiteSpace(sub)) return sub;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DaikinOAuth] Failed to extract subject from id_token: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Migrates user data (tokens, settings, schedule history) from one userId directory to another.
    /// Used when remapping a browser-generated random userId to a deterministic Daikin-based userId.
    /// If destination already exists, only moves files that don't exist in the destination.
    /// </summary>
    public static void MigrateUserData(IConfiguration cfg, string fromUserId, string toUserId)
    {
        if (string.IsNullOrWhiteSpace(fromUserId) || string.IsNullOrWhiteSpace(toUserId)) return;
        if (fromUserId == toUserId) return;

        var sanitizedFrom = SanitizeUser(fromUserId);
        var sanitizedTo = SanitizeUser(toUserId);

        // 1) Migrate token directory
        var fromTokenDir = StoragePaths.GetUserTokenDir(sanitizedFrom, cfg);
        var toTokenDir = StoragePaths.GetUserTokenDir(sanitizedTo, cfg);
        MigratePerUserDirectory(fromTokenDir, toTokenDir);

        // 2) Migrate schedule history directory
        try
        {
            var historyBaseDir = StoragePaths.GetScheduleHistoryDir(cfg);
            var fromHistoryDir = Path.Combine(historyBaseDir, sanitizedFrom);
            var toHistoryDir = Path.Combine(historyBaseDir, sanitizedTo);
            MigratePerUserDirectory(fromHistoryDir, toHistoryDir);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DaikinOAuth] Schedule history migration failed: {ex.Message}");
        }

        Console.WriteLine($"[DaikinOAuth] Migrated user data from {sanitizedFrom} to {sanitizedTo}");
    }

    /// <summary>
    /// Migrates files from one per-user directory to another.
    /// If the source directory does not exist, nothing happens.
    /// If the destination exists, only files missing in the destination are moved.
    /// </summary>
    private static void MigratePerUserDirectory(string fromDir, string toDir)
    {
        if (!Directory.Exists(fromDir)) return;
        Directory.CreateDirectory(toDir);
        foreach (var srcFile in Directory.GetFiles(fromDir))
        {
            var destFile = Path.Combine(toDir, Path.GetFileName(srcFile));
            if (!File.Exists(destFile))
            {
                try { File.Move(srcFile, destFile); }
                catch (Exception ex) { Console.WriteLine($"[DaikinOAuth] Migration file move failed: {ex.Message}"); }
            }
        }
        // Clean up the old directory if empty
        try
        {
            if (Directory.GetFiles(fromDir).Length == 0 && Directory.GetDirectories(fromDir).Length == 0)
                Directory.Delete(fromDir);
        }
        catch { }
    }

    private static (string codeChallenge, string verifier) CreatePkcePair()
    {
        var verifierBytes = new byte[32];
        RandomNumberGenerator.Fill(verifierBytes);
        var verifier = Base64Url(Convert.ToBase64String(verifierBytes));
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.ASCII.GetBytes(verifier));
        var challenge = Base64Url(Convert.ToBase64String(hash));
        return (challenge, verifier);
    }

    private static string Base64Url(string s) => s.Replace("+","-").Replace("/","_").Replace("=","");



    private static string ResolveRedirectUri(IConfiguration cfg, HttpContext? ctx)
    {
        var explicitUri = cfg["Daikin:RedirectUri"];
        if (!string.IsNullOrWhiteSpace(explicitUri)) return explicitUri!;
    if (ctx is not null)
        {
            var forwardedHost = ctx.Request.Headers["X-Forwarded-Host"].FirstOrDefault();
            var forwardedProto = ctx.Request.Headers["X-Forwarded-Proto"].FirstOrDefault();
            var host = forwardedHost ?? ctx.Request.Host.ToString();
            var scheme = forwardedProto ?? ctx.Request.Scheme;
            var path = cfg["Daikin:RedirectPath"] ?? "/auth/daikin/callback";
            return $"{scheme}://{host}{path}";
        }
        var baseUrl = cfg["PublicBaseUrl"] ?? Environment.GetEnvironmentVariable("PUBLIC_BASE_URL");
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            var path = cfg["Daikin:RedirectPath"] ?? "/auth/daikin/callback";
            return baseUrl!.TrimEnd('/') + path;
        }
        throw new InvalidOperationException("Daikin:RedirectUri saknas och kunde inte härledas (ange Daikin:RedirectUri eller PublicBaseUrl).");
    }

    private static string TokenFilePath(IConfiguration cfg, string? userId)
    {
        var sanitized = SanitizeUser(userId);
        string basePath;
        if (!string.IsNullOrEmpty(sanitized))
        {
            basePath = Path.Combine(StoragePaths.GetUserTokenDir(sanitized, cfg), "daikin.json");
        }
        else
        {
            basePath = Path.Combine(StoragePaths.GetTokensDir(cfg), "daikin.json");
        }
        var dir = Path.GetDirectoryName(basePath)!;
        Directory.CreateDirectory(dir);
        return basePath;
    }

    private static async Task<TokenFile?> LoadTokensAsync(IConfiguration cfg, string? userId)
    {
        try
        {
            var path = TokenFilePath(cfg, userId);
            if(!File.Exists(path)) return null;
            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<TokenFile>(json);
        }
        catch (Exception ex) 
        { 
            Console.WriteLine($"[DaikinOAuth] Failed to load tokens: {ex.Message}");
            return null; 
        }
    }

    private static TokenFile? LoadTokens(IConfiguration cfg, string? userId)
    {
        return LoadTokensAsync(cfg, userId).GetAwaiter().GetResult();
    }

    private static async Task SaveTokensAsync(IConfiguration cfg, TokenFile tf, string? userId)
    {
        var path = TokenFilePath(cfg, userId);
        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(tf, new JsonSerializerOptions{WriteIndented=true});
        await File.WriteAllTextAsync(tmp, json);
        File.Move(tmp, path, true);
    }

    private static void SaveTokens(IConfiguration cfg, TokenFile tf, string? userId)
    {
        SaveTokensAsync(cfg, tf, userId).GetAwaiter().GetResult();
    }
}
