using System.Net;
using Microsoft.Extensions.Options;
using Prisstyrning.Thermal.Jobs;
using Prisstyrning.Thermal.Optimization;

namespace Prisstyrning.Tests.Thermal;

public sealed class EmhassConnectionWorkerTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    public async Task Check_UsesOnlyReadOnlyEndpoint_WithoutChangingSolverEvidence(HttpStatusCode code, bool reachable)
    {
        var health = new EmhassHealthState();
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/get-config", request.RequestUri!.AbsolutePath);
            Assert.Null(request.Content);
            return Task.FromResult(new HttpResponseMessage(code));
        });
        using var worker = Create(handler, health);
        await worker.CheckAsync(default);
        Assert.Equal(reachable, health.Connection(DateTimeOffset.UtcNow).Reachable);
        Assert.False(health.Available);
        Assert.Null(health.LastSuccessUtc);
        Assert.Null(health.LastError);
    }

    [Fact]
    public async Task Check_HungRequest_TimesOut_AndNextCheckCanRecover()
    {
        var health = new EmhassHealthState();
        var attempts = 0;
        using var handler = new Handler(async (_, ct) =>
        {
            if (++attempts == 1) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var worker = Create(handler, health);
        await worker.CheckAsync(default).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(health.Connection(DateTimeOffset.UtcNow).Reachable);
        await worker.CheckAsync(default);
        Assert.True(health.Connection(DateTimeOffset.UtcNow).Reachable);
        Assert.False(health.Available);
    }

    [Fact]
    public async Task Check_Disabled_DoesNotContactServer()
    {
        var health = new EmhassHealthState();
        using var handler = new Handler((_, _) => throw new Exception("Must not contact EMHASS"));
        using var worker = Create(handler, health, false);
        await worker.CheckAsync(default);
        Assert.Null(health.Connection(DateTimeOffset.UtcNow).CheckedUtc);
    }

    [Fact]
    public async Task Check_NetworkFailure_IsIsolatedFromSuccessfulSolverEvidence()
    {
        var health = new EmhassHealthState();
        health.Success(123);
        using var handler = new Handler((_, _) => throw new HttpRequestException("private diagnostics"));
        using var worker = Create(handler, health);
        await worker.CheckAsync(default);
        Assert.False(health.Connection(DateTimeOffset.UtcNow).Reachable);
        Assert.True(health.Available);
        Assert.Null(health.LastError);
    }

    [Fact]
    public async Task Check_Shutdown_DoesNotReportAnOutage()
    {
        var health = new EmhassHealthState();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var handler = new Handler((_, ct) => Task.FromCanceled<HttpResponseMessage>(ct));
        using var worker = Create(handler, health);
        await worker.CheckAsync(cancelled.Token);
        Assert.Null(health.Connection(DateTimeOffset.UtcNow).Reachable);
    }

    [Fact]
    public void Connection_Expires_AndRejectsFutureTimestamp()
    {
        var health = new EmhassHealthState();
        var now = DateTimeOffset.UtcNow;
        health.RecordConnection(true, now);
        Assert.True(health.Connection(now.AddMinutes(2)).Reachable);
        Assert.Null(health.Connection(now.AddMinutes(2).AddTicks(1)).Reachable);
        Assert.Null(health.Connection(now.AddSeconds(-1)).Reachable);
        Assert.Equal(now, health.Connection(now.AddMinutes(3)).CheckedUtc);
    }

    private static EmhassConnectionWorker Create(HttpMessageHandler handler, EmhassHealthState health, bool enabled = true) =>
        new(new Factory(handler), Options.Create(new EmhassOptions { Enabled = enabled }), health);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("Emhass", name);
            return new(handler, disposeHandler: false) { BaseAddress = new Uri("http://emhass:5000/") };
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
