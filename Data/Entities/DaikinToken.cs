namespace Prisstyrning.Data.Entities;

public class DaikinToken
{
    public string UserId { get; set; } = string.Empty;  // PK
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAtUtc { get; set; }
}
