using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Nodes;
using System;
using System.Net.Http.Headers;

public class HomeAssistantClient
{
    private readonly HttpClient _client;
    private readonly string _baseUrl;

    public HomeAssistantClient(string baseUrl, string token, HttpClient? httpClient = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _client = httpClient ?? new HttpClient();
        _client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("Prisstyrning/1.0");
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var masked = token.Length > 12 ? token.Substring(0,8) + "..." + token[^4..] : "(kort)";
        Console.WriteLine($"[HA] Token length={token.Length} preview={masked}");
    }

    public async Task<bool> TestConnectionAsync()
    {
        var url = $"{_baseUrl}/api/";
        try
        {
            var resp = await _client.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"[HA] Test /api status={(int)resp.StatusCode} bodySnippet={Truncate(body,120)}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HA] Test connection error: {ex.Message}");
            return false;
        }
    }

    private async Task<string?> GetStringOrLogAsync(string url)
    {
        var resp = await _client.GetAsync(url);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"[HA] Request {url} failed {(int)resp.StatusCode} {resp.ReasonPhrase}. Body: {Truncate(body,200)}");
            return null;
        }
        return await resp.Content.ReadAsStringAsync();
    }

    private static string Truncate(string s, int max) => string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0,max) + "...");

    public async Task<decimal?> GetSensorPriceAsync(string sensorEntity)
    {
    var url = $"{_baseUrl}/api/states/{sensorEntity}";
    var json = await GetStringOrLogAsync(url);
    if (json == null) return null;
        var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("state", out var stateProp))
        {
            if (decimal.TryParse(stateProp.GetString(), out var price))
                return price;
        }
        return null;
    }

    public async Task<JsonArray?> GetRawPricesAsync(string sensorEntity, string attributeName)
    {
    var url = $"{_baseUrl}/api/states/{sensorEntity}";
    var json = await GetStringOrLogAsync(url);
    if (json == null) return null;
        var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("attributes", out var attrProp))
        {
            if (attrProp.TryGetProperty(attributeName, out var rawProp) && rawProp.ValueKind == JsonValueKind.Array)
            {
                var arr = JsonNode.Parse(rawProp.GetRawText()) as JsonArray;
                return arr;
            }
        }
        return null;
    }
}
