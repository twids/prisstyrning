using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;

internal class NordpoolClient
{
    private readonly HttpClient _http = new HttpClient(new HttpClientHandler{ AutomaticDecompression = System.Net.DecompressionMethods.All });
    private readonly string _currency;
    private readonly string? _pageId; // configurable page id (default 10)
    private readonly bool _allowFallback;
    private readonly string? _apiKey;
    public NordpoolClient(string? currency = null, string? pageId = null, bool allowFallback = true, string? apiKey = null)
    {
        _currency = string.IsNullOrWhiteSpace(currency) ? "SEK" : currency!;
        _pageId = string.IsNullOrWhiteSpace(pageId) ? null : pageId.Trim();
        _allowFallback = allowFallback;
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Prisstyrning/1.0 (+https://example.local)");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json, */*;q=0.8");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.8");
    try { _http.DefaultRequestHeaders.Referrer = new Uri("https://www.nordpoolgroup.com/en/market-data/"); } catch { }
    if (!_http.DefaultRequestHeaders.Contains("Origin")) _http.DefaultRequestHeaders.Add("Origin", "https://www.nordpoolgroup.com");
        if (_apiKey != null && !_http.DefaultRequestHeaders.Contains("x-api-key")) _http.DefaultRequestHeaders.Add("x-api-key", _apiKey);
    }

    private string BuildElprisetUrl(DateTime date, string zone)
    {
        return $"https://www.elprisetjustnu.se/api/v1/prices/{date:yyyy}/{date:MM-dd}_{zone.ToUpperInvariant()}.json";
    }

    public record FetchAttempt(string url, int? status, string? error, int bytes);

    // Returns (prices, attempts)
    public async Task<(JsonArray prices, List<FetchAttempt> attempts)> GetDailyPricesDetailedAsync(DateTime date, string zone)
    {
        var attempts = new List<FetchAttempt>();
        var url = BuildElprisetUrl(date, zone);
        try
        {
            var resp = await _http.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            attempts.Add(new FetchAttempt(url, (int)resp.StatusCode, resp.IsSuccessStatusCode? null : "http-status", body.Length));
            if (!resp.IsSuccessStatusCode) return (new JsonArray(), attempts);
            var arr = JsonDocument.Parse(body).RootElement;
            if (arr.ValueKind != JsonValueKind.Array) return (new JsonArray(), attempts);
            var list = new JsonArray();
            foreach (var item in arr.EnumerateArray())
            {
                if (!item.TryGetProperty("time_start", out var tsEl)) continue;
                if (!item.TryGetProperty("SEK_per_kWh", out var valEl)) continue;
                if (!DateTime.TryParse(tsEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ts)) continue;
                if (!decimal.TryParse(valEl.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var dec)) continue;
                list.Add(new JsonObject { ["start"] = ts.ToString("o"), ["value"] = dec });
            }
            return (list, attempts);
        }
        catch (Exception ex)
        {
            attempts.Add(new FetchAttempt(url, null, ex.GetType().Name+": "+ex.Message, 0));
            return (new JsonArray(), attempts);
        }
    }

    // Raw fetch for debug: only the new Data Portal API
    public async Task<IReadOnlyList<FetchAttempt>> GetRawCandidateResponsesAsync(DateTime date, string zone = "SE3")
    {
        var list = new List<FetchAttempt>();
        var url = BuildElprisetUrl(date, zone);
        try
        {
            var resp = await _http.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            list.Add(new FetchAttempt(url, (int)resp.StatusCode, resp.IsSuccessStatusCode? null : body.Substring(0, Math.Min(160, body.Length)), body.Length));
        }
        catch (Exception ex)
        {
            list.Add(new FetchAttempt(url, null, ex.Message, 0));
        }
        return list;
    }


    // Backwards compatibility
    public async Task<JsonArray> GetDailyPricesAsync(DateTime date, string zone)
    {
        var (p, _) = await GetDailyPricesDetailedAsync(date, zone);
        return p;
    }

    // Convenience: today + tomorrow arrays (tomorrow may be empty before publication)
    public async Task<(JsonArray today, JsonArray tomorrow)> GetTodayTomorrowAsync(string zone, TimeZoneInfo? tz = null)
    {
        tz ??= TimeZoneInfo.Local;
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz).DateTime;
        var todayDate = now.Date;
        var tomorrowDate = todayDate.AddDays(1);
        var today = await GetDailyPricesAsync(todayDate, zone);
        JsonArray tomorrow;
        try { tomorrow = await GetDailyPricesAsync(tomorrowDate, zone); }
        catch { tomorrow = new JsonArray(); }
        return (today, tomorrow);
    }
}
