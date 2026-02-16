using Microsoft.Extensions.Configuration;
using Prisstyrning.Tests.Fixtures;
using Xunit;

namespace Prisstyrning.Tests.Unit;

/// <summary>
/// Tests for device auto-detection helper that extracts device IDs
/// </summary>
public class DeviceAutoDetectionTests
{
    [Fact]
    public void ConfigOverrides_AreRespected()
    {
        using var fs = new TempFileSystem();
        
        // Test that configuration overrides are read correctly
        var cfg = fs.GetTestConfig(new Dictionary<string, string?>
        {
            ["Daikin:SiteId"] = "override-site-123",
            ["Daikin:DeviceId"] = "override-device-456",
            ["Daikin:ManagementPointEmbeddedId"] = "2"
        });
        
        // Verify overrides are set
        Assert.Equal("override-site-123", cfg["Daikin:SiteId"]);
        Assert.Equal("override-device-456", cfg["Daikin:DeviceId"]);
        Assert.Equal("2", cfg["Daikin:ManagementPointEmbeddedId"]);
    }

    [Fact]
    public void FindDhwEmbeddedId_FindsDHWManagementPoint()
    {
        // Test JSON for DHW management point detection
        var deviceJson = """
        {
            "id": "test-device-123",
            "managementPoints": [
                {
                    "embeddedId": "1",
                    "managementPointType": "climateControl"
                },
                {
                    "embeddedId": "2",
                    "managementPointType": "domesticHotWaterTank"
                }
            ]
        }
        """;
        
        var embeddedId = DeviceAutoDetection.FindDhwEmbeddedId(deviceJson);
        
        // Verify DHW management point is found with correct embeddedId
        Assert.Equal("2", embeddedId);
    }
    
    [Fact]
    public void FindDhwEmbeddedId_NoDHW_ReturnsNull()
    {
        // Test that when no DHW management point exists, returns null
        var deviceJson = """
        {
            "id": "test-device-456",
            "managementPoints": [
                {
                    "embeddedId": "1",
                    "managementPointType": "climateControl"
                }
            ]
        }
        """;
        
        var embeddedId = DeviceAutoDetection.FindDhwEmbeddedId(deviceJson);
        
        // Verify no DHW management point found
        Assert.Null(embeddedId);
    }

    [Fact]
    public void GetFirstSiteId_ExtractsFirstSiteId()
    {
        // Test that the first site ID is correctly extracted
        var sitesJson = """
        [
            {
                "id": "site-001",
                "name": "Test Site 1"
            },
            {
                "id": "site-002",
                "name": "Test Site 2"
            }
        ]
        """;
        
        var siteId = DeviceAutoDetection.GetFirstSiteId(sitesJson);
        
        // Verify first site is selected
        Assert.Equal("site-001", siteId);
    }
    
    [Fact]
    public void GetFirstDevice_ExtractsFirstDeviceIdAndJson()
    {
        // Test that first device is correctly extracted with full JSON
        var devicesJson = """
        [
            {
                "id": "device-001",
                "name": "Test Device 1",
                "managementPoints": [
                    {
                        "embeddedId": "2",
                        "managementPointType": "domesticHotWaterTank"
                    }
                ]
            },
            {
                "id": "device-002",
                "name": "Test Device 2"
            }
        ]
        """;
        
        var (deviceId, deviceJsonRaw) = DeviceAutoDetection.GetFirstDevice(devicesJson);
        
        // Verify first device is selected
        Assert.Equal("device-001", deviceId);
        Assert.NotNull(deviceJsonRaw);
        Assert.Contains("domesticHotWaterTank", deviceJsonRaw);
    }
}
