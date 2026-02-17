using Microsoft.EntityFrameworkCore;
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
        // Expected format: array of objects with "time_start" and "SEK_per_kWh"
        var priceArray = new List<object>();
        var baseDate = DateTime.UtcNow.Date; // Use current date to avoid time-dependent tests
        
        for (int h = 0; h < 24; h++)
        {
            priceArray.Add(new
            {
                time_start = baseDate.AddHours(h).ToString("o"),
                SEK_per_kWh = 0.5m + (h * 0.02m), // Prices from 0.50 to 0.96 SEK/kWh
                EUR_per_kWh = 0.045m + (h * 0.002m)
            });
        }
        
        // Match any elprisetjustnu.se price endpoint (uses Contains matching)
        handler.AddRoute("elprisetjustnu.se",
            HttpStatusCode.OK,
            JsonSerializer.Serialize(priceArray));
        
        return handler;
    }
    
    /// <summary>
    /// Creates a BatchRunner with test IHttpClientFactory and DaikinOAuthService.
    /// </summary>
    internal static BatchRunner CreateMockBatchRunner(IHttpClientFactory? httpClientFactory = null, DaikinOAuthService? daikinOAuthService = null)
    {
        var factory = httpClientFactory ?? CreateMockHttpClientFactory();
        var oauthService = daikinOAuthService ?? CreateMockDaikinOAuthService(factory);
        return new BatchRunner(factory, oauthService);
    }
    
    /// <summary>
    /// Creates a DaikinOAuthService with test dependencies.
    /// </summary>
    internal static DaikinOAuthService CreateMockDaikinOAuthService(IHttpClientFactory? httpClientFactory = null, IConfiguration? config = null)
    {
        var factory = httpClientFactory ?? CreateMockHttpClientFactory();
        var cfg = config ?? new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Prisstyrning.Data.PrisstyrningDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        var db = new Prisstyrning.Data.PrisstyrningDbContext(options);
        db.Database.EnsureCreated();
        var tokenRepo = new Prisstyrning.Data.Repositories.DaikinTokenRepository(db);
        return new DaikinOAuthService(cfg, tokenRepo, factory);
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
