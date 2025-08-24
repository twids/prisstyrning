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

    public async Task<string> SetScheduleAsync(string deviceId, string schedulePayload)
    {
        var scheduleUrl = $"https://api.onecta.daikineurope.com/v1/devices/{deviceId}/dhw/schedule";
        var content = new StringContent(schedulePayload, System.Text.Encoding.UTF8, "application/json");
        var response = await _client.PostAsync(scheduleUrl, content);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
