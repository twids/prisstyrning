using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Prisstyrning.Tests.Fixtures;
using Xunit;

namespace Prisstyrning.Tests.Unit;

/// <summary>
/// Tests for BatchRunner persist flag behavior
/// NOTE: These tests are network-dependent because BatchRunner.RunBatchAsync always fetches prices from Nordpool API
/// and ignores PriceMemory. To make these true unit tests, BatchRunner would need to support injecting a price source.
/// </summary>
public class BatchRunnerTests
{
    [Fact(Skip = "Network-dependent: BatchRunner always fetches from Nordpool API, ignoring PriceMemory.Set(). Requires refactoring to inject price source.")]
    public async Task Test_BatchRunner_PersistFalse_DoesNotSaveHistory()
    {
        // Arrange
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();
        var userId = "test-user-persist-false";
        var date = new DateTime(2026, 2, 7);
        
        // Setup price data (NOTE: This is ignored by BatchRunner - it always fetches from Nordpool)
        var today = TestDataFactory.CreatePriceData(date);
        var tomorrow = TestDataFactory.CreatePriceData(date.AddDays(1));
        PriceMemory.Set(today, tomorrow);
        
        // Act: Call with persist=false
        var (generated, payload, _) = await BatchRunner.RunBatchAsync(
            cfg, 
            userId, 
            applySchedule: false, 
            persist: false
        );
        
        // Assert: Schedule should be generated but NOT saved to history
        Assert.True(generated, "Schedule should be generated");
        Assert.NotNull(payload);
        
        var historyPath = fs.GetHistoryPath(userId);
        Assert.False(File.Exists(historyPath), 
            "History file should NOT exist when persist=false");
    }
    
    [Fact(Skip = "Network-dependent: BatchRunner always fetches from Nordpool API, ignoring PriceMemory.Set(). Requires refactoring to inject price source.")]
    public async Task Test_BatchRunner_PersistTrue_SavesHistory()
    {
        // Arrange
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();
        var userId = "test-user-persist-true";
        var date = new DateTime(2026, 2, 7);
        
        // Setup price data (NOTE: This is ignored by BatchRunner - it always fetches from Nordpool)
        var today = TestDataFactory.CreatePriceData(date);
        var tomorrow = TestDataFactory.CreatePriceData(date.AddDays(1));
        PriceMemory.Set(today, tomorrow);
        
        // Act: Call with persist=true
        var (generated, payload, _) = await BatchRunner.RunBatchAsync(
            cfg, 
            userId, 
            applySchedule: false, 
            persist: true
        );
        
        // Assert: Schedule should be generated AND saved to history
        Assert.True(generated, "Schedule should be generated");
        Assert.NotNull(payload);
        
        // Wait briefly for async history save
        await Task.Delay(100);
        
        var historyPath = fs.GetHistoryPath(userId);
        Assert.True(File.Exists(historyPath), 
            "History file SHOULD exist when persist=true");
        
        // Verify history content
        var historyJson = await File.ReadAllTextAsync(historyPath);
        var historyArray = JsonNode.Parse(historyJson) as JsonArray;
        Assert.NotNull(historyArray);
        Assert.Single(historyArray);
    }
}
