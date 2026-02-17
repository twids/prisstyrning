using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using Prisstyrning.Data.Repositories;

internal class DaikinOAuthService
{
    private static readonly object _lock = new();
    private static readonly Dictionary<string,string> _stateToVerifier = new();

    private readonly IConfiguration _cfg;
    private readonly DaikinTokenRepository _tokenRepo;

    private const string DefaultAuthEndpoint = "https://idp.onecta.daikineurope.com/v1/oidc/authorize"; // OIDC authorize
    private const string DefaultTokenEndpoint = "https://idp.onecta.daikineurope.com/v1/oidc/token";   // token
    private const string DefaultRevokeEndpoint = "https://idp.onecta.daikineurope.com/v1/oidc/revoke"; // revoke
    private const string DefaultIntrospectEndpoint = "https://idp.onecta.daikineurope.com/v1/oidc/introspect"; // introspect

    public DaikinOAuthService(IConfiguration cfg, DaikinTokenRepository tokenRepo)
    {
        _cfg = cfg;
        _tokenRepo = tokenRepo;
    }

    public string GetAuthorizationUrl(HttpContext? httpContext = null)
    {
        var clientId = _cfg["Daikin:ClientId"] ?? throw new InvalidOperationException("Daikin:ClientId saknas");
        var redirectUri = ResolveRedirectUri(_cfg, httpContext);
        var includeOffline = (_cfg["Daikin:IncludeOfflineAccess"] ?? "false").Equals("true", StringComparison.OrdinalIgnoreCase);
        var scopeCfg = _cfg["Daikin:Scope"];
        var scope = string.IsNullOrWhiteSpace(scopeCfg) ? "openid onecta:basic.integration" : scopeCfg!;
        if (includeOffline && !scope.Split(' ',StringSplitOptions.RemoveEmptyEntries).Contains("offline_access"))
            scope += " offline_access";
        var authEndpoint = _cfg["Daikin:AuthEndpoint"] ?? DefaultAuthEndpoint;
        var state = Guid.NewGuid().ToString("N");
        var nonce = Guid.NewGuid().ToString("N");
        var (codeChallenge, verifier) = CreatePkcePair();
        lock(_lock) _stateToVerifier[state] = verifier;
        var url = $"{authEndpoint}?response_type=code&client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(redirectUri)}&scope={Uri.EscapeDataString(scope)}&state={state}&nonce={nonce}&code_challenge={codeChallenge}&code_challenge_method=S256";
        Console.WriteLine($"[DaikinOAuth] Built authorize URL (host only) authHost={new Uri(authEndpoint).Host} redirect={redirectUri} scope='{scope}' state={state.Substring(0,8)}... offline={(includeOffline?"yes":"no")}");
        return url;
    }

    // Minimal variant utan state/PKCE/nonce – ENDAST för felsökning av 403.
    public string GetMinimalAuthorizationUrl(HttpContext? httpContext = null)
    {
        var clientId = _cfg["Daikin:ClientId"] ?? throw new InvalidOperationException("Daikin:ClientId saknas");
        var redirectUri = ResolveRedirectUri(_cfg, httpContext);
        var scopeCfg = _cfg["Daikin:Scope"];
        var scope = string.IsNullOrWhiteSpace(scopeCfg) ? "openid onecta:basic.integration" : scopeCfg!;
        // OBS: inget state, ingen PKCE – använd ej i produktion.
        var authEndpoint = _cfg["Daikin:AuthEndpoint"] ?? DefaultAuthEndpoint;
        var url = $"{authEndpoint}?response_type=code&client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(redirectUri)}&scope={Uri.EscapeDataString(scope)}";
        Console.WriteLine($"[DaikinOAuth][MIN] URL => {url}");
        return url;
    }

    public async Task<bool> HandleCallbackAsync(string code, string state, HttpClient? httpClient = null) => await HandleCallbackAsync(code, state, userId: null, httpClient);
    public async Task<bool> HandleCallbackAsync(string code, string state, string? userId, HttpClient? httpClient = null)
    {
        string? verifier;
        lock(_lock)
        {
            if(!_stateToVerifier.TryGetValue(state, out verifier)) return false;
            _stateToVerifier.Remove(state);
        }
    var clientId = _cfg["Daikin:ClientId"] ?? throw new InvalidOperationException("Daikin:ClientId saknas");
    var redirectUri = ResolveRedirectUri(_cfg, null);
    var clientSecret = _cfg["Daikin:ClientSecret"];
    var tokenEndpoint = _cfg["Daikin:TokenEndpoint"] ?? DefaultTokenEndpoint;
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
        Console.WriteLine($"[DaikinOAuth] Exchanging code for tokens at {tokenEndpoint}");
        var resp = await http.PostAsync(tokenEndpoint, new FormUrlEncodedContent(form));
        if(!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"[DaikinOAuth][Error] Token exchange failed {(int)resp.StatusCode} {resp.StatusCode}");
            if (httpClient == null) http.Dispose();
            return false;
        }
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var access = root.GetProperty("access_token").GetString()!;
        var refresh = root.GetProperty("refresh_token").GetString()!;
        var expiresIn = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 30);
        await _tokenRepo.SaveAsync(userId ?? string.Empty, access, refresh, expiresAt);
        Console.WriteLine($"[DaikinOAuth] Token exchange OK expiresAt={expiresAt:O} refresh={(refresh?.Length>8?refresh[..8]+"...":"(none)")}");
        if (httpClient == null) http.Dispose();
        return true;
    }

    public async Task<(string? accessToken, DateTimeOffset? expiresAt)> TryGetValidAccessTokenAsync(string? userId = null)
    {
        var tf = await _tokenRepo.LoadAsync(userId ?? string.Empty);
        if(tf == null) return (null,null);
        if(tf.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1)) return (tf.AccessToken, tf.ExpiresAtUtc);
        return (null, tf.ExpiresAtUtc);
    }

    public async Task<string?> RefreshIfNeededAsync(string? userId = null, HttpClient? httpClient = null) => await RefreshIfNeededAsync(userId, TimeSpan.FromMinutes(1), httpClient);

    /// <summary>
    /// Refresh the access token if it will expire within the provided window.
    /// Default window in existing calls is 1 minute; callers can request a larger proactive window.
    /// </summary>
    public async Task<string?> RefreshIfNeededAsync(string? userId, TimeSpan window, HttpClient? httpClient = null)
    {
        var existing = await _tokenRepo.LoadAsync(userId ?? string.Empty);
        if (existing == null) return null;
        if (existing.ExpiresAtUtc > DateTimeOffset.UtcNow.Add(window)) return existing.AccessToken; // still valid enough
        var clientId = _cfg["Daikin:ClientId"] ?? throw new InvalidOperationException("Daikin:ClientId saknas");
        var clientSecret = _cfg["Daikin:ClientSecret"];
        var tokenEndpoint = _cfg["Daikin:TokenEndpoint"] ?? DefaultTokenEndpoint;
        var form = new Dictionary<string,string>{
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = existing.RefreshToken,
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
            var refresh = root.TryGetProperty("refresh_token", out var rEl)? rEl.GetString() ?? existing.RefreshToken : existing.RefreshToken;
            var expiresIn = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
            var expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 30);
            await _tokenRepo.SaveAsync(userId ?? string.Empty, access, refresh!, expiresAt);
            Console.WriteLine($"[DaikinOAuth] Refresh OK newExpiry={expiresAt:O}");
            return access;
        }
        finally
        {
            if (httpClient == null) http.Dispose();
        }
    }

    public async Task<object> StatusAsync(string? userId = null)
    {
        var t = await _tokenRepo.LoadAsync(userId ?? string.Empty);
        if(t == null) return new { authorized = false };
        return new {
            authorized = true,
            expiresAtUtc = t.ExpiresAtUtc,
            expiresInSeconds = (int)Math.Max(0,(t.ExpiresAtUtc - DateTimeOffset.UtcNow).TotalSeconds)
        };
    }

    public async Task<bool> RevokeAsync(string? userId = null, HttpClient? httpClient = null)
    {
        var t = await _tokenRepo.LoadAsync(userId ?? string.Empty);
        if (t == null) return false;
        var clientId = _cfg["Daikin:ClientId"] ?? throw new InvalidOperationException("Daikin:ClientId saknas");
        var clientSecret = _cfg["Daikin:ClientSecret"];
        var revokeEndpoint = _cfg["Daikin:RevokeEndpoint"] ?? DefaultRevokeEndpoint;
        
        var http = httpClient ?? new HttpClient();
        try
        {
            var okAccess = await RevokeToken(http, revokeEndpoint, clientId, clientSecret, t.AccessToken, "access_token");
            var okRefresh = await RevokeToken(http, revokeEndpoint, clientId, clientSecret, t.RefreshToken, "refresh_token");
            if(okAccess || okRefresh)
            {
                try 
                { 
                    await _tokenRepo.DeleteAsync(userId ?? string.Empty); 
                } 
                catch (Exception ex)
                {
                    Console.WriteLine($"[DaikinOAuth] Failed to delete token: {ex.Message}");
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

    public async Task<object?> IntrospectAsync(string? userId = null, bool refresh=false, HttpClient? httpClient = null)
    {
        var t = await _tokenRepo.LoadAsync(userId ?? string.Empty); if(t==null) return null;
        var clientId = _cfg["Daikin:ClientId"] ?? throw new InvalidOperationException("Daikin:ClientId saknas");
        var clientSecret = _cfg["Daikin:ClientSecret"] ?? throw new InvalidOperationException("ClientSecret krävs för introspection");
        var introspectEndpoint = _cfg["Daikin:IntrospectEndpoint"] ?? DefaultIntrospectEndpoint;
        var token = refresh ? t.RefreshToken : t.AccessToken;
        
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

    public async Task<string?> GetAccessTokenUnsafeAsync(string? userId = null)
    {
        var t = await _tokenRepo.LoadAsync(userId ?? string.Empty);
        return t?.AccessToken;
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

}
