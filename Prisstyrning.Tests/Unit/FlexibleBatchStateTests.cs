using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Data.Repositories;
using Prisstyrning.Tests.Fixtures;
using Xunit;

namespace Prisstyrning.Tests.Unit;

public class FlexibleBatchStateTests : IDisposable
{
    private ServiceProvider? _serviceProvider;

    public void Dispose()
    {
        _serviceProvider?.Dispose();
    }

    private IServiceScopeFactory BuildScopeFactory(
        IConfiguration cfg,
        MockHttpMessageHandler mockHandler,
        Action<PrisstyrningDbContext>? seed = null)
    {
        var services = new ServiceCollection();
        var dbName = Guid.NewGuid().ToString();
        services.AddDbContext<PrisstyrningDbContext>(o =>
            o.UseInMemoryDatabase(dbName));
        services.AddSingleton(cfg);
        services.AddTestCredentialProtection();

        var httpFactory = MockServiceFactory.CreateMockHttpClientFactory(mockHandler);
        services.AddSingleton<IHttpClientFactory>(httpFactory);

        services.AddScoped<UserSettingsRepository>();
        services.AddScoped<ScheduleHistoryRepository>();
        services.AddScoped<DaikinTokenRepository>();
        services.AddScoped<FlexibleScheduleStateRepository>();
        services.AddScoped<PriceRepository>();
        services.AddScoped<DaikinOAuthService>();
        services.AddScoped(sp => new BatchRunner(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<DaikinOAuthService>()));

        _serviceProvider = services.BuildServiceProvider();

        // Ensure DB schema is created and seed data
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
        db.Database.EnsureCreated();
        seed?.Invoke(db);

        return _serviceProvider.GetRequiredService<IServiceScopeFactory>();
    }

    private static IConfiguration CreateConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Daikin:AccessToken"] = "test-token",
                ["Daikin:ApplySchedule"] = "true",
                ["Daikin:ScheduleMode"] = "heating",
                ["Price:Nordpool:DefaultZone"] = "SE3",
                ["Price:Nordpool:Currency"] = "SEK",
                ["Storage:Directory"] = Path.GetTempPath(),
            })
            .Build();

    private static MockHttpMessageHandler CreateMockHandlerWithFailingDaikin()
    {
        var handler = new MockHttpMessageHandler();

        // Nordpool prices — valid data so schedule generation succeeds
        var prices = new List<object>();
        var baseDate = DateTime.UtcNow.Date;
        for (int h = 0; h < 24; h++)
        {
            prices.Add(new
            {
                time_start = baseDate.AddHours(h).ToString("o"),
                SEK_per_kWh = 0.5m + (h * 0.02m),
                EUR_per_kWh = 0.045m + (h * 0.002m)
            });
        }
        var priceJson = JsonSerializer.Serialize(prices);
        handler.AddRoute("elprisetjustnu.se", HttpStatusCode.OK, priceJson);

        // Daikin — all endpoints return 401 so apply fails
        handler.AddRoute("/v1/sites", HttpStatusCode.Unauthorized, "{}");
        handler.AddRoute("gateway-devices", HttpStatusCode.Unauthorized, "{}");

        return handler;
    }

    [Fact]
    public async Task FlexibleBatch_StateAdvances_EvenWhenApplyFails()
    {
        var userId = "stall-test-user";
        var now = DateTimeOffset.UtcNow;
        var twoDaysAgo = now.AddDays(-2);
        var cfg = CreateConfig();
        var handler = CreateMockHandlerWithFailingDaikin();
        var scopeFactory = BuildScopeFactory(cfg, handler, db =>
        {
            db.UserSettings.Add(new UserSettings
            {
                UserId = userId,
                SchedulingMode = "Flexible",
                AutoApplySchedule = true,
                EcoIntervalHours = 20,
                EcoFlexibilityHours = 8,
                ComfortHours = 3,
            });
            db.FlexibleScheduleStates.Add(new FlexibleScheduleState
            {
                UserId = userId,
                LastEcoRunUtc = twoDaysAgo,
            });
            db.SaveChanges();
        });

        // Act: run batch with apply=true, persist=true — apply will fail because Daikin returns 401
        using var scope = scopeFactory.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<BatchRunner>();
        var (generated, _, message) = await runner.RunBatchAsync(cfg, userId,
            applySchedule: true, persist: true, scopeFactory);

        // Assert: schedule was generated
        Assert.True(generated, "Schedule should have been generated");
        // Assert: apply should have failed
        Assert.Contains("Apply failed", message);

        // Assert: state should have advanced despite apply failure
        using var scope2 = scopeFactory.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
        var state = await db2.FlexibleScheduleStates.FindAsync(userId);
        Assert.NotNull(state);
        // NextScheduledEcoUtc should be set to the newly scheduled eco hour
        // (LastEcoRunUtc is only advanced once the scheduled hour actually passes)
        Assert.NotNull(state!.NextScheduledEcoUtc);
        Assert.True(state.NextScheduledEcoUtc >= now,
            $"NextScheduledEcoUtc should be in the future, but was {state.NextScheduledEcoUtc}");
        // LastEcoRunUtc should still be the old 2-days-ago value (not advanced until eco runs)
        Assert.Equal(twoDaysAgo, state.LastEcoRunUtc);
    }

    [Fact]
    public async Task FlexibleBatch_StateDoesNotAdvance_WhenPersistIsFalse()
    {
        var userId = "no-persist-user";
        var twoDaysAgo = DateTimeOffset.UtcNow.AddDays(-2);
        var cfg = CreateConfig();
        var handler = CreateMockHandlerWithFailingDaikin();
        var scopeFactory = BuildScopeFactory(cfg, handler, db =>
        {
            db.UserSettings.Add(new UserSettings
            {
                UserId = userId,
                SchedulingMode = "Flexible",
                AutoApplySchedule = true,
                EcoIntervalHours = 20,
                EcoFlexibilityHours = 8,
                ComfortHours = 3,
            });
            db.FlexibleScheduleStates.Add(new FlexibleScheduleState
            {
                UserId = userId,
                LastEcoRunUtc = twoDaysAgo,
            });
            db.SaveChanges();
        });

        // Act: run batch with persist=false — state should NOT advance
        using var scope = scopeFactory.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<BatchRunner>();
        var (generated, _, _) = await runner.RunBatchAsync(cfg, userId,
            applySchedule: false, persist: false, scopeFactory);

        Assert.True(generated, "Schedule should have been generated");

        // Assert: state should NOT have advanced
        using var scope2 = scopeFactory.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
        var state = await db2.FlexibleScheduleStates.FindAsync(userId);
        Assert.NotNull(state);
        Assert.Equal(twoDaysAgo, state!.LastEcoRunUtc);
        Assert.Null(state.NextScheduledEcoUtc);
    }

    /// <summary>
    /// Creates a mock HTTP handler that returns Nordpool prices for today only.
    /// Prices are ascending throughout the day (cheapest at midnight, expensive at night).
    /// </summary>
    private static MockHttpMessageHandler CreateMockHandlerWithPrices(decimal[]? customPrices = null)
    {
        var handler = new MockHttpMessageHandler();
        var prices = new List<object>();
        var baseDate = DateTime.UtcNow.Date;
        for (int h = 0; h < 24; h++)
        {
            var price = customPrices != null && h < customPrices.Length
                ? customPrices[h]
                : 0.5m + (h * 0.02m);
            prices.Add(new
            {
                time_start = baseDate.AddHours(h).ToString("o"),
                SEK_per_kWh = price,
                EUR_per_kWh = price * 0.09m
            });
        }
        var priceJson = JsonSerializer.Serialize(prices);
        handler.AddRoute("elprisetjustnu.se", HttpStatusCode.OK, priceJson);
        handler.AddRoute("/v1/sites", HttpStatusCode.Unauthorized, "{}");
        handler.AddRoute("gateway-devices", HttpStatusCode.Unauthorized, "{}");
        return handler;
    }

    [Fact]
    public async Task FlexibleBatch_PendingEco_IsPreservedOnSubsequentBatchRun()
    {
        // Regression test for: "Scheduled run is deleted even if it's still ahead of current time"
        // Scenario:
        //   - First batch run schedules eco for today at some future hour
        //   - Second batch run runs BEFORE that hour → pending eco must be preserved, not overwritten
        var userId = "pending-eco-test";
        var twoDaysAgo = DateTimeOffset.UtcNow.AddDays(-2);
        var cfg = CreateConfig();

        var handler = CreateMockHandlerWithPrices();
        var scopeFactory = BuildScopeFactory(cfg, handler, db =>
        {
            db.UserSettings.Add(new UserSettings
            {
                UserId = userId,
                SchedulingMode = "Flexible",
                AutoApplySchedule = false,
                EcoIntervalHours = 20,
                EcoFlexibilityHours = 8,
                ComfortHours = 3,
            });
            db.FlexibleScheduleStates.Add(new FlexibleScheduleState
            {
                UserId = userId,
                LastEcoRunUtc = twoDaysAgo,
            });
            db.SaveChanges();
        });

        // First batch run: should schedule eco and record NextScheduledEcoUtc
        using var scope1 = scopeFactory.CreateScope();
        var runner1 = scope1.ServiceProvider.GetRequiredService<BatchRunner>();
        var (gen1, _, _) = await runner1.RunBatchAsync(cfg, userId,
            applySchedule: false, persist: true, scopeFactory);
        Assert.True(gen1, "First batch run should generate a schedule");

        // Read the scheduled eco time
        using var scopeRead = scopeFactory.CreateScope();
        var dbRead = scopeRead.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
        var stateAfterFirst = await dbRead.FlexibleScheduleStates.FindAsync(userId);
        Assert.NotNull(stateAfterFirst);
        var pendingEco = stateAfterFirst!.NextScheduledEcoUtc;
        Assert.NotNull(pendingEco);
        // LastEcoRunUtc should NOT have been updated (still points to the old value)
        Assert.Equal(twoDaysAgo, stateAfterFirst.LastEcoRunUtc);

        // Second batch run: runs BEFORE the pending eco time → must keep the pending eco
        using var scope2 = scopeFactory.CreateScope();
        var runner2 = scope2.ServiceProvider.GetRequiredService<BatchRunner>();
        var (gen2, _, _) = await runner2.RunBatchAsync(cfg, userId,
            applySchedule: false, persist: true, scopeFactory);
        Assert.True(gen2, "Second batch run should generate a schedule");

        // The pending eco must still be set (possibly same or rescheduled, but NOT null)
        using var scopeRead2 = scopeFactory.CreateScope();
        var dbRead2 = scopeRead2.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
        var stateAfterSecond = await dbRead2.FlexibleScheduleStates.FindAsync(userId);
        Assert.NotNull(stateAfterSecond);
        Assert.NotNull(stateAfterSecond!.NextScheduledEcoUtc);
        // LastEcoRunUtc should still be the old value (eco hasn't run yet)
        Assert.Equal(twoDaysAgo, stateAfterSecond.LastEcoRunUtc);
    }

    [Fact]
    public async Task FlexibleBatch_EcoAlreadyRan_AdvancesLastEcoRunUtc()
    {
        // When the batch runs AFTER the scheduled eco hour, LastEcoRunUtc should advance
        // and NextScheduledEcoUtc should be cleared.
        var userId = "eco-ran-test";
        // Eco was scheduled 2 hours ago and has already passed
        var pendingEco = DateTimeOffset.UtcNow.AddHours(-2);
        var lastEcoRun = pendingEco.AddDays(-1); // original last run

        var cfg = CreateConfig();
        var handler = CreateMockHandlerWithFailingDaikin();
        var scopeFactory = BuildScopeFactory(cfg, handler, db =>
        {
            db.UserSettings.Add(new UserSettings
            {
                UserId = userId,
                SchedulingMode = "Flexible",
                AutoApplySchedule = false,
                EcoIntervalHours = 20,
                EcoFlexibilityHours = 8,
                ComfortHours = 3,
            });
            db.FlexibleScheduleStates.Add(new FlexibleScheduleState
            {
                UserId = userId,
                LastEcoRunUtc = lastEcoRun,
                NextScheduledEcoUtc = pendingEco, // eco ran 2h ago
            });
            db.SaveChanges();
        });

        using var scope = scopeFactory.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<BatchRunner>();
        await runner.RunBatchAsync(cfg, userId,
            applySchedule: false, persist: true, scopeFactory);
        // A payload may or may not be generated here depending on whether the next eco window
        // has comparable future prices available (e.g., tomorrow data availability).
        // The important invariant is that state advancement still happens correctly.

        using var scopeRead = scopeFactory.CreateScope();
        var db2 = scopeRead.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
        var state = await db2.FlexibleScheduleStates.FindAsync(userId);
        Assert.NotNull(state);

        // LastEcoRunUtc should now be updated to pendingEco (the hour that ran)
        Assert.Equal(pendingEco, state!.LastEcoRunUtc);
        // NextScheduledEcoUtc should be cleared (eco ran) or set to the next scheduled eco
        // Either way it should not equal the pendingEco that just ran
        if (state.NextScheduledEcoUtc.HasValue)
        {
            Assert.True(state.NextScheduledEcoUtc.Value > DateTimeOffset.UtcNow,
                "If a new eco is scheduled, it must be in the future");
        }
    }
}
