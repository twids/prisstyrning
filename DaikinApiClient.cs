using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

public class DaikinApiClient
{
    private readonly HttpClient _client;
    private readonly string _accessToken;

    public DaikinApiClient(string accessToken)
    {
        _accessToken = accessToken;
        _client = new HttpClient();
        _client.DefaultRequestHeaders.Add("Authorization", $"Bearer {_accessToken}");
    }

    public async Task<string> GetSitesAsync()
    {
        var sitesUrl = "https://api.onecta.daikineurope.com/v1/sites";
        var response = await _client.GetAsync(sitesUrl);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    public async Task<string> GetDevicesAsync(string siteId)
    {
        var devicesUrl = $"https://api.onecta.daikineurope.com/v1/sites/{siteId}/gateway-devices";
        var response = await _client.GetAsync(devicesUrl);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    public async Task<string> GetScheduleAsync(string deviceId)
    {
        var scheduleUrl = $"https://api.onecta.daikineurope.com/v1/devices/{deviceId}/dhw/schedule";
        var response = await _client.GetAsync(scheduleUrl);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    // Legacy DHW endpoint (may not work on newer API – kept for fallback).
    public async Task<string> LegacySetDhwScheduleAsync(string deviceId, string schedulePayload)
    {
        var url = $"https://api.onecta.daikineurope.com/v1/devices/{deviceId}/dhw/schedule";
        var content = new StringContent(schedulePayload, System.Text.Encoding.UTF8, "application/json");
        var response = await _client.PostAsync(url, content);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            var snippet = body?.Replace('\n',' ').Replace('\r',' ');
            if (snippet != null && snippet.Length > 300) snippet = snippet.Substring(0,300) + "...";
            throw new HttpRequestException($"LegacySetDhwSchedule { (int)response.StatusCode } {response.StatusCode} body='{snippet}'");
        }
        return body;
    }

    // PUT full schedules for specified management point & mode (heating/cooling/any)
    public async Task PutSchedulesAsync(string gatewayDeviceId, string embeddedId, string mode, string schedulePayload)
    {
        var url = $"https://api.onecta.daikineurope.com/v1/gateway-devices/{gatewayDeviceId}/management-points/{embeddedId}/schedule/{mode}/schedules";
        var content = new StringContent(schedulePayload, System.Text.Encoding.UTF8, "application/json");
        var response = await _client.PutAsync(url, content);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            var snippet = body?.Replace('\n',' ').Replace('\r',' ');
            if (snippet != null && snippet.Length > 300) snippet = snippet.Substring(0,300) + "...";
            throw new HttpRequestException($"PutSchedules { (int)response.StatusCode } {response.StatusCode} body='{snippet}'");
        }
    }

    // Enable a specific schedule id (expects 204)
    public async Task SetCurrentScheduleAsync(string gatewayDeviceId, string embeddedId, string mode, string scheduleId)
    {
        var url = $"https://api.onecta.daikineurope.com/v1/gateway-devices/{gatewayDeviceId}/management-points/{embeddedId}/schedule/{mode}/current";
        var bodyObj = new { scheduleId, enabled = true };
        var json = JsonSerializer.Serialize(bodyObj);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var response = await _client.PutAsync(url, content);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            var snippet = body?.Replace('\n',' ').Replace('\r',' ');
            if (snippet != null && snippet.Length > 300) snippet = snippet.Substring(0,300) + "...";
            throw new HttpRequestException($"SetCurrentSchedule { (int)response.StatusCode } {response.StatusCode} body='{snippet}'");
        }
    }
}
