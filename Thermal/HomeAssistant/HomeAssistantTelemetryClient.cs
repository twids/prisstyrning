using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Prisstyrning.Thermal.HomeAssistant;

public sealed class HomeAssistantTelemetryClient : IHomeAssistantTelemetryClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HomeAssistantTelemetryOptions _options;
    private readonly IHomeAssistantCredentialProvider _credentials;
    private readonly ILogger<HomeAssistantTelemetryClient> _logger;

    public HomeAssistantTelemetryClient(
        IHttpClientFactory httpClientFactory,
        IOptions<HomeAssistantTelemetryOptions> options,
        IHomeAssistantCredentialProvider credentials,
        ILogger<HomeAssistantTelemetryClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _credentials = credentials;
        _logger = logger;
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured()) return false;

        try
        {
            using var response = await SendAsync(HttpMethod.Get, "/api/", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning("Home Assistant connection test failed: {Message}", exception.Message);
            return false;
        }
    }

    public async Task<HomeAssistantState?> GetStateAsync(
        string entityId,
        CancellationToken cancellationToken = default)
    {
        EnsureSafeEntityId(entityId);
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/states/{Uri.EscapeDataString(entityId)}",
            cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseState(document.RootElement, DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<HomeAssistantState>> GetStatesAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "/api/states", cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var receivedAt = DateTimeOffset.UtcNow;
        return document.RootElement.EnumerateArray()
            .Select(item => ParseState(item, receivedAt))
            .Where(item => item is not null)
            .Cast<HomeAssistantState>()
            .ToArray();
    }

    public async Task<IReadOnlyList<HomeAssistantState>> GetHistoryAsync(
        string entityId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default)
    {
        EnsureSafeEntityId(entityId);
        if (toUtc <= fromUtc) throw new ArgumentException("History end must be after start.", nameof(toUtc));

        var path = QueryHelpers.AddQueryString(
            $"/api/history/period/{Uri.EscapeDataString(fromUtc.UtcDateTime.ToString("O"))}",
            new Dictionary<string, string?>
            {
                ["filter_entity_id"] = entityId,
                ["end_time"] = toUtc.UtcDateTime.ToString("O"),
                ["minimal_response"] = "false",
                ["no_attributes"] = "false"
            });

        using var response = await SendAsync(HttpMethod.Get, path, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var receivedAt = DateTimeOffset.UtcNow;
        var result = new List<HomeAssistantState>();
        foreach (var series in document.RootElement.EnumerateArray())
        {
            foreach (var item in series.EnumerateArray())
            {
                var state = ParseState(item, receivedAt);
                if (state is not null) result.Add(state);
            }
        }

        return result.OrderBy(x => x.LastUpdatedUtc).ToArray();
    }

    internal static HomeAssistantState? ParseState(JsonElement element, DateTimeOffset receivedAt)
    {
        if (!element.TryGetProperty("entity_id", out var entityProperty) ||
            string.IsNullOrWhiteSpace(entityProperty.GetString()))
        {
            return null;
        }

        var attributes = element.TryGetProperty("attributes", out var attributeProperty)
            ? JsonNode.Parse(attributeProperty.GetRawText()) as JsonObject ?? new JsonObject()
            : new JsonObject();

        return new HomeAssistantState(
            entityProperty.GetString()!,
            element.TryGetProperty("state", out var stateProperty) ? stateProperty.GetString() ?? string.Empty : string.Empty,
            attributes,
            ParseTimestamp(element, "last_changed"),
            ParseTimestamp(element, "last_updated"),
            receivedAt);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        CancellationToken cancellationToken)
    {
        var token = _credentials.GetTelemetryToken();
        if (!_options.Enabled || !IsSupportedBaseUrl(_options.BaseUrl) || string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Home Assistant telemetry is not configured.");
        var client = _httpClientFactory.CreateClient("HomeAssistantTelemetry");
        using var request = new HttpRequestMessage(method, BuildUri(path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private Uri BuildUri(string path) =>
        new(new Uri(_options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute), path.TrimStart('/'));

    private bool IsConfigured() =>
        _options.Enabled &&
        IsSupportedBaseUrl(_options.BaseUrl) &&
        _credentials.HasTelemetryToken;

    internal static bool IsSupportedBaseUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
        string.IsNullOrEmpty(uri.UserInfo);

    private static DateTimeOffset? ParseTimestamp(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) &&
        DateTimeOffset.TryParse(property.GetString(), out var timestamp)
            ? timestamp.ToUniversalTime()
            : null;

    private static void EnsureSafeEntityId(string entityId)
    {
        if (string.IsNullOrWhiteSpace(entityId) ||
            entityId.Length > 255 ||
            entityId.Any(character => !(char.IsLetterOrDigit(character) || character is '_' or '.')))
        {
            throw new ArgumentException("Invalid Home Assistant entity id.", nameof(entityId));
        }
    }
}
