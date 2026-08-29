using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
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
                ReservedDurationMinutes = 60
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
                    HeatPumpPowerKw = late ? 3.5 : middle ? 2.5 : 2,
                    Cop = late ? 1 : middle ? 2.5 : 3.2,
                    BackupHeaterActive = late
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
    }
}
