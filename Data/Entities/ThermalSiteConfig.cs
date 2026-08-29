namespace Prisstyrning.Data.Entities;

public class ThermalSiteConfig
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string UserId { get; set; } = string.Empty;
    public string ControlMode { get; set; } = "Legacy";
    public string DhwWriter { get; set; } = "Legacy";
    [System.Text.Json.Serialization.JsonIgnore]
    public string? DhwLeaseOwner { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTimeOffset? DhwLeaseExpiresUtc { get; set; }
    public double BaseRoomTargetC { get; set; } = 21.5;
    public double LowerComfortBandC { get; set; } = 0.5;
    public double UpperComfortBandC { get; set; } = 0.7;
    public double ActiveDeviationLimitC { get; set; } = 1.0;
    public bool TariffEnabled { get; set; }
    public bool HeatPumpPowerSignVerified { get; set; }
    public bool WeatherCurveVerified { get; set; }
    public bool ComfortSetpointConfirmed { get; set; }
    public double ComfortSetpointC { get; set; } = 60.0;
    public int ComfortIntervalDays { get; set; } = 21;
    public int ComfortFlexibilityDays { get; set; } = 7;
    public string TimeZone { get; set; } = "Europe/Stockholm";
    public string VariableCostComponentsJson { get; set; } = "{}";
    public string TariffDefinitionJson { get; set; } = "{}";
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
