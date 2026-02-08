using Microsoft.Extensions.Configuration;
using Hangfire;

namespace Prisstyrning.Jobs;

/// <summary>
/// Hangfire job that checks if it's 14:00 and runs batch operations
/// </summary>
public class DailyPriceHangfireJob
{
    private readonly IConfiguration _cfg;
    private readonly BatchRunner _batchRunner;

    public DailyPriceHangfireJob(IConfiguration cfg, BatchRunner batchRunner)
    {
        _cfg = cfg;
        _batchRunner = batchRunner;
    }

    [DisableConcurrentExecution(600)] // Prevent overlapping executions with 10min timeout
    public async Task ExecuteAsync()
    {
        try
        {
            var now = DateTimeOffset.Now;
            if (now.Hour == 14 && now.Minute < 10) // första 10 min efter 14
            {
                Console.WriteLine($"[DailyPriceHangfireJob] Executing daily batch at {now:yyyy-MM-dd HH:mm}");
                await _batchRunner.RunBatchAsync(_cfg, null, applySchedule: false, persist: true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DailyPriceHangfireJob] error: {ex.Message}");
        }
    }
}