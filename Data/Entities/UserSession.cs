namespace Prisstyrning.Data.Entities;

public sealed class UserSession
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public string? UserAgentHash { get; set; }
}
