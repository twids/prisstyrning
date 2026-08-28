using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Jobs;

namespace Prisstyrning.Tests.Thermal;

public sealed class DhwLifecycleWorkerTests
{
    [Fact]
    public async Task LegacyRun_IsObservedSeparatelyAndCompletesAfterTwoValidTargetSamples()
    {
        await using var services = BuildServices();
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(services, db =>
        {
            db.ThermalSiteConfigs.Add(new ThermalSiteConfig { UserId = "default", DhwWriter = "Legacy" });
            db.DhwCycles.Add(new DhwCycle
            {
                UserId = "default",
                Source = "Shadow",
                Status = "Shadow",
                Kind = "Eco",
                PlannedStartUtc = now.AddMinutes(5),
                TargetTemperatureC = 45,
                ReservedDurationMinutes = 60
            });
        });
        var worker = new DhwLifecycleWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DhwLifecycleWorker>.Instance);

        await AddSampleAndObserveAsync(services, worker, now, true, 44);
        await AddSampleAndObserveAsync(services, worker, now.AddMinutes(5), true, 46);
        await AddSampleAndObserveAsync(services, worker, now.AddMinutes(10), true, 46);
        await AddSampleAndObserveAsync(services, worker, now.AddMinutes(15), false, 46);

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
        var observed = await db.DhwCycles.SingleAsync(x => x.Source == "LegacyObserved");
        var shadow = await db.DhwCycles.SingleAsync(x => x.Source == "Shadow");
        Assert.Equal("Completed", observed.Status);
        Assert.NotNull(observed.TargetReachedUtc);
        Assert.NotNull(observed.ActualEndUtc);
        Assert.Null(shadow.ActualStartUtc);
        Assert.Equal("Shadow", shadow.Status);
    }

    [Fact]
    public async Task LegacyRun_IsClassifiedAsComfortOnlyAfterTwoSixtyDegreeSamples()
    {
        await using var services = BuildServices();
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(services, db =>
            db.ThermalSiteConfigs.Add(new ThermalSiteConfig { UserId = "default", DhwWriter = "Legacy" }));
        var worker = new DhwLifecycleWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DhwLifecycleWorker>.Instance);

        await AddSampleAndObserveAsync(services, worker, now, true, 59);
        await AddSampleAndObserveAsync(services, worker, now.AddMinutes(5), true, 60);
        await AddSampleAndObserveAsync(services, worker, now.AddMinutes(10), true, 60.2);
        await AddSampleAndObserveAsync(services, worker, now.AddMinutes(15), false, 59.8);

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
        var cycle = await db.DhwCycles.SingleAsync(x => x.Source == "LegacyObserved");
        Assert.Equal("Comfort", cycle.Kind);
        Assert.Equal(60, cycle.TargetTemperatureC);
        Assert.Equal("Completed", cycle.Status);
        Assert.NotNull(cycle.TargetReachedUtc);
    }

    [Fact]
    public async Task JointRun_RecordsCloudDelayWhenPlannedStartIsNotObserved()
    {
        await using var services = BuildServices();
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(services, db =>
        {
            db.ThermalSiteConfigs.Add(new ThermalSiteConfig { UserId = "default", DhwWriter = "Joint" });
            db.DhwCycles.Add(new DhwCycle
            {
                UserId = "default",
                Source = "Joint",
                Status = "Accepted",
                Kind = "Eco",
                PlannedStartUtc = now.AddMinutes(-31),
                TargetTemperatureC = 45,
                ReservedDurationMinutes = 60
            });
            db.ThermalTelemetrySamples.Add(new ThermalTelemetrySample
            {
                UserId = "default",
                TimestampUtc = now,
                DhwActive = false,
                TankTemperatureC = 42
            });
        });
        var worker = new DhwLifecycleWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DhwLifecycleWorker>.Instance);

        await worker.ObserveAsync("default", CancellationToken.None);

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
        Assert.Equal("StartDelayed", (await db.DhwCycles.SingleAsync()).Status);
        Assert.Contains(await db.ThermalEvents.ToListAsync(), x =>
            x.Severity == "Warning" && x.Message.Contains("inte verifierats"));
    }

    [Fact]
    public async Task JointRun_MissedTargetIsMarkedForReplanning()
    {
        await using var services = BuildServices();
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(services, db =>
        {
            db.ThermalSiteConfigs.Add(new ThermalSiteConfig { UserId = "default", DhwWriter = "Joint" });
            db.DhwCycles.Add(new DhwCycle
            {
                UserId = "default",
                Source = "Joint",
                Status = "Running",
                Kind = "Eco",
                PlannedStartUtc = now.AddMinutes(-30),
                ActualStartUtc = now.AddMinutes(-20),
                StartTemperatureC = 40,
                TargetTemperatureC = 50,
                ReservedDurationMinutes = 60
            });
            db.ThermalTelemetrySamples.Add(new ThermalTelemetrySample
            {
                UserId = "default",
                TimestampUtc = now,
                DhwActive = false,
                TankTemperatureC = 46,
                HeatPumpPowerKw = 0.1
            });
        });
        var worker = new DhwLifecycleWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DhwLifecycleWorker>.Instance);

        await worker.ObserveAsync("default", CancellationToken.None);

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
        var cycle = await db.DhwCycles.SingleAsync();
        Assert.Equal("TargetMissed", cycle.Status);
        Assert.NotNull(cycle.ActualEndUtc);
        Assert.Contains(await db.ThermalEvents.ToListAsync(), x =>
            x.Severity == "ActionRequired" && x.Message.Contains("omplaneras"));
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        var databaseName = $"dhw-lifecycle-{Guid.NewGuid():N}";
        services.AddDbContext<PrisstyrningDbContext>(options =>
            options.UseInMemoryDatabase(databaseName));
        return services.BuildServiceProvider();
    }

    private static async Task SeedAsync(ServiceProvider services, Action<PrisstyrningDbContext> seed)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
        seed(db);
        await db.SaveChangesAsync();
    }

    private static async Task AddSampleAndObserveAsync(
        ServiceProvider services,
        DhwLifecycleWorker worker,
        DateTimeOffset timestamp,
        bool dhwActive,
        double tankTemperatureC)
    {
        await SeedAsync(services, db => db.ThermalTelemetrySamples.Add(new ThermalTelemetrySample
        {
            UserId = "default",
            TimestampUtc = timestamp,
            DhwActive = dhwActive,
            TankTemperatureC = tankTemperatureC,
            HeatPumpPowerKw = dhwActive ? 2.2 : 0.1,
            BackupHeaterActive = false
        }));
        await worker.ObserveAsync("default", CancellationToken.None);
    }
}
