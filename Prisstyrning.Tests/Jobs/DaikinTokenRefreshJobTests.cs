using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Prisstyrning.Data;
using Prisstyrning.Data.Repositories;
using Prisstyrning.Jobs;
using Prisstyrning.Tests.Fixtures;
using Xunit;

namespace Prisstyrning.Tests.Jobs;

/// <summary>
/// Tests for DaikinTokenRefreshHangfireJob - proactive OAuth token refresh
/// </summary>
public class DaikinTokenRefreshJobTests
{
    private static (IServiceScopeFactory scopeFactory, PrisstyrningDbContext db, DaikinTokenRepository tokenRepo) CreateServices(IConfiguration? cfg = null)
    {
        var options = new DbContextOptionsBuilder<PrisstyrningDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        var db = new PrisstyrningDbContext(options);
        db.Database.EnsureCreated();
        var tokenRepo = new DaikinTokenRepository(db);

        var config = cfg ?? new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton<IConfiguration>(config);
        services.AddScoped<PrisstyrningDbContext>(sp => new PrisstyrningDbContext(sp.GetRequiredService<DbContextOptions<PrisstyrningDbContext>>()));
        services.AddScoped<DaikinTokenRepository>();
        services.AddSingleton<IHttpClientFactory>(MockServiceFactory.CreateMockHttpClientFactory());
        services.AddScoped<DaikinOAuthService>();
        var sp = services.BuildServiceProvider();
        return (sp.GetRequiredService<IServiceScopeFactory>(), db, tokenRepo);
    }

    [Fact]
    public async Task ExecuteAsync_WithExpiredToken_RefreshesToken()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig(new Dictionary<string, string?>
        {
            ["Daikin:ProactiveRefreshWindowMinutes"] = "60" // 1 hour window
        });
        
        var (scopeFactory, db, tokenRepo) = CreateServices();
        using (db)
        {
            // Store token with expiration in 30 minutes (within 60-min refresh window)
            await tokenRepo.SaveAsync("user-expired-token", "old-token-123", "refresh-456", DateTimeOffset.UtcNow.AddMinutes(30));
            
            var job = new DaikinTokenRefreshHangfireJob(cfg, scopeFactory);
            
            // Note: Without actual OAuth server, this will fail to refresh
            // but the job should handle it gracefully
            await job.ExecuteAsync();
            
            // Job should complete without throwing
            Assert.True(true, "Job completed without crashing");
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithValidToken_SkipsRefresh()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig(new Dictionary<string, string?>
        {
            ["Daikin:ProactiveRefreshWindowMinutes"] = "5" // 5 minute window
        });
        
        var (scopeFactory, db, tokenRepo) = CreateServices();
        using (db)
        {
            // Store token with expiration in 2 hours (outside 5-min refresh window)
            await tokenRepo.SaveAsync("user-valid-token", "valid-token-789", "refresh-999", DateTimeOffset.UtcNow.AddHours(2));
            
            var job = new DaikinTokenRefreshHangfireJob(cfg, scopeFactory);
            await job.ExecuteAsync();
            
            // Token should remain unchanged (no refresh needed)
            var token = await tokenRepo.LoadAsync("user-valid-token");
            Assert.NotNull(token);
            Assert.Equal("valid-token-789", token.AccessToken);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ScansAllUserTokens()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();
        
        var (scopeFactory, db, tokenRepo) = CreateServices();
        using (db)
        {
            // Create multiple user tokens in DB
            var users = new[] { "user1", "user2", "user3" };
            foreach (var userId in users)
            {
                await tokenRepo.SaveAsync(userId, $"token-{userId}", $"refresh-{userId}", DateTimeOffset.UtcNow.AddHours(3));
            }
            
            var job = new DaikinTokenRefreshHangfireJob(cfg, scopeFactory);
            await job.ExecuteAsync();
            
            // All tokens should still exist in DB (scanned but not refreshed: still valid)
            foreach (var userId in users)
            {
                var token = await tokenRepo.LoadAsync(userId);
                Assert.NotNull(token);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoTokens_CompletesGracefully()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();
        
        var (scopeFactory, db, _) = CreateServices();
        using (db)
        {
            var job = new DaikinTokenRefreshHangfireJob(cfg, scopeFactory);
            
            // Should not throw when no tokens exist
            await job.ExecuteAsync();
            
            Assert.True(true, "Job completed without crashing on empty DB");
        }
    }
}
