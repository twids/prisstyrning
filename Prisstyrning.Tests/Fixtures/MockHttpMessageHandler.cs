using System.Net;
using System.Text;

namespace Prisstyrning.Tests.Fixtures;

/// <summary>
/// Mock HTTP message handler that intercepts and simulates HTTP requests for testing.
/// Allows tests to verify outbound requests and provide controlled responses.
/// </summary>
public class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Dictionary<string, (HttpStatusCode status, string body)> _routes = new();
    private readonly List<HttpRequestMessage> _requests = new();
    private readonly object _lock = new();
    
    /// <summary>
    /// All HTTP requests sent through this handler.
    /// </summary>
    public IReadOnlyList<HttpRequestMessage> Requests
    {
        get
        {
            lock (_lock)
            {
                return _requests.ToList();
            }
        }
    }
    
    /// <summary>
    /// Registers a mock response for URLs matching the given pattern.
    /// Pattern matching is case-insensitive substring match.
    /// </summary>
    public void AddRoute(string urlPattern, HttpStatusCode status, string body)
    {
        lock (_lock)
        {
            _routes[urlPattern] = (status, body);
        }
    }
    
    /// <summary>
    /// Clears all captured requests (useful for multi-phase tests).
    /// </summary>
    public void ClearRequests()
    {
        lock (_lock)
        {
            _requests.Clear();
        }
    }
    
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, 
        CancellationToken cancellationToken)
    {
        // Capture the request
        lock (_lock)
        {
            _requests.Add(request);
        }
        
        var url = request.RequestUri?.ToString() ?? "";
        
        // Find matching route
        foreach (var route in _routes)
        {
            if (url.Contains(route.Key, StringComparison.OrdinalIgnoreCase))
            {
                var (status, body) = route.Value;
                return await Task.FromResult(new HttpResponseMessage
                {
                    StatusCode = status,
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                    RequestMessage = request
                });
            }
        }
        
        // No route matched - return 404 with diagnostic message
        return await Task.FromResult(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.NotFound,
            Content = new StringContent($"No mock configured for {url}", Encoding.UTF8, "text/plain"),
            RequestMessage = request
        });
    }
}
