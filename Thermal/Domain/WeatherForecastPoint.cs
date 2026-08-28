namespace Prisstyrning.Thermal.Domain;

public sealed record WeatherForecastPoint(
    DateTimeOffset TimestampUtc,
    double TemperatureC,
    double? WindSpeedMps,
    double? SolarIrradianceWm2);
