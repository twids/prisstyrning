using System.Net;
using System.Text.Json.Nodes;
using Prisstyrning.Tests.Fixtures;

namespace Prisstyrning.Tests.Unit;

public class NordpoolClientTests
{
    private class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("Connection refused");
        }
    }

    // --- Constructor tests ---

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenHttpClientIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new NordpoolClient(null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_DefaultsCurrencyToSEK_WhenNullOrWhitespace(string? currency)
    {
        var handler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        // Should not throw; currency defaults to "SEK" internally
        var client = new NordpoolClient(httpClient, currency);
        Assert.NotNull(client);
    }

    [Fact]
    public void Constructor_AddsApiKeyHeader_WhenProvided()
    {
        var handler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        _ = new NordpoolClient(httpClient, apiKey: "test-key-123");
        Assert.True(httpClient.DefaultRequestHeaders.Contains("x-api-key"));
        Assert.Equal("test-key-123", httpClient.DefaultRequestHeaders.GetValues("x-api-key").Single());
    }

    [Fact]
    public void Constructor_SkipsApiKeyHeader_WhenAlreadyPresent()
    {
        var handler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        httpClient.DefaultRequestHeaders.Add("x-api-key", "existing-key");
        _ = new NordpoolClient(httpClient, apiKey: "new-key");
        // Should still have only the original header value, not duplicated
        var values = httpClient.DefaultRequestHeaders.GetValues("x-api-key").ToList();
        Assert.Single(values);
        Assert.Equal("existing-key", values[0]);
    }

    // --- GetDailyPricesAsync / GetDailyPricesDetailedAsync tests ---

    [Fact]
    public async Task GetDailyPricesAsync_ReturnsEmpty_WhenResponseIsNotJsonArray()
    {
        var handler = new MockHttpMessageHandler();
        handler.AddRoute("elprisetjustnu.se", HttpStatusCode.OK, "{}");
        var client = new NordpoolClient(new HttpClient(handler));

        var result = await client.GetDailyPricesAsync(new DateTime(2025, 1, 15), "SE3");

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetDailyPricesAsync_ReturnsEmpty_WhenResponseIsMalformedJson()
    {
        var handler = new MockHttpMessageHandler();
        handler.AddRoute("elprisetjustnu.se", HttpStatusCode.OK, "not json at all");
        var client = new NordpoolClient(new HttpClient(handler));

        var (prices, attempts) = await client.GetDailyPricesDetailedAsync(new DateTime(2025, 1, 15), "SE3");

        Assert.Empty(prices);
        // First attempt records the successful HTTP response, then JsonDocument.Parse throws
        // which is caught and recorded as a second attempt
        Assert.Equal(2, attempts.Count);
        Assert.Null(attempts[0].error); // HTTP 200 succeeded
        Assert.NotNull(attempts[1].error); // Parse exception
    }

    [Fact]
    public async Task GetDailyPricesAsync_ReturnsEmpty_WhenResponseIsEmptyArray()
    {
        var handler = new MockHttpMessageHandler();
        handler.AddRoute("elprisetjustnu.se", HttpStatusCode.OK, "[]");
        var client = new NordpoolClient(new HttpClient(handler));

        var result = await client.GetDailyPricesAsync(new DateTime(2025, 1, 15), "SE3");

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetDailyPricesAsync_SkipsItems_MissingTimeStart()
    {
        var json = @"[{""SEK_per_kWh"": 0.5}]";
        var handler = new MockHttpMessageHandler();
        handler.AddRoute("elprisetjustnu.se", HttpStatusCode.OK, json);
        var client = new NordpoolClient(new HttpClient(handler));

        var result = await client.GetDailyPricesAsync(new DateTime(2025, 1, 15), "SE3");

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetDailyPricesAsync_SkipsItems_MissingSekPerKwh()
    {
        var json = @"[{""time_start"": ""2025-01-15T00:00:00+01:00""}]";
        var handler = new MockHttpMessageHandler();
        handler.AddRoute("elprisetjustnu.se", HttpStatusCode.OK, json);
        var client = new NordpoolClient(new HttpClient(handler));

        var result = await client.GetDailyPricesAsync(new DateTime(2025, 1, 15), "SE3");

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetDailyPricesAsync_SkipsItems_WithInvalidTimeStart()
    {
        var json = @"[{""time_start"": ""not-a-date"", ""SEK_per_kWh"": 0.5}]";
        var handler = new MockHttpMessageHandler();
        handler.AddRoute("elprisetjustnu.se", HttpStatusCode.OK, json);
        var client = new NordpoolClient(new HttpClient(handler));

        var result = await client.GetDailyPricesAsync(new DateTime(2025, 1, 15), "SE3");

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetDailyPricesAsync_SkipsItems_WithInvalidSekPerKwhValue()
    {
        var json = @"[{""time_start"": ""2025-01-15T00:00:00+01:00"", ""SEK_per_kWh"": ""abc""}]";
        var handler = new MockHttpMessageHandler();
        handler.AddRoute("elprisetjustnu.se", HttpStatusCode.OK, json);
        var client = new NordpoolClient(new HttpClient(handler));

        var result = await client.GetDailyPricesAsync(new DateTime(2025, 1, 15), "SE3");

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetDailyPricesAsync_ParsesValidAndSkipsInvalidItems()
    {
        var json = @"[
            {""time_start"": ""2025-01-15T00:00:00+01:00"", ""SEK_per_kWh"": 0.5},
            {""SEK_per_kWh"": 0.3},
            {""time_start"": ""not-a-date"", ""SEK_per_kWh"": 0.2},
            {""time_start"": ""2025-01-15T01:00:00+01:00"", ""SEK_per_kWh"": ""abc""},
            {""time_start"": ""2025-01-15T02:00:00+01:00"", ""SEK_per_kWh"": 0.7}
        ]";
        var handler = new MockHttpMessageHandler();
        handler.AddRoute("elprisetjustnu.se", HttpStatusCode.OK, json);
        var client = new NordpoolClient(new HttpClient(handler));

        var result = await client.GetDailyPricesAsync(new DateTime(2025, 1, 15), "SE3");

        Assert.Equal(2, result.Count);
        Assert.Equal(0.5m, result[0]!["value"]!.GetValue<decimal>());
        Assert.Equal(0.7m, result[1]!["value"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task GetDailyPricesAsync_HandlesNetworkException_ReturnsEmpty()
    {
        var client = new NordpoolClient(new HttpClient(new ThrowingHttpMessageHandler()));

        var result = await client.GetDailyPricesAsync(new DateTime(2025, 1, 15), "SE3");

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetDailyPricesDetailedAsync_RecordsErrorAttempt_OnHttpFailure()
    {
        var handler = new MockHttpMessageHandler();
        handler.AddRoute("elprisetjustnu.se", HttpStatusCode.InternalServerError, "server error");
        var client = new NordpoolClient(new HttpClient(handler));

        var (prices, attempts) = await client.GetDailyPricesDetailedAsync(new DateTime(2025, 1, 15), "SE3");

        Assert.Empty(prices);
        Assert.Single(attempts);
        Assert.Equal(500, attempts[0].status);
        Assert.Equal("http-status", attempts[0].error);
    }

    [Fact]
    public async Task GetDailyPricesDetailedAsync_RecordsErrorAttempt_OnException()
    {
        var client = new NordpoolClient(new HttpClient(new ThrowingHttpMessageHandler()));

        var (prices, attempts) = await client.GetDailyPricesDetailedAsync(new DateTime(2025, 1, 15), "SE3");

        Assert.Empty(prices);
        Assert.Single(attempts);
        Assert.Null(attempts[0].status);
        Assert.Contains("HttpRequestException", attempts[0].error);
        Assert.Contains("Connection refused", attempts[0].error);
        Assert.Equal(0, attempts[0].bytes);
    }

    [Fact]
    public async Task GetDailyPricesDetailedAsync_BuildsCorrectUrl()
    {
        var handler = new MockHttpMessageHandler();
        handler.AddRoute("elprisetjustnu.se", HttpStatusCode.OK, "[]");
        var client = new NordpoolClient(new HttpClient(handler));

        await client.GetDailyPricesDetailedAsync(new DateTime(2025, 3, 7), "SE3");

        Assert.Single(handler.Requests);
        var requestUrl = handler.Requests[0].RequestUri!.ToString();
        Assert.Equal("https://www.elprisetjustnu.se/api/v1/prices/2025/03-07_SE3.json", requestUrl);
    }

    [Fact]
    public async Task GetDailyPricesDetailedAsync_NormalizesZoneToUpperCase()
    {
        var handler = new MockHttpMessageHandler();
        handler.AddRoute("elprisetjustnu.se", HttpStatusCode.OK, "[]");
        var client = new NordpoolClient(new HttpClient(handler));

        await client.GetDailyPricesDetailedAsync(new DateTime(2025, 3, 7), "se3");

        Assert.Single(handler.Requests);
        var requestUrl = handler.Requests[0].RequestUri!.ToString();
        Assert.Contains("_SE3.json", requestUrl);
    }

    // --- GetTodayTomorrowAsync tests ---

    [Fact]
    public async Task GetTodayTomorrowAsync_ReturnsEmptyTomorrow_OnTomorrowException()
    {
        // Use a handler that succeeds for today's date but throws for tomorrow
        // We'll use a custom handler that tracks call count
        var handler = new TomorrowThrowingHandler();
        var client = new NordpoolClient(new HttpClient(handler));

        var tz = TimeZoneInfo.Utc;
        var (today, tomorrow) = await client.GetTodayTomorrowAsync("SE3", tz);

        // Today should have parsed data, tomorrow should be empty due to caught exception
        Assert.NotEmpty(today);
        Assert.Empty(tomorrow);
    }

    private class TomorrowThrowingHandler : HttpMessageHandler
    {
        private int _callCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _callCount++;
            if (_callCount == 1)
            {
                // First call (today) - return valid data
                var json = @"[{""time_start"": ""2025-01-15T00:00:00+00:00"", ""SEK_per_kWh"": 0.5}]";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json")
                });
            }
            // Second call (tomorrow) - throw
            throw new HttpRequestException("Tomorrow not available");
        }
    }

    // --- GetRawCandidateResponsesAsync tests ---

    [Fact]
    public async Task GetRawCandidateResponsesAsync_TruncatesErrorBody_To160Chars()
    {
        var longBody = new string('x', 200);
        var handler = new MockHttpMessageHandler();
        handler.AddRoute("elprisetjustnu.se", HttpStatusCode.BadRequest, longBody);
        var client = new NordpoolClient(new HttpClient(handler));

        var attempts = await client.GetRawCandidateResponsesAsync(new DateTime(2025, 1, 15), "SE3");

        Assert.Single(attempts);
        Assert.Equal(400, attempts[0].status);
        Assert.NotNull(attempts[0].error);
        Assert.Equal(160, attempts[0].error!.Length);
        Assert.Equal(200, attempts[0].bytes);
    }

    [Fact]
    public async Task GetRawCandidateResponsesAsync_ReturnsAttempt_OnNetworkException()
    {
        var client = new NordpoolClient(new HttpClient(new ThrowingHttpMessageHandler()));

        var attempts = await client.GetRawCandidateResponsesAsync(new DateTime(2025, 1, 15), "SE3");

        Assert.Single(attempts);
        Assert.Null(attempts[0].status);
        Assert.Contains("Connection refused", attempts[0].error);
        Assert.Equal(0, attempts[0].bytes);
    }
}
