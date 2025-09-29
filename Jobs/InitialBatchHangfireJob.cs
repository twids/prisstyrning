using Microsoft.Extensions.Configuration;
using Hangfire;
using System.ComponentModel;

namespace Prisstyrning.Jobs;

/// <summary>
/// Hangfire job for initial batch execution that can be triggered on-demand.
/// This job fetches/prerenders data without auto-applying schedules.
/// Can be triggered manually from Hangfire dashboard or programmatically.
/// </summary>
public class InitialBatchHangfireJob
{
    private readonly IConfiguration _cfg;

    public InitialBatchHangfireJob(IConfiguration cfg)
    {
        _cfg = cfg;
    }

    [DisplayName("Initial Batch - Fetch and Prerender Data")]
    public async Task ExecuteAsync()
    {
        try
        {
            Console.WriteLine("[InitialBatchHangfireJob] Running initial batch (persist + prerender, no auto apply)");
            await BatchRunner.RunBatchAsync(_cfg, null, applySchedule: false, persist: true);
            Console.WriteLine("[InitialBatchHangfireJob] Initial batch completed");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[InitialBatchHangfireJob] error: {ex.Message}");
            throw; // Re-throw to mark job as failed in Hangfire
        }
    }
}