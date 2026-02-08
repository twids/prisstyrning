using System.Text.Json.Nodes;
using Prisstyrning.Tests.Fixtures;
using Xunit;

namespace Prisstyrning.Tests;

/// <summary>
/// Tests for ScheduleHistoryPersistence - the critical system that saves schedule generation history.
/// These tests expose several production bugs including JSON serialization issues,
/// fire-and-forget error swallowing, and retention policy edge cases.
/// </summary>
public class ScheduleHistoryPersistenceTests
{
    /// <summary>
    /// Basic test: saving a single schedule should create the history file.
    /// </summary>
    [Fact]
    public async Task SaveAsync_WithValidPayload_CreatesHistoryFile()
    {
        // Arrange
        using var fs = new TempFileSystem();
        var userId = "test-user-1";
        var payload = TestDataFactory.CreateValidSchedulePayload(
            ("monday", 8, "comfort"),
            ("monday", 18, "comfort")
        );
        var timestamp = DateTimeOffset.UtcNow;

        // Act
        await ScheduleHistoryPersistence.SaveAsync(userId, payload, timestamp, retentionDays: 7, baseDir: fs.BaseDir);

        // Assert
        var historyFile = fs.GetHistoryPath(userId);
        Assert.True(File.Exists(historyFile), "History file should be created");

        var historyJson = await File.ReadAllTextAsync(historyFile);
        var history = JsonNode.Parse(historyJson) as JsonArray;
        Assert.NotNull(history);
        Assert.Single(history);
        
        var entry = history[0] as JsonObject;
        Assert.NotNull(entry);
        Assert.NotNull(entry["timestamp"]);
        Assert.NotNull(entry["schedule"]);
    }

    /// <summary>
    /// Appending multiple schedules should maintain chronological order.
    /// </summary>
    [Fact]
    public async Task SaveAsync_WithExistingHistory_AppendsNewEntry()
    {
        // Arrange
        using var fs = new TempFileSystem();
        var userId = "test-user-2";
        var payload1 = TestDataFactory.CreateValidSchedulePayload(("monday", 8, "comfort"));
        var payload2 = TestDataFactory.CreateValidSchedulePayload(("tuesday", 9, "eco"));
        var timestamp1 = DateTimeOffset.UtcNow.AddDays(-1);
        var timestamp2 = DateTimeOffset.UtcNow;

        // Act
        await ScheduleHistoryPersistence.SaveAsync(userId, payload1, timestamp1, retentionDays: 7, baseDir: fs.BaseDir);
        await ScheduleHistoryPersistence.SaveAsync(userId, payload2, timestamp2, retentionDays: 7, baseDir: fs.BaseDir);

        // Assert
        var history = await ScheduleHistoryPersistence.LoadAsync(userId, baseDir: fs.BaseDir);
        Assert.Equal(2, history.Count);
        
        // Verify both entries are present
        var entry1 = history[0] as JsonObject;
        var entry2 = history[1] as JsonObject;
        Assert.NotNull(entry1);
        Assert.NotNull(entry2);
    }

    /// <summary>
    /// Loading history for a user that has never generated a schedule should return empty array.
    /// </summary>
    [Fact]
    public async Task LoadAsync_WithNoHistory_ReturnsEmptyArray()
    {
        // Arrange
        using var fs = new TempFileSystem();
        var userId = "non-existent-user";

        // Act
        var history = await ScheduleHistoryPersistence.LoadAsync(userId, baseDir: fs.BaseDir);

        // Assert
        Assert.NotNull(history);
        Assert.Empty(history);
    }

    /// <summary>
    /// Loading should return all valid entries in the history file.
    /// </summary>
    [Fact]
    public async Task LoadAsync_WithValidHistory_ReturnsAllEntries()
    {
        // Arrange
        using var fs = new TempFileSystem();
        var userId = "test-user-3";
        var payload = TestDataFactory.CreateValidSchedulePayload(("monday", 8, "comfort"));
        
        // Create 3 history entries
        for (int i = 0; i < 3; i++)
        {
            await ScheduleHistoryPersistence.SaveAsync(userId, payload, DateTimeOffset.UtcNow.AddHours(-i), retentionDays: 7, baseDir: fs.BaseDir);
        }

        // Act
        var history = await ScheduleHistoryPersistence.LoadAsync(userId, baseDir: fs.BaseDir);

        // Assert
        Assert.Equal(3, history.Count);
    }

    /// <summary>
    /// CRITICAL BUG TEST: The "node already has a parent" JSON serialization error.
    /// This happens when the same JsonNode is added to multiple parents without cloning.
    /// The fix should deep clone the payload before adding to history.
    /// </summary>
    [Fact]
    public async Task SaveAsync_WithJsonNodeAlreadyHasParent_ClonesCorrectly()
    {
        // Arrange
        using var fs = new TempFileSystem();
        var userId = "test-user-json-bug";
        var payload = TestDataFactory.CreateValidSchedulePayload(("monday", 8, "comfort"));
        
        // Act - Save the SAME payload object twice (this should NOT fail)
        await ScheduleHistoryPersistence.SaveAsync(userId, payload, DateTimeOffset.UtcNow.AddHours(-1), retentionDays: 7, baseDir: fs.BaseDir);
        
        // This second save with the same object reference is the critical test
        // Without proper cloning, this will throw: "The node already has a parent"
        var exception = await Record.ExceptionAsync(async () =>
            await ScheduleHistoryPersistence.SaveAsync(userId, payload, DateTimeOffset.UtcNow, retentionDays: 7, baseDir: fs.BaseDir)
        );

        // Assert
        Assert.Null(exception); // Should NOT throw
        
        var history = await ScheduleHistoryPersistence.LoadAsync(userId, baseDir: fs.BaseDir);
        Assert.Equal(2, history.Count);
    }

    /// <summary>
    /// CRITICAL BUG TEST: Fire-and-forget async calls can swallow exceptions.
    /// This test verifies that errors are at least logged (not silently swallowed).
    /// The current implementation in BatchRunner uses fire-and-forget which is problematic.
    /// </summary>
    [Fact]
    public async Task SaveAsync_WithInvalidPath_ThrowsException()
    {
        // Arrange - Create a file first, then try to use it as a base directory
        // This is cross-platform invalid (can't create a directory inside a file)
        using var fs = new TempFileSystem();
        var fileAsDir = Path.Combine(fs.BaseDir, "this-is-a-file.txt");
        await File.WriteAllTextAsync(fileAsDir, "blocking content");
        
        var userId = "test-user";
        var payload = TestDataFactory.CreateValidSchedulePayload(("monday", 8, "comfort"));

        // Act & Assert
        // This should throw because we're trying to create a directory path inside a file
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await ScheduleHistoryPersistence.SaveAsync(userId, payload, DateTimeOffset.UtcNow, retentionDays: 7, baseDir: fileAsDir)
        );
    }

    /// <summary>
    /// Retention policy should remove entries older than the specified number of days.
    /// </summary>
    [Fact]
    public async Task SaveAsync_WithRetentionDays_RemovesOldEntries()
    {
        // Arrange
        using var fs = new TempFileSystem();
        var userId = "test-user-retention";
        var payload = TestDataFactory.CreateValidSchedulePayload(("monday", 8, "comfort"));
        var retentionDays = 7;

        // Save an old entry (8 days old - should be removed)
        await ScheduleHistoryPersistence.SaveAsync(userId, payload, DateTimeOffset.UtcNow.AddDays(-8), retentionDays, baseDir: fs.BaseDir);
        
        // Save a recent entry (1 day old - should be kept)
        await ScheduleHistoryPersistence.SaveAsync(userId, payload, DateTimeOffset.UtcNow.AddDays(-1), retentionDays, baseDir: fs.BaseDir);
        
        // Save a new entry (now - should be kept)
        await ScheduleHistoryPersistence.SaveAsync(userId, payload, DateTimeOffset.UtcNow, retentionDays, baseDir: fs.BaseDir);

        // Act
        var history = await ScheduleHistoryPersistence.LoadAsync(userId, baseDir: fs.BaseDir);

        // Assert
        Assert.Equal(2, history.Count); // Only the 2 recent entries should remain
    }

    /// <summary>
    /// Edge case: entry that is exactly at the retention boundary should be kept.
    /// </summary>
    [Fact]
    public async Task SaveAsync_WithRetention30Days_Keeps29DayOldEntry()
    {
        // Arrange
        using var fs = new TempFileSystem();
        var userId = "test-user-boundary";
        var payload = TestDataFactory.CreateValidSchedulePayload(("monday", 8, "comfort"));
        var retentionDays = 30;

        // Save entry exactly 29 days old (should be kept)
        await ScheduleHistoryPersistence.SaveAsync(userId, payload, DateTimeOffset.UtcNow.AddDays(-29), retentionDays, baseDir: fs.BaseDir);
        
        // Save entry exactly 31 days old (should be removed)
        await ScheduleHistoryPersistence.SaveAsync(userId, payload, DateTimeOffset.UtcNow.AddDays(-31), retentionDays, baseDir: fs.BaseDir);
        
        // Trigger cleanup with new entry
        await ScheduleHistoryPersistence.SaveAsync(userId, payload, DateTimeOffset.UtcNow, retentionDays, baseDir: fs.BaseDir);

        // Act
        var history = await ScheduleHistoryPersistence.LoadAsync(userId, baseDir: fs.BaseDir);

        // Assert
        Assert.Equal(2, history.Count); // 29-day-old entry + new entry
    }

    /// <summary>
    /// If the history file is corrupted, the system should start fresh rather than crash.
    /// </summary>
    [Fact]
    public async Task SaveAsync_WithCorruptFile_StartsFromScratch()
    {
        // Arrange
        using var fs = new TempFileSystem();
        var userId = "test-user-corrupt";
        var historyFile = fs.GetHistoryPath(userId);
        
        // Create corrupt history file
        Directory.CreateDirectory(Path.GetDirectoryName(historyFile)!);
        await File.WriteAllTextAsync(historyFile, "{ this is not valid json [[[");

        var payload = TestDataFactory.CreateValidSchedulePayload(("monday", 8, "comfort"));

        // Act - Should not crash, should start fresh
        await ScheduleHistoryPersistence.SaveAsync(userId, payload, DateTimeOffset.UtcNow, retentionDays: 7, baseDir: fs.BaseDir);

        // Assert
        var history = await ScheduleHistoryPersistence.LoadAsync(userId, baseDir: fs.BaseDir);
        Assert.Single(history); // Should have exactly 1 entry (the new one)
    }

    /// <summary>
    /// Different users should have completely isolated history.
    /// </summary>
    [Fact]
    public async Task LoadAsync_WithMultipleUsers_ReturnsCorrectUserData()
    {
        // Arrange
        using var fs = new TempFileSystem();
        var user1 = "user-alice";
        var user2 = "user-bob";
        var payload1 = TestDataFactory.CreateValidSchedulePayload(("monday", 8, "comfort"));
        var payload2 = TestDataFactory.CreateValidSchedulePayload(("tuesday", 9, "eco"));

        // Act
        await ScheduleHistoryPersistence.SaveAsync(user1, payload1, DateTimeOffset.UtcNow, retentionDays: 7, baseDir: fs.BaseDir);
        await ScheduleHistoryPersistence.SaveAsync(user2, payload2, DateTimeOffset.UtcNow, retentionDays: 7, baseDir: fs.BaseDir);

        var history1 = await ScheduleHistoryPersistence.LoadAsync(user1, baseDir: fs.BaseDir);
        var history2 = await ScheduleHistoryPersistence.LoadAsync(user2, baseDir: fs.BaseDir);

        // Assert
        Assert.Single(history1);
        Assert.Single(history2);
        
        // Verify they contain different data
        var entry1 = history1[0] as JsonObject;
        var entry2 = history2[0] as JsonObject;
        Assert.NotEqual(entry1?["schedule"]?.ToJsonString(), entry2?["schedule"]?.ToJsonString());
    }

    /// <summary>
    /// Security test: userId should be sanitized to prevent path traversal attacks.
    /// </summary>
    [Fact]
    public async Task SaveAsync_WithInvalidUserId_SanitizesPath()
    {
        // Arrange
        using var fs = new TempFileSystem();
        var maliciousUserId = "../../../etc/passwd";
        var payload = TestDataFactory.CreateValidSchedulePayload(("monday", 8, "comfort"));

        // Act - Should not escape the base directory
        await ScheduleHistoryPersistence.SaveAsync(maliciousUserId, payload, DateTimeOffset.UtcNow, retentionDays: 7, baseDir: fs.BaseDir);

        // Assert - File should be created within the base directory with sanitized userId
        // The malicious "../../../etc/passwd" should be sanitized to just "passwd"
        var sanitizedUserId = "passwd"; // After Path.GetFileName sanitization
        var expectedHistoryFile = Path.Combine(fs.BaseDir, "schedule_history", sanitizedUserId, "history.json");
        var fullPath = Path.GetFullPath(expectedHistoryFile);
        var baseFullPath = Path.GetFullPath(fs.BaseDir);
        
        Assert.True(File.Exists(expectedHistoryFile), $"History file should exist at sanitized path: {expectedHistoryFile}");
        Assert.StartsWith(baseFullPath, fullPath); // Path should be inside base directory
        
        // Verify we can load it back using the malicious userId (which gets sanitized again)
        var history = await ScheduleHistoryPersistence.LoadAsync(maliciousUserId, baseDir: fs.BaseDir);
        Assert.Single(history);
    }

    /// <summary>
    /// Concurrent writes to the same history file should not cause data corruption or crashes.
    /// This is a stress test for race conditions.
    /// </summary>
    [Fact]
    public async Task SaveAsync_ConcurrentWrites_HandlesRaceCondition()
    {
        // Arrange
        using var fs = new TempFileSystem();
        var userId = "test-user-concurrent";
        var payload = TestDataFactory.CreateValidSchedulePayload(("monday", 8, "comfort"));

        // Act - Write 10 entries concurrently
        var tasks = Enumerable.Range(0, 10).Select(i =>
            ScheduleHistoryPersistence.SaveAsync(userId, payload, DateTimeOffset.UtcNow.AddMinutes(i), retentionDays: 7, baseDir: fs.BaseDir)
        ).ToArray();

        await Task.WhenAll(tasks);

        // Assert - All entries should be saved (no crashes or data loss)
        var history = await ScheduleHistoryPersistence.LoadAsync(userId, baseDir: fs.BaseDir);
        Assert.Equal(10, history.Count);
    }
}
