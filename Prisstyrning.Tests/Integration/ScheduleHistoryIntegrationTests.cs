using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Prisstyrning.Tests.Fixtures;
using Xunit;

namespace Prisstyrning.Tests.Integration;

/// <summary>
/// Integration tests for schedule history persistence
/// Phase 2: Verify history is only saved on apply, not on preview
/// </summary>
public class ScheduleHistoryIntegrationTests
{
    [Fact]
    public async Task Test_SchedulePreview_DoesNotSaveHistory()
    {
        // Arrange
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();
        var userId = "test-preview-no-history";
        var date = new DateTime(2026, 2, 7);
        
        var today = TestDataFactory.CreatePriceData(date);
        var tomorrow = TestDataFactory.CreatePriceData(date.AddDays(1));
        PriceMemory.Set(today, tomorrow);
        
        // Act: Call GenerateSchedulePreview (used by /api/schedule/preview endpoint)
        await BatchRunner.GenerateSchedulePreview(cfg);
        
        // Assert: No history should be saved
        await Task.Delay(100); // Wait for any async saves
        
        var historyPath = fs.GetHistoryPath(userId);
        Assert.False(File.Exists(historyPath), 
            "Preview should NOT create history file");
    }
    
    [Fact]
    public async Task Test_ScheduleApply_SavesHistory()
    {
        // Arrange
        using var fs = new TempFileSystem();
        var userId = "test-apply-saves-history";
        var date = new DateTime(2026, 2, 7);
        
        var today = TestDataFactory.CreatePriceData(date);
        var tomorrow = TestDataFactory.CreatePriceData(date.AddDays(1));
        PriceMemory.Set(today, tomorrow);
        
        // Create a schedule payload to "apply"
        var schedulePayload = new JsonObject
        {
            ["schedule1"] = new JsonObject
            {
                ["actions"] = new JsonArray
                {
                    new JsonObject { ["hour"] = 0, ["state"] = "comfort" },
                    new JsonObject { ["hour"] = 8, ["state"] = "turn_off" }
                }
            }
        };
        
        // Act: Simulate schedule application by saving to history
        // (This mimics what /api/daikin/gateway/schedule/put should do after successful PUT)
        await ScheduleHistoryPersistence.SaveAsync(
            userId, 
            schedulePayload, 
            DateTimeOffset.UtcNow, 
            7, 
            fs.BaseDir
        );
        
        // Assert: History should be saved
        var historyPath = fs.GetHistoryPath(userId);
        Assert.True(File.Exists(historyPath), 
            "Apply SHOULD create history file");
        
        var historyJson = await File.ReadAllTextAsync(historyPath);
        var historyArray = JsonNode.Parse(historyJson) as JsonArray;
        Assert.NotNull(historyArray);
        Assert.Single(historyArray);
    }
    
    [Fact]
    public async Task Integration_PreviewThenApply_OnlyOneHistoryEntry()
    {
        // Arrange
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();
        var userId = "test-preview-apply-sequence";
        var date = new DateTime(2026, 2, 7);
        
        var today = TestDataFactory.CreatePriceData(date);
        var tomorrow = TestDataFactory.CreatePriceData(date.AddDays(1));
        PriceMemory.Set(today, tomorrow);
        
        // Act 1: Preview multiple times (should NOT save history)
        await BatchRunner.GenerateSchedulePreview(cfg);
        await BatchRunner.GenerateSchedulePreview(cfg);
        await BatchRunner.GenerateSchedulePreview(cfg);
        
        await Task.Delay(150); // Wait for any async saves
        
        var historyPath = fs.GetHistoryPath(userId);
        Assert.False(File.Exists(historyPath), 
            "Multiple previews should NOT create history");
        
        // Act 2: Apply once (should save history once)
        var (generated, payload, _) = await BatchRunner.RunBatchAsync(
            cfg, userId, applySchedule: false, persist: false);
        
        Assert.True(generated);
        Assert.NotNull(payload);
        
        // Simulate the /api/daikin/gateway/schedule/put endpoint saving history
        if (payload is JsonObject scheduleObj)
        {
            await ScheduleHistoryPersistence.SaveAsync(
                userId, scheduleObj, DateTimeOffset.UtcNow, 7, fs.BaseDir);
        }
        
        await Task.Delay(100);
        
        // Assert: Only ONE history entry from the apply
        Assert.True(File.Exists(historyPath), 
            "Apply should create history file");
        
        var historyJson = await File.ReadAllTextAsync(historyPath);
        var historyArray = JsonNode.Parse(historyJson) as JsonArray;
        Assert.NotNull(historyArray);
        Assert.Single(historyArray); // Only 1 entry from apply, not 3 from previews
    }
}
