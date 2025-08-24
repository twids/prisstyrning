using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;

internal static class DaikinOAuthService
{
    private static readonly object _lock = new();
    private static readonly Dictionary<string,string> _stateToVerifier = new();

    private const string DefaultAuthEndpoint = "https://idp.onecta.daikineurope.com/v1/oidc/authorize"; // uppdaterad OIDC authorize
    private const string DefaultTokenEndpoint = "https://idp.onecta.daikineurope.com/v1/oidc/token";   // uppdaterad token

    private record TokenFile(string access_token, string refresh_token, DateTimeOffset expires_at_utc);

    public static string GetAuthorizationUrl(IConfiguration cfg, HttpContext? httpContext = null)
    {
        var clientId = cfg["Daikin:ClientId"] ?? throw new InvalidOperationException("Daikin:ClientId saknas");
        var redirectUri = ResolveRedirectUri(cfg, httpContext);
    var scope = cfg["Daikin:Scope"] ?? "openid onecta:basic.integration offline_access";
    var authEndpoint = cfg["Daikin:AuthEndpoint"] ?? DefaultAuthEndpoint;
        var state = Guid.NewGuid().ToString("N");
        var (codeChallenge, verifier) = CreatePkcePair();
        lock(_lock) _stateToVerifier[state] = verifier;
    var url = $"{authEndpoint}?response_type=code&client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(redirectUri)}&scope={Uri.EscapeDataString(scope)}&state={state}&code_challenge={codeChallenge}&code_challenge_method=S256";
        return url;
    }

    public static async Task<bool> HandleCallbackAsync(IConfiguration cfg, string code, string state)
    {
        string? verifier;
        lock(_lock)
        {
            if(!_stateToVerifier.TryGetValue(state, out verifier)) return false;
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
        using var http = new HttpClient();
    var resp = await http.PostAsync(tokenEndpoint, new FormUrlEncodedContent(form));
        if(!resp.IsSuccessStatusCode) return false;
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var access = root.GetProperty("access_token").GetString()!;
        var refresh = root.GetProperty("refresh_token").GetString()!;
        var expiresIn = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 30);
        SaveTokens(cfg, new TokenFile(access, refresh, expiresAt));
        return true;
    }

    public static (string? accessToken, DateTimeOffset? expiresAt) TryGetValidAccessToken(IConfiguration cfg)
    {
        var tf = LoadTokens(cfg);
        if(tf == null) return (null,null);
        if(tf.expires_at_utc > DateTimeOffset.UtcNow.AddMinutes(1)) return (tf.access_token, tf.expires_at_utc);
        return (null, tf.expires_at_utc);
    }

    public static async Task<string?> RefreshIfNeededAsync(IConfiguration cfg)
    {
        var existing = LoadTokens(cfg);
        if(existing == null) return null;
        if(existing.expires_at_utc > DateTimeOffset.UtcNow.AddMinutes(1)) return existing.access_token; // still valid
        var clientId = cfg["Daikin:ClientId"] ?? throw new InvalidOperationException("Daikin:ClientId saknas");
    var clientSecret = cfg["Daikin:ClientSecret"];
    var tokenEndpoint = cfg["Daikin:TokenEndpoint"] ?? DefaultTokenEndpoint;
        var form = new Dictionary<string,string>{
            ["grant_type"]="refresh_token",
            ["refresh_token"] = existing.refresh_token,
            ["client_id"] = clientId
        };
        if(!string.IsNullOrWhiteSpace(clientSecret))
            form["client_secret"] = clientSecret;
        using var http = new HttpClient();
    var resp = await http.PostAsync(tokenEndpoint, new FormUrlEncodedContent(form));
        if(!resp.IsSuccessStatusCode)
        {
            Console.WriteLine($"[DaikinOAuth] refresh failed {resp.StatusCode}");
            return null;
        }
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var access = root.GetProperty("access_token").GetString()!;
        var refresh = root.TryGetProperty("refresh_token", out var rEl)? rEl.GetString() ?? existing.refresh_token : existing.refresh_token;
        var expiresIn = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 30);
        SaveTokens(cfg, new TokenFile(access, refresh!, expiresAt));
        return access;
    }

    public static object Status(IConfiguration cfg)
    {
        var t = LoadTokens(cfg);
        if(t == null) return new { authorized = false };
        return new {
            authorized = true,
            expiresAtUtc = t.expires_at_utc,
            expiresInSeconds = (int)Math.Max(0,(t.expires_at_utc - DateTimeOffset.UtcNow).TotalSeconds)
        };
    }

    public static string? GetAccessTokenUnsafe(IConfiguration cfg)
    {
        return LoadTokens(cfg)?.access_token;
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

    private static string TokenFilePath(IConfiguration cfg)
    {
        var path = cfg["Daikin:TokenFile"] ?? Path.Combine("tokens","daikin.json");
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        return path;
    }

    private static TokenFile? LoadTokens(IConfiguration cfg)
    {
        try
        {
            var path = TokenFilePath(cfg);
            if(!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<TokenFile>(json);
        }
        catch { return null; }
    }

    private static void SaveTokens(IConfiguration cfg, TokenFile tf)
    {
        var path = TokenFilePath(cfg);
        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(tf, new JsonSerializerOptions{WriteIndented=true});
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, true);
    }
}
