using System.Net;
using System.Text.Json.Nodes;
using Prisstyrning.Tests.Fixtures;
using Xunit;

namespace Prisstyrning.Tests.Integration;

/// <summary>
/// Integration tests for NordpoolClient with HTTP mocking
/// </summary>
public class NordpoolClientIntegrationTests
{
    [Fact]
    public async Task GetDailyPricesAsync_WithValidDate_ReturnsPriceArray()
    {
        var mockHandler = new MockHttpMessageHandler();
        var mockResponse = @"[
            {""time_start"":""2026-02-07T00:00:00Z"",""SEK_per_kWh"":0.45},
            {""time_start"":""2026-02-07T01:00:00Z"",""SEK_per_kWh"":0.42},
            {""time_start"":""2026-02-07T02:00:00Z"",""SEK_per_kWh"":0.38}
        ]";
        mockHandler.AddRoute("elprisetjustnu.se", HttpStatusCode.OK, mockResponse);
        
        var httpClient = new HttpClient(mockHandler);
        var client = new NordpoolClient(currency: "SEK", httpClient: httpClient);
        
        var result = await client.GetDailyPricesAsync(new DateTime(2026, 2, 7), "SE3");
        
        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
        
        var first = result[0] as JsonObject;
        Assert.NotNull(first);
        Assert.Contains("start", first.Select(kv => kv.Key));
        Assert.Contains("value", first.Select(kv => kv.Key));
    }

    [Fact]
    public async Task GetDailyPricesAsync_WithFutureDate_ReturnsEmptyArray()
    {
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.AddRoute("elprisetjustnu.se", HttpStatusCode.NotFound, "Not found");
        
        var httpClient = new HttpClient(mockHandler);
        var client = new NordpoolClient(currency: "SEK", httpClient: httpClient);
        
        var result = await client.GetDailyPricesAsync(new DateTime(2030, 12, 31), "SE3");
        
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetTodayTomorrowAsync_ReturnsCorrectDateRanges()
    {
        var mockHandler = new MockHttpMessageHandler();
        
        var todayMock = @"[
            {""time_start"":""2026-02-07T00:00:00Z"",""SEK_per_kWh"":0.50},
            {""time_start"":""2026-02-07T01:00:00Z"",""SEK_per_kWh"":0.48}
        ]";
        var tomorrowMock = @"[
            {""time_start"":""2026-02-08T00:00:00Z"",""SEK_per_kWh"":0.55},
            {""time_start"":""2026-02-08T01:00:00Z"",""SEK_per_kWh"":0.52}
        ]";
        
        mockHandler.AddRoute("2026/02-07_SE3", HttpStatusCode.OK, todayMock);
        mockHandler.AddRoute("2026/02-08_SE3", HttpStatusCode.OK, tomorrowMock);
        
        var httpClient = new HttpClient(mockHandler);
        var client = new NordpoolClient(currency: "SEK", httpClient: httpClient);
        
        var (today, tomorrow) = await client.GetTodayTomorrowAsync("SE3");
        
        Assert.NotNull(today);
        Assert.NotNull(tomorrow);
        Assert.Equal(2, today.Count);
        Assert.Equal(2, tomorrow.Count);
    }

    [Fact]
    public async Task GetDailyPricesDetailedAsync_TracksAttempts()
    {
        var mockHandler = new MockHttpMessageHandler();
        var mockResponse = @"[
            {""time_start"":""2026-02-07T00:00:00Z"",""SEK_per_kWh"":0.45}
        ]";
        mockHandler.AddRoute("elprisetjustnu.se", HttpStatusCode.OK, mockResponse);
        
        var httpClient = new HttpClient(mockHandler);
        var client = new NordpoolClient(currency: "SEK", httpClient: httpClient);
        
        var (prices, attempts) = await client.GetDailyPricesDetailedAsync(
            new DateTime(2026, 2, 7), "SE3");
        
        Assert.NotNull(attempts);
        Assert.NotEmpty(attempts);
        Assert.Equal(200, attempts[0].status);
        Assert.Null(attempts[0].error);
        Assert.True(attempts[0].bytes > 0);
    }

    [Fact]
    public async Task GetDailyPricesAsync_WithHTTPError_ReturnsEmptyArray()
    {
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.AddRoute("elprisetjustnu.se", HttpStatusCode.InternalServerError, 
            "Server Error");
        
        var httpClient = new HttpClient(mockHandler);
        var client = new NordpoolClient(currency: "SEK", httpClient: httpClient);
        
        var result = await client.GetDailyPricesAsync(new DateTime(2026, 2, 7), "SE3");
        
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetDailyPricesAsync_ParsesElprisetjustnuFormat()
    {
        var mockHandler = new MockHttpMessageHandler();
        var mockResponse = @"[
            {
                ""SEK_per_kWh"": 0.456789,
                ""EUR_per_kWh"": 0.040123,
                ""EXR"": 11.3823,
                ""time_start"": ""2026-02-07T00:00:00Z"",
                ""time_end"": ""2026-02-07T01:00:00Z""
            },
            {
                ""SEK_per_kWh"": 0.523456,
                ""EUR_per_kWh"": 0.045987,
                ""EXR"": 11.3823,
                ""time_start"": ""2026-02-07T01:00:00Z"",
                ""time_end"": ""2026-02-07T02:00:00Z""
            }
        ]";
        mockHandler.AddRoute("elprisetjustnu.se", HttpStatusCode.OK, mockResponse);
        
        var httpClient = new HttpClient(mockHandler);
        var client = new NordpoolClient(currency: "SEK", httpClient: httpClient);
        
        var result = await client.GetDailyPricesAsync(new DateTime(2026, 2, 7), "SE3");
        
        Assert.Equal(2, result.Count);
        
        var item = result[0] as JsonObject;
        Assert.NotNull(item);
        
        var valueStr = item["value"]!.ToString();
        var value = decimal.Parse(valueStr, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(value, 0.4m, 0.5m);
    }

    [Fact]
    public async Task GetTodayTomorrowAsync_HandlesTimezoneCorrectly()
    {
        var mockHandler = new MockHttpMessageHandler();
        
        // Mock any date patterns
        mockHandler.AddRoute("/202", HttpStatusCode.OK, 
            @"[{""time_start"":""2026-02-07T00:00:00Z"",""SEK_per_kWh"":0.50}]");
        
        var httpClient = new HttpClient(mockHandler);
        var client = new NordpoolClient(currency: "SEK", httpClient: httpClient);
        
        var stockholmTz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");
        var (today, tomorrow) = await client.GetTodayTomorrowAsync("SE3", stockholmTz);
        
        Assert.NotNull(today);
        Assert.NotNull(tomorrow);
    }

    [Fact]
    public async Task GetRawCandidateResponsesAsync_ReturnsDebugInfo()
    {
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.AddRoute("elprisetjustnu.se", HttpStatusCode.OK, 
            @"[{""time_start"":""2026-02-07T00:00:00Z"",""SEK_per_kWh"":0.45}]");
        
        var httpClient = new HttpClient(mockHandler);
        var client = new NordpoolClient(currency: "SEK", httpClient: httpClient);
        
        var attempts = await client.GetRawCandidateResponsesAsync(
            new DateTime(2026, 2, 7), "SE3");
        
        Assert.NotEmpty(attempts);
        Assert.Equal(200, attempts[0].status);
        Assert.True(attempts[0].bytes > 0);
    }
}
