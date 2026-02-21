namespace Prisstyrning.Data.Entities;

public class FlexibleScheduleState
{
    public string UserId { get; set; } = string.Empty;  // PK
    public DateTimeOffset? LastEcoRunUtc { get; set; }
    public DateTimeOffset? LastComfortRunUtc { get; set; }
    /// <summary>
    /// The next comfort run that has been scheduled but not yet executed.
    /// Used for re-optimization: if new price data reveals a cheaper hour,
    /// the scheduled comfort run can be postponed to that cheaper hour
    /// as long as we remain within the comfort window.
    /// </summary>
    public DateTimeOffset? NextScheduledComfortUtc { get; set; }
}
