namespace Prisstyrning.Thermal.HomeAssistant;

public sealed class HomeAssistantTelemetryOptions
{
    public const string SectionName = "HomeAssistant:Telemetry";
    public string BaseUrl { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public string TokenFile { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int StaleAfterMinutes { get; set; } = 10;
}

public sealed class HomeAssistantControlOptions
{
    public const string SectionName = "HomeAssistant:Control";
    public string BaseUrl { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public string TokenFile { get; set; } = string.Empty;
    public string HeatingDeviationEntityId { get; set; } = string.Empty;
}
