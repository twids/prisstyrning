namespace Prisstyrning.Data.Entities;

/// <summary>One primary ONECTA installation owned by one local account.</summary>
public sealed class DaikinInstallation
{
    public string UserId { get; set; } = string.Empty;
    public string SiteId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string DhwManagementPointEmbeddedId { get; set; } = string.Empty;
    public string? HeatingManagementPointEmbeddedId { get; set; }
    public string ScheduleMode { get; set; } = "heating";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
