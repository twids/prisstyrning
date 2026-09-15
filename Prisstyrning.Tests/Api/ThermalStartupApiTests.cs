using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Tests.Fixtures;
using Prisstyrning.Thermal.Control;

namespace Prisstyrning.Tests.Api;

public sealed class ThermalStartupApiTests
{
    [Theory]
    [InlineData("/api/thermal/startup")]
    [InlineData("/api/thermal/startup/preview")]
    public async Task ReadOnlyStartup_RequiresExistingAccountSession(string url)
    {
        await using var host = await AccountApiTestHost.CreateAsync(includeThermalStartup: true);
        using var browser = host.CreateBrowser();
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.Client.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task StartupReceipt_IsIsolatedBySignedInAccount_AndReadOnly()
    {
        await using var host = await AccountApiTestHost.CreateAsync(includeThermalStartup: true);
        using var first = host.CreateBrowser();
        using var second = host.CreateBrowser();
        await first.SignInAsync("account-a");
        await second.SignInAsync("account-b");
        await host.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<PrisstyrningDbContext>();
            db.ThermalStartupStates.Add(new ThermalStartupState { UserId = "account-a", Phase = "Failed", ConfigurationFingerprint = "private-fingerprint" });
            await db.SaveChangesAsync();
        });
        var a = await first.Client.GetFromJsonAsync<ThermalStartupStatus>("/api/thermal/startup");
        var b = await second.Client.GetFromJsonAsync<ThermalStartupStatus>("/api/thermal/startup");
        Assert.Equal("Failed", a!.Phase);
        Assert.Equal("NotCommissioned", b!.Phase);
        Assert.False(a.ReadyToCommission);
        Assert.DoesNotContain("private-fingerprint", await first.Client.GetStringAsync("/api/thermal/startup"));
        var preview = await first.Client.GetFromJsonAsync<ConservativePreview>("/api/thermal/startup/preview");
        Assert.True(preview!.SimulationOnly);
        Assert.Null(preview.SuggestedDeviationC);
        await host.WithServicesAsync(async services => Assert.Empty(await services.GetRequiredService<PrisstyrningDbContext>().ThermalControlCommands.ToListAsync()));
    }
}
