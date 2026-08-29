namespace Prisstyrning.Data.Entities;

public class ThermalHourlyAggregate
{
    public long Id { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string UserId { get; set; } = string.Empty;
    public DateTimeOffset HourUtc { get; set; }
    public double? AverageOutsideTemperatureC { get; set; }
    public double? AverageRoomTemperatureC { get; set; }
    public double? AverageLwtC { get; set; }
    public double? AverageCop { get; set; }
    public double HeatPumpEnergyKwh { get; set; }
    public double HeatOutputKwh { get; set; }
    public string RoomsJson { get; set; } = "{}";
}
