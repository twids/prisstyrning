namespace Prisstyrning.Data.Entities;

/// <summary>
/// Immutable audit record for a command sent to Home Assistant/P1P2 or ONECTA.
/// It deliberately contains no credentials or raw authorization metadata.
/// </summary>
public class ThermalControlCommand
{
    public long Id { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string UserId { get; set; } = string.Empty;
    public DateTimeOffset TimestampUtc { get; set; }
    public string CommandType { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public double? RequestedValue { get; set; }
    public double? PreviousValue { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}
