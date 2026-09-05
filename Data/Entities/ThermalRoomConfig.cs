namespace Prisstyrning.Data.Entities;

public class ThermalRoomConfig
{
    public long Id { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string UserId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public double TargetOffsetC { get; set; }
    public double Weight { get; set; } = 1.0;
    public bool IsCritical { get; set; }
    public bool Enabled { get; set; } = true;
    public int? MaximumReportAgeMinutes { get; set; }
    public double MinimumValidC { get; set; } = 5.0;
    public double MaximumValidC { get; set; } = 35.0;
    public double MaximumRateCPerHour { get; set; } = 3.0;
}
