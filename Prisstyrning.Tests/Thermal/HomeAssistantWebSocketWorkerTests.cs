using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Tests.Fixtures;
using Prisstyrning.Thermal.HomeAssistant;

namespace Prisstyrning.Tests.Thermal;

public sealed class HomeAssistantWebSocketWorkerTests
{
    [Fact]
    public async Task PeriodicRefresh_ObtainsUnchangedReports_WithoutControlWrites()
    {
        await using var test = new Harness(refreshInterval: TimeSpan.FromMilliseconds(30));
        test.Sockets.Queue("ha-a");
        var updated = DateTimeOffset.UtcNow.AddHours(-2);
        var count = 0;
        test.Telemetry.Snapshot = (_, _) => Task.FromResult<IReadOnlyList<HomeAssistantState>>(
            [State("sensor.room", "21", updated) with { LastReportedUtc = updated.AddMinutes(Interlocked.Increment(ref count)) }]);
        await test.SaveAsync("account-a", "ha-a");
        await test.Worker.StartAsync(default);
        await EventuallyAsync(() => test.Cache.Snapshot("account-a").SingleOrDefault()?.LastReportedUtc >= updated.AddMinutes(2));
        Assert.All(test.Telemetry.Requests, request => Assert.Equal(test.Cache.ReadAccount("account-a").ConfigurationUpdatedAtUtc, request.UpdatedAtUtc));
        Assert.True(test.Cache.IsConnected("account-a"));
        await test.AssertLegacyUnchangedAsync();
    }

    [Fact]
    public async Task PeriodicRefresh_FailureRetiresConnectedCacheAndDoesNotLogResponseText()
    {
        await using var test = new Harness(refreshInterval: TimeSpan.FromMilliseconds(30));
        test.Sockets.Queue("ha-a");
        var count = 0;
        test.Telemetry.Snapshot = (_, _) => Interlocked.Increment(ref count) == 1
            ? Task.FromResult<IReadOnlyList<HomeAssistantState>>([State("sensor.room", "21", DateTimeOffset.UtcNow)])
            : Task.FromException<IReadOnlyList<HomeAssistantState>>(new HttpRequestException("private-server-secret"));
        await test.SaveAsync("account-a", "ha-a");
        await test.Worker.StartAsync(default);
        await EventuallyAsync(() => test.Cache.ReadAccount("account-a").Phase == HomeAssistantLivePhase.Reconnecting);
        Assert.False(test.Cache.IsConnected("account-a"));
        Assert.All(test.Log.Messages, message => Assert.DoesNotContain("private-server-secret", message));
        Assert.Empty(test.Log.Exceptions);
        await test.AssertLegacyUnchangedAsync();
    }

    [Fact]
    public async Task Connect_RequiresAcknowledgementAndSnapshot_AndBuffersInterveningEvents()
    {
        await using var test = new Harness();
        var socket = test.Sockets.Queue("ha-a", acknowledged: false);
        var result = new TaskCompletionSource<IReadOnlyList<HomeAssistantState>>(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Telemetry.Snapshot = (_, token) => result.Task.WaitAsync(token);
        await test.SaveAsync("account-a", "ha-a");
        await test.Worker.StartAsync(default);
        await EventuallyAsync(() => socket.Sent.Count == 2);

        Assert.False(test.Cache.IsConnected("account-a"));
        Assert.Empty(test.Telemetry.Requests);
        socket.Push("{\"type\":\"result\",\"id\":1,\"success\":true,\"result\":null}");
        await EventuallyAsync(() => test.Telemetry.Requests.Count == 1);
        Assert.Equal(HomeAssistantLivePhase.Synchronizing, test.Cache.ReadAccount("account-a").Phase);
        var now = DateTimeOffset.UtcNow;
        socket.Push(Event("sensor.room", "22", now));
        await EventuallyAsync(() => test.Cache.LastActivityUtcFor("account-a") is not null);
        Assert.Empty(test.Cache.Snapshot("account-a"));
        Assert.False(test.Cache.IsConnected("account-a"));

        result.SetResult([State("sensor.room", "21", now.AddSeconds(-1))]);
        await EventuallyAsync(() => test.Cache.IsConnected("account-a"));
        Assert.Equal("22", Assert.Single(test.Cache.Snapshot("account-a")).State);
        Assert.Equal(new[] { "auth", "subscribe_events" }, socket.Sent.Select(TypeOf));
        await test.AssertLegacyUnchangedAsync();
    }

    [Theory]
    [InlineData("{\"type\":\"result\",\"id\":1,\"success\":false,\"error\":{\"message\":\"private-server-secret\"}}")]
    [InlineData("{\"type\":\"result\",\"id\":2,\"success\":true}")]
    [InlineData("{\"type\":\"result\",\"id\":1,\"success\":\"true\"}")]
    public async Task Subscription_NotConfirmed_NeverPublishesOrLogsServerText(string acknowledgement)
    {
        await using var test = new Harness();
        var socket = test.Sockets.Queue("ha-a", acknowledged: false);
        socket.Push(acknowledgement);
        await test.SaveAsync("account-a", "ha-a");
        await test.Worker.StartAsync(default);
        await EventuallyAsync(() => test.Log.Messages.Count > 0);

        Assert.True(socket.Disposed);
        Assert.False(test.Cache.IsConnected("account-a"));
        Assert.Empty(test.Telemetry.Requests);
        Assert.Empty(test.Cache.Snapshot("account-a"));
        Assert.All(test.Log.Messages, message =>
        {
            Assert.DoesNotContain("private-server-secret", message);
            Assert.DoesNotContain("synthetic-telemetry", message);
        });
        Assert.Empty(test.Log.Exceptions);
    }

    [Fact]
    public async Task Save_ReconnectsOnlyChangedAccount_WithNewEndpointTokenAndRevision()
    {
        await using var test = new Harness();
        var first = test.Sockets.Queue("ha-a");
        var other = test.Sockets.Queue("ha-b");
        await test.SaveAsync("account-a", "ha-a");
        await test.SaveAsync("account-b", "ha-b");
        await test.Worker.StartAsync(default);
        await EventuallyAsync(() => test.Cache.IsConnected("account-a") && test.Cache.IsConnected("account-b"));
        var otherSnapshot = test.Cache.LastSnapshotUtcFor("account-b");
        var replacement = test.Sockets.Queue("ha-new");
        var saved = await test.SaveAsync("account-a", "ha-new", "rotated-synthetic-telemetry", staleMinutes: 15);
        await EventuallyAsync(() => first.Disposed && test.Cache.IsConnected("account-a") && test.Telemetry.Requests.Count == 3);

        var request = test.Telemetry.Requests.Single(x => x.BaseUri.Host == "ha-new.example.test");
        Assert.Equal(saved.UpdatedAtUtc, request.UpdatedAtUtc);
        Assert.Equal("rotated-synthetic-telemetry", request.TelemetryToken);
        Assert.Equal(15, request.StaleAfterMinutes);
        using var auth = JsonDocument.Parse(replacement.Sent.First());
        Assert.Equal(request.TelemetryToken, auth.RootElement.GetProperty("access_token").GetString());
        Assert.Equal(saved.UpdatedAtUtc, test.Cache.ReadAccount("account-a").ConfigurationUpdatedAtUtc);
        Assert.Equal(otherSnapshot, test.Cache.LastSnapshotUtcFor("account-b"));
        Assert.False(other.Disposed);
        Assert.Single(test.Sockets.Requests.Where(x => x.Host == "ha-b.example.test"));
        await test.AssertLegacyUnchangedAsync();
    }

    [Fact]
    public async Task SaveDuringSnapshot_CancelsOldRequest_AndLateResultCannotReplaceNewAccountData()
    {
        await using var test = new Harness();
        var oldSocket = test.Sockets.Queue("ha-a");
        var late = new TaskCompletionSource<IReadOnlyList<HomeAssistantState>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken oldRequestToken = default;
        test.Telemetry.Snapshot = (connection, token) =>
        {
            if (connection.BaseUri.Host == "ha-a.example.test")
            {
                oldRequestToken = token;
                return late.Task.WaitAsync(token);
            }
            return Task.FromResult<IReadOnlyList<HomeAssistantState>>([State("sensor.new", "22", DateTimeOffset.UtcNow)]);
        };
        await test.SaveAsync("account-a", "ha-a");
        await test.Worker.StartAsync(default);
        await EventuallyAsync(() => test.Telemetry.Requests.Count == 1);

        test.Sockets.Queue("ha-new");
        await test.SaveAsync("account-a", "ha-new");
        await EventuallyAsync(() => test.Cache.IsConnected("account-a"));
        Assert.True(oldRequestToken.IsCancellationRequested);
        Assert.True(oldSocket.Disposed);
        late.SetResult([State("sensor.old", "99", DateTimeOffset.UtcNow)]);
        Assert.Equal("sensor.new", Assert.Single(test.Cache.Snapshot("account-a")).EntityId);
        Assert.True(test.Cache.IsConnected("account-a"));
    }

    [Fact]
    public async Task DisableAndDelete_StopWorkersAndClearTheirCache_WithoutAffectingOtherAccounts()
    {
        await using var test = new Harness();
        var first = test.Sockets.Queue("ha-a");
        var other = test.Sockets.Queue("ha-b");
        await test.SaveAsync("account-a", "ha-a");
        await test.SaveAsync("account-b", "ha-b");
        await test.Worker.StartAsync(default);
        await EventuallyAsync(() => test.Cache.IsConnected("account-a") && test.Cache.IsConnected("account-b"));

        await test.SaveAsync("account-a", "ha-a", telemetryEnabled: false);
        Assert.Empty(test.Cache.Snapshot("account-a"));
        Assert.False(test.Cache.IsConnected("account-a"));
        Assert.True(test.Cache.IsConnected("account-b"));
        await EventuallyAsync(() => first.Disposed);
        using (var scope = test.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<HomeAssistantConnectionService>().DeleteAsync("account-b");
        Assert.Empty(test.Cache.Snapshot("account-b"));
        await EventuallyAsync(() => other.Disposed);
        Assert.Equal(2, test.Sockets.Requests.Count);
        await test.AssertLegacyUnchangedAsync();
    }

    [Fact]
    public async Task ConnectionLoss_ReauthenticatesResubscribesAndReplacesEntireSnapshot()
    {
        await using var test = new Harness(retryDelay: TimeSpan.Zero);
        var first = test.Sockets.Queue("ha-a");
        var second = test.Sockets.Queue("ha-a");
        var count = 0;
        test.Telemetry.Snapshot = (_, _) => Task.FromResult<IReadOnlyList<HomeAssistantState>>(
            [State(Interlocked.Increment(ref count) == 1 ? "sensor.before" : "sensor.after", "21", DateTimeOffset.UtcNow)]);
        await test.SaveAsync("account-a", "ha-a");
        await test.Worker.StartAsync(default);
        await EventuallyAsync(() => test.Cache.IsConnected("account-a"));
        first.Push(string.Empty, WebSocketMessageType.Close);
        await EventuallyAsync(() => test.Cache.IsConnected("account-a") && test.Telemetry.Requests.Count == 2);

        Assert.True(first.Disposed);
        Assert.Equal(new[] { "auth", "subscribe_events" }, second.Sent.Select(TypeOf));
        Assert.Equal("sensor.after", Assert.Single(test.Cache.Snapshot("account-a")).EntityId);
        Assert.NotEmpty(test.Log.Messages);
    }

    [Fact]
    public async Task InvalidationAfterWorkerStartedSameRevision_ReloadsEvenWhenNoSensorEventsArrive()
    {
        await using var test = new Harness();
        var first = test.Sockets.Queue("ha-a");
        test.Sockets.Queue("ha-a");
        var saved = await test.SaveAsync("account-a", "ha-a");
        await test.Worker.StartAsync(default);
        await EventuallyAsync(() => test.Cache.IsConnected("account-a"));

        // Reproduce a DB poll between the SaveChanges commit and cache invalidation.
        test.Cache.Invalidate("account-a", saved.UpdatedAtUtc, telemetryEnabled: true);
        test.Services.GetRequiredService<HomeAssistantConnectionChanges>().Notify();
        await EventuallyAsync(() => first.Disposed && test.Cache.IsConnected("account-a") && test.Telemetry.Requests.Count == 2);
        Assert.Equal(saved.UpdatedAtUtc, test.Cache.ReadAccount("account-a").ConfigurationUpdatedAtUtc);
    }

    [Fact]
    public async Task HandshakeTimeout_IsBounded_AndDoesNotStopOtherAccountOrLegacy()
    {
        await using var test = new Harness(startupTimeout: TimeSpan.FromMilliseconds(250));
        var stalled = test.Sockets.Queue("ha-a", handshake: false);
        test.Sockets.Queue("ha-b");
        await test.SaveAsync("account-a", "ha-a");
        await test.SaveAsync("account-b", "ha-b");
        await test.Worker.StartAsync(default);
        await EventuallyAsync(() => stalled.Disposed && test.Cache.IsConnected("account-b"));

        Assert.False(test.Cache.IsConnected("account-a"));
        Assert.Equal(HomeAssistantLivePhase.Reconnecting, test.Cache.ReadAccount("account-a").Phase);
        Assert.Single(test.Telemetry.Requests);
        await test.AssertLegacyUnchangedAsync();
    }

    [Fact]
    public async Task AuthenticationFailure_DoesNotLogTokenOrResponse_AndNeverFetchesSnapshot()
    {
        await using var test = new Harness();
        var socket = test.Sockets.Queue("ha-a", handshake: false);
        socket.Push("{\"type\":\"auth_required\"}");
        socket.Push("{\"type\":\"auth_invalid\",\"message\":\"synthetic-telemetry private-error\"}");
        await test.SaveAsync("account-a", "ha-a");
        await test.Worker.StartAsync(default);
        await EventuallyAsync(() => socket.Disposed);

        Assert.Empty(test.Telemetry.Requests);
        Assert.False(test.Cache.IsConnected("account-a"));
        Assert.All(test.Log.Messages, message =>
        {
            Assert.DoesNotContain("synthetic-telemetry", message);
            Assert.DoesNotContain("private-error", message);
        });
        Assert.Empty(test.Log.Exceptions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedOrBinaryMessage_DropsSubscriptionWithoutPublishingItsContents(bool oversized)
    {
        await using var test = new Harness();
        var socket = test.Sockets.Queue("ha-a", handshake: false);
        socket.Push(oversized ? new string('x', 1024 * 1024 + 1) : "unsupported binary", oversized ? WebSocketMessageType.Text : WebSocketMessageType.Binary);
        await test.SaveAsync("account-a", "ha-a");
        await test.Worker.StartAsync(default);
        await EventuallyAsync(() => socket.Disposed);
        Assert.False(test.Cache.IsConnected("account-a"));
        Assert.Empty(test.Telemetry.Requests);
    }

    [Theory]
    [InlineData("https://ha.example.test", "wss://ha.example.test/api/websocket")]
    [InlineData("https://ha.example.test/ha/", "wss://ha.example.test/ha/api/websocket")]
    [InlineData("https://ha.example.test:8443/ha", "wss://ha.example.test:8443/ha/api/websocket")]
    public void WebSocketUri_PreservesValidatedBasePathAndPort(string source, string expected) =>
        Assert.Equal(expected, HomeAssistantWebSocketWorker.BuildWebSocketUri(new Uri(source)).AbsoluteUri);

    [Theory]
    [InlineData("id", "7")]
    [InlineData("type", "\"result\"")]
    [InlineData("id", "\"1\"")]
    public void Event_IgnoresWrongSubscriptionOrMessageType(string field, string value)
    {
        var json = JsonNode.Parse(Event("sensor.room", "21", DateTimeOffset.UtcNow))!.AsObject();
        json[field] = JsonNode.Parse(value);
        using var document = JsonDocument.Parse(json.ToJsonString());
        Assert.Null(HomeAssistantWebSocketWorker.ParseEvent(document.RootElement, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Event_RequiresMatchingEntityAndRecognizesRemoval()
    {
        var now = DateTimeOffset.UtcNow;
        var json = JsonNode.Parse(Event("sensor.room", "21", now))!.AsObject();
        json["event"]!["data"]!["new_state"]!["entity_id"] = "sensor.other";
        using var wrong = JsonDocument.Parse(json.ToJsonString());
        Assert.Null(HomeAssistantWebSocketWorker.ParseEvent(wrong.RootElement, now));
        json["event"]!["data"]!["new_state"] = null;
        using var removed = JsonDocument.Parse(json.ToJsonString());
        var change = HomeAssistantWebSocketWorker.ParseEvent(removed.RootElement, now);
        Assert.Equal("sensor.room", change!.EntityId);
        Assert.Null(change.State);
        Assert.Equal(now, change.OccurredAtUtc);
    }

    private static string Event(string entityId, string value, DateTimeOffset updated) => JsonSerializer.Serialize(new
    {
        id = 1, type = "event", @event = new
        {
            event_type = "state_changed", time_fired = updated, data = new
            {
                entity_id = entityId,
                new_state = new { entity_id = entityId, state = value, attributes = new { unit_of_measurement = "°C" }, last_updated = updated, last_changed = updated }
            }
        }
    });

    private static HomeAssistantState State(string id, string value, DateTimeOffset updated) =>
        new(id, value, new JsonObject { ["unit_of_measurement"] = "°C" }, updated, updated, DateTimeOffset.UtcNow);

    private static string? TypeOf(string message)
    {
        using var document = JsonDocument.Parse(message);
        return document.RootElement.GetProperty("type").GetString();
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
        Assert.True(condition(), "The expected isolated test state was not observed within five seconds.");
    }

    private sealed class Harness : IAsyncDisposable
    {
        public HomeAssistantStateCache Cache { get; } = new();
        public FakeSockets Sockets { get; } = new();
        public FakeTelemetry Telemetry { get; } = new();
        public RecordingLogger<HomeAssistantWebSocketWorker> Log { get; } = new();
        public ServiceProvider Services { get; }
        public HomeAssistantWebSocketWorker Worker { get; }

        public Harness(TimeSpan? startupTimeout = null, TimeSpan? retryDelay = null, TimeSpan? refreshInterval = null)
        {
            var databaseName = $"ha-live-{Guid.NewGuid():N}";
            var services = new ServiceCollection();
            services.AddDbContext<PrisstyrningDbContext>(options => options.UseInMemoryDatabase(databaseName));
            services.AddTestCredentialProtection();
            services.AddSingleton<IHomeAssistantStateCache>(Cache);
            services.AddSingleton<HomeAssistantConnectionChanges>();
            services.AddSingleton<IHomeAssistantEndpointValidator, SyntheticEndpointValidator>();
            services.AddScoped<HomeAssistantConnectionService>();
            services.AddSingleton<IHomeAssistantTelemetryClient>(Telemetry);
            Services = services.BuildServiceProvider();
            Worker = new(Services.GetRequiredService<IServiceScopeFactory>(), Cache,
                Services.GetRequiredService<HomeAssistantConnectionChanges>(), Sockets, Log)
            {
                StartupTimeout = startupTimeout ?? TimeSpan.FromSeconds(3),
                RefreshInterval = refreshInterval ?? TimeSpan.FromMinutes(1),
                RetryDelay = _ => retryDelay ?? TimeSpan.FromSeconds(30)
            };
        }

        public async Task<HomeAssistantConnectionDto> SaveAsync(string userId, string host, string? token = "synthetic-telemetry", bool telemetryEnabled = true, int staleMinutes = 10)
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
            if (!await db.ThermalSiteConfigs.AnyAsync(x => x.UserId == userId))
            {
                db.ThermalSiteConfigs.Add(new ThermalSiteConfig { UserId = userId, ControlMode = "Legacy", DhwWriter = "Legacy" });
                await db.SaveChangesAsync();
            }
            return await scope.ServiceProvider.GetRequiredService<HomeAssistantConnectionService>().SaveAsync(userId,
                new($"https://{host}.example.test", token, null, telemetryEnabled, false, string.Empty, staleMinutes));
        }

        public async Task AssertLegacyUnchangedAsync()
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
            Assert.All(await db.ThermalSiteConfigs.ToListAsync(), site =>
            {
                Assert.Equal("Legacy", site.ControlMode);
                Assert.Equal("Legacy", site.DhwWriter);
            });
            Assert.Empty(await db.ThermalControlCommands.ToListAsync());
            Assert.Null(Services.GetService<IHomeAssistantControlClient>());
            Assert.Null(Services.GetService<BatchRunner>());
        }

        public async ValueTask DisposeAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Worker.StopAsync(timeout.Token);
            Worker.Dispose();
            await Services.DisposeAsync();
        }
    }

    private sealed class SyntheticEndpointValidator : IHomeAssistantEndpointValidator
    {
        public Task<Uri> ValidateAsync(string value, CancellationToken cancellationToken = default) => Task.FromResult(new Uri(value));
    }

    private sealed class FakeTelemetry : IHomeAssistantTelemetryClient
    {
        public ConcurrentQueue<ResolvedHomeAssistantConnection> Requests { get; } = new();
        public Func<ResolvedHomeAssistantConnection, CancellationToken, Task<IReadOnlyList<HomeAssistantState>>> Snapshot { get; set; } =
            (_, _) => Task.FromResult<IReadOnlyList<HomeAssistantState>>([State("sensor.room", "21", DateTimeOffset.UtcNow)]);
        public Task<IReadOnlyList<HomeAssistantState>> GetStatesAsync(ResolvedHomeAssistantConnection connection, CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(connection);
            return Snapshot(connection, cancellationToken);
        }
        public Task<bool> TestConnectionAsync(string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<HomeAssistantState?> GetStateAsync(string userId, string entityId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<HomeAssistantState>> GetStatesAsync(string userId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A subscription must not re-resolve the snapshot connection by account ID.");
        public Task<IReadOnlyList<HomeAssistantState>> GetHistoryAsync(string userId, string entityId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeSockets : IHomeAssistantWebSocketFactory
    {
        private readonly ConcurrentDictionary<string, Channel<FakeSocket>> _sockets = new();
        public ConcurrentQueue<Uri> Requests { get; } = new();
        public FakeSocket Queue(string host, bool handshake = true, bool acknowledged = true)
        {
            var socket = new FakeSocket();
            if (handshake)
            {
                socket.Push("{\"type\":\"auth_required\"}");
                socket.Push("{\"type\":\"auth_ok\"}");
                if (acknowledged) socket.Push("{\"type\":\"result\",\"id\":1,\"success\":true,\"result\":null}");
            }
            ForHost($"{host}.example.test").Writer.TryWrite(socket);
            return socket;
        }
        public async Task<WebSocket> ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            Requests.Enqueue(uri);
            return await ForHost(uri.Host).Reader.ReadAsync(cancellationToken);
        }
        private Channel<FakeSocket> ForHost(string host) => _sockets.GetOrAdd(host, _ => Channel.CreateUnbounded<FakeSocket>());
    }

    private sealed class FakeSocket : WebSocket
    {
        private readonly Channel<(byte[] Data, WebSocketMessageType Type)> _incoming = Channel.CreateUnbounded<(byte[], WebSocketMessageType)>();
        private readonly CancellationTokenSource _abort = new();
        private (byte[] Data, WebSocketMessageType Type)? _current;
        private int _offset;
        public ConcurrentQueue<string> Sent { get; } = new();
        public bool Disposed { get; private set; }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _abort.IsCancellationRequested ? WebSocketState.Aborted : WebSocketState.Open;
        public override string? SubProtocol => null;
        public void Push(string message, WebSocketMessageType type = WebSocketMessageType.Text) => _incoming.Writer.TryWrite((Encoding.UTF8.GetBytes(message), type));
        public override void Abort() => _abort.Cancel();
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? description, CancellationToken cancellationToken) { Abort(); return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? description, CancellationToken cancellationToken) { Abort(); return Task.CompletedTask; }
        public override void Dispose() { if (Disposed) return; Abort(); Disposed = true; }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Sent.Enqueue(Encoding.UTF8.GetString(buffer));
            return Task.CompletedTask;
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _abort.Token);
            _current ??= await _incoming.Reader.ReadAsync(linked.Token);
            var frame = _current.Value;
            var count = Math.Min(buffer.Count, frame.Data.Length - _offset);
            frame.Data.AsSpan(_offset, count).CopyTo(buffer.AsSpan());
            _offset += count;
            var complete = _offset == frame.Data.Length;
            if (complete) { _current = null; _offset = 0; }
            return new WebSocketReceiveResult(count, frame.Type, complete);
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ConcurrentQueue<Exception> Exceptions { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Enqueue(formatter(state, exception));
            if (exception is not null) Exceptions.Enqueue(exception);
        }
    }
}
