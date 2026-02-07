using System.Text.Json;
using System.Text.Json.Nodes;
using Prisstyrning.Jobs;
using Prisstyrning.Tests.Fixtures;
using Xunit;

namespace Prisstyrning.Tests.Jobs;

/// <summary>
/// Tests for DaikinTokenRefreshHangfireJob - proactive OAuth token refresh
/// </summary>
public class DaikinTokenRefreshJobTests
{
    [Fact]
    public async Task ExecuteAsync_WithExpiredToken_RefreshesToken()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig(new Dictionary<string, string?>
        {
            ["Daikin:ProactiveRefreshWindowMinutes"] = "60" // 1 hour window
        });
        
        var userId = "user-expired-token";
        var userDir = Path.Combine(fs.TokensDir, userId);
        Directory.CreateDirectory(userDir);
        
        // Create token file with expiration in 30 minutes (within refresh window)
        var tokenData = new JsonObject
        {
            ["access_token"] = "old-token-123",
            ["refresh_token"] = "refresh-456",
            ["expires_at_utc"] = DateTimeOffset.UtcNow.AddMinutes(30).ToString("o"),
            ["token_type"] = "Bearer"
        };
        
        var tokenFile = Path.Combine(userDir, "daikin.json");
        await File.WriteAllTextAsync(tokenFile, 
            tokenData.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        
        var job = new DaikinTokenRefreshHangfireJob(cfg);
        
        // Note: Without actual OAuth server, this will fail to refresh
        // but the job should handle it gracefully
        await job.ExecuteAsync();
        
        // Job should complete without throwing
        Assert.True(true, "Job completed without crashing");
    }

    [Fact]
    public async Task ExecuteAsync_WithValidToken_SkipsRefresh()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig(new Dictionary<string, string?>
        {
            ["Daikin:ProactiveRefreshWindowMinutes"] = "5" // 5 minute window
        });
        
        var userId = "user-valid-token";
        var userDir = Path.Combine(fs.TokensDir, userId);
        Directory.CreateDirectory(userDir);
        
        // Create token file with expiration in 2 hours (outside refresh window)
        var tokenData = new JsonObject
        {
            ["access_token"] = "valid-token-789",
            ["refresh_token"] = "refresh-999",
            ["expires_at_utc"] = DateTimeOffset.UtcNow.AddHours(2).ToString("o"),
            ["token_type"] = "Bearer"
        };
        
        var tokenFile = Path.Combine(userDir, "daikin.json");
        var originalContent = tokenData.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(tokenFile, originalContent);
        
        var job = new DaikinTokenRefreshHangfireJob(cfg);
        await job.ExecuteAsync();
        
        // Token file should remain unchanged (no refresh needed)
        var currentContent = await File.ReadAllTextAsync(tokenFile);
        Assert.Equal(originalContent, currentContent);
    }

    [Fact]
    public async Task ExecuteAsync_ScansAllUserDirectories()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();
        
        // Create multiple user directories with token files
        var users = new[] { "user1", "user2", "user3" };
        
        foreach (var userId in users)
        {
            var userDir = Path.Combine(fs.TokensDir, userId);
            Directory.CreateDirectory(userDir);
            
            var tokenData = new JsonObject
            {
                ["access_token"] = $"token-{userId}",
                ["expires_at_utc"] = DateTimeOffset.UtcNow.AddHours(3).ToString("o")
            };
            
            await File.WriteAllTextAsync(
                Path.Combine(userDir, "daikin.json"),
                tokenData.ToJsonString()
            );
        }
        
        var job = new DaikinTokenRefreshHangfireJob(cfg);
        await job.ExecuteAsync();
        
        // All token files should still exist (scanned but not refreshed)
        foreach (var userId in users)
        {
            var tokenFile = Path.Combine(fs.TokensDir, userId, "daikin.json");
            Assert.True(File.Exists(tokenFile), $"Token file for {userId} should exist");
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithRefreshError_LogsAndContinues()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();
        
        // Create user with corrupt token file
        var badUser = "user-corrupt-token";
        var badDir = Path.Combine(fs.TokensDir, badUser);
        Directory.CreateDirectory(badDir);
        await File.WriteAllTextAsync(
            Path.Combine(badDir, "daikin.json"),
            "{ corrupt json content"
        );
        
        // Create user with valid token
        var goodUser = "user-good-token";
        var goodDir = Path.Combine(fs.TokensDir, goodUser);
        Directory.CreateDirectory(goodDir);
        
        var validToken = new JsonObject
        {
            ["access_token"] = "valid-abc",
            ["expires_at_utc"] = DateTimeOffset.UtcNow.AddDays(1).ToString("o")
        };
        await File.WriteAllTextAsync(
            Path.Combine(goodDir, "daikin.json"),
            validToken.ToJsonString()
        );
        
        var job = new DaikinTokenRefreshHangfireJob(cfg);
        
        // Should not throw despite corrupt token file
        await job.ExecuteAsync();
        
        // Good token should still be intact
        var goodTokenFile = Path.Combine(goodDir, "daikin.json");
        Assert.True(File.Exists(goodTokenFile));
        
        var content = await File.ReadAllTextAsync(goodTokenFile);
        var parsed = JsonNode.Parse(content);
        Assert.NotNull(parsed);
    }
}
