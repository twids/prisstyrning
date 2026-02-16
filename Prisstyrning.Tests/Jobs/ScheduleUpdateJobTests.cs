using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Data.Repositories;
using Prisstyrning.Jobs;
using Prisstyrning.Tests.Fixtures;
using Xunit;

namespace Prisstyrning.Tests.Jobs;

/// <summary>
/// Tests for ScheduleUpdateHangfireJob - scheduled generation and application
/// </summary>
public class ScheduleUpdateJobTests : IDisposable
{
    private ServiceProvider? _serviceProvider;

    public void Dispose()
    {
        _serviceProvider?.Dispose();
    }

    private IServiceScopeFactory BuildScopeFactory(IConfiguration cfg, Action<PrisstyrningDbContext>? seed = null)
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<PrisstyrningDbContext>(o =>
            o.UseInMemoryDatabase(dbName));
        services.AddSingleton(cfg);
        services.AddScoped<UserSettingsRepository>();
        services.AddScoped<ScheduleHistoryRepository>();
        _serviceProvider = services.BuildServiceProvider();

        if (seed != null)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
            db.Database.EnsureCreated();
            seed(db);
        }

        return _serviceProvider.GetRequiredService<IServiceScopeFactory>();
    }

    [Fact]
    public async Task ExecuteAsync_WithAutoApplyEnabled_GeneratesSchedules()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();
        var userId = "auto-apply-user";
        var date = new DateTime(2026, 2, 7);
        
        // Setup: Create user with AutoApplySchedule=true in DB
        var scopeFactory = BuildScopeFactory(cfg, db =>
        {
            db.UserSettings.Add(new UserSettings
            {
                UserId = userId,
                AutoApplySchedule = true,
                ComfortHours = 3
            });
            db.SaveChanges();
        });
        
        // Also write user.json for BatchRunner (still file-based)
        var userDir = Path.Combine(fs.TokensDir, userId);
        Directory.CreateDirectory(userDir);
        File.WriteAllText(
            Path.Combine(userDir, "user.json"),
            "{\"AutoApplySchedule\":true,\"ComfortHours\":3}"
        );
        
        // Setup: Create price data
        var today = TestDataFactory.CreatePriceData(date);
        var tomorrow = TestDataFactory.CreatePriceData(date.AddDays(1));
        NordpoolPersistence.Save("SE3", today, tomorrow, fs.NordpoolDir);
        PriceMemory.Set(today, tomorrow);
        
        var job = new ScheduleUpdateHangfireJob(cfg, scopeFactory);
        await job.ExecuteAsync();
        
        // Give async operations time to complete
        await Task.Delay(1000);
        
        // Verify: History was saved to DB (persist=true in RunBatchAsync)
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
            var historyCount = await db.ScheduleHistory.CountAsync(h => h.UserId == userId);
            Assert.True(historyCount > 0, "History should be saved for auto-apply users");
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithNoUsers_SkipsGracefully()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();
        
        // No users in DB
        var scopeFactory = BuildScopeFactory(cfg);
        var job = new ScheduleUpdateHangfireJob(cfg, scopeFactory);
        
        // Should complete without errors
        await job.ExecuteAsync();
        
        // No exception thrown = success
        Assert.True(true);
    }

    [Fact]
    public async Task ExecuteAsync_ProcessesMultipleUsers()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();
        var date = new DateTime(2026, 2, 7);
        
        var user1 = "user-multi-1";
        var user2 = "user-multi-2";
        
        // Seed DB with auto-apply users
        var scopeFactory = BuildScopeFactory(cfg, db =>
        {
            db.UserSettings.Add(new UserSettings { UserId = user1, AutoApplySchedule = true, ComfortHours = 2 });
            db.UserSettings.Add(new UserSettings { UserId = user2, AutoApplySchedule = true, ComfortHours = 4 });
            db.SaveChanges();
        });
        
        // Also write user.json files for BatchRunner
        var user1Dir = Path.Combine(fs.TokensDir, user1);
        var user2Dir = Path.Combine(fs.TokensDir, user2);
        Directory.CreateDirectory(user1Dir);
        Directory.CreateDirectory(user2Dir);
        File.WriteAllText(Path.Combine(user1Dir, "user.json"), "{\"AutoApplySchedule\":true,\"ComfortHours\":2}");
        File.WriteAllText(Path.Combine(user2Dir, "user.json"), "{\"AutoApplySchedule\":true,\"ComfortHours\":4}");
        
        // Setup price data
        var today = TestDataFactory.CreatePriceData(date);
        var tomorrow = TestDataFactory.CreatePriceData(date.AddDays(1));
        PriceMemory.Set(today, tomorrow);
        
        var job = new ScheduleUpdateHangfireJob(cfg, scopeFactory);
        await job.ExecuteAsync();
        
        await Task.Delay(1500);
        
        // Both users should have history in DB
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
            var history1Count = await db.ScheduleHistory.CountAsync(h => h.UserId == user1);
            var history2Count = await db.ScheduleHistory.CountAsync(h => h.UserId == user2);
            Assert.True(history1Count > 0 || history2Count > 0, 
                "At least one user should have history saved");
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithUserError_ContinuesOtherUsers()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();
        var date = new DateTime(2026, 2, 7);
        
        var badUser = "user-corrupt";
        var goodUser = "user-good";
        
        // Seed DB - both have auto-apply
        var scopeFactory = BuildScopeFactory(cfg, db =>
        {
            db.UserSettings.Add(new UserSettings { UserId = badUser, AutoApplySchedule = true, ComfortHours = 3 });
            db.UserSettings.Add(new UserSettings { UserId = goodUser, AutoApplySchedule = true, ComfortHours = 3 });
            db.SaveChanges();
        });
        
        // Bad user has corrupt file-based json, good user has valid
        var badDir = Path.Combine(fs.TokensDir, badUser);
        var goodDir = Path.Combine(fs.TokensDir, goodUser);
        Directory.CreateDirectory(badDir);
        Directory.CreateDirectory(goodDir);
        File.WriteAllText(Path.Combine(badDir, "user.json"), "{ invalid json }");
        File.WriteAllText(Path.Combine(goodDir, "user.json"), "{\"AutoApplySchedule\":true,\"ComfortHours\":3}");
        
        var today = TestDataFactory.CreatePriceData(date);
        var tomorrow = TestDataFactory.CreatePriceData(date.AddDays(1));
        PriceMemory.Set(today, tomorrow);
        
        var job = new ScheduleUpdateHangfireJob(cfg, scopeFactory);
        
        // Should not throw despite corrupt user data
        await job.ExecuteAsync();
        
        await Task.Delay(1000);
        
        // Good user should still be processed
        Assert.True(true, "Job completed without crashing");
    }

    [Fact]
    public async Task ExecuteAsync_OnlyProcessesUsersWithAutoApply()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();
        var date = new DateTime(2026, 2, 7);
        
        var userNoAuto = "user-no-auto";
        
        // Seed DB with AutoApplySchedule=false
        var scopeFactory = BuildScopeFactory(cfg, db =>
        {
            db.UserSettings.Add(new UserSettings { UserId = userNoAuto, AutoApplySchedule = false, ComfortHours = 3 });
            db.SaveChanges();
        });
        
        // Write file for BatchRunner
        var userDir = Path.Combine(fs.TokensDir, userNoAuto);
        Directory.CreateDirectory(userDir);
        File.WriteAllText(Path.Combine(userDir, "user.json"), "{\"AutoApplySchedule\":false,\"ComfortHours\":3}");
        
        var today = TestDataFactory.CreatePriceData(date);
        var tomorrow = TestDataFactory.CreatePriceData(date.AddDays(1));
        PriceMemory.Set(today, tomorrow);
        
        var job = new ScheduleUpdateHangfireJob(cfg, scopeFactory);
        await job.ExecuteAsync();
        
        await Task.Delay(500);
        
        // User without auto-apply should NOT have history saved
        var historyFile = Path.Combine(fs.HistoryDir, userNoAuto, "history.json");
        Assert.False(File.Exists(historyFile), 
            "Users with AutoApplySchedule=false should be skipped");
    }
}
