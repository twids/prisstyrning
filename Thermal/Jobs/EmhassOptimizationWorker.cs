using Microsoft.Extensions.Options;
using Prisstyrning.Thermal.Optimization;

namespace Prisstyrning.Thermal.Jobs;

public sealed class EmhassOptimizationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ThermalOptimizationQueue _queue;
    private readonly EmhassOptions _emhassOptions;
    private readonly ThermalOptimizationQueueOptions _queueOptions;
    private readonly ILogger<EmhassOptimizationWorker> _logger;
    private readonly string _leaseOwner = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    public EmhassOptimizationWorker(
        IServiceScopeFactory scopeFactory,
        ThermalOptimizationQueue queue,
        IOptions<EmhassOptions> emhassOptions,
        IOptions<ThermalOptimizationQueueOptions> queueOptions,
        ILogger<EmhassOptimizationWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _queue = queue;
        _emhassOptions = emhassOptions.Value;
        _queueOptions = queueOptions.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var idleDelay = TimeSpan.FromMilliseconds(Math.Clamp(_queueOptions.PollIntervalMilliseconds, 100, 2000));
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_emhassOptions.Enabled)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            var claimed = await _queue.ClaimNextAsync(_leaseOwner, stoppingToken);
            if (claimed is null)
            {
                await Task.Delay(idleDelay, stoppingToken);
                continue;
            }

            try
            {
                var request = _queue.DeserializeRequest(claimed);
                await using var scope = _scopeFactory.CreateAsyncScope();
                var client = scope.ServiceProvider.GetRequiredService<IEmhassClient>();
                var result = await client.OptimizeAsync(request, stoppingToken);
                await _queue.CompleteAsync(claimed, result, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Queued EMHASS optimization {JobId} failed for account {UserId}.",
                    claimed.Id,
                    claimed.UserId);
                try
                {
                    await _queue.FailAsync(claimed, exception.Message, stoppingToken);
                }
                catch (Exception updateException) when (updateException is not OperationCanceledException)
                {
                    _logger.LogError(updateException, "Could not persist failure for EMHASS optimization {JobId}.", claimed.Id);
                }
            }
        }
    }
}
