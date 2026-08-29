namespace Prisstyrning.Data.Entities;

public class ThermalControlState
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string UserId { get; set; } = string.Empty;
    public double CurrentDeviationC { get; set; }
    public DateTimeOffset? LastDeviationWriteUtc { get; set; }
    public Guid? CurrentPlanId { get; set; }
    public DateTimeOffset? LastHeartbeatUtc { get; set; }
    public string FallbackReason { get; set; } = string.Empty;
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresUtc { get; set; }
    public double PiIntegral { get; set; }
    public DateTimeOffset? ManualOverrideUntilUtc { get; set; }
    public double? ManualOverrideDeviationC { get; set; }
    public string ManualOverrideReason { get; set; } = string.Empty;
}
