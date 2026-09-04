using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;

namespace Prisstyrning.Thermal.Optimization;

public sealed class ThermalOptimizationQueueOptions
{
    public const string SectionName = "Thermal:OptimizationQueue";
    public int PollIntervalMilliseconds { get; set; } = 250;
    public int LeaseSeconds { get; set; } = 90;
    public int ResultWaitTimeoutSeconds { get; set; } = 90;
    public int MaximumAttempts { get; set; } = 3;
}

public static class ThermalOptimizationJobStatuses
{
    public const string Pending = "Pending";
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

public interface IEmhassOptimizationDispatcher
{
    Task<EmhassOptimizationResult> EnqueueAndWaitAsync(
        string userId,
        string reason,
        EmhassOptimizationRequest request,
        int priority = 0,
        CancellationToken cancellationToken = default);
}

public sealed record ClaimedThermalOptimizationJob(
    Guid Id,
    string UserId,
    string RequestJson,
    int AttemptCount,
    string LeaseOwner);

public sealed record ThermalOptimizationQueueSnapshot(
    int Pending,
    int Running,
    DateTimeOffset? OldestPendingUtc,
    DateTimeOffset? LastCompletedUtc,
    DateTimeOffset? LastFailedUtc);

public sealed class ThermalOptimizationQueue : IEmhassOptimizationDispatcher
{
    private const string EvidenceErrorPrefix = "MODEL_EVIDENCE:";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ThermalOptimizationQueueOptions _options;
    private readonly ILogger<ThermalOptimizationQueue> _logger;

    public ThermalOptimizationQueue(
        IServiceScopeFactory scopeFactory,
        IOptions<ThermalOptimizationQueueOptions> options,
        ILogger<ThermalOptimizationQueue> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<EmhassOptimizationResult> EnqueueAndWaitAsync(
        string userId,
        string reason,
        EmhassOptimizationRequest request,
        int priority = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var expectedRequestJson = JsonSerializer.Serialize(request, JsonOptions);
        var jobId = await EnqueueOrCoalesceAsync(userId, reason, request, priority, cancellationToken);
        return await WaitForResultAsync(jobId, expectedRequestJson, cancellationToken);
    }

    internal async Task<Guid> EnqueueOrCoalesceAsync(
        string userId,
        string reason,
        EmhassOptimizationRequest request,
        int priority,
        CancellationToken cancellationToken)
    {
        var requestJson = JsonSerializer.Serialize(request, JsonOptions);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
            var now = DateTimeOffset.UtcNow;
            var pending = await db.ThermalOptimizationJobs
                .SingleOrDefaultAsync(x => x.PendingKey == userId, cancellationToken);
            if (pending is null)
            {
                pending = new ThermalOptimizationJob
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    PendingKey = userId,
                    Status = ThermalOptimizationJobStatuses.Pending,
                    Priority = priority,
                    Reason = Limit(reason, 100),
                    RequestJson = requestJson,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    ConcurrencyStamp = Guid.NewGuid()
                };
                db.ThermalOptimizationJobs.Add(pending);
            }
            else
            {
                pending.Priority = Math.Max(pending.Priority, priority);
                pending.Reason = Limit(reason, 100);
                pending.RequestJson = requestJson;
                pending.UpdatedAtUtc = now;
                pending.Error = null;
                pending.ConcurrencyStamp = Guid.NewGuid();
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return pending.Id;
            }
            catch (DbUpdateException) when (attempt < 2)
            {
                if (!await PendingJobExistsAsync(userId, cancellationToken)) throw;
                _logger.LogDebug("A concurrent optimization enqueue for account {UserId} was coalesced.", userId);
            }
        }

        throw new InvalidOperationException("Could not enqueue the EMHASS optimization job.");
    }

    internal async Task<ClaimedThermalOptimizationJob?> ClaimNextAsync(
        string leaseOwner,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        var leaseDuration = TimeSpan.FromSeconds(Math.Clamp(_options.LeaseSeconds, 60, 300));
        var maximumAttempts = Math.Clamp(_options.MaximumAttempts, 1, 10);

        for (var claimAttempt = 0; claimAttempt < 8; claimAttempt++)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
            var now = DateTimeOffset.UtcNow;
            var job = await db.ThermalOptimizationJobs
                .Where(x => x.Status == ThermalOptimizationJobStatuses.Pending ||
                            x.Status == ThermalOptimizationJobStatuses.Running &&
                            x.LeaseExpiresAtUtc != null && x.LeaseExpiresAtUtc <= now)
                .OrderByDescending(x => x.Priority)
                .ThenBy(x => x.CreatedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);
            if (job is null) return null;

            if (job.AttemptCount >= maximumAttempts)
            {
                job.Status = ThermalOptimizationJobStatuses.Failed;
                job.PendingKey = null;
                job.Error = "Optimization job exceeded the maximum lease recovery attempts.";
                job.CompletedAtUtc = now;
                job.UpdatedAtUtc = now;
                job.LeaseOwner = null;
                job.LeaseExpiresAtUtc = null;
                job.ConcurrencyStamp = Guid.NewGuid();
                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateConcurrencyException)
                {
                    continue;
                }
                continue;
            }

            job.Status = ThermalOptimizationJobStatuses.Running;
            job.PendingKey = null;
            job.StartedAtUtc ??= now;
            job.UpdatedAtUtc = now;
            job.LeaseOwner = Limit(leaseOwner, 100);
            job.LeaseExpiresAtUtc = now.Add(leaseDuration);
            job.AttemptCount++;
            job.ConcurrencyStamp = Guid.NewGuid();
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return new ClaimedThermalOptimizationJob(
                    job.Id,
                    job.UserId,
                    job.RequestJson,
                    job.AttemptCount,
                    job.LeaseOwner);
            }
            catch (DbUpdateConcurrencyException)
            {
                continue;
            }
        }

        return null;
    }

    internal async Task CompleteAsync(
        ClaimedThermalOptimizationJob claimed,
        EmhassOptimizationResult result,
        CancellationToken cancellationToken)
    {
        await UpdateClaimedAsync(claimed, job =>
        {
            var now = DateTimeOffset.UtcNow;
            job.Status = ThermalOptimizationJobStatuses.Completed;
            job.ResultJson = JsonSerializer.Serialize(result, JsonOptions);
            job.Error = null;
            job.CompletedAtUtc = now;
            job.UpdatedAtUtc = now;
        }, cancellationToken);
    }

    internal async Task FailAsync(
        ClaimedThermalOptimizationJob claimed,
        string error,
        CancellationToken cancellationToken,
        bool evidenceFailure = false)
    {
        await UpdateClaimedAsync(claimed, job =>
        {
            var now = DateTimeOffset.UtcNow;
            job.Status = ThermalOptimizationJobStatuses.Failed;
            job.Error = Limit(evidenceFailure ? EvidenceErrorPrefix + error : error, 1000);
            job.CompletedAtUtc = now;
            job.UpdatedAtUtc = now;
        }, cancellationToken);
    }

    internal async Task<ThermalOptimizationQueueSnapshot> GetSnapshotAsync(
        string? userId,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
        var jobs = db.ThermalOptimizationJobs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(userId)) jobs = jobs.Where(x => x.UserId == userId);
        return new ThermalOptimizationQueueSnapshot(
            await jobs.CountAsync(x => x.Status == ThermalOptimizationJobStatuses.Pending, cancellationToken),
            await jobs.CountAsync(x => x.Status == ThermalOptimizationJobStatuses.Running, cancellationToken),
            await jobs.Where(x => x.Status == ThermalOptimizationJobStatuses.Pending)
                .MinAsync(x => (DateTimeOffset?)x.CreatedAtUtc, cancellationToken),
            await jobs.Where(x => x.Status == ThermalOptimizationJobStatuses.Completed)
                .MaxAsync(x => (DateTimeOffset?)x.CompletedAtUtc, cancellationToken),
            await jobs.Where(x => x.Status == ThermalOptimizationJobStatuses.Failed)
                .MaxAsync(x => (DateTimeOffset?)x.CompletedAtUtc, cancellationToken));
    }

    internal EmhassOptimizationRequest DeserializeRequest(ClaimedThermalOptimizationJob claimed)
        => JsonSerializer.Deserialize<EmhassOptimizationRequest>(claimed.RequestJson, JsonOptions)
           ?? throw new InvalidOperationException("Optimization job contains no request.");

    private async Task<EmhassOptimizationResult> WaitForResultAsync(Guid jobId, string expectedRequestJson, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.ResultWaitTimeoutSeconds, 60, 300)));
        var delay = TimeSpan.FromMilliseconds(Math.Clamp(_options.PollIntervalMilliseconds, 100, 2000));
        try
        {
            while (true)
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
                var job = await db.ThermalOptimizationJobs.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == jobId, timeout.Token)
                    ?? throw new InvalidOperationException("Optimization job disappeared from the queue.");
                if (!SameRequest(job.RequestJson, expectedRequestJson))
                    throw new ThermalPlanningEvidenceException("Beräkningen ersattes av nyare underlag. Det äldre anropet får inte använda ersättningsresultatet.");
                if (job.Status == ThermalOptimizationJobStatuses.Completed)
                    return JsonSerializer.Deserialize<EmhassOptimizationResult>(job.ResultJson ?? string.Empty, JsonOptions)
                           ?? throw new InvalidOperationException("Optimization job completed without a valid result.");
                if (job.Status == ThermalOptimizationJobStatuses.Failed)
                {
                    if (job.Error?.StartsWith(EvidenceErrorPrefix, StringComparison.Ordinal) == true)
                        throw new ThermalPlanningEvidenceException(job.Error[EvidenceErrorPrefix.Length..]);
                    throw new InvalidOperationException(job.Error ?? "EMHASS optimization failed.");
                }
                await Task.Delay(delay, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out while waiting for the queued EMHASS optimization result.");
        }
    }

    private async Task UpdateClaimedAsync(
        ClaimedThermalOptimizationJob claimed,
        Action<ThermalOptimizationJob> update,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
        var now = DateTimeOffset.UtcNow;
        var job = await db.ThermalOptimizationJobs.SingleOrDefaultAsync(
            x => x.Id == claimed.Id && x.Status == ThermalOptimizationJobStatuses.Running &&
                 x.UserId == claimed.UserId && x.LeaseOwner == claimed.LeaseOwner &&
                 x.AttemptCount == claimed.AttemptCount && x.LeaseExpiresAtUtc > now && x.RequestJson == claimed.RequestJson,
            cancellationToken);
        if (job is null) throw new InvalidOperationException("Optimization job lease was lost before completion.");
        update(job);
        job.PendingKey = null;
        job.LeaseOwner = null;
        job.LeaseExpiresAtUtc = null;
        job.ConcurrencyStamp = Guid.NewGuid();
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> PendingJobExistsAsync(string userId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
        return await db.ThermalOptimizationJobs.AsNoTracking()
            .AnyAsync(x => x.PendingKey == userId, cancellationToken);
    }

    private static string Limit(string value, int maximumLength)
        => value.Length <= maximumLength ? value : value[..maximumLength];

    private static bool SameRequest(string actual, string expected)
    {
        // PostgreSQL jsonb normalizes spacing and object-key order. Compare the
        // complete values, including ordered forecasts, not the serialized text.
        try
        {
            using var actualJson = JsonDocument.Parse(actual);
            using var expectedJson = JsonDocument.Parse(expected);
            return JsonElement.DeepEquals(actualJson.RootElement, expectedJson.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
