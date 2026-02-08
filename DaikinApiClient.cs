using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

public class DaikinApiClient
{
    private readonly HttpClient _client;
    private readonly string _accessToken;
    private readonly bool _log;
    private readonly bool _logBody;
    private readonly int _bodySnippetLen;
    private readonly string _baseApi;
    private static readonly object _devicesLock = new();
    private static string? _devicesCacheJson;
    private static DateTimeOffset _devicesCacheFetched;

    public DaikinApiClient(string accessToken, bool log = false, bool logBody = false, int? snippetLen = null, string? baseApiOverride = null, HttpClient? httpClient = null)
    {
        _accessToken = accessToken;
        _client = httpClient ?? new HttpClient();
        _client.DefaultRequestHeaders.Add("Authorization", $"Bearer {_accessToken}");
    // Force logging for every request regardless of supplied flag.
    _log = true;
        _logBody = logBody;
        _bodySnippetLen = (snippetLen is > 20 and < 5000) ? snippetLen!.Value : 300;
        _baseApi = string.IsNullOrWhiteSpace(baseApiOverride) ? "https://api.onecta.daikineurope.com" : baseApiOverride!.TrimEnd('/');
    }

    private async Task<(HttpResponseMessage resp, string body)> SendAsync(HttpRequestMessage req)
    {
        var sw = Stopwatch.StartNew();
        HttpResponseMessage? resp = null;
        string body = string.Empty;
        try
        {
            resp = await _client.SendAsync(req);
            body = await resp.Content.ReadAsStringAsync();
            sw.Stop();
            if (_log)
            {
                var path = req.RequestUri != null ? req.RequestUri.PathAndQuery : "(null)";
                if (resp.IsSuccessStatusCode)
                {
                    if (_logBody)
                    {
                        var snippet = Trim(body, _bodySnippetLen);
                        Console.WriteLine($"[DaikinHTTP] {req.Method} {path} -> {(int)resp.StatusCode} {resp.StatusCode} in {sw.ElapsedMilliseconds}ms bytes={body.Length} body='{snippet}'");
                    }
                    else
                    {
                        Console.WriteLine($"[DaikinHTTP] {req.Method} {path} -> {(int)resp.StatusCode} {resp.StatusCode} in {sw.ElapsedMilliseconds}ms bytes={body.Length}");
                    }
                }
                else
                {
                    var snippet = Trim(body, _bodySnippetLen);
                    Console.WriteLine($"[DaikinHTTP][Error] {req.Method} {path} -> {(int)resp.StatusCode} {resp.StatusCode} in {sw.ElapsedMilliseconds}ms body='{snippet}'");
                }
            }
            return (resp, body);
        }
        catch (Exception ex)
        {
            sw.Stop();
            if (_log)
            {
                var path = req.RequestUri != null ? req.RequestUri.PathAndQuery : "(null)";
                Console.WriteLine($"[DaikinHTTP][Exception] {req.Method} {path} after {sw.ElapsedMilliseconds}ms ex={ex.GetType().Name} msg={Trim(ex.Message,200)}");
            }
            throw;
        }
    }

    private static string Trim(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        s = s.Replace('\n',' ').Replace('\r',' ');
        return s.Length <= max ? s : s.Substring(0, max) + "...";
    }

    public async Task<string> GetSitesAsync()
    {
    var url = _baseApi + "/v1/sites";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        var (resp, body) = await SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return body;
    }

    public async Task<string> GetDevicesAsync(string siteId)
    {
    // NOTE: The correct endpoint for listing gateway devices is not site-scoped.
    // Keeping the signature (siteId) for backward compatibility but ignoring it now.
    var url = _baseApi + "/v1/gateway-devices";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        var (resp, body) = await SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return body;
    }

    // Cached variant with simple rate-limit: if last fetch < minIntervalAgo return cached JSON
    public async Task<string> GetDevicesCachedAsync(string siteId, TimeSpan minInterval)
    {
        lock (_devicesLock)
        {
            if (_devicesCacheJson != null && (DateTimeOffset.UtcNow - _devicesCacheFetched) < minInterval)
            {
                if (_log) Console.WriteLine("[DaikinHTTP][Cache] gateway-devices hit");
                return _devicesCacheJson;
            }
        }
        var fresh = await GetDevicesAsync(siteId);
        lock (_devicesLock)
        {
            _devicesCacheJson = fresh;
            _devicesCacheFetched = DateTimeOffset.UtcNow;
        }
        return fresh;
    }

    public async Task<string> GetScheduleAsync(string deviceId)
    {
    var url = $"{_baseApi}/v1/devices/{deviceId}/dhw/schedule";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        var (resp, body) = await SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return body;
    }

    // Legacy DHW endpoint (may not work on newer API – kept for fallback).
    public async Task<string> LegacySetDhwScheduleAsync(string deviceId, string schedulePayload)
    {
    var url = $"{_baseApi}/v1/devices/{deviceId}/dhw/schedule";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(schedulePayload, System.Text.Encoding.UTF8, "application/json")
        };
        var (resp, body) = await SendAsync(req);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"LegacySetDhwSchedule {(int)resp.StatusCode} {resp.StatusCode} body='{Trim(body,300)}'");
        return body;
    }

    // PUT full schedules for specified management point & mode (heating/cooling/any)
    public async Task PutSchedulesAsync(string gatewayDeviceId, string embeddedId, string mode, string schedulePayload)
    {
    var url = $"{_baseApi}/v1/gateway-devices/{gatewayDeviceId}/management-points/{embeddedId}/schedule/{mode}/schedules";
        using var req = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new StringContent(schedulePayload, System.Text.Encoding.UTF8, "application/json")
        };
        var (resp, body) = await SendAsync(req);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"PutSchedules {(int)resp.StatusCode} {resp.StatusCode} body='{Trim(body,300)}'");
    }

    // Enable a specific schedule id (expects 204)
    public async Task SetCurrentScheduleAsync(string gatewayDeviceId, string embeddedId, string mode, string scheduleId)
    {
    var url = $"{_baseApi}/v1/gateway-devices/{gatewayDeviceId}/management-points/{embeddedId}/schedule/{mode}/current";
        var bodyObj = new { scheduleId, enabled = true };
        var json = JsonSerializer.Serialize(bodyObj);
        using var req = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
        var (resp, body) = await SendAsync(req);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"SetCurrentSchedule {(int)resp.StatusCode} {resp.StatusCode} body='{Trim(body,300)}'");
    }
}
