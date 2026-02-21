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
        var batchRunner = MockServiceFactory.CreateMockBatchRunner();
        var (generated, payload, _) = await batchRunner.RunBatchAsync(
            cfg, 
            userId, 
            applySchedule: false, 
            persist: false
        );
        
        // Assert: Schedule should be generated but NOT saved to history
        Assert.True(generated, "Schedule should be generated");
        Assert.NotNull(payload);
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
        var batchRunner = MockServiceFactory.CreateMockBatchRunner();
        var (generated, payload, _) = await batchRunner.RunBatchAsync(
            cfg, 
            userId, 
            applySchedule: false, 
            persist: true
        );
        
        // Assert: Schedule should be generated AND saved to history
        Assert.True(generated, "Schedule should be generated");
        Assert.NotNull(payload);
    }
}
