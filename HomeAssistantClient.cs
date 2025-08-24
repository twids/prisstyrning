using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;

public class HomeAssistantClient
{
    private readonly HttpClient _client;
    private readonly string _baseUrl;

    public HomeAssistantClient(string baseUrl, string token)
    {
        _baseUrl = baseUrl;
        _client = new HttpClient();
        _client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
    }

    public async Task<decimal?> GetSensorPriceAsync(string sensorEntity)
    {
        var url = $"{_baseUrl}/api/states/{sensorEntity}";
        var response = await _client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("state", out var stateProp))
        {
            if (decimal.TryParse(stateProp.GetString(), out var price))
                return price;
        }
        return null;
    }
}
