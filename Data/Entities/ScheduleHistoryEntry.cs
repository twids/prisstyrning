namespace Prisstyrning.Data.Entities;

public class ScheduleHistoryEntry
{
    public int Id { get; set; }  // Auto-increment PK
    public string UserId { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public string SchedulePayloadJson { get; set; } = "{}"; // jsonb
}
