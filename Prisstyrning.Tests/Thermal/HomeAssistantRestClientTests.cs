using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Tests.Fixtures;
using Prisstyrning.Thermal.HomeAssistant;

namespace Prisstyrning.Tests.Thermal;

public sealed class HomeAssistantRestClientTests
{
    [Fact]
    public async Task Snapshot_UsesAlreadyResolvedEndpointPathAndIdentity_NotNewSavedSettings()
    {
        await using var db = Database();
        db.HomeAssistantConnections.Add(new HomeAssistantConnection
        {
            UserId = "account-a", BaseUrl = "https://new.example.test", TelemetryEnabled = true,
            TelemetryTokenCiphertext = "must-not-resolve-this-revision"
        });
        await db.SaveChangesAsync();
        var log = new RecordingLogger();
        var validator = new NeverResolveValidator();
        var connections = new HomeAssistantConnectionService(db, TestSecretProtector.Instance, validator,
            new HomeAssistantStateCache(), new HomeAssistantConnectionChanges());
        var handler = new Handler(request =>
        {
            Assert.Equal("https://old.example.test/ha/api/states", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer old-synthetic-telemetry", request.Headers.Authorization!.ToString());
            Assert.Equal(HttpMethod.Get, request.Method);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[{\"entity_id\":\"sensor.room\",\"state\":\"21\",\"attributes\":{\"unit_of_measurement\":\"°C\"},\"last_updated\":\"2026-08-31T04:00:00Z\"}]", Encoding.UTF8, "application/json")
            };
        });
        using var http = new HttpClient(handler);
        var client = new HomeAssistantTelemetryClient(new Factory(http), connections, log);
        var resolved = new ResolvedHomeAssistantConnection("account-a", new Uri("https://old.example.test/ha"), "old-synthetic-telemetry",
            null, true, false, string.Empty, 10, DateTimeOffset.UtcNow.AddMinutes(-1));

        var before = DateTimeOffset.UtcNow;
        var state = Assert.Single(await client.GetStatesAsync(resolved));
        Assert.Equal("21", state.State);
        Assert.Equal("°C", state.Unit);
        Assert.True(state.ReceivedAtUtc >= before);
        Assert.Equal(new DateTimeOffset(2026, 8, 31, 4, 0, 0, TimeSpan.Zero), state.LastUpdatedUtc);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(0, validator.Calls);
        Assert.Empty(log.Messages);
    }

    [Fact]
    public async Task DisabledResolvedConnection_MakesNoHttpRequest()
    {
        await using var db = Database();
        var connections = new HomeAssistantConnectionService(db, TestSecretProtector.Instance, new NeverResolveValidator(),
            new HomeAssistantStateCache(), new HomeAssistantConnectionChanges());
        var handler = new Handler(_ => throw new InvalidOperationException("No request was allowed."));
        using var http = new HttpClient(handler);
        var client = new HomeAssistantTelemetryClient(new Factory(http), connections, new RecordingLogger());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetStatesAsync(new ResolvedHomeAssistantConnection(
            "account-a", new Uri("https://ha.example.test"), "synthetic-secret", null, false, false, string.Empty, 10)));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ConnectionTest_ExceptionTextIsNeverLogged()
    {
        await using var db = Database();
        db.HomeAssistantConnections.Add(new HomeAssistantConnection
        {
            UserId = "account-a", BaseUrl = "https://ha.example.test", TelemetryEnabled = true,
            TelemetryTokenCiphertext = TestSecretProtector.Instance.Protect("synthetic-secret", "account-a", "ha-telemetry")
        });
        await db.SaveChangesAsync();
        var log = new RecordingLogger();
        var connections = new HomeAssistantConnectionService(db, TestSecretProtector.Instance, new AcceptingValidator(),
            new HomeAssistantStateCache(), new HomeAssistantConnectionChanges());
        using var http = new HttpClient(new Handler(_ => throw new HttpRequestException("synthetic-secret private-response private-url")));
        var client = new HomeAssistantTelemetryClient(new Factory(http), connections, log);

        Assert.False(await client.TestConnectionAsync("account-a"));
        Assert.Single(log.Messages);
        Assert.DoesNotContain("synthetic-secret", log.Messages[0]);
        Assert.DoesNotContain("private-", log.Messages[0]);
        Assert.Empty(log.Exceptions);
    }

    private static PrisstyrningDbContext Database() => new(new DbContextOptionsBuilder<PrisstyrningDbContext>()
        .UseInMemoryDatabase($"ha-rest-{Guid.NewGuid():N}").Options);

    private sealed class Factory(HttpClient http) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) { Assert.Equal("HomeAssistantTelemetry", name); return http; }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> action) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(action(request));
        }
    }

    private sealed class NeverResolveValidator : IHomeAssistantEndpointValidator
    {
        public int Calls { get; private set; }
        public Task<Uri> ValidateAsync(string value, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("The subscription snapshot must not resolve a different connection.");
        }
    }

    private sealed class AcceptingValidator : IHomeAssistantEndpointValidator
    {
        public Task<Uri> ValidateAsync(string value, CancellationToken cancellationToken = default) => Task.FromResult(new Uri(value));
    }

    private sealed class RecordingLogger : ILogger<HomeAssistantTelemetryClient>
    {
        public List<string> Messages { get; } = [];
        public List<Exception> Exceptions { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            if (exception is not null) Exceptions.Add(exception);
        }
    }
}
