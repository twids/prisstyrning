using Microsoft.Extensions.Configuration;
using Hangfire;

namespace Prisstyrning.Jobs;

/// <summary>
/// Hangfire job for initial batch execution at startup
/// </summary>
public class InitialBatchHangfireJob
{
    private readonly IConfiguration _cfg;

    public InitialBatchHangfireJob(IConfiguration cfg)
    {
        _cfg = cfg;
    }

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
        }
    }
}