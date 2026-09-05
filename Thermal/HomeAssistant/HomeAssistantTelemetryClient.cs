using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;

namespace Prisstyrning.Thermal.HomeAssistant;

public sealed class HomeAssistantTelemetryClient : IHomeAssistantTelemetryClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HomeAssistantConnectionService _connections;
    private readonly ILogger<HomeAssistantTelemetryClient> _logger;

    public HomeAssistantTelemetryClient(
        IHttpClientFactory httpClientFactory,
        HomeAssistantConnectionService connections,
        ILogger<HomeAssistantTelemetryClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _connections = connections;
        _logger = logger;
    }

    public Task<bool> TestConnectionAsync(string userId, CancellationToken cancellationToken = default) =>
        TestConnectionCoreAsync(userId, cancellationToken);

    public async Task<NormalizedWeatherForecast> GetWeatherForecastAsync(string userId, string entityId, CancellationToken cancellationToken = default)
    {
        EnsureSafeEntityId(entityId);
        if (!entityId.StartsWith("weather.", StringComparison.Ordinal)) throw new ArgumentException("Välj en weather-entity.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var connection = await ResolveAsync(userId, timeout.Token);
            using var stateResponse = await SendAsync(connection, HttpMethod.Get, $"/api/states/{Uri.EscapeDataString(entityId)}", timeout.Token);
            stateResponse.EnsureSuccessStatusCode();
            using var stateDocument = JsonDocument.Parse(await stateResponse.Content.ReadAsStringAsync(timeout.Token));
            var state = ParseState(stateDocument.RootElement, DateTimeOffset.UtcNow);
            if (state is null || state.EntityId != entityId) throw new InvalidOperationException();
            // Fixed read-only weather action; never expose a generic HA service call.
            using var request = new HttpRequestMessage(HttpMethod.Post,
                new Uri(connection.BaseUri.AbsoluteUri.TrimEnd('/') + "/api/services/weather/get_forecasts?return_response"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.TelemetryToken);
            request.Content = JsonContent.Create(new { entity_id = entityId, type = "hourly" });
            using var response = await _httpClientFactory.CreateClient("HomeAssistantTelemetry").SendAsync(request, timeout.Token);
            response.EnsureSuccessStatusCode();
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            if (json?["service_response"]?[entityId]?["forecast"] is not JsonArray forecast) throw new InvalidOperationException();
            var attributes = (JsonObject)state.Attributes.DeepClone();
            attributes["forecast"] = forecast.DeepClone();
            var fetched = DateTimeOffset.UtcNow;
            // This is retrieval time for a forecast, never a new physical measurement.
            var forecastState = state with { Attributes = attributes, LastChangedUtc = fetched, LastUpdatedUtc = fetched,
                LastReportedUtc = fetched, ReceivedAtUtc = fetched, ReportTimestampMalformed = false };
            var current = await _connections.ResolveAsync(userId, timeout.Token);
            if (current?.UpdatedAtUtc != connection.UpdatedAtUtc) throw new InvalidOperationException();
            return HomeAssistantWeatherForecastParser.Parse(forecastState, fetched);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            return new([], Prisstyrning.Thermal.Domain.DataQuality.Unavailable, "Timprognosen kunde inte hämtas. Kontrollera HA-anslutningen och att vald weather-entity stöder timprognos.");
        }
    }

    private async Task<bool> TestConnectionCoreAsync(string userId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(userId, HttpMethod.Get, "/api/", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or ArgumentException)
        {
            // HTTP/DNS/transport exceptions can contain URLs or server-controlled text.
            _logger.LogWarning("Home Assistant connection test failed; check the saved telemetry connection.");
            return false;
        }
    }

    public Task<HomeAssistantState?> GetStateAsync(string userId, string entityId, CancellationToken cancellationToken = default) =>
        GetStateCoreAsync(userId, entityId, cancellationToken);

    private async Task<HomeAssistantState?> GetStateCoreAsync(string userId, string entityId, CancellationToken cancellationToken)
    {
        EnsureSafeEntityId(entityId);
        using var response = await SendAsync(userId, HttpMethod.Get, $"/api/states/{Uri.EscapeDataString(entityId)}", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseState(document.RootElement, DateTimeOffset.UtcNow);
    }

    public Task<IReadOnlyList<HomeAssistantState>> GetStatesAsync(string userId, CancellationToken cancellationToken = default) =>
        GetStatesCoreAsync(userId, cancellationToken);

    private async Task<IReadOnlyList<HomeAssistantState>> GetStatesCoreAsync(string userId, CancellationToken cancellationToken)
    {
        var connection = await ResolveAsync(userId, cancellationToken);
        return await GetStatesAsync(connection, cancellationToken);
    }

    public async Task<IReadOnlyList<HomeAssistantState>> GetStatesAsync(ResolvedHomeAssistantConnection connection, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(connection, HttpMethod.Get, "/api/states", cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var receivedAt = DateTimeOffset.UtcNow;
        return document.RootElement.EnumerateArray().Select(item => ParseState(item, receivedAt))
            .Where(item => item is not null).Cast<HomeAssistantState>().ToArray();
    }

    public Task<IReadOnlyList<HomeAssistantState>> GetHistoryAsync(
        string userId, string entityId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default) =>
        GetHistoryCoreAsync(userId, entityId, fromUtc, toUtc, cancellationToken);

    private async Task<IReadOnlyList<HomeAssistantState>> GetHistoryCoreAsync(
        string userId, string entityId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken)
    {
        EnsureSafeEntityId(entityId);
        if (toUtc <= fromUtc) throw new ArgumentException("History end must be after start.", nameof(toUtc));
        var path = QueryHelpers.AddQueryString(
            $"/api/history/period/{Uri.EscapeDataString(fromUtc.UtcDateTime.ToString("O"))}",
            new Dictionary<string, string?>
            {
                ["filter_entity_id"] = entityId,
                ["end_time"] = toUtc.UtcDateTime.ToString("O"),
                // HA treats minimal_response/no_attributes as presence flags, even
                // when their value is "false". Full records preserve unit changes.
                ["significant_changes_only"] = "0"
            });
        using var response = await SendAsync(userId, HttpMethod.Get, path, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var receivedAt = DateTimeOffset.UtcNow;
        var result = new List<HomeAssistantState>();
        foreach (var series in document.RootElement.EnumerateArray())
        {
            if (series.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in series.EnumerateArray())
                if (ParseState(item, receivedAt) is { } state && state.EntityId.Equals(entityId, StringComparison.OrdinalIgnoreCase))
                    result.Add(state);
        }
        return result.OrderBy(x => x.LastUpdatedUtc).ToArray();
    }

    internal static HomeAssistantState? ParseState(JsonElement element, DateTimeOffset receivedAt)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("entity_id", out var entityProperty) ||
            entityProperty.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(entityProperty.GetString())) return null;
        var attributesValid = element.TryGetProperty("attributes", out var attributeProperty) && attributeProperty.ValueKind == JsonValueKind.Object;
        var attributes = attributesValid ? JsonNode.Parse(attributeProperty.GetRawText())!.AsObject() : new JsonObject();
        return new HomeAssistantState(
            entityProperty.GetString()!,
            element.TryGetProperty("state", out var stateProperty) && stateProperty.ValueKind == JsonValueKind.String
                ? stateProperty.GetString() ?? string.Empty : string.Empty,
            attributes,
            ParseTimestamp(element, "last_changed"),
            ParseTimestamp(element, "last_updated"),
            receivedAt)
        {
            AttributesMalformed = !attributesValid,
            LastReportedUtc = ParseTimestamp(element, "last_reported"),
            ReportTimestampMalformed = element.TryGetProperty("last_reported", out _) &&
                                       ParseTimestamp(element, "last_reported") is null
        };
    }

    private async Task<HttpResponseMessage> SendAsync(string userId, HttpMethod method, string path, CancellationToken cancellationToken)
    {
        var connection = await ResolveAsync(userId, cancellationToken);
        return await SendAsync(connection, method, path, cancellationToken);
    }

    private async Task<ResolvedHomeAssistantConnection> ResolveAsync(string userId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId)) throw new InvalidOperationException("An account identity is required for Home Assistant telemetry.");
        var connection = await _connections.ResolveAsync(userId, cancellationToken);
        if (connection is null || !connection.TelemetryEnabled) throw new InvalidOperationException("Home Assistant telemetry is not configured for this account.");
        return connection;
    }

    private async Task<HttpResponseMessage> SendAsync(ResolvedHomeAssistantConnection connection, HttpMethod method, string path, CancellationToken cancellationToken)
    {
        if (!connection.TelemetryEnabled) throw new InvalidOperationException("Home Assistant telemetry is disabled.");
        var client = _httpClientFactory.CreateClient("HomeAssistantTelemetry");
        using var request = new HttpRequestMessage(method, new Uri(connection.BaseUri.AbsoluteUri.TrimEnd('/') + path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.TelemetryToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    internal static bool IsSupportedBaseUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && string.IsNullOrEmpty(uri.UserInfo);

    private static DateTimeOffset? ParseTimestamp(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return null;
        var text = property.GetString();
        // A local date/time without an explicit offset must not silently acquire
        // a different meaning on Windows, in Docker, or across a DST change.
        if (text is not { Length: >= 20 } || text[10] != 'T' ||
            !(text.EndsWith('Z') || text.Length >= 25 && text[^6] is '+' or '-' && text[^3] == ':')) return null;
        return property.TryGetDateTimeOffset(out var timestamp) ? timestamp.ToUniversalTime() : null;
    }

    private static void EnsureSafeEntityId(string entityId)
    {
        if (string.IsNullOrWhiteSpace(entityId) || entityId.Length > 255 ||
            entityId.Any(character => !(char.IsLetterOrDigit(character) || character is '_' or '.')))
            throw new ArgumentException("Invalid Home Assistant entity id.", nameof(entityId));
    }
}
