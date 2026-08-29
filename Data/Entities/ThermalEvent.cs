namespace Prisstyrning.Data.Entities;

public class ThermalEvent
{
    public long Id { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string UserId { get; set; } = string.Empty;
    public DateTimeOffset TimestampUtc { get; set; }
    public string Severity { get; set; } = "Information";
    public string Category { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string DetailsJson { get; set; } = "{}";
}
