using System.IO;
using System.Text.Json.Nodes;
using System;
using System.Text.Json;
using System.Threading.Tasks;

class Program
{
    static async Task Main(string[] args)
    {
        // Läs konfiguration från appsettings.json
        var configJson = await File.ReadAllTextAsync("appsettings.json");
        var config = JsonNode.Parse(configJson);
        var haBaseUrl = config["HomeAssistant"]?["BaseUrl"]?.ToString() ?? "";
        var haToken = config["HomeAssistant"]?["Token"]?.ToString() ?? "";
        var haSensor = config["HomeAssistant"]?["Sensor"]?.ToString() ?? "";
        var accessToken = config["Daikin"]?["AccessToken"]?.ToString() ?? "";

        // Hämta timpris från Home Assistant
        var homeAssistant = new HomeAssistantClient(haBaseUrl, haToken);
        var price = await homeAssistant.GetSensorPriceAsync(haSensor);
        if (price.HasValue)
            Console.WriteLine($"Timpris från Home Assistant: {price.Value} kr/kWh");
        else
            Console.WriteLine("Kunde inte hämta timpris från Home Assistant.");

        var daikin = new DaikinApiClient(accessToken);

        // 1. Hämta alla sites
        string siteId = null;
        try
        {
            var sitesJson = await daikin.GetSitesAsync();
            Console.WriteLine($"Sites: {sitesJson}");
            var doc = JsonDocument.Parse(sitesJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
            {
                siteId = doc.RootElement[0].GetProperty("id").GetString();
                Console.WriteLine($"Vald siteId: {siteId}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fel vid hämtning av sites: {ex.Message}");
            return;
        }

        // 2. Hämta alla devices för site
        string deviceId = null;
        if (!string.IsNullOrEmpty(siteId))
        {
            try
            {
                var devicesJson = await daikin.GetDevicesAsync(siteId);
                Console.WriteLine($"Devices: {devicesJson}");
                var doc = JsonDocument.Parse(devicesJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                {
                    deviceId = doc.RootElement[0].GetProperty("id").GetString();
                    Console.WriteLine($"Vald deviceId: {deviceId}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fel vid hämtning av devices: {ex.Message}");
                return;
            }
        }

        // 3. Hämta nuvarande schema för varmvattenberedare
        if (!string.IsNullOrEmpty(deviceId))
        {
            try
            {
                var scheduleJson = await daikin.GetScheduleAsync(deviceId);
                Console.WriteLine($"Nuvarande schema: {scheduleJson}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fel vid hämtning av schema: {ex.Message}");
            }

            // 4. Sätt nytt schema (exempelpayload)
            var schedulePayload = "{"
                + "  \\\"0\\\": {"
                + "    \\\"actions\\\": {"
                + "      \\\"monday\\\": {"
                + "        \\\"10:00:00\\\": {\\\"domesticHotWaterTemperature\\\": \\\"eco\\\"},"
                + "        \\\"11:00:00\\\": {\\\"domesticHotWaterTemperature\\\": \\\"comfort\\\"},"
                + "        \\\"12:00:00\\\": {\\\"domesticHotWaterTemperature\\\": \\\"turn_off\\\"}"
                + "      },"
                + "      \\\"tuesday\\\": {\\\"10:00:00\\\": {\\\"domesticHotWaterTemperature\\\": \\\"comfort\\\"}},"
                + "      \\\"wednesday\\\": {\\\"10:00:00\\\": {\\\"domesticHotWaterTemperature\\\": \\\"comfort\\\"}},"
                + "      \\\"thursday\\\": {\\\"10:00:00\\\": {\\\"domesticHotWaterTemperature\\\": \\\"comfort\\\"}},"
                + "      \\\"friday\\\": {\\\"10:00:00\\\": {\\\"domesticHotWaterTemperature\\\": \\\"comfort\\\"}},"
                + "      \\\"saturday\\\": {\\\"10:00:00\\\": {\\\"domesticHotWaterTemperature\\\": \\\"comfort\\\"}},"
                + "      \\\"sunday\\\": {\\\"10:00:00\\\": {\\\"domesticHotWaterTemperature\\\": \\\"comfort\\\"}}"
                + "    }"
                + "  }"
                + "}";

            try
            {
                var setScheduleJson = await daikin.SetScheduleAsync(deviceId, schedulePayload);
                Console.WriteLine($"Svar från Daikin API (schema satt): {setScheduleJson}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fel vid sättning av schema: {ex.Message}");
            }
        }
    }
}