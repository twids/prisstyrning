using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Hangfire;

namespace Prisstyrning.Jobs;

/// <summary>
/// Hangfire job that generates and applies schedules for users with auto-apply enabled
/// </summary>
public class ScheduleUpdateHangfireJob
{
    private readonly IConfiguration _cfg;
    private readonly BatchRunner _batchRunner;

    public ScheduleUpdateHangfireJob(IConfiguration cfg, BatchRunner batchRunner)
    {
        _cfg = cfg;
        _batchRunner = batchRunner;
    }

    [DisableConcurrentExecution(120)] // Prevent overlapping executions with 120s timeout
    public async Task ExecuteAsync()
    {
        Console.WriteLine("[ScheduleUpdateHangfireJob] Starting schedule update for users with auto-apply enabled");
        
        var tokensDir = StoragePaths.GetTokensDir(_cfg);
        if (!Directory.Exists(tokensDir))
        {
            Console.WriteLine("[ScheduleUpdateHangfireJob] Tokens directory not found, skipping");
            return;
        }

        var userDirs = Directory.GetDirectories(tokensDir);
        var processedCount = 0;
        var skippedCount = 0;
        var errorCount = 0;

        foreach (var userDir in userDirs)
        {
            try
            {
                var userId = Path.GetFileName(userDir);
                var userJsonPath = Path.Combine(userDir, "user.json");
                
                if (!File.Exists(userJsonPath))
                {
                    skippedCount++;
                    continue;
                }

                var json = await File.ReadAllTextAsync(userJsonPath);
                var node = JsonNode.Parse(json) as JsonObject;
                if (node == null)
                {
                    skippedCount++;
                    continue;
                }

                bool autoApply = bool.TryParse(node["AutoApplySchedule"]?.ToString(), out var parsedAutoApply) ? parsedAutoApply : false;
                if (!autoApply)
                {
                    skippedCount++;
                    continue;
                }

                Console.WriteLine($"[ScheduleUpdateHangfireJob] Processing user {userId}");
                try
                {
                    var (generated, schedulePayload, message) = await _batchRunner.RunBatchAsync(_cfg, userId, applySchedule: true, persist: true);
                    Console.WriteLine($"[ScheduleUpdateHangfireJob] user={userId} generated={generated} message={message}");
                    processedCount++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ScheduleUpdateHangfireJob] user={userId} error: {ex.Message}");
                    errorCount++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ScheduleUpdateHangfireJob] userdir={userDir} error: {ex.Message}");
                errorCount++;
            }
        }

        Console.WriteLine($"[ScheduleUpdateHangfireJob] Completed: processed={processedCount}, skipped={skippedCount}, errors={errorCount}");
    }
}
