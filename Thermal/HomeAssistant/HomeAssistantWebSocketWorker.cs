using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;

namespace Prisstyrning.Thermal.HomeAssistant;

/// <summary>One revision-bound, read-only HA subscription per configured account.</summary>
public sealed class HomeAssistantWebSocketWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHomeAssistantStateCache _cache;
    private readonly HomeAssistantConnectionChanges _changes;
    private readonly IHomeAssistantWebSocketFactory _sockets;
    private readonly ILogger<HomeAssistantWebSocketWorker> _logger;
    // Reconciled by the single manager loop. Account loops never mutate this map.
    private readonly Dictionary<string, AccountWorker> _workers = new(StringComparer.Ordinal);

    internal TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(30);
    internal Func<int, TimeSpan> RetryDelay { get; init; } = failure =>
        TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(failure, 6))) + Random.Shared.NextDouble());

    public HomeAssistantWebSocketWorker(
        IServiceScopeFactory scopeFactory,
        IHomeAssistantStateCache cache,
        HomeAssistantConnectionChanges changes,
        IHomeAssistantWebSocketFactory sockets,
        ILogger<HomeAssistantWebSocketWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _cache = cache;
        _changes = changes;
        _sockets = sockets;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await ReconcileAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception)
                {
                    // An optional HA/database failure must not stop legacy Hangfire.
                    // Never log exception text, URLs, tokens or server response bodies.
                    _logger.LogWarning("Could not refresh Home Assistant connections; retrying independently of legacy scheduling.");
                }
                await _changes.WaitAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            foreach (var worker in _workers.Values) worker.Cts.Cancel();
            foreach (var worker in _workers.Values) await StopWorkerAsync(worker);
            _workers.Clear();
        }
    }

    internal async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, DateTimeOffset> desired;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
            var configured = await db.HomeAssistantConnections.AsNoTracking()
                .Where(x => x.TelemetryEnabled && x.TelemetryTokenCiphertext != "")
                .Select(x => new { x.UserId, x.UpdatedAtUtc })
                .ToListAsync(cancellationToken);
            desired = configured.Where(x => AdminService.IsValidUserId(x.UserId))
                .ToDictionary(x => x.UserId, x => x.UpdatedAtUtc, StringComparer.Ordinal);
        }

        foreach (var pair in _workers.ToArray())
        {
            var changed = !desired.TryGetValue(pair.Key, out var revision) || revision != pair.Value.Revision;
            var live = _cache.ReadAccount(pair.Key);
            // A poll can start this revision between DB commit and invalidation.
            // In that race the revision matches, but its running lease is retired.
            var reload = live.ConfigurationUpdatedAtUtc == pair.Value.Revision && live.Phase == HomeAssistantLivePhase.Reloading;
            if (!changed && !reload && !pair.Value.Task.IsCompleted) continue;
            if (changed) _cache.RetireRevision(pair.Key, pair.Value.Revision);
            _workers.Remove(pair.Key);
            await StopWorkerAsync(pair.Value);
        }
        foreach (var pair in desired)
        {
            if (_workers.ContainsKey(pair.Key)) continue;
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _workers.Add(pair.Key, new AccountWorker(pair.Value, linked, RunAccountLoopAsync(pair.Key, pair.Value, linked.Token)));
        }
    }

    private async Task StopWorkerAsync(AccountWorker worker)
    {
        worker.Cts.Cancel();
        try { await worker.Task; }
        catch (OperationCanceledException) when (worker.Cts.IsCancellationRequested) { }
        catch (Exception) { _logger.LogWarning("A stopped Home Assistant connection task failed; its cache lease has ended."); }
        finally { worker.Cts.Dispose(); }
    }

    private async Task RunAccountLoopAsync(string userId, DateTimeOffset revision, CancellationToken cancellationToken)
    {
        var failureCount = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var session = _cache.BeginSession(userId, revision);
                if (session is null) return;
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    startup.CancelAfter(StartupTimeout);
                    var connections = scope.ServiceProvider.GetRequiredService<HomeAssistantConnectionService>();
                    var connection = await connections.ResolveAsync(userId, startup.Token)
                        ?? throw new InvalidOperationException("Home Assistant connection is unavailable.");
                    // The database may have changed after the manager's poll.
                    if (!connection.TelemetryEnabled || connection.UpdatedAtUtc != revision) return;
                    var client = scope.ServiceProvider.GetRequiredService<IHomeAssistantTelemetryClient>();
                    await RunSubscriptionAsync(session, connection, client, startup, () => failureCount = 0, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                catch (Exception)
                {
                    failureCount++;
                    _logger.LogWarning("Home Assistant live telemetry could not be maintained; retrying with a new subscription and snapshot.");
                }
                finally { _cache.EndSession(session); }

                await Task.Delay(RetryDelay(failureCount), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        // Completed workers are reaped by the next settings signal or poll. Do not
        // self-signal here: a stale/rejected revision must not create a busy loop.
    }

    private async Task RunSubscriptionAsync(
        HomeAssistantCacheSession session,
        ResolvedHomeAssistantConnection connection,
        IHomeAssistantTelemetryClient client,
        CancellationTokenSource startup,
        Action connected,
        CancellationToken cancellationToken)
    {
        using var socket = await _sockets.ConnectAsync(BuildWebSocketUri(connection.BaseUri), startup.Token);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var abort = lifetime.Token.Register(socket.Abort);
        await AuthenticateAsync(socket, connection.TelemetryToken, startup.Token);
        await SendJsonAsync(socket, new { id = 1, type = "subscribe_events", event_type = "state_changed" }, startup.Token);
        using (var acknowledgement = await ReceiveJsonAsync(socket, startup.Token))
        {
            var root = acknowledgement.RootElement;
            if (!IsType(root, "result") || !HasId(root, 1) ||
                !root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
                throw new InvalidOperationException("Home Assistant did not confirm the state subscription.");
        }
        if (!_cache.BeginSnapshot(session)) return;

        // Receive concurrently, before starting REST. The cache buffers events until
        // the complete snapshot is published, all under the same connection revision.
        var events = ReceiveEventsAsync(session, socket, lifetime.Token);
        Task<IReadOnlyList<HomeAssistantState>>? snapshot = null;
        try
        {
            snapshot = client.GetStatesAsync(connection, startup.Token);
            if (await Task.WhenAny(snapshot, events) == events) await events;
            var states = await snapshot;
            if (events.IsCompleted) await events;
            startup.Token.ThrowIfCancellationRequested();
            if (!_cache.PublishSnapshot(session, states)) return;
            connected();
            startup.CancelAfter(Timeout.InfiniteTimeSpan);
            await events;
        }
        finally
        {
            lifetime.Cancel();
            startup.Cancel();
            try { await events; }
            catch (Exception) when (lifetime.IsCancellationRequested) { }
            if (snapshot is not null)
            {
                try { await snapshot; }
                catch (Exception) when (startup.IsCancellationRequested) { }
            }
        }
    }

    private async Task ReceiveEventsAsync(HomeAssistantCacheSession session, WebSocket socket, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var message = await ReceiveJsonAsync(socket, cancellationToken);
            if (ParseEvent(message.RootElement, DateTimeOffset.UtcNow) is { } change && !_cache.ApplyEvent(session, change)) return;
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal static HomeAssistantStateChange? ParseEvent(JsonElement root, DateTimeOffset receivedAt)
    {
        if (!IsType(root, "event") || !HasId(root, 1) ||
            !root.TryGetProperty("event", out var eventElement) || eventElement.ValueKind != JsonValueKind.Object ||
            !eventElement.TryGetProperty("event_type", out var eventType) || eventType.ValueKind != JsonValueKind.String || eventType.GetString() != "state_changed" ||
            !eventElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("entity_id", out var entityId) || entityId.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(entityId.GetString()) ||
            !data.TryGetProperty("new_state", out var newState)) return null;
        DateTimeOffset? occurred = eventElement.TryGetProperty("time_fired", out var time) && time.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(time.GetString(), out var parsedTime) ? parsedTime.ToUniversalTime() : null;
        if (newState.ValueKind == JsonValueKind.Null) return new(entityId.GetString()!, null, occurred);
        if (newState.ValueKind != JsonValueKind.Object) return null;
        var state = HomeAssistantTelemetryClient.ParseState(newState, receivedAt);
        return state is not null && string.Equals(state.EntityId, entityId.GetString(), StringComparison.Ordinal)
            ? new HomeAssistantStateChange(state.EntityId, state, occurred) : null;
    }

    private static async Task AuthenticateAsync(WebSocket socket, string token, CancellationToken cancellationToken)
    {
        using var required = await ReceiveJsonAsync(socket, cancellationToken);
        if (!IsType(required.RootElement, "auth_required")) throw new InvalidOperationException("Unexpected Home Assistant handshake.");
        await SendJsonAsync(socket, new { type = "auth", access_token = token }, cancellationToken);
        using var result = await ReceiveJsonAsync(socket, cancellationToken);
        if (!IsType(result.RootElement, "auth_ok")) throw new InvalidOperationException("Home Assistant authentication failed.");
    }

    private static bool IsType(JsonElement root, string value) => root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == value;

    private static bool HasId(JsonElement root, int value) => root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var number) && number == value;

    internal static Uri BuildWebSocketUri(Uri baseUri) => new UriBuilder(baseUri)
    {
        Scheme = "wss", Path = baseUri.AbsolutePath.TrimEnd('/') + "/api/websocket", Query = string.Empty
    }.Uri;

    private static async Task SendJsonAsync(WebSocket socket, object value, CancellationToken cancellationToken) =>
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value), WebSocketMessageType.Text, true, cancellationToken);

    private static async Task<JsonDocument> ReceiveJsonAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        const int maxBytes = 1024 * 1024;
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType != WebSocketMessageType.Text || stream.Length + result.Count > maxBytes)
                throw new WebSocketException("Home Assistant closed the connection or sent an unsupported message.");
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        stream.Position = 0;
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private sealed record AccountWorker(DateTimeOffset Revision, CancellationTokenSource Cts, Task Task);
}
