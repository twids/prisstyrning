using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Tests.Fixtures;
using Prisstyrning.Thermal.Control;
using Prisstyrning.Thermal.Domain;
using Prisstyrning.Thermal.HomeAssistant;

namespace Prisstyrning.Tests.Thermal;

public sealed class ThermalStartupServiceTests
{
    private static ThermalModeRequest Approved => new(ControlMode.LwtActive, true, true, true, true);

    [Fact]
    public async Task Preview_IsReadOnlyAndDoesNotGrantCommissioningOrStoreIntegral()
    {
        await using var f = await Fixture.Create();
        (await f.Db.ThermalTelemetrySamples.SingleAsync()).RoomTemperaturesJson = "{\"sensor.room\":20.5}";
        await f.Db.SaveChangesAsync();
        var preview = await f.Service.PreviewAsync("account-a", default);
        Assert.True(preview.SimulationOnly);
        Assert.Equal(.5, preview.SuggestedDeviationC);
        Assert.Empty(f.Control.Sent);
        Assert.Empty(await f.Db.ThermalStartupStates.ToListAsync());
        Assert.Empty(await f.Db.ThermalControlCommands.ToListAsync());
        Assert.Equal("Shadow", (await f.Db.ThermalSiteConfigs.SingleAsync()).ControlMode);
    }

    [Fact]
    public async Task Startup_DoesNotRequireModelsOrTwentyOneDays_ButStillRequiresSafety()
    {
        await using var f = await Fixture.Create();
        var status = await f.Service.GetAsync("account-a", default);
        Assert.True(status.ReadyToCommission, string.Join("; ", status.SafetyChecks.Where(x => !x.Passed).Select(x => x.Key + ": " + x.Action)));
        Assert.DoesNotContain(status.SafetyChecks, x => x.Key is "model" or "shadow-duration");
        Assert.Empty(await f.Db.ThermalModelVersions.ToListAsync());
        Assert.Empty(f.Control.Sent);
        Assert.Equal("Shadow", (await f.Db.ThermalSiteConfigs.SingleAsync()).ControlMode);
    }

    [Fact]
    public async Task DeploymentFlag_DefaultIsClosed()
    {
        await using var f = await Fixture.Create();
        f.Config["Thermal:AllowConservativeStart"] = null;
        Assert.False((await f.Service.GetAsync("account-a", default)).ReadyToCommission);
        Assert.False((await f.Service.StartAsync("account-a", Approved, default)).Success);
        Assert.Empty(f.Control.Sent);
    }

    [Fact]
    public async Task ManualCommissioningConsent_IsMandatory()
    {
        await using var f = await Fixture.Create();
        Assert.False((await f.Service.StartAsync("account-a", Approved with { IndependentFallbackConfirmed = false }, default)).Success);
        Assert.Empty(f.Control.Sent);
        Assert.Empty(await f.Db.ThermalStartupStates.ToListAsync());
    }

    [Fact]
    public async Task CompletePulseAndZero_CreatesAccountBoundEvidenceAndKeepsLegacyDhw()
    {
        await using var f = await Fixture.Create();
        var result = await f.Service.StartAsync("account-a", Approved, default);
        Assert.True(result.Success, result.Message);
        Assert.Equal(new[] { 0d, .5, 0 }, f.Control.Sent);
        var site = await f.Db.ThermalSiteConfigs.SingleAsync();
        var proof = await f.Db.ThermalStartupStates.SingleAsync();
        Assert.Equal("LwtActive", site.ControlMode);
        Assert.Equal("Legacy", site.DhwWriter);
        Assert.Equal("Verified", proof.Phase);
        Assert.True(await f.Service.EvidenceMatchesAsync("account-a", site, proof, default));
        Assert.All(await f.Db.ThermalControlCommands.ToListAsync(), x => Assert.Equal("Accepted", x.Outcome));
        Assert.Equal(0, (await f.Db.ThermalControlStates.SingleAsync()).CurrentDeviationC);
    }

    [Fact]
    public async Task PulseFailure_StillAttemptsZeroAndReturnsToShadow()
    {
        await using var f = await Fixture.Create();
        f.Control.FailAt = 2;
        Assert.False((await f.Service.StartAsync("account-a", Approved, default)).Success);
        Assert.Equal(new[] { 0d, .5, 0 }, f.Control.Sent);
        Assert.Equal("Shadow", (await f.Db.ThermalSiteConfigs.SingleAsync()).ControlMode);
        Assert.Equal("Failed", (await f.Db.ThermalStartupStates.SingleAsync()).Phase);
    }

    [Fact]
    public async Task ZeroFailure_DoesNotClaimShadowOrSuccessfulCommissioning()
    {
        await using var f = await Fixture.Create();
        f.Control.FailAt = 3;
        Assert.False((await f.Service.StartAsync("account-a", Approved, default)).Success);
        Assert.Equal("LwtActive", (await f.Db.ThermalSiteConfigs.SingleAsync()).ControlMode);
        var proof = await f.Db.ThermalStartupStates.SingleAsync();
        Assert.Equal("RecoveryRequired", proof.Phase);
        Assert.Null(proof.CommissionedAtUtc);
        Assert.Contains(await f.Db.ThermalEvents.ToListAsync(), x => x.Severity == "ActionRequired");
    }

    [Fact]
    public async Task CancelledPulse_CannotCancelCleanup()
    {
        await using var f = await Fixture.Create();
        f.Control.CancelAt = 2;
        Assert.False((await f.Service.StartAsync("account-a", Approved, default)).Success);
        Assert.Equal(new[] { 0d, .5, 0 }, f.Control.Sent);
        Assert.Equal("Shadow", (await f.Db.ThermalSiteConfigs.SingleAsync()).ControlMode);
    }

    [Theory]
    [InlineData("room")]
    [InlineData("connection")]
    [InlineData("target")]
    [InlineData("mapping")]
    public async Task ConfigurationChanges_InvalidateCommissioningEvidence(string change)
    {
        await using var f = await Fixture.Create();
        await f.Activate();
        var site = await f.Db.ThermalSiteConfigs.SingleAsync();
        switch (change)
        {
            case "room": (await f.Db.ThermalRoomConfigs.SingleAsync()).TargetOffsetC = .2; break;
            case "connection": (await f.Db.HomeAssistantConnections.SingleAsync()).UpdatedAtUtc = DateTimeOffset.UtcNow; break;
            case "target": site.BaseRoomTargetC += .1; break;
            case "mapping": (await f.Db.ThermalEntityConfigs.FirstAsync()).EntityId = "sensor.changed"; break;
        }
        await f.Db.SaveChangesAsync();
        Assert.False(await f.Service.EvidenceMatchesAsync("account-a", site, await f.Db.ThermalStartupStates.SingleAsync(), default));
        await f.Tick();
        Assert.Equal(new[] { 0d }, f.Control.Sent);
        Assert.Equal("Shadow", (await f.Db.ThermalSiteConfigs.SingleAsync()).ControlMode);
    }

    [Theory]
    [InlineData("Commissioning")]
    [InlineData("RecoveryRequired")]
    public async Task RestartDuringCommissioning_OnlyZerosAndReturnsToShadow(string phase)
    {
        await using var f = await Fixture.Create();
        await f.Activate();
        (await f.Db.ThermalStartupStates.SingleAsync()).Phase = phase;
        await f.Db.SaveChangesAsync();
        await f.Tick();
        Assert.Equal(new[] { 0d }, f.Control.Sent);
        Assert.Equal("Shadow", (await f.Db.ThermalSiteConfigs.SingleAsync()).ControlMode);
    }

    [Fact]
    public async Task ColdRoom_NoModelNoPlan_CanUseConservativeCorrection()
    {
        await using var f = await Fixture.Create();
        await f.Activate();
        (await f.Db.ThermalTelemetrySamples.SingleAsync()).RoomTemperaturesJson = "{\"sensor.room\":20.5}";
        await f.Db.SaveChangesAsync();
        await f.Tick();
        Assert.Equal(new[] { .5 }, f.Control.Sent);
        Assert.Equal("", (await f.Db.ThermalControlStates.SingleAsync()).FallbackReason);
        Assert.NotNull((await f.Db.ThermalStartupStates.SingleAsync()).LastEvaluationUtc);
        Assert.Empty(await f.Db.ThermalModelVersions.ToListAsync());
    }

    [Fact]
    public async Task ShadowPlan_CannotContributeToActiveCorrection()
    {
        await using var f = await Fixture.Create();
        await f.Activate();
        f.Db.ThermalPlans.Add(new ThermalPlan { UserId = "account-a", IsShadow = true, Status = "Valid",
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1), ValidFromUtc = DateTimeOffset.UtcNow.AddMinutes(-5), ValidUntilUtc = DateTimeOffset.UtcNow.AddHours(48) });
        await f.Db.SaveChangesAsync();
        await f.Tick();
        Assert.Empty(f.Control.Sent);
        Assert.Equal("", (await f.Db.ThermalControlStates.SingleAsync()).FallbackReason);
    }

    [Fact]
    public async Task MissingLease_NoCommandAndNoStateMutation()
    {
        await using var f = await Fixture.Create();
        await f.Activate();
        await f.Service.EvaluateWithLeaseAsync("account-a", "not-the-owner", default);
        Assert.Empty(f.Control.Sent);
        Assert.Null((await f.Db.ThermalStartupStates.SingleAsync()).LastEvaluationUtc);
    }

    [Fact]
    public async Task ExternalDeviationChange_DoesNotBecomeANewControlBaseline()
    {
        await using var f = await Fixture.Create();
        await f.Activate();
        f.Report(.5);
        await f.Tick();
        Assert.Equal(new[] { 0d }, f.Control.Sent);
        Assert.Equal("RecoveryRequired", (await f.Db.ThermalStartupStates.SingleAsync()).Phase);
    }

    [Theory]
    [InlineData("dhw")]
    [InlineData("stale")]
    [InlineData("disconnected")]
    [InlineData("lease")]
    [InlineData("kill-switch")]
    public async Task SafetyIsRecheckedImmediatelyBeforeNonzeroWrite(string fault)
    {
        await using var f = await Fixture.Create();
        await f.Activate();
        var state = await f.Db.ThermalControlStates.SingleAsync();
        if (fault == "dhw") (await f.Db.ThermalTelemetrySamples.SingleAsync()).DhwActive = true;
        if (fault == "stale") (await f.Db.ThermalTelemetrySamples.SingleAsync()).TimestampUtc = DateTimeOffset.UtcNow.AddMinutes(-11);
        if (fault == "disconnected") f.Cache.MarkDisconnected("account-a");
        if (fault == "lease") state.LeaseExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        if (fault == "kill-switch") f.Config["Thermal:AllowConservativeStart"] = "false";
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.SendAsync("account-a", .5, state, "Test", default));
        Assert.Empty(f.Control.Sent);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal PrisstyrningDbContext Db { get; } = new(new DbContextOptionsBuilder<PrisstyrningDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        internal HomeAssistantStateCache Cache { get; } = new();
        internal FakeControl Control { get; } = new();
        internal IConfigurationRoot Config { get; } = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Thermal:AllowLwtActive"] = "true", ["Thermal:AllowConservativeStart"] = "true" }).Build();
        internal ThermalStartupService Service { get; private set; } = null!;
        internal static async Task<Fixture> Create()
        {
            var f = new Fixture();
            var now = DateTimeOffset.UtcNow;
            var revision = now.AddHours(-1);
            f.Db.HomeAssistantConnections.Add(new HomeAssistantConnection { UserId = "account-a", BaseUrl = "https://ha.example.test",
                TelemetryEnabled = true, ControlEnabled = true, TelemetryTokenCiphertext = "synthetic", ControlTokenCiphertext = "synthetic",
                HeatingDeviationEntityId = "number.deviation", UpdatedAtUtc = revision });
            f.Db.ThermalSiteConfigs.Add(new ThermalSiteConfig { UserId = "account-a", ControlMode = "Shadow", UpdatedAtUtc = now.AddHours(-2) });
            f.Db.ThermalRoomConfigs.Add(new ThermalRoomConfig { UserId = "account-a", EntityId = "sensor.room", IsCritical = true });
            foreach (var role in new[] { ThermalEntityRoles.OutsideTemperature, ThermalEntityRoles.LeavingWaterTemperature,
                         ThermalEntityRoles.ReturnWaterTemperature, ThermalEntityRoles.Flow, ThermalEntityRoles.DhwActive,
                         ThermalEntityRoles.DefrostActive, ThermalEntityRoles.HeatingDeviation })
                f.Db.ThermalEntityConfigs.Add(new ThermalEntityConfig { UserId = "account-a", Role = role,
                    EntityId = role == ThermalEntityRoles.HeatingDeviation ? "sensor.deviation" : "sensor." + role,
                    ExpectedUnit = role == ThermalEntityRoles.Flow ? "l/min" : role.Contains("active") ? "" : "°C" });
            f.Db.ThermalTelemetrySamples.Add(ThermalReadinessEvidenceTests.Sample(now.AddMinutes(-1)));
            await f.Db.SaveChangesAsync();
            f.Cache.Invalidate("account-a", revision, true);
            var session = f.Cache.BeginSession("account-a", revision)!;
            Assert.True(f.Cache.BeginSnapshot(session));
            Assert.True(f.Cache.PublishSnapshot(session, [State("number.deviation", 0), State("sensor.deviation", 0)]));
            f.Control.Report = f.Report;
            var connections = new HomeAssistantConnectionService(f.Db, TestSecretProtector.Instance, new NoNetwork(), f.Cache, new HomeAssistantConnectionChanges());
            var readiness = new ThermalReadinessService(f.Db, f.Cache, connections, ThermalCurrentModelTestData.Build);
            f.Service = new(f.Db, readiness, f.Cache, f.Config, f.Control, ThermalCurrentModelTestData.Build);
            return f;
        }
        internal async Task Activate()
        {
            var result = await Service.StartAsync("account-a", Approved, default);
            Assert.True(result.Success, result.Message + " " + string.Join(",", (await Service.GetAsync("account-a", default)).SafetyChecks.Where(x => !x.Passed).Select(x => x.Key)));
            Control.Sent.Clear();
            var state = await Db.ThermalControlStates.SingleAsync();
            state.LeaseOwner = "test-owner";
            state.LeaseExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(15);
            state.LastDeviationWriteUtc = DateTimeOffset.UtcNow.AddMinutes(-31);
            (await Db.ThermalTelemetrySamples.SingleAsync()).TimestampUtc = DateTimeOffset.UtcNow;
            await Db.SaveChangesAsync();
        }
        internal Task Tick() => Service.EvaluateWithLeaseAsync("account-a", "test-owner", default);
        internal void Report(double value) => Cache.Upsert("account-a", State("sensor.deviation", value));
        private static HomeAssistantState State(string id, double value) => new(id, value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            new JsonObject { ["unit_of_measurement"] = "°C", ["min"] = -3, ["max"] = 3, ["step"] = .5 }, DateTimeOffset.UtcNow.AddHours(-3), DateTimeOffset.UtcNow.AddHours(-3), DateTimeOffset.UtcNow);
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
    private sealed class FakeControl : IHomeAssistantControlClient
    {
        internal List<double> Sent { get; } = [];
        internal int FailAt { get; set; }
        internal int CancelAt { get; set; }
        internal Action<double>? Report { get; set; }
        public Task SetHeatingDeviationAsync(string userId, double value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Sent.Add(value);
            if (Sent.Count == FailAt) throw new InvalidOperationException("Synthetic feedback failure");
            if (Sent.Count == CancelAt) throw new OperationCanceledException();
            Report?.Invoke(value);
            return Task.CompletedTask;
        }
    }
    private sealed class NoNetwork : IHomeAssistantEndpointValidator
    { public Task<Uri> ValidateAsync(string value, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No network allowed"); }
}
