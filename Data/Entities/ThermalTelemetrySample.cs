namespace Prisstyrning.Data.Entities;

public class ThermalTelemetrySample
{
    public long Id { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string UserId { get; set; } = string.Empty;
    public DateTimeOffset TimestampUtc { get; set; }
    public double? OutsideTemperatureC { get; set; }
    public string OutsideTemperatureForecastJson { get; set; } = "[]";
    public double? WindSpeedMps { get; set; }
    public double? SolarIrradianceWm2 { get; set; }
    public double? LeavingWaterTemperatureC { get; set; }
    public double? ReturnWaterTemperatureC { get; set; }
    public double? FlowLitresPerMinute { get; set; }
    public double? BrineInC { get; set; }
    public double? BrineOutC { get; set; }
    public double? TankTemperatureC { get; set; }
    public double? HeatPumpPowerKw { get; set; }
    public double? PropertyPowerKw { get; set; }
    public decimal? SpotPriceSekPerKwh { get; set; }
    public double? HeatOutputKw { get; set; }
    public double? Cop { get; set; }
    public bool? DhwActive { get; set; }
    public bool? DefrostActive { get; set; }
    public bool? BackupHeaterActive { get; set; }
    public string RoomTemperaturesJson { get; set; } = "{}";
    public string QualityJson { get; set; } = "{}";
}
