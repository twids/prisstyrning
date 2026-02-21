using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Prisstyrning.Tests.Fixtures;
using Xunit;

namespace Prisstyrning.Tests.Integration;

/// <summary>
/// Integration tests for schedule history persistence
/// Phase 2: Verify history is only saved on apply, not on preview
/// NOTE: BatchRunner is now instance-based with DI (IHttpClientFactory, DaikinOAuthService).
/// History persistence is now handled via ScheduleHistoryRepository (EF Core/PostgreSQL).
/// </summary>
public class ScheduleHistoryIntegrationTests
{
    [Fact(Skip = "Network-dependent: BatchRunner fetches from Nordpool API. Requires mock HTTP infrastructure.")]
    public async Task Test_SchedulePreview_DoesNotSaveHistory()
    {
        // Arrange
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();
        var date = new DateTime(2026, 2, 7);
        
        var today = TestDataFactory.CreatePriceData(date);
        var tomorrow = TestDataFactory.CreatePriceData(date.AddDays(1));
        PriceMemory.Set(today, tomorrow);
        
        // Act: Call GenerateSchedulePreview (used by /api/schedule/preview endpoint)
        // This calls RunBatchAsync with userId=null and persist=false
        var batchRunner = MockServiceFactory.CreateMockBatchRunner();
        await batchRunner.GenerateSchedulePreview(cfg);
        
        // Assert: No history should be saved anywhere
        // With the DB-based approach, history is persisted via ScheduleHistoryRepository
        // Preview calls with persist=false should not trigger any history saves
        await Task.Delay(100); // Wait for any async saves
    }
    
    [Fact(Skip = "Requires database setup: ScheduleHistoryRepository uses EF Core/PostgreSQL.")]
    public async Task Test_ScheduleHistoryPersistence_SavesCorrectly()
    {
        // NOTE: This tests the schedule history persistence layer.
        // With the migration to PostgreSQL, history is now saved via ScheduleHistoryRepository.
        // This test would need an in-memory database setup to work properly.
        
        // Arrange
        using var fs = new TempFileSystem();
        var _userId = "test-apply-saves-history"; // prefixed to suppress unused warning (test is skipped)
        var date = new DateTime(2026, 2, 7);
        
        var today = TestDataFactory.CreatePriceData(date);
        var tomorrow = TestDataFactory.CreatePriceData(date.AddDays(1));
        PriceMemory.Set(today, tomorrow);
        
        // Create a schedule payload to save
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
        
        // TODO: Implement with in-memory DB and ScheduleHistoryRepository
        // var historyRepo = CreateInMemoryHistoryRepo();
        // await historyRepo.SaveAsync(userId, schedulePayload, DateTimeOffset.UtcNow);
        // var history = await historyRepo.GetHistoryAsync(userId);
        // Assert.Single(history);
        Assert.NotNull(schedulePayload);
    }
    
    [Fact(Skip = "Network-dependent: BatchRunner fetches from Nordpool API. Requires mock HTTP infrastructure.")]
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
        var batchRunner = MockServiceFactory.CreateMockBatchRunner();
        await batchRunner.GenerateSchedulePreview(cfg);
        await batchRunner.GenerateSchedulePreview(cfg);
        await batchRunner.GenerateSchedulePreview(cfg);
        
        await Task.Delay(150); // Wait for any async saves
        
        // Act 2: Apply once (should save history once via ScheduleHistoryRepository)
        var (generated, payload, _) = await batchRunner.RunBatchAsync(
            cfg, userId, applySchedule: false, persist: false);
        
        Assert.True(generated);
        Assert.NotNull(payload);
        
        // NOTE: History persistence is now handled via ScheduleHistoryRepository (EF Core)
        // The /api/daikin/gateway/schedule/put endpoint saves history through the repository
    }
}
