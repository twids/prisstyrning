namespace Prisstyrning.Data.Entities;

public class ThermalEntityConfig
{
    public long Id { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string UserId { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string ExpectedUnit { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public double? MinimumValid { get; set; }
    public double? MaximumValid { get; set; }
    public double? MaximumRatePerHour { get; set; }
}
