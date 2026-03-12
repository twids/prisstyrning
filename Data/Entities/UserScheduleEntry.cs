namespace Prisstyrning.Data.Entities;

public class UserScheduleEntry
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public DateTimeOffset ScheduledTimeUtc { get; set; }
    public string State { get; set; } = "comfort";
    public bool CountsAsLegionella { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
