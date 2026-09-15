namespace Prisstyrning.Data.Entities;

/// <summary>Server-owned commissioning evidence; never accepted from config DTOs.</summary>
public sealed class ThermalStartupState
{
    public string UserId { get; set; } = string.Empty;
    public string Phase { get; set; } = "NotCommissioned";
    public bool ConservativeEnabled { get; set; }
    public string ConfigurationFingerprint { get; set; } = string.Empty;
    public DateTimeOffset? CommissionedAtUtc { get; set; }
    public DateTimeOffset? LastEvaluationUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
