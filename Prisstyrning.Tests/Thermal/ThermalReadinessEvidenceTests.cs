using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Tests.Fixtures;
using Prisstyrning.Thermal.Control;
using Prisstyrning.Thermal.Domain;
using Prisstyrning.Thermal.HomeAssistant;
using Prisstyrning.Thermal.Optimization;

namespace Prisstyrning.Tests.Thermal;

public sealed class ThermalReadinessEvidenceTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("{\"twoHourMaeC\":\"0.1\",\"dayMaeC\":0.2}")]
    [InlineData("{\"twoHourMaeC\":-0.1,\"dayMaeC\":0.2}")]
    [InlineData("{\"twoHourMaeC\":0.1,\"dayMaeC\":-1e999}")]
    [InlineData("{\"twoHourMaeC\":0.1,\"dayMaeC\":0.2}")]
    public async Task Readiness_MalformedOrUnprovenThermalMetricsNeverApproveOrThrow(string metrics)
    {
        await using var fixture = new Fixture();
        fixture.Db.ThermalModelVersions.Add(Model("2R2C", metrics));
        await fixture.Db.SaveChangesAsync();

        Assert.False((await fixture.EvaluateAsync()).Single(x => x.Key == "model").Passed);
        await fixture.AssertLegacyAsync();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"mae\":true}")]
    [InlineData("{\"mae\":-0.2}")]
    [InlineData("{\"mae\":-1e999}")]
    [InlineData("{\"mae\":0.1}")]
    public async Task Readiness_MalformedOrUnprovenCopMetricsNeverApproveOrThrow(string metrics)
    {
        await using var fixture = new Fixture();
        fixture.Db.ThermalModelVersions.Add(Model("COP", metrics));
        await fixture.Db.SaveChangesAsync();

        Assert.False((await fixture.EvaluateAsync(ControlMode.FullActive)).Single(x => x.Key == "cop-model").Passed);
        await fixture.AssertLegacyAsync();
    }

    [Theory]
    [InlineData(ControlMode.LwtActive, false)]
    [InlineData(ControlMode.FullActive, false)]
    [InlineData(ControlMode.LwtActive, true)]
    [InlineData(ControlMode.FullActive, true)]
    public async Task Readiness_AllActiveModesRequireValidatedCopAndVerifiedPower(ControlMode target, bool verified)
    {
        await using var fixture = new Fixture();
        fixture.Db.ThermalSiteConfigs.Local.Single().HeatPumpPowerSignVerified = verified;
        if (verified) fixture.Db.ThermalModelVersions.Add(ThermalModelEvidenceTests.ValidModel("COP", fixture.Now));
        await fixture.Db.SaveChangesAsync();

        var checks = await fixture.EvaluateAsync(target);

        Assert.Equal(verified, checks.Single(x => x.Key == "power-sign").Passed);
        Assert.Equal(verified, checks.Single(x => x.Key == "cop-model").Passed);
        await fixture.AssertLegacyAsync();
    }

    [Fact]
    public async Task Readiness_ShadowCanCollectTrainingDataBeforeCopOrPowerSignIsProven()
    {
        await using var fixture = new Fixture();
        await fixture.Db.SaveChangesAsync();

        var checks = await fixture.EvaluateAsync(ControlMode.Shadow);

        Assert.DoesNotContain(checks, x => x.Key is "cop-model" or "power-sign");
        await fixture.AssertLegacyAsync();
    }

    [Fact]
    public async Task Readiness_OnlyActiveLwtModesRequireVerifiedControlSafetyInputs()
    {
        await using var fixture = new Fixture();
        await fixture.Db.SaveChangesAsync();

        var shadow = await fixture.EvaluateAsync(ControlMode.Shadow);
        var active = await fixture.EvaluateAsync(ControlMode.LwtActive);

        Assert.DoesNotContain(shadow, x => x.Key == "lwt-safety-inputs");
        var check = active.Single(x => x.Key == "lwt-safety-inputs");
        Assert.False(check.Passed);
        Assert.Contains("DHW-status", check.Requirement);
        await fixture.AssertLegacyAsync();
    }

    [Theory]
    [InlineData("isolated")]
    [InlineData("imported")]
    [InlineData("future")]
    [InlineData("dhw")]
    [InlineData("defrost")]
    public async Task Readiness_HeatOnTenDatesIsNotTenVerifiedSpaceHeatingDays(string fault)
    {
        await using var fixture = new Fixture();
        for (var day = 0; day < 10; day++)
        {
            var timestamp = fixture.Now.AddDays(fault == "future" ? day + 1 : -day - 1);
            var sample = Sample(timestamp);
            if (fault == "imported") sample.QualityJson = "{\"source\":\"HomeAssistantHistoryImport\"}";
            if (fault == "dhw") sample.DhwActive = true;
            if (fault == "defrost") sample.DefrostActive = true;
            fixture.Db.ThermalTelemetrySamples.Add(sample);
        }
        await fixture.Db.SaveChangesAsync();

        Assert.False((await fixture.EvaluateAsync()).Single(x => x.Key == "heating-days").Passed);
        await fixture.AssertLegacyAsync();
    }

    [Theory]
    [InlineData("one-distant-point")]
    [InlineData("gap")]
    [InlineData("nonfinite")]
    [InlineData("missing-temperature")]
    [InlineData("future-only")]
    public async Task Readiness_DistantForecastTimestampIsNotContinuousWeatherCoverage(string fault)
    {
        await using var fixture = new Fixture();
        var sample = Sample(fixture.Now.AddMinutes(-1));
        var points = Enumerable.Range(0, 27).Select(hour => new WeatherForecastPoint(fixture.Now.AddHours(hour - 1), 5, null, null)).ToArray();
        sample.OutsideTemperatureForecastJson = fault switch
        {
            "one-distant-point" => JsonSerializer.Serialize(points.TakeLast(1)),
            "gap" => JsonSerializer.Serialize(points.Where((_, index) => index < 3 || index > 6)),
            "nonfinite" => $"[{{\"timestampUtc\":\"{fixture.Now.AddHours(26):O}\",\"temperatureC\":1e999}}]",
            "missing-temperature" => $"[{{\"timestampUtc\":\"{fixture.Now.AddHours(26):O}\"}}]",
            "future-only" => JsonSerializer.Serialize(points.Skip(3)),
            _ => throw new InvalidOperationException()
        };
        fixture.Db.ThermalTelemetrySamples.Add(sample);
        await fixture.Db.SaveChangesAsync();

        Assert.False((await fixture.EvaluateAsync()).Single(x => x.Key == "weather-forecast").Passed);
        await fixture.AssertLegacyAsync();
    }

    [Fact]
    public async Task Readiness_ShadowEntryFollowedByLegacyDoesNotProveContinuousShadow()
    {
        await using var fixture = new Fixture();
        fixture.Db.ThermalEvents.AddRange(
            new ThermalEvent { UserId = "account-a", Category = "ControlMode", TimestampUtc = fixture.Now.AddDays(-30), Message = "Driftläget ändrades från Legacy till Shadow." },
            new ThermalEvent { UserId = "account-a", Category = "ControlMode", TimestampUtc = fixture.Now.AddDays(-10), Message = "Driftläget ändrades från Shadow till Legacy." });
        await fixture.Db.SaveChangesAsync();

        Assert.False((await fixture.EvaluateAsync()).Single(x => x.Key == "shadow-duration").Passed);
        await fixture.AssertLegacyAsync();
    }

    [Theory]
    [InlineData("2026-03-29", 276)]
    [InlineData("2026-08-20", 288)]
    [InlineData("2026-10-25", 300)]
    public void HeatingDays_CountsCompletedLocalDaysWithActualDstLength(string date, int expected)
    {
        var (samples, start, end) = Day(date);
        Assert.Equal(expected, samples.Length);

        var result = AssessDay(samples, start, end);

        Assert.Equal(new HeatingDayEvidence(1, 1, 1), result);
    }

    [Theory]
    [InlineData("imported")]
    [InlineData("missing-buckets")]
    [InlineData("duplicate-buckets")]
    [InlineData("dhw-only")]
    [InlineData("unknown-defrost")]
    [InlineData("single-heat-point")]
    [InlineData("quality-missing")]
    [InlineData("period-start-midday")]
    [InlineData("unfinished-day")]
    public void HeatingDays_IncompleteOrUnprovenCivilDaysDoNotCount(string fault)
    {
        var (samples, start, end) = Day("2026-08-20");
        if (fault == "missing-buckets") samples = samples.Skip(6).ToArray();
        if (fault == "duplicate-buckets") samples = samples.Concat(samples).ToArray();
        if (fault == "period-start-midday") start = start.AddHours(12);
        if (fault == "unfinished-day") end = end.AddMinutes(-1);
        foreach (var sample in samples)
        {
            if (fault == "imported") sample.QualityJson = sample.QualityJson.Replace("{\"rooms\"", "{\"source\":\"HomeAssistantHistoryImport\",\"rooms\"");
            if (fault == "dhw-only") sample.DhwActive = true;
            if (fault == "unknown-defrost") sample.DefrostActive = null;
            if (fault == "quality-missing") sample.QualityJson = "{}";
            if (fault == "single-heat-point" && sample != samples[0]) { sample.HeatOutputKw = 0; sample.LeavingWaterTemperatureC = sample.ReturnWaterTemperatureC; }
        }

        Assert.Equal(0, AssessDay(samples, start, end).HeatingDays);
    }

    [Theory]
    [InlineData("missing-feedback")]
    [InlineData("nonzero-feedback")]
    [InlineData("cold-critical-room")]
    public void HeatingDays_HeatDoesNotCertifyZeroCurveOrComfort(string fault)
    {
        var (samples, start, end) = Day("2026-08-20");
        if (fault == "missing-feedback") samples[0].QualityJson = samples[0].QualityJson.Replace("\"heatingDeviationC\":0", "\"heatingDeviationC\":null");
        if (fault == "nonzero-feedback") samples[0].QualityJson = samples[0].QualityJson.Replace("\"heatingDeviationC\":0", "\"heatingDeviationC\":0.5");
        if (fault == "cold-critical-room") samples[0].RoomTemperaturesJson = "{\"sensor.room\":20}";
        var result = AssessDay(samples, start, end);

        Assert.Equal(1, result.HeatingDays);
        Assert.Equal(0, result.ZeroDeviationDays);
        Assert.Equal(fault == "cold-critical-room" ? 0 : 1, result.ComfortDays);
    }

    [Fact]
    public void ModePeriods_PreservesContinuousShadowToActiveHistoryWithoutCountingRollback()
    {
        var now = DateTimeOffset.UtcNow;
        var events = new[]
        {
            new ThermalEvent { Category = "ControlMode", TimestampUtc = now.AddDays(-30), Message = "Driftläget ändrades från Legacy till Shadow." },
            new ThermalEvent { Category = "ControlMode", TimestampUtc = now.AddDays(-8), Message = "Driftläget ändrades från Shadow till LwtActive." },
            new ThermalEvent { Category = "ControlMode", TimestampUtc = now.AddDays(-1), Message = "Driftläget ändrades från LwtActive till FullActive." }
        };
        Assert.Equal(new ModePeriodEvidence(now.AddDays(-30), now.AddDays(-8)), ThermalReadinessEvidence.ModePeriods(events, "FullActive", now));
        Assert.Equal(new ModePeriodEvidence(null, null), ThermalReadinessEvidence.ModePeriods(events, "Legacy", now));
    }

    [Fact]
    public void Forecast_OnlyContinuousHourlyCoverageFromNowCounts()
    {
        var now = DateTimeOffset.UtcNow;
        var points = Enumerable.Range(0, 25).Select(hour => new WeatherForecastPoint(now.AddHours(hour), 5, null, null)).ToArray();
        Assert.Equal(24, ThermalReadinessEvidence.ForecastHours(JsonSerializer.Serialize(points), now));
        Assert.Equal(11, ThermalReadinessEvidence.ForecastHours(JsonSerializer.Serialize(points.Where((_, index) => index != 12)), now));
        Assert.Equal(0, ThermalReadinessEvidence.ForecastHours("[null]", now));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Readiness_DuplicateOrFuturePlansCannotFillTwentyOneDays(bool future)
    {
        await using var fixture = new Fixture();
        fixture.Db.ThermalSiteConfigs.Local.Single().ControlMode = "Shadow";
        fixture.Db.ThermalEvents.Add(new ThermalEvent { UserId = "account-a", Category = "ControlMode", TimestampUtc = fixture.Now.AddDays(-30), Message = "Driftläget ändrades från Legacy till Shadow." });
        var at = future ? fixture.Now.AddDays(1) : fixture.Now.AddDays(-1);
        fixture.Db.ThermalPlans.AddRange(Enumerable.Range(0, 2016).Select(_ => new ThermalPlan
        {
            UserId = "account-a",
            CreatedAtUtc = at,
            ValidFromUtc = at,
            ValidUntilUtc = at.AddHours(1),
            Status = "Valid",
            SolverDurationMs = 1000,
            Confidence = .9
        }));
        await fixture.Db.SaveChangesAsync();

        Assert.False((await fixture.EvaluateAsync()).Single(x => x.Key == "shadow-plans").Passed);
        Assert.Empty(await fixture.Db.ThermalControlCommands.ToListAsync());
        Assert.Equal("Shadow", fixture.Db.ThermalSiteConfigs.Local.Single().ControlMode);
    }

    private static (ThermalTelemetrySample[] Samples, DateTimeOffset Start, DateTimeOffset End) Day(string date)
    {
        var local = DateTime.SpecifyKind(DateTime.ParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Unspecified);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");
        var start = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
        var end = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local.AddDays(1), zone));
        return (Enumerable.Range(0, (int)(end - start).TotalMinutes / 5).Select(index => Sample(start.AddMinutes(index * 5))).ToArray(), start, end);
    }

    private static HeatingDayEvidence AssessDay(ThermalTelemetrySample[] samples, DateTimeOffset start, DateTimeOffset end) =>
        ThermalReadinessEvidence.HeatingDays(samples, [new ThermalRoomConfig { EntityId = "sensor.room", IsCritical = true }], [], new ThermalSiteConfig(), start, end);

    private static ThermalModelVersion Model(string type, string metrics)
    {
        var model = ThermalModelEvidenceTests.ValidModel(type, DateTimeOffset.UtcNow);
        model.MetricsJson = metrics;
        return model;
    }

    internal static ThermalTelemetrySample Sample(DateTimeOffset timestamp) => new()
    {
        UserId = "account-a",
        TimestampUtc = timestamp,
        OutsideTemperatureC = 5,
        LeavingWaterTemperatureC = 35,
        ReturnWaterTemperatureC = 30,
        FlowLitresPerMinute = 12,
        HeatOutputKw = 4.186,
        DhwActive = false,
        DefrostActive = false,
        BackupHeaterActive = false,
        RoomTemperaturesJson = "{\"sensor.room\":21.5}",
        QualityJson = """
            {"rooms":{"sensor.room":{"quality":0,"excluded":false}},
             "entities":{"outside_temperature":{"quality":0,"excluded":false},
             "leaving_water_temperature":{"quality":0,"excluded":false},"return_water_temperature":{"quality":0,"excluded":false},
             "flow":{"quality":0,"excluded":false},"dhw_active":{"quality":0,"excluded":false},
             "defrost_active":{"quality":0,"excluded":false},"heating_deviation":{"quality":0,"excluded":false}},
             "forecast":{"quality":0},"heatingDeviationC":0}
            """
    };

    private sealed class Fixture : IAsyncDisposable
    {
        internal PrisstyrningDbContext Db { get; } = new(new DbContextOptionsBuilder<PrisstyrningDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        internal DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;
        private readonly HomeAssistantStateCache _cache = new();

        internal Fixture()
        {
            Db.ThermalSiteConfigs.Add(new ThermalSiteConfig { UserId = "account-a", UpdatedAtUtc = Now.AddDays(-60) });
            Db.ThermalRoomConfigs.Add(new ThermalRoomConfig { UserId = "account-a", EntityId = "sensor.room", IsCritical = true });
        }

        internal Task<IReadOnlyList<ReadinessCheck>> EvaluateAsync(ControlMode mode = ControlMode.LwtActive)
        {
            var connections = new HomeAssistantConnectionService(Db, TestSecretProtector.Instance, new UnusedValidator(), _cache, new HomeAssistantConnectionChanges());
            return new ThermalReadinessService(Db, _cache, connections).EvaluateAsync("account-a", mode);
        }

        internal async Task AssertLegacyAsync()
        {
            var site = await Db.ThermalSiteConfigs.SingleAsync();
            Assert.Equal("Legacy", site.ControlMode);
            Assert.Equal("Legacy", site.DhwWriter);
            Assert.Empty(await Db.ThermalControlCommands.ToListAsync());
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class UnusedValidator : IHomeAssistantEndpointValidator
    {
        public Task<Uri> ValidateAsync(string value, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Readiness must not contact HA.");
    }
}
