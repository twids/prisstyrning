namespace Prisstyrning.Data.Entities;

public class ThermalModelVersion
{
    public long Id { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string UserId { get; set; } = string.Empty;
    public string ModelType { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset TrainingFromUtc { get; set; }
    public DateTimeOffset TrainingToUtc { get; set; }
    public bool IsActive { get; set; }
    public string ParametersJson { get; set; } = "{}";
    public string MetricsJson { get; set; } = "{}";
}
