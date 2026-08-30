using Microsoft.EntityFrameworkCore;
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

    private static HomeAssistantConnectionService Service(PrisstyrningDbContext db) =>
        new(db, TestSecretProtector.Instance, new AcceptingEndpointValidator());

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
