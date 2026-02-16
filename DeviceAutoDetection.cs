using System.Text.Json;

/// <summary>
/// Helper methods for auto-detecting Daikin device IDs and management points
/// </summary>
internal static class DeviceAutoDetection
{
    /// <summary>
    /// Extracts the embedded ID for a domestic hot water tank management point from device JSON
    /// </summary>
    /// <param name="deviceJson">JSON string representing a Daikin device</param>
    /// <returns>The embedded ID if found, null otherwise</returns>
    public static string? FindDhwEmbeddedId(string deviceJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(deviceJson);
            return FindDhwEmbeddedId(doc.RootElement);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts the embedded ID for a domestic hot water tank management point from a device JsonElement
    /// </summary>
    /// <param name="deviceElement">JsonElement representing a Daikin device</param>
    /// <returns>The embedded ID if found, null otherwise</returns>
    public static string? FindDhwEmbeddedId(JsonElement deviceElement)
    {
        if (!deviceElement.TryGetProperty("managementPoints", out var mpArray) || 
            mpArray.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var dhwPoint = mpArray.EnumerateArray()
            .Where(mp => mp.TryGetProperty("managementPointType", out var mpt) && 
                        mpt.GetString() == "domesticHotWaterTank" && 
                        mp.TryGetProperty("embeddedId", out _))
            .FirstOrDefault();
        
        if (dhwPoint.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        return dhwPoint.GetProperty("embeddedId").GetString();
    }

    /// <summary>
    /// Extracts the first site ID from sites JSON
    /// </summary>
    /// <param name="sitesJson">JSON string representing Daikin sites array</param>
    /// <returns>The first site ID if found, null otherwise</returns>
    public static string? GetFirstSiteId(string sitesJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(sitesJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
            {
                return doc.RootElement[0].GetProperty("id").GetString();
            }
        }
        catch
        {
            // Ignore parsing errors
        }
        return null;
    }

    /// <summary>
    /// Extracts the first device ID and its JSON from devices JSON
    /// </summary>
    /// <param name="devicesJson">JSON string representing Daikin devices array</param>
    /// <returns>Tuple of (deviceId, deviceJsonRaw) if found, (null, null) otherwise</returns>
    public static (string? deviceId, string? deviceJsonRaw) GetFirstDevice(string devicesJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(devicesJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
            {
                var elem = doc.RootElement[0];
                var deviceId = elem.GetProperty("id").GetString();
                var deviceJsonRaw = elem.GetRawText();
                return (deviceId, deviceJsonRaw);
            }
        }
        catch
        {
            // Ignore parsing errors
        }
        return (null, null);
    }
}
