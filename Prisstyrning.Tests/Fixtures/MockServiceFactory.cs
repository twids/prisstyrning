using Microsoft.Extensions.Configuration;
using System.Net;
using System.Text.Json;

namespace Prisstyrning.Tests.Fixtures;

/// <summary>
/// Factory for creating mock services and dependencies for testing.
/// </summary>
public static class MockServiceFactory
{
    /// <summary>
    /// Creates a test IHttpClientFactory that returns HttpClient instances with the provided message handler.
    /// If no handler is provided, creates one with default Nordpool mock responses.
    /// </summary>
    public static IHttpClientFactory CreateMockHttpClientFactory(MockHttpMessageHandler? mockHandler = null)
    {
        var handler = mockHandler ?? CreateDefaultMockHandler();
        return new TestHttpClientFactory(handler);
    }
    
    /// <summary>
    /// Creates a default mock handler with Nordpool API routes that return valid price data.
    /// </summary>
    private static MockHttpMessageHandler CreateDefaultMockHandler()
    {
        var handler = new MockHttpMessageHandler();
        
        // Mock elprisetjustnu.se API (used by NordpoolClient)
        // Returns 24 hours of price data in the expected format
        var priceArray = new System.Text.Json.Nodes.JsonArray();
        for (int h = 0; h < 24; h++)
        {
            var hourPrice = new System.Text.Json.Nodes.JsonObject
            {
                ["start"] = DateTime.Today.AddHours(h).ToString("o"),
                ["value"] = 0.5m + (h * 0.02m) // Prices from 0.50 to 0.96 SEK/kWh
            };
            priceArray.Add(hourPrice);
        }
        
        // Match any elprisetjustnu.se price endpoint
        handler.AddRoute("www.elprisetjustnu.se/api/v1/prices",
            HttpStatusCode.OK,
            priceArray.ToJsonString());
        
        return handler;
    }
    
    /// <summary>
    /// Creates a BatchRunner with test IHttpClientFactory and DaikinOAuthService.
    /// </summary>
    public static BatchRunner CreateMockBatchRunner(IHttpClientFactory? httpClientFactory = null, DaikinOAuthService? daikinOAuthService = null)
    {
        var factory = httpClientFactory ?? CreateMockHttpClientFactory();
        var oauthService = daikinOAuthService ?? CreateMockDaikinOAuthService(factory);
        return new BatchRunner(factory, oauthService);
    }
    
    /// <summary>
    /// Creates a DaikinOAuthService with test IHttpClientFactory.
    /// </summary>
    public static DaikinOAuthService CreateMockDaikinOAuthService(IHttpClientFactory? httpClientFactory = null)
    {
        var factory = httpClientFactory ?? CreateMockHttpClientFactory();
        return new DaikinOAuthService(factory);
    }
    
    /// <summary>
    /// Simple IHttpClientFactory implementation for testing.
    /// </summary>
    private class TestHttpClientFactory : IHttpClientFactory
    {
        private readonly MockHttpMessageHandler _handler;
        
        public TestHttpClientFactory(MockHttpMessageHandler handler)
        {
            _handler = handler;
        }
        
        public HttpClient CreateClient(string name)
        {
            return new HttpClient(_handler, disposeHandler: false);
        }
    }
}
