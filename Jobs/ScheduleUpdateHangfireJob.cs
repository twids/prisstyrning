using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Hangfire;
using Prisstyrning.Data;
using Prisstyrning.Data.Repositories;
using Prisstyrning.Thermal.Control;

namespace Prisstyrning.Jobs;

/// <summary>
/// Hangfire job that generates and applies schedules for users with auto-apply enabled
/// </summary>
internal class ScheduleUpdateHangfireJob
{
    private readonly IConfiguration _cfg;
    private readonly IServiceScopeFactory _scopeFactory;

    public ScheduleUpdateHangfireJob(IConfiguration cfg, IServiceScopeFactory scopeFactory)
    {
        _cfg = cfg;
        _scopeFactory = scopeFactory;
    }

    [DisableConcurrentExecution(120)] // Prevent overlapping executions with 120s timeout
    public async Task ExecuteAsync()
    {
        Console.WriteLine("[ScheduleUpdateHangfireJob] Starting schedule update for users with auto-apply enabled");
        
        List<string> autoApplyUserIds;
        using (var scope = _scopeFactory.CreateScope())
        {
            var settingsRepo = scope.ServiceProvider.GetRequiredService<UserSettingsRepository>();
            autoApplyUserIds = await settingsRepo.GetAutoApplyUserIdsAsync();
        }

        if (autoApplyUserIds.Count == 0)
        {
            Console.WriteLine("[ScheduleUpdateHangfireJob] No users with auto-apply enabled, skipping");
            return;
        }

        var processedCount = 0;
        var errorCount = 0;

        foreach (var userId in autoApplyUserIds)
        {
            Console.WriteLine($"[ScheduleUpdateHangfireJob] Processing user {userId}");
            try
            {
                using var scope = _scopeFactory.CreateScope();
                DhwWriterLeaseService? lease = null;
                string? leaseOwner = null;
                if (_cfg.GetValue("Thermal:EnableDhwWriterCoordination", false))
                {
                    var writerGuard = scope.ServiceProvider.GetRequiredService<DhwWriterGuard>();
                    if (!await writerGuard.IsLegacyWriterAsync(userId))
                    {
                        Console.WriteLine($"[ScheduleUpdateHangfireJob] user={userId} skipped because the joint DHW writer owns the lease");
                        continue;
                    }
                    lease = scope.ServiceProvider.GetRequiredService<DhwWriterLeaseService>();
                    leaseOwner = $"legacy-hangfire:{Guid.NewGuid():N}";
                    if (!await lease.TryAcquireAsync(userId, Prisstyrning.Thermal.Domain.DhwWriter.Legacy, leaseOwner, TimeSpan.FromMinutes(5)))
                    {
                        Console.WriteLine($"[ScheduleUpdateHangfireJob] user={userId} skipped because another DHW write holds the database lease");
                        continue;
                    }
                }
                try
                {
                    var batchRunner = scope.ServiceProvider.GetRequiredService<BatchRunner>();
                    var (generated, schedulePayload, message) = await batchRunner.RunBatchAsync(_cfg, userId, applySchedule: true, persist: true, _scopeFactory);
                    Console.WriteLine($"[ScheduleUpdateHangfireJob] user={userId} generated={generated} message={message}");
                    processedCount++;
                }
                finally
                {
                    if (lease is not null && leaseOwner is not null)
                        await lease.ReleaseAsync(userId, leaseOwner);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ScheduleUpdateHangfireJob] user={userId} error: {ex.Message}");
                errorCount++;
            }
        }

        Console.WriteLine($"[ScheduleUpdateHangfireJob] Completed: processed={processedCount}, skipped=0, errors={errorCount}");
    }
}
