using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Prisstyrning.Thermal.HomeAssistant;

public sealed class HomeAssistantWebSocketWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHomeAssistantStateCache _cache;
    private readonly HomeAssistantTelemetryOptions _options;
    private readonly IHomeAssistantCredentialProvider _credentials;
    private readonly ILogger<HomeAssistantWebSocketWorker> _logger;

    public HomeAssistantWebSocketWorker(
        IServiceScopeFactory scopeFactory,
        IHomeAssistantStateCache cache,
        IOptions<HomeAssistantTelemetryOptions> options,
        IHomeAssistantCredentialProvider credentials,
        ILogger<HomeAssistantWebSocketWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _cache = cache;
        _options = options.Value;
        _credentials = credentials;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !TryBuildWebSocketUri(out var socketUri) || !_credentials.HasTelemetryToken)
        {
            _logger.LogInformation("Home Assistant telemetry is disabled or not configured.");
            return;
        }

        var failureCount = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(socketUri, stoppingToken);
                await AuthenticateAsync(socket, stoppingToken);
                await RefreshSnapshotAsync(stoppingToken);
                await SendJsonAsync(socket, new { id = 1, type = "subscribe_events", event_type = "state_changed" }, stoppingToken);
                _cache.MarkConnected();
                failureCount = 0;
                await ReceiveEventsAsync(socket, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is WebSocketException or HttpRequestException or JsonException or InvalidOperationException)
            {
                failureCount++;
                _logger.LogWarning("Home Assistant WebSocket disconnected: {Message}", exception.Message);
            }
            finally
            {
                _cache.MarkDisconnected();
            }

            var delaySeconds = Math.Min(60, Math.Pow(2, Math.Min(failureCount, 5))) + Random.Shared.NextDouble();
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
        }
    }

    private async Task AuthenticateAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var required = await ReceiveJsonAsync(socket, cancellationToken);
        if (required.RootElement.GetProperty("type").GetString() != "auth_required")
        {
            throw new InvalidOperationException("Unexpected Home Assistant WebSocket handshake.");
        }

        await SendJsonAsync(socket, new { type = "auth", access_token = _credentials.GetTelemetryToken() }, cancellationToken);
        using var result = await ReceiveJsonAsync(socket, cancellationToken);
        if (result.RootElement.GetProperty("type").GetString() != "auth_ok")
        {
            throw new InvalidOperationException("Home Assistant WebSocket authentication failed.");
        }
    }

    private async Task RefreshSnapshotAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IHomeAssistantTelemetryClient>();
        _cache.Replace(await client.GetStatesAsync(cancellationToken));
    }

    private async Task ReceiveEventsAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using var message = await ReceiveJsonAsync(socket, cancellationToken);
            var root = message.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "event" ||
                !root.TryGetProperty("event", out var eventElement) ||
                !eventElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("new_state", out var newState) ||
                newState.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var parsed = HomeAssistantTelemetryClient.ParseState(newState, DateTimeOffset.UtcNow);
            if (parsed is not null) _cache.Upsert(parsed);
        }
    }

    private static async Task SendJsonAsync(ClientWebSocket socket, object value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<JsonDocument> ReceiveJsonAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException("Home Assistant closed the WebSocket connection.");
            }
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        stream.Position = 0;
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private bool TryBuildWebSocketUri(out Uri socketUri)
    {
        socketUri = null!;
        if (!HomeAssistantTelemetryClient.IsSupportedBaseUrl(_options.BaseUrl) ||
            !Uri.TryCreate(_options.BaseUrl, UriKind.Absolute, out var baseUri)) return false;
        var builder = new UriBuilder(baseUri)
        {
            Scheme = baseUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Path = "/api/websocket",
            Query = string.Empty
        };
        socketUri = builder.Uri;
        return true;
    }
}
