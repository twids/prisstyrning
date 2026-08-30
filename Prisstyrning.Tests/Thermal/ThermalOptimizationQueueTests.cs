using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Prisstyrning.Data;
using Prisstyrning.Thermal.Optimization;

namespace Prisstyrning.Tests.Thermal;

public sealed class ThermalOptimizationQueueTests
{
    [Fact]
    public async Task EnqueueOrCoalesce_ReplacesPendingRequestForSameAccount()
    {
        await using var services = Services();
        var queue = Queue(services);
        var first = Request(.5m);
        var latest = Request(1.25m);

        var firstId = await queue.EnqueueOrCoalesceAsync("account-a", "Prices", first, 0, CancellationToken.None);
        var latestId = await queue.EnqueueOrCoalesceAsync("account-a", "Telemetry", latest, 5, CancellationToken.None);

        Assert.Equal(firstId, latestId);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
        var job = await db.ThermalOptimizationJobs.SingleAsync();
        Assert.Equal("account-a", job.PendingKey);
        Assert.Equal(ThermalOptimizationJobStatuses.Pending, job.Status);
        Assert.Equal(5, job.Priority);
        Assert.Contains("1.25", job.RequestJson);
    }

    [Fact]
    public async Task EnqueueAndWait_ReturnsOnlyResultFromClaimedPersistentJob()
    {
        await using var services = Services();
        var queue = Queue(services);
        var waiting = queue.EnqueueAndWaitAsync("account-a", "JointPlan", Request(.5m));
        var claimed = await ClaimEventuallyAsync(queue, "worker-a");
        var expected = new EmhassOptimizationResult(
            [new EmhassOptimizationStep(0, 1200, 21.1, .5)],
            230,
            .15m);

        await queue.CompleteAsync(claimed, expected, CancellationToken.None);
        var actual = await waiting;

        Assert.Equal(expected.SolverDurationMs, actual.SolverDurationMs);
        Assert.Equal(expected.ObjectiveCost, actual.ObjectiveCost);
        Assert.Equal(expected.Steps.ToArray(), actual.Steps.ToArray());
        await using var scope = services.CreateAsyncScope();
        var persisted = await scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>()
            .ThermalOptimizationJobs.SingleAsync();
        Assert.Equal(ThermalOptimizationJobStatuses.Completed, persisted.Status);
        Assert.Null(persisted.LeaseOwner);
        Assert.Null(persisted.PendingKey);
    }

    [Fact]
    public async Task ClaimNext_RecoversExpiredLeaseWithoutRunningJobsConcurrently()
    {
        await using var services = Services();
        var queue = Queue(services);
        await queue.EnqueueOrCoalesceAsync("account-a", "JointPlan", Request(.5m), 0, CancellationToken.None);
        var first = await ClaimEventuallyAsync(queue, "worker-a");

        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
            var job = await db.ThermalOptimizationJobs.SingleAsync();
            job.LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }

        var recovered = await queue.ClaimNextAsync("worker-b", CancellationToken.None);

        Assert.NotNull(recovered);
        Assert.Equal(first.Id, recovered!.Id);
        Assert.Equal(2, recovered.AttemptCount);
        Assert.Equal("worker-b", recovered.LeaseOwner);
        Assert.Null(await queue.ClaimNextAsync("worker-c", CancellationToken.None));
    }

    [Fact]
    public async Task FailedJob_IsReportedToWaitingCoordinatorAndRemainsAuditable()
    {
        await using var services = Services();
        var queue = Queue(services);
        var waiting = queue.EnqueueAndWaitAsync("account-a", "JointPlan", Request(.5m));
        var claimed = await ClaimEventuallyAsync(queue, "worker-a");

        await queue.FailAsync(claimed, "solver failed", CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => waiting);
        Assert.Equal("solver failed", exception.Message);
        var snapshot = await queue.GetSnapshotAsync("account-a", CancellationToken.None);
        Assert.Equal(0, snapshot.Pending);
        Assert.Equal(0, snapshot.Running);
        Assert.NotNull(snapshot.LastFailedUtc);
    }

    private static async Task<ClaimedThermalOptimizationJob> ClaimEventuallyAsync(
        ThermalOptimizationQueue queue,
        string owner)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var claimed = await queue.ClaimNextAsync(owner, CancellationToken.None);
            if (claimed is not null) return claimed;
            await Task.Delay(10);
        }
        throw new InvalidOperationException("Test job was not queued in time.");
    }

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection();
        var databaseName = $"thermal-queue-{Guid.NewGuid():N}";
        services.AddLogging();
        services.AddDbContext<PrisstyrningDbContext>(options =>
            options.UseInMemoryDatabase(databaseName));
        return services.BuildServiceProvider();
    }

    private static ThermalOptimizationQueue Queue(ServiceProvider services) => new(
        services.GetRequiredService<IServiceScopeFactory>(),
        Options.Create(new ThermalOptimizationQueueOptions
        {
            PollIntervalMilliseconds = 10,
            ResultWaitTimeoutSeconds = 60,
            LeaseSeconds = 60
        }),
        NullLogger<ThermalOptimizationQueue>.Instance);

    private static EmhassOptimizationRequest Request(decimal price) => new(
        [price],
        [2d],
        [500d],
        new EmhassThermalConfig(2, .1, 1, 21, [20.5d], [22d]),
        null,
        0,
        2500,
        2500);
}
