using Prisstyrning.Data.Entities;

namespace Prisstyrning.Thermal.Domain;

public static class ThermalEntityRoles
{
    public const string OutsideTemperature = "outside_temperature";
    public const string LeavingWaterTemperature = "leaving_water_temperature";
    public const string ReturnWaterTemperature = "return_water_temperature";
    public const string Flow = "flow";
    public const string BrineIn = "brine_in";
    public const string BrineOut = "brine_out";
    public const string TankTemperature = "tank_temperature";
    public const string HeatPumpPower = "heat_pump_power";
    public const string PropertyPower = "property_power";
    public const string DhwActive = "dhw_active";
    public const string DefrostActive = "defrost_active";
    public const string BackupHeaterActive = "backup_heater_active";
    public const string SpotPrice = "spot_price";
    public const string HeatingDeviation = "heating_deviation";
    public const string WeatherForecast = "weather_forecast";
    public const string WindSpeed = "wind_speed";
    public const string SolarIrradiance = "solar_irradiance";

    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        OutsideTemperature, LeavingWaterTemperature, ReturnWaterTemperature, Flow,
        BrineIn, BrineOut, TankTemperature, HeatPumpPower, PropertyPower, DhwActive,
        DefrostActive, BackupHeaterActive, SpotPrice, HeatingDeviation,
        WeatherForecast, WindSpeed, SolarIrradiance
    };
}

public sealed record ThermalConfigDto(
    ThermalSiteConfig Site,
    IReadOnlyList<ThermalRoomConfig> Rooms,
    IReadOnlyList<ThermalEntityConfig> Entities);

public sealed record ThermalStatusDto(
    ControlMode Mode,
    DhwWriter DhwWriter,
    DateTimeOffset? LastTelemetryUtc,
    DataQuality OverallDataQuality,
    bool EmhassAvailable,
    DateTimeOffset? PlanCreatedUtc,
    int? PlanAgeMinutes,
    double CurrentLwtDeviationC,
    string? FallbackReason,
    DateTimeOffset? NextControlEventUtc,
    bool ManualOverride,
    string? DataQualityReason = null,
    bool EmhassEnabled = false);

public sealed record ReadinessCheck(
    string Key,
    string Requirement,
    bool Passed,
    string Action,
    string Severity = "ActionRequired")
{
    public bool BlocksMode(ControlMode target) => !Passed &&
        !(target == ControlMode.Shadow && Severity == "Warning" &&
          Key is "telemetry-fresh" or "telemetry-quality");
}

public sealed record DecisionReason(
    string MainReason,
    decimal? Price,
    double? ComfortMarginC,
    double ModelConfidence,
    string? Alternative);

public sealed record ThermalModeRequest(ControlMode Mode, bool Confirmed = false);
public sealed record WeatherTestRequest(string EntityId);

public sealed record ThermalOverrideRequest(
    DateTimeOffset? UntilUtc,
    double? LwtDeviationC,
    string Reason);

public sealed record HomeAssistantHistoryImportRequest(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc);

public sealed record ThermalEntityStateDto(
    string EntityId,
    string FriendlyName,
    string State,
    string? Unit,
    DateTimeOffset? LastUpdatedUtc,
    DateTimeOffset ReceivedAtUtc,
    DataQuality Quality,
    string? QualityReason,
    IReadOnlyList<string>? CompatibleUnits = null,
    DateTimeOffset? CheckedAtUtc = null,
    DateTimeOffset? ValidUntilUtc = null,
    DateTimeOffset? LastReportedUtc = null,
    IReadOnlyDictionary<string, double>? NormalizedValues = null);

public static class ThermalEnumParser
{
    public static ControlMode ControlModeOrLegacy(string? value) =>
        Enum.TryParse<ControlMode>(value, true, out var result) ? result : ControlMode.Legacy;

    public static DhwWriter DhwWriterOrLegacy(string? value) =>
        Enum.TryParse<DhwWriter>(value, true, out var result) ? result : DhwWriter.Legacy;
}
