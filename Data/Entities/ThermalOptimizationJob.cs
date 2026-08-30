namespace Prisstyrning.Data.Entities;

public sealed class ThermalOptimizationJob
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string? PendingKey { get; set; }
    public string Status { get; set; } = "Pending";
    public int Priority { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string RequestJson { get; set; } = "{}";
    public string? ResultJson { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public Guid ConcurrencyStamp { get; set; }
}
