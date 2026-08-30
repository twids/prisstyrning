namespace Prisstyrning.Data.Entities;

/// <summary>Account-scoped Home Assistant connection and P1P2 allowlist.</summary>
public sealed class HomeAssistantConnection
{
    public string UserId { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string TelemetryTokenCiphertext { get; set; } = string.Empty;
    public string? ControlTokenCiphertext { get; set; }
    public int EncryptionVersion { get; set; } = 1;
    public bool TelemetryEnabled { get; set; }
    public bool ControlEnabled { get; set; }
    public string HeatingDeviationEntityId { get; set; } = string.Empty;
    public int StaleAfterMinutes { get; set; } = 10;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
