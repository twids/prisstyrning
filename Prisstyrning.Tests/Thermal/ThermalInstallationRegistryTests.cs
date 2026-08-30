using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Data;

namespace Prisstyrning.Tests.Thermal;

public sealed class ThermalInstallationRegistryTests
{
    [Fact]
    public async Task ResolveUser_DoesNotInferLegacyOwnerForAnotherAccount()
    {
        await using var db = Database();
        db.UserSettings.Add(new UserSettings { UserId = "legacy-owner", AutoApplySchedule = true });
        await db.SaveChangesAsync();

        var registry = new ThermalInstallationRegistry(db);
        var resolved = await registry.ResolveUserAsync("new-browser-session", CancellationToken.None);

        Assert.Equal("new-browser-session", resolved);
    }

    [Fact]
    public async Task ResolveUser_DoesNotCrossAccountBoundary()
    {
        await using var db = Database();
        db.ThermalSiteConfigs.Add(new ThermalSiteConfig
        {
            UserId = "installation-owner",
            ControlMode = "Shadow"
        });
        await db.SaveChangesAsync();

        var registry = new ThermalInstallationRegistry(db);
        var resolved = await registry.ResolveUserAsync("another-browser", CancellationToken.None);
        var plannedUsers = await registry.GetUsersAsync(includeLegacy: false, activeLwtOnly: false, cancellationToken: CancellationToken.None);
        var lwtUsers = await registry.GetUsersAsync(includeLegacy: false, activeLwtOnly: true, cancellationToken: CancellationToken.None);

        Assert.Equal("another-browser", resolved);
        Assert.Equal(["installation-owner"], plannedUsers);
        Assert.Empty(lwtUsers);
    }

    private static PrisstyrningDbContext Database() => new(
        new DbContextOptionsBuilder<PrisstyrningDbContext>()
            .UseInMemoryDatabase($"thermal-installation-{Guid.NewGuid():N}")
            .Options);
}
