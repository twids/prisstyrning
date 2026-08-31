using System.Net;
using Microsoft.Extensions.Options;
using Prisstyrning.Thermal.Optimization;

namespace Prisstyrning.Tests.Thermal;

public class EmhassClientTests
{
    [Fact]
    public async Task Optimize_ReadsFreshOfficialCsvAndNeverPublishesToHa()
    {
        using var resultFile = new TemporaryResultFile();
        string? posted = null;
        var requests = 0;
        var handler = new StubHandler(async request =>
        {
            requests++;
            posted = await request.Content!.ReadAsStringAsync();
            resultFile.Write(
                ",P_deferrable0,predicted_temp_heater0,unit_load_cost\n" +
                "2026-01-01T00:00:00Z,1200,21.1,0.5\n" +
                "2026-01-01T00:15:00Z,600,21.2,0.8\n");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
        });
        var client = CreateClient(handler, resultFile.Path);

        var result = await client.OptimizeAsync(Request());

        Assert.Equal(2, result.Steps.Count);
        Assert.Equal(1200, result.Steps[0].SpaceHeatingPowerW);
        Assert.Equal(21.2, result.Steps[1].PredictedTemperatureC);
        Assert.Equal(1, requests);
        Assert.NotNull(posted);
        Assert.Contains("\"continual_publish\":false", posted);
        Assert.Contains("\"entity_save\":false", posted);
        Assert.DoesNotContain("retrieve_hass_conf", posted);
    }

    [Fact]
    public async Task Optimize_RejectsStaleResultFromEarlierSolve()
    {
        using var resultFile = new TemporaryResultFile();
        resultFile.Write(
            ",P_deferrable0,predicted_temp_heater0\n" +
            "2026-01-01T00:00:00Z,1200,21.1\n" +
            "2026-01-01T00:15:00Z,600,21.2\n");
        File.SetLastWriteTimeUtc(resultFile.Path, DateTime.UtcNow.AddMinutes(-5));
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateClient(handler, resultFile.Path).OptimizeAsync(Request()));

        Assert.Contains("stale", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Optimize_RejectsCsvWithoutThermalPrediction()
    {
        using var resultFile = new TemporaryResultFile();
        var handler = new StubHandler(request =>
        {
            resultFile.Write(
                ",P_deferrable0,unit_load_cost\n" +
                "2026-01-01T00:00:00Z,1200,0.5\n" +
                "2026-01-01T00:15:00Z,600,0.8\n");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateClient(handler, resultFile.Path).OptimizeAsync(Request()));

        Assert.Contains("predicted_temp_heater0", exception.Message);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("gap")]
    [InlineData("wrong-start")]
    [InlineData("unzoned")]
    public async Task Optimize_RejectsAmbiguousOrNoncontiguousResultTimeline(string fault)
    {
        using var resultFile = new TemporaryResultFile();
        var second = fault switch
        {
            "duplicate" => "2026-01-01T00:00:00Z",
            "gap" => "2026-01-01T00:30:00Z",
            "unzoned" => "2026-01-01T00:15:00",
            _ => "2026-01-01T00:15:00Z"
        };
        var first = fault == "wrong-start" ? "2026-01-01T00:15:00Z" : "2026-01-01T00:00:00Z";
        var handler = new StubHandler(request =>
        {
            resultFile.Write(",P_deferrable0,predicted_temp_heater0,unit_load_cost\n" +
                $"{first},1200,21.1,0.5\n{second},600,21.2,0.8\n");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var request = Request() with { HorizonStartUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) };

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateClient(handler, resultFile.Path).OptimizeAsync(request));
    }

    [Fact]
    public async Task Optimize_AcceptsOffsetTimelineEquivalentToExpectedUtc()
    {
        using var resultFile = new TemporaryResultFile();
        var handler = new StubHandler(request =>
        {
            resultFile.Write(",P_deferrable0,predicted_temp_heater0,unit_load_cost\n" +
                "2026-01-01T01:00:00+01:00,1200,21.1,0.5\n2026-01-01T01:15:00+01:00,600,21.2,0.8\n");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var request = Request() with { HorizonStartUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) };

        Assert.Equal(2, (await CreateClient(handler, resultFile.Path).OptimizeAsync(request)).Steps.Count);
    }

    [Fact]
    public async Task Optimize_RejectsRowsBeyondTheRequestedHorizon()
    {
        using var resultFile = new TemporaryResultFile();
        var handler = new StubHandler(request =>
        {
            resultFile.Write(",P_deferrable0,predicted_temp_heater0,unit_load_cost\n" +
                "2026-01-01T00:00:00Z,1200,21.1,0.5\n2026-01-01T00:15:00Z,600,21.2,0.8\n" +
                "2026-01-01T00:30:00Z,0,21.2,0.8\n");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateClient(handler, resultFile.Path).OptimizeAsync(Request()));
    }

    [Fact]
    public void CsvParser_HandlesQuotedFieldsAndEscapedQuotes()
    {
        var fields = EmhassClient.ParseCsvLine("timestamp,\"value,with,commas\",\"escaped \"\"quote\"\"\"");

        Assert.Equal(["timestamp", "value,with,commas", "escaped \"quote\""], fields);
    }

    [Fact]
    public void RuntimePayload_MakesSpaceHeatingAndDhwMutuallyExclusive()
    {
        using var resultFile = new TemporaryResultFile();
        var client = CreateClient(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))), resultFile.Path);
        var request = Request() with { DhwStartStep = 1, DhwDurationSteps = 1 };

        var json = System.Text.Json.JsonSerializer.Serialize(client.BuildRuntimePayload(request));

        Assert.Contains("\"names\":[\"deferrable0\",\"deferrable1\"]", json);
        Assert.Contains("\"mutual_exclusion\":true", json);
    }

    [Fact]
    public void RuntimePayload_KeepsAccountModelEvidenceInsideOrchestrator()
    {
        using var resultFile = new TemporaryResultFile();
        var client = CreateClient(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))), resultFile.Path);
        var request = Request();
        var withEvidence = request with
        {
            ModelEvidence = new(10, 11, "Shadow", DateTimeOffset.UtcNow, "local-fingerprint"),
            HorizonStartUtc = DateTimeOffset.UtcNow
        };

        var original = System.Text.Json.JsonSerializer.Serialize(client.BuildRuntimePayload(request));
        var actual = System.Text.Json.JsonSerializer.Serialize(client.BuildRuntimePayload(withEvidence));

        Assert.Equal(original, actual);
        Assert.DoesNotContain("local-fingerprint", actual);
    }

    [Fact]
    public void RuntimePayload_PreservesComfortBoundsAndKeepsTariffOptional()
    {
        using var resultFile = new TemporaryResultFile();
        var client = CreateClient(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))), resultFile.Path);

        var disabled = System.Text.Json.JsonSerializer.Serialize(client.BuildRuntimePayload(Request()));
        var enabled = System.Text.Json.JsonSerializer.Serialize(client.BuildRuntimePayload(
            Request() with { TariffEnabled = true, CapacityCostPerKw = 125 }));

        Assert.Contains("\"min_temperatures\":[20.5,20.5]", disabled);
        Assert.Contains("\"max_temperatures\":[22,22]", disabled);
        Assert.Contains("\"capacity_cost_per_kw\":0", disabled);
        Assert.Contains("\"capacity_cost_per_kw\":125", enabled);
    }

    [Fact]
    public async Task Optimize_HardTimeoutCancelsSolverAndMarksItUnavailable()
    {
        using var resultFile = new TemporaryResultFile();
        var health = new EmhassHealthState();
        var client = CreateClient(new NeverCompletingHandler(), resultFile.Path, solverTimeoutSeconds: 1, health);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.OptimizeAsync(Request()));

        Assert.False(health.Available);
        Assert.Contains("1 sekunder", health.LastError);
    }

    private static EmhassClient CreateClient(
        HttpMessageHandler handler,
        string resultPath,
        int solverTimeoutSeconds = 45,
        EmhassHealthState? health = null)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://emhass:5000/") };
        return new EmhassClient(
            new StubFactory(httpClient),
            Options.Create(new EmhassOptions
            {
                Enabled = true,
                SolverTimeoutSeconds = solverTimeoutSeconds,
                OptimizationTimeStepMinutes = 15,
                ResultPath = resultPath
            }),
            health ?? new EmhassHealthState());
    }

    private static EmhassOptimizationRequest Request() => new(
        [.5m, .8m],
        [2d, 2d],
        [500d, 500d],
        new EmhassThermalConfig(2, .1, 1, 21, [20.5d, 20.5d], [22d, 22d]),
        null,
        0,
        2500,
        2500);

    private sealed class StubFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request);
    }

    private sealed class NeverCompletingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }
    }

    private sealed class TemporaryResultFile : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"emhass-test-{Guid.NewGuid():N}");
        public string Path => System.IO.Path.Combine(_directory, "opt_res_latest.csv");

        public TemporaryResultFile() => Directory.CreateDirectory(_directory);
        public void Write(string contents) => File.WriteAllText(Path, contents);
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
