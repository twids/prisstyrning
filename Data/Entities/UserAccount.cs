namespace Prisstyrning.Data.Entities;

/// <summary>
/// Local account backed by one verified Daikin identity. The raw external
/// subject is deliberately not persisted; only its deterministic hash is kept.
/// </summary>
public sealed class UserAccount
{
    public string UserId { get; set; } = string.Empty;
    public string DaikinSubjectHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset LastLoginUtc { get; set; }
    public bool Disabled { get; set; }
}
