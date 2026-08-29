namespace Prisstyrning.Data.Entities;

public class ThermalPlan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [System.Text.Json.Serialization.JsonIgnore]
    public string UserId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ValidFromUtc { get; set; }
    public DateTimeOffset ValidUntilUtc { get; set; }
    public string Status { get; set; } = "Shadow";
    public bool IsShadow { get; set; } = true;
    public int SolverDurationMs { get; set; }
    public decimal? ObjectiveCost { get; set; }
    public double Confidence { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string InputSnapshotJson { get; set; } = "{}";
    public List<ThermalPlanStep> Steps { get; set; } = [];
}

public class ThermalPlanStep
{
    public long Id { get; set; }
    public Guid ThermalPlanId { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public ThermalPlan? ThermalPlan { get; set; }
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset EndUtc { get; set; }
    public double DesiredHeatOutputKw { get; set; }
    public double DesiredLwtDeviationC { get; set; }
    public bool DhwReserved { get; set; }
    public string DhwMode { get; set; } = string.Empty;
    public decimal IncrementalCost { get; set; }
    public double Confidence { get; set; }
    public string ExpectedRoomsJson { get; set; } = "{}";
    public string DecisionReasonJson { get; set; } = "{}";
}
