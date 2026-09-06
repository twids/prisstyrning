using Microsoft.Extensions.Options;
using Prisstyrning.Thermal.Optimization;

namespace Prisstyrning.Thermal.Jobs;

/// <summary>Read-only reachability check, independent of control mode and solver execution.</summary>
public sealed class EmhassConnectionWorker(
    IHttpClientFactory clients,
    IOptions<EmhassOptions> options,
    EmhassHealthState health) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            await CheckAsync(stoppingToken);
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task CheckAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var client = clients.CreateClient("Emhass");
            // Do not retrieve/log the configuration body or invoke an optimization action.
            using var response = await client.GetAsync("get-config", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            health.RecordConnection(response.IsSuccessStatusCode, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or InvalidOperationException or ArgumentException or FormatException)
        {
            // Neither exception text nor response bodies belong in diagnostics: they may contain secrets.
            health.RecordConnection(false, DateTimeOffset.UtcNow);
        }
    }
}
