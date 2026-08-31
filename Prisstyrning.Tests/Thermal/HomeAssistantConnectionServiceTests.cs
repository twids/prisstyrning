using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Tests.Fixtures;
using Prisstyrning.Thermal.HomeAssistant;

namespace Prisstyrning.Tests.Thermal;

public sealed class HomeAssistantConnectionServiceTests
{
    [Fact]
    public async Task SaveAndResolve_EncryptsTokensAndKeepsConnectionsAccountScoped()
    {
        await using var db = Database();
        var service = Service(db);
        var saved = await service.SaveAsync("account-a", new UpdateHomeAssistantConnectionRequest(
            "https://ha.example.se",
            "telemetry-secret",
            "control-secret",
            TelemetryEnabled: true,
            ControlEnabled: false,
            HeatingDeviationEntityId: "number.daikin_deviation_heating",
            StaleAfterMinutes: 10));

        db.ChangeTracker.Clear();
        var stored = await db.HomeAssistantConnections.SingleAsync();
        var resolved = await service.ResolveAsync("account-a");

        Assert.True(saved.TelemetryTokenConfigured);
        Assert.True(saved.ControlTokenConfigured);
        Assert.DoesNotContain("secret", stored.TelemetryTokenCiphertext, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", stored.ControlTokenCiphertext!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("telemetry-secret", resolved!.TelemetryToken);
        Assert.Equal("control-secret", resolved.ControlToken);
        Assert.Null(await service.GetAsync("account-b"));
    }

    [Fact]
    public async Task Save_WithEmptyTokenFields_PreservesExistingEncryptedTokens()
    {
        await using var db = Database();
        var service = Service(db);
        await service.SaveAsync("account-a", new UpdateHomeAssistantConnectionRequest(
            "https://ha.example.se", "telemetry-secret", "control-secret", true, false,
            "number.daikin_deviation_heating", 10));

        await service.SaveAsync("account-a", new UpdateHomeAssistantConnectionRequest(
            "https://ha.example.se", null, null, true, false,
            "number.daikin_deviation_heating", 15));
        var resolved = await service.ResolveAsync("account-a");

        Assert.Equal("telemetry-secret", resolved!.TelemetryToken);
        Assert.Equal("control-secret", resolved.ControlToken);
        Assert.Equal(15, resolved.StaleAfterMinutes);
    }

    [Fact]
    public async Task Save_RefusesControlBoundaryChangesInActiveMode()
    {
        await using var db = Database();
        var service = Service(db);
        await service.SaveAsync("account-a", new UpdateHomeAssistantConnectionRequest(
            "https://ha.example.se", "telemetry-secret", "control-secret", true, true,
            "number.daikin_deviation_heating", 10));
        db.ThermalSiteConfigs.Add(new ThermalSiteConfig { UserId = "account-a", ControlMode = "LwtActive" });
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(
            "account-a",
            new UpdateHomeAssistantConnectionRequest(
                "https://another.example.se", null, null, true, true,
                "number.daikin_deviation_heating", 10)));

        Assert.Contains("Legacy", exception.Message);
    }

    [Fact]
    public async Task Save_InvalidatesCommittedRevisionAndUsesStablePostgresPrecision()
    {
        await using var db = Database();
        var cache = new HomeAssistantStateCache();
        var changes = new HomeAssistantConnectionChanges();
        var service = new HomeAssistantConnectionService(db, TestSecretProtector.Instance, new AcceptingEndpointValidator(), cache, changes);
        var request = new UpdateHomeAssistantConnectionRequest("https://ha.example.se", "synthetic-telemetry", null, true, false, string.Empty, 10);
        var first = await service.SaveAsync("account-a", request);
        var oldSession = cache.BeginSession("account-a", first.UpdatedAtUtc)!;
        cache.BeginSnapshot(oldSession);
        cache.PublishSnapshot(oldSession, []);

        var saved = await service.SaveAsync("account-a", request with { TelemetryToken = null, StaleAfterMinutes = 15 });
        Assert.True(saved.UpdatedAtUtc > first.UpdatedAtUtc);
        Assert.Equal(0, saved.UpdatedAtUtc.Ticks % 10);
        Assert.Equal(saved.UpdatedAtUtc, (await service.ResolveAsync("account-a"))!.UpdatedAtUtc);
        Assert.False(cache.IsConnected("account-a"));
        Assert.Equal(HomeAssistantLivePhase.Reloading, cache.ReadAccount("account-a").Phase);
        Assert.Empty(cache.Snapshot("account-a"));
        Assert.False(cache.PublishSnapshot(oldSession, []));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await changes.WaitAsync(timeout.Token);
        Assert.False(timeout.IsCancellationRequested);
    }

    [Fact]
    public async Task RejectedSaveAndDelete_LeaveExistingLiveConnectionUnchanged()
    {
        await using var db = Database();
        var cache = new HomeAssistantStateCache();
        var service = new HomeAssistantConnectionService(db, TestSecretProtector.Instance, new AcceptingEndpointValidator(), cache, new HomeAssistantConnectionChanges());
        var request = new UpdateHomeAssistantConnectionRequest("https://ha.example.se", "synthetic-telemetry", null, true, false, string.Empty, 10);
        var saved = await service.SaveAsync("account-a", request);
        var session = cache.BeginSession("account-a", saved.UpdatedAtUtc)!;
        cache.BeginSnapshot(session);
        cache.PublishSnapshot(session, []);
        var before = cache.ReadAccount("account-a");
        db.ThermalSiteConfigs.Add(new ThermalSiteConfig { UserId = "account-a", ControlMode = "LwtActive" });
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync("account-a", request with { StaleAfterMinutes = 0 }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync("account-a"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync("account-a", request with { BaseUrl = "https://other.example.se" }));

        Assert.True(cache.IsConnected("account-a"));
        Assert.Equal(before.LastSnapshotUtc, cache.LastSnapshotUtcFor("account-a"));
        Assert.Equal(saved.UpdatedAtUtc, (await service.GetAsync("account-a"))!.UpdatedAtUtc);
        Assert.Single(await db.HomeAssistantConnections.ToListAsync());
    }

    [Fact]
    public async Task ConcurrentSaves_SerializeCommitAndCacheInvalidationForTheSameAccount()
    {
        var writes = new GatedSaveInterceptor();
        var options = new DbContextOptionsBuilder<PrisstyrningDbContext>().UseInMemoryDatabase($"ha-concurrent-{Guid.NewGuid():N}")
            .AddInterceptors(writes).Options;
        await using var firstDb = new PrisstyrningDbContext(options);
        await using var secondDb = new PrisstyrningDbContext(options);
        var cache = new HomeAssistantStateCache();
        var changes = new HomeAssistantConnectionChanges();
        var firstService = new HomeAssistantConnectionService(firstDb, TestSecretProtector.Instance, new AcceptingEndpointValidator(), cache, changes);
        var secondService = new HomeAssistantConnectionService(secondDb, TestSecretProtector.Instance, new AcceptingEndpointValidator(), cache, changes);
        var request = new UpdateHomeAssistantConnectionRequest("https://ha.example.se", "synthetic-telemetry", null, true, false, string.Empty, 10);
        var firstSave = firstService.SaveAsync("account-a", request);
        Task<HomeAssistantConnectionDto>? secondSave = null;
        try
        {
            await writes.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            secondSave = secondService.SaveAsync("account-a", request with { StaleAfterMinutes = 15 });
            Assert.False(secondSave.IsCompleted);
            Assert.Equal(1, writes.Calls);
        }
        finally { writes.Release.TrySetResult(); }
        var first = await firstSave.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await secondSave!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(second.UpdatedAtUtc > first.UpdatedAtUtc);
        Assert.Equal(second.UpdatedAtUtc, cache.ReadAccount("account-a").ConfigurationUpdatedAtUtc);
        Assert.Equal(second.UpdatedAtUtc, (await firstService.GetAsync("account-a"))!.UpdatedAtUtc);
        Assert.Equal(15, (await firstService.GetAsync("account-a"))!.StaleAfterMinutes);
    }

    private sealed class GatedSaveInterceptor : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private static HomeAssistantConnectionService Service(PrisstyrningDbContext db) =>
        new(db, TestSecretProtector.Instance, new AcceptingEndpointValidator(), new HomeAssistantStateCache(), new HomeAssistantConnectionChanges());

    private static PrisstyrningDbContext Database() => new(
        new DbContextOptionsBuilder<PrisstyrningDbContext>()
            .UseInMemoryDatabase($"ha-connections-{Guid.NewGuid():N}")
            .Options);

    private sealed class AcceptingEndpointValidator : IHomeAssistantEndpointValidator
    {
        public Task<Uri> ValidateAsync(string value, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Uri(value));
    }
}
