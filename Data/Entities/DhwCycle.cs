namespace Prisstyrning.Data.Entities;

public class DhwCycle
{
    public long Id { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string UserId { get; set; } = string.Empty;
    public string Kind { get; set; } = "Eco";
    public string Source { get; set; } = "Legacy";
    public string Status { get; set; } = "Planned";
    public DateTimeOffset PlannedStartUtc { get; set; }
    public DateTimeOffset? ScheduleAcceptedUtc { get; set; }
    public DateTimeOffset? ActualStartUtc { get; set; }
    public DateTimeOffset? TargetReachedUtc { get; set; }
    public DateTimeOffset? ActualEndUtc { get; set; }
    public double? StartTemperatureC { get; set; }
    public double TargetTemperatureC { get; set; }
    public int PredictedDurationMinutes { get; set; }
    public int ReservedDurationMinutes { get; set; }
    public decimal? PredictedCost { get; set; }
    public decimal? ActualCost { get; set; }
    public bool BackupHeaterUsed { get; set; }
    public string PowerProfileJson { get; set; } = "[]";
    public int TargetVerificationCount { get; set; }
    public DateTimeOffset? EstimatedCompletionUtc { get; set; }
    public DateTimeOffset? LastVerificationSampleUtc { get; set; }
}
