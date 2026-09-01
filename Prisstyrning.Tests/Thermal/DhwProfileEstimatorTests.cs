using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Domain;
using Prisstyrning.Thermal.Optimization;

namespace Prisstyrning.Tests.Thermal;

public sealed class DhwProfileEstimatorTests
{
    [Fact]
    public void Percentile_UsesConservativeNinetiethPercentileDuration()
    {
        Assert.Equal(70, DhwProfileEstimator.Percentile([30, 35, 40, 45, 50, 55, 60, 65, 70, 75], 0.9));
    }

    [Fact]
    public async Task Estimate_LearnsLateBackupHeaterPhaseSeparatelyFromCompressorCop()
    {
        var options = new DbContextOptionsBuilder<PrisstyrningDbContext>()
            .UseInMemoryDatabase($"dhw-profile-{Guid.NewGuid():N}")
            .Options;
        await using var db = new PrisstyrningDbContext(options);
        db.ThermalSiteConfigs.Add(new ThermalSiteConfig
        {
            UserId = "default",
            HeatPumpPowerSignVerified = true
        });
        var roles = new[]
        {
            ThermalEntityRoles.DhwActive,
            ThermalEntityRoles.DefrostActive,
            ThermalEntityRoles.BackupHeaterActive,
            ThermalEntityRoles.HeatPumpPower
        };
        db.ThermalEntityConfigs.AddRange(roles.Select(role => new ThermalEntityConfig
        {
            UserId = "default",
            Role = role,
            EntityId = $"sensor.{role}"
        }));
        var quality = JsonSerializer.Serialize(new
        {
            entities = roles.ToDictionary(
                role => role,
                role => (object)new { quality = 0, excluded = false })
        });
        var firstStart = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var cycleIndex = 0; cycleIndex < 3; cycleIndex++)
        {
            var start = firstStart.AddDays(cycleIndex);
            db.DhwCycles.Add(new DhwCycle
            {
                UserId = "default",
                Source = "LegacyObserved",
                Status = "Completed",
                Kind = "Comfort",
                PlannedStartUtc = start,
                ActualStartUtc = start,
                TargetReachedUtc = start.AddMinutes(60),
                ActualEndUtc = start.AddMinutes(65),
                StartTemperatureC = 42,
                TargetTemperatureC = 60,
                ReservedDurationMinutes = 60,
                TargetVerificationCount = 2
            });
            for (var minute = 0; minute <= 60; minute += 5)
            {
                var late = minute >= 55;
                var middle = minute is >= 40 and < 55;
                db.ThermalTelemetrySamples.Add(new ThermalTelemetrySample
                {
                    UserId = "default",
                    TimestampUtc = start.AddMinutes(minute),
                    DhwActive = true,
                    DefrostActive = false,
                    HeatPumpPowerKw = late ? 3.5 : middle ? 2.5 : 2,
                    Cop = late ? 1 : middle ? 2.5 : 3.2,
                    BackupHeaterActive = late,
                    QualityJson = quality
                });
            }
        }
        await db.SaveChangesAsync();

        var profile = await new DhwProfileEstimator(db).EstimateAsync("default", "Comfort", 42, 60, 2);

        Assert.Equal(60, profile.ExpectedDurationMinutes);
        Assert.Equal(60, profile.ReservedDurationMinutes);
        var latePhase = profile.PowerSteps.Last();
        Assert.True(latePhase.BackupHeater);
        Assert.Equal(1, latePhase.ExpectedCop);
        Assert.True(latePhase.ElectricPowerKw > profile.PowerSteps.First().ElectricPowerKw);
        var evidence = Assert.IsType<DhwProfileSourceEvidence>(profile.SourceEvidence);
        Assert.Equal(3, evidence.CompletedCycleCount);
        Assert.Equal(39, evidence.PhaseSampleCount);
        Assert.True(evidence.UsedEmpiricalDuration);
        Assert.True(evidence.UsedEmpiricalPower);
        Assert.False(string.IsNullOrWhiteSpace(evidence.SourceFingerprint));
        await DhwProfileEstimator.EnsureCurrentAsync(db, "default", evidence, CancellationToken.None);

        (await db.ThermalTelemetrySamples.FirstAsync()).HeatPumpPowerKw += .1;
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<ThermalPlanningEvidenceException>(() =>
            DhwProfileEstimator.EnsureCurrentAsync(db, "default", evidence, CancellationToken.None));
    }

    [Fact]
    public async Task Estimate_ExcludesUnverifiedCyclesAndKeepsFallbackSourceVerifiablePerAccount()
    {
        var options = new DbContextOptionsBuilder<PrisstyrningDbContext>()
            .UseInMemoryDatabase($"dhw-profile-fallback-{Guid.NewGuid():N}")
            .Options;
        await using var db = new PrisstyrningDbContext(options);
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var ownCycle = new DhwCycle
        {
            UserId = "default",
            Kind = "Eco",
            Source = "LegacyObserved",
            Status = "Completed",
            PlannedStartUtc = start,
            ActualStartUtc = start,
            TargetReachedUtc = start.AddMinutes(45),
            ActualEndUtc = start.AddMinutes(50),
            StartTemperatureC = 40,
            TargetTemperatureC = 45,
            TargetVerificationCount = 1
        };
        db.DhwCycles.AddRange(
            ownCycle,
            new DhwCycle
            {
                UserId = "other",
                Kind = "Eco",
                Source = "LegacyObserved",
                Status = "Completed",
                PlannedStartUtc = start,
                ActualStartUtc = start,
                TargetReachedUtc = start.AddMinutes(45),
                ActualEndUtc = start.AddMinutes(50),
                StartTemperatureC = 40,
                TargetTemperatureC = 45,
                TargetVerificationCount = 2
            });
        await db.SaveChangesAsync();

        var profile = await new DhwProfileEstimator(db).EstimateAsync("default", "Eco", 40, 45, 0);

        Assert.Equal(30, profile.ExpectedDurationMinutes);
        Assert.Equal(45, profile.ReservedDurationMinutes);
        var evidence = Assert.IsType<DhwProfileSourceEvidence>(profile.SourceEvidence);
        Assert.Equal(0, evidence.CompletedCycleCount);
        Assert.Equal(0, evidence.PhaseSampleCount);
        Assert.False(evidence.UsedEmpiricalDuration);
        Assert.False(evidence.UsedEmpiricalPower);
        await DhwProfileEstimator.EnsureCurrentAsync(db, "default", evidence, CancellationToken.None);

        ownCycle.TargetVerificationCount = 2;
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<ThermalPlanningEvidenceException>(() =>
            DhwProfileEstimator.EnsureCurrentAsync(db, "default", evidence, CancellationToken.None));
    }
}
