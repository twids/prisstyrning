using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Prisstyrning.Tests.Fixtures;
using Xunit;

namespace Prisstyrning.Tests.Unit;

/// <summary>
/// Unit tests for BatchRunner persist flag behavior
/// Phase 2: Verify that schedule history is only saved when persist=true
/// </summary>
public class BatchRunnerTests
{
    [Fact]
    public async Task Test_BatchRunner_PersistFalse_DoesNotSaveHistory()
    {
        // Arrange
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();
        var userId = "test-user-persist-false";
        var date = new DateTime(2026, 2, 7);
        
        // Setup price data
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
    
    [Fact]
    public async Task Test_BatchRunner_PersistTrue_SavesHistory()
    {
        // Arrange
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();
        var userId = "test-user-persist-true";
        var date = new DateTime(2026, 2, 7);
        
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
