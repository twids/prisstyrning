using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Data;
using Prisstyrning.Thermal.Domain;

namespace Prisstyrning.Tests.Thermal;

public sealed class ThermalCopConfigurationTests
{
    [Fact]
    public async Task NoDefrost_RoundTripsOnlyExplicitDeclaration_AndPreservesLegacy()
    {
        await using var db = Database();
        var service = new ThermalDataService(db, new ThermalInstallationRegistry(db));
        var config = new ThermalConfigDto(new ThermalSiteConfig(), [], [new ThermalEntityConfig
        { Role = ThermalEntityRoles.DefrostActive, ExpectedUnit = "bool", NotApplicable = true }]);
        var saved = await service.UpdateConfigAsync("account-a", config);
        Assert.True(Assert.Single(saved.Entities).NotApplicable);
        Assert.Equal("", saved.Entities[0].EntityId);
        Assert.Equal("Legacy", saved.Site.ControlMode);
        Assert.Equal("Legacy", saved.Site.DhwWriter);
        config.Entities[0].Role = ThermalEntityRoles.DhwActive;
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateConfigAsync("account-a", config));
        Assert.True((await service.GetConfigAsync("account-a")).Entities[0].NotApplicable);
    }
    private static PrisstyrningDbContext Database() => new(new DbContextOptionsBuilder<PrisstyrningDbContext>()
        .UseInMemoryDatabase($"cop-config-{Guid.NewGuid():N}").Options);
    private static ThermalConfigDto Config(string role = ThermalEntityRoles.CopAverage, string? period = "Lifetime", string? unit = "COP") =>
        new(new ThermalSiteConfig { UserId = "account-b", ControlMode = "FullActive", DhwWriter = "Joint" }, [],
            [new ThermalEntityConfig { UserId = "account-b", Role = role, EntityId = "sensor.cop", ExpectedUnit = unit!, AveragingPeriod = period }]);

    [Fact]
    public async Task SaveCopPeriod_RoundTripsOnlySessionAccount_AndCannotChangeWriters()
    {
        await using var db = Database();
        var service = new ThermalDataService(db, new ThermalInstallationRegistry(db));
        await service.UpdateConfigAsync("account-b", Config(period: "Month"));
        await service.UpdateConfigAsync("account-a", Config());

        var saved = await service.GetConfigAsync("account-a");
        Assert.Equal("Lifetime", Assert.Single(saved.Entities).AveragingPeriod);
        Assert.Equal("account-a", saved.Entities[0].UserId);
        Assert.Equal("Legacy", saved.Site.ControlMode);
        Assert.Equal("Legacy", saved.Site.DhwWriter);
        Assert.Equal("Month", Assert.Single((await service.GetConfigAsync("account-b")).Entities).AveragingPeriod);

        await service.UpdateConfigAsync("account-a", Config(period: null));
        Assert.Null(Assert.Single((await service.GetConfigAsync("account-a")).Entities).AveragingPeriod);
        Assert.Empty(await db.ThermalControlCommands.ToListAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("kW")]
    [InlineData("%")]
    public async Task SaveCopMissingOrWrongUnit_IsValidationError_AndPersistsNothing(string? unit)
    {
        await using var db = Database();
        var service = new ThermalDataService(db, new ThermalInstallationRegistry(db));
        var error = await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateConfigAsync("account-a", Config(unit: unit)));
        Assert.Contains("COP", error.Message);
        Assert.Empty(await db.ThermalEntityConfigs.ToListAsync());
        Assert.Empty(await db.ThermalSiteConfigs.ToListAsync());
    }

    [Theory]
    [InlineData(ThermalEntityRoles.CopRealtime, "Lifetime")]
    [InlineData(ThermalEntityRoles.CopAverage, "UnrecognizedPeriod")]
    [InlineData(ThermalEntityRoles.CopAverage, "")]
    public async Task SaveCopWrongPeriod_IsValidationError_BeforeDatabaseChanges(string role, string period)
    {
        await using var db = Database();
        var service = new ThermalDataService(db, new ThermalInstallationRegistry(db));
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateConfigAsync("account-a", Config(role, period)));
        Assert.Empty(await db.ThermalEntityConfigs.ToListAsync());
    }
}
