using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;

namespace Prisstyrning.Thermal.HomeAssistant;

/// <summary>Maintains one isolated HA WebSocket subscription per configured account.</summary>
public sealed class HomeAssistantWebSocketWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHomeAssistantStateCache _cache;
    private readonly ILogger<HomeAssistantWebSocketWorker> _logger;
    private readonly ConcurrentDictionary<string, (CancellationTokenSource Cts, Task Task)> _workers = new(StringComparer.Ordinal);

    public HomeAssistantWebSocketWorker(
        IServiceScopeFactory scopeFactory,
        IHomeAssistantStateCache cache,
        ILogger<HomeAssistantWebSocketWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _cache = cache;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            do
            {
                IReadOnlyList<string> configured;
                using (var scope = _scopeFactory.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
                    configured = await db.HomeAssistantConnections.AsNoTracking()
                        .Where(x => x.TelemetryEnabled)
                        .Select(x => x.UserId)
                        .ToListAsync(stoppingToken);
                }

                var desired = configured.Where(AdminService.IsValidUserId).ToHashSet(StringComparer.Ordinal);
                foreach (var userId in desired)
                {
                    _workers.GetOrAdd(userId, id =>
                    {
                        var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        return (linked, RunAccountLoopAsync(id, linked.Token));
                    });
                }
                foreach (var stale in _workers.Keys.Where(x => !desired.Contains(x)).ToArray())
                {
                    if (_workers.TryRemove(stale, out var worker)) worker.Cts.Cancel();
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            foreach (var worker in _workers.Values) worker.Cts.Cancel();
            try { await Task.WhenAll(_workers.Values.Select(x => x.Task)); } catch (OperationCanceledException) { }
        }
    }

    private async Task RunAccountLoopAsync(string userId, CancellationToken cancellationToken)
    {
        var failureCount = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                ResolvedHomeAssistantConnection connection;
                using (var scope = _scopeFactory.CreateScope())
                {
                    var connections = scope.ServiceProvider.GetRequiredService<HomeAssistantConnectionService>();
                    connection = await connections.ResolveAsync(userId, cancellationToken)
                                 ?? throw new InvalidOperationException("Home Assistant connection is unavailable.");
                }
                if (!connection.TelemetryEnabled) return;

                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(BuildWebSocketUri(connection.BaseUri), cancellationToken);
                await AuthenticateAsync(socket, connection.TelemetryToken, cancellationToken);
                await RefreshSnapshotAsync(userId, cancellationToken);
                await SendJsonAsync(socket, new { id = 1, type = "subscribe_events", event_type = "state_changed" }, cancellationToken);
                _cache.MarkConnected(userId);
                failureCount = 0;
                await ReceiveEventsAsync(userId, socket, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception exception) when (exception is WebSocketException or HttpRequestException or JsonException or InvalidOperationException or ArgumentException)
            {
                failureCount++;
                _logger.LogWarning("Home Assistant WebSocket disconnected for account {UserId}: {Message}", userId, exception.Message);
            }
            finally { _cache.MarkDisconnected(userId); }

            var delaySeconds = Math.Min(60, Math.Pow(2, Math.Min(failureCount, 5))) + Random.Shared.NextDouble();
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
        }
    }

    private async Task RefreshSnapshotAsync(string userId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IHomeAssistantTelemetryClient>();
        _cache.Replace(userId, await client.GetStatesAsync(userId, cancellationToken));
    }

    private async Task ReceiveEventsAsync(string userId, ClientWebSocket socket, CancellationToken cancellationToken)
    {
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using var message = await ReceiveJsonAsync(socket, cancellationToken);
            var root = message.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "event" ||
                !root.TryGetProperty("event", out var eventElement) ||
                !eventElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("new_state", out var newState) || newState.ValueKind != JsonValueKind.Object) continue;
            if (HomeAssistantTelemetryClient.ParseState(newState, DateTimeOffset.UtcNow) is { } parsed) _cache.Upsert(userId, parsed);
        }
    }

    private static async Task AuthenticateAsync(ClientWebSocket socket, string token, CancellationToken cancellationToken)
    {
        using var required = await ReceiveJsonAsync(socket, cancellationToken);
        if (required.RootElement.GetProperty("type").GetString() != "auth_required")
            throw new InvalidOperationException("Unexpected Home Assistant WebSocket handshake.");
        await SendJsonAsync(socket, new { type = "auth", access_token = token }, cancellationToken);
        using var result = await ReceiveJsonAsync(socket, cancellationToken);
        if (result.RootElement.GetProperty("type").GetString() != "auth_ok")
            throw new InvalidOperationException("Home Assistant WebSocket authentication failed.");
    }

    private static Uri BuildWebSocketUri(Uri baseUri) => new UriBuilder(baseUri)
    {
        Scheme = "wss",
        Path = "/api/websocket",
        Query = string.Empty
    }.Uri;

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
            if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException("Home Assistant closed the WebSocket connection.");
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        stream.Position = 0;
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }
}
