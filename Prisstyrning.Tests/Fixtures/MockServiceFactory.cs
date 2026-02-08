using Microsoft.Extensions.Configuration;
using Moq;

namespace Prisstyrning.Tests.Fixtures;

/// <summary>
/// Factory for creating mock services and dependencies for testing.
/// </summary>
public static class MockServiceFactory
{
    /// <summary>
    /// Creates a mock IHttpClientFactory that returns HttpClient instances with the provided message handler.
    /// </summary>
    public static IHttpClientFactory CreateMockHttpClientFactory(MockHttpMessageHandler? mockHandler = null)
    {
        var handler = mockHandler ?? new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler, disposeHandler: false);
        
        var mockFactory = new Mock<IHttpClientFactory>();
        mockFactory
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(httpClient);
        
        return mockFactory.Object;
    }
    
    /// <summary>
    /// Creates a mock BatchRunner with IHttpClientFactory and DaikinOAuthService.
    /// </summary>
    public static BatchRunner CreateMockBatchRunner(IHttpClientFactory? httpClientFactory = null, DaikinOAuthService? daikinOAuthService = null)
    {
        var factory = httpClientFactory ?? CreateMockHttpClientFactory();
        var oauthService = daikinOAuthService ?? CreateMockDaikinOAuthService(factory);
        return new BatchRunner(factory, oauthService);
    }
    
    /// <summary>
    /// Creates a mock DaikinOAuthService with IHttpClientFactory.
    /// </summary>
    public static DaikinOAuthService CreateMockDaikinOAuthService(IHttpClientFactory? httpClientFactory = null)
    {
        var factory = httpClientFactory ?? CreateMockHttpClientFactory();
        return new DaikinOAuthService(factory);
    }
}
