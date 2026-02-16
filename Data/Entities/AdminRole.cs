namespace Prisstyrning.Data.Entities;

public class AdminRole
{
    public string UserId { get; set; } = string.Empty;  // PK
    public bool IsAdmin { get; set; }
    public bool HasHangfireAccess { get; set; }
}
