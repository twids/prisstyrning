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

        var httpFactory = MockServiceFactory.CreateMockHttpClientFactory(mockHandler);
        services.AddSingleton<IHttpClientFactory>(httpFactory);

        services.AddScoped<UserSettingsRepository>();
        services.AddScoped<ScheduleHistoryRepository>();
        services.AddScoped<DaikinTokenRepository>();
        services.AddScoped<FlexibleScheduleStateRepository>();
        services.AddScoped<PriceRepository>();
        services.AddScoped<DaikinOAuthService>();
        services.AddScoped<BatchRunner>();

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
        Assert.NotNull(state!.LastEcoRunUtc);
        // LastEcoRunUtc should no longer be the old 2-days-ago value
        Assert.True(state.LastEcoRunUtc > twoDaysAgo,
            $"LastEcoRunUtc should have advanced from {twoDaysAgo} but was {state.LastEcoRunUtc}");
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
    }
}
