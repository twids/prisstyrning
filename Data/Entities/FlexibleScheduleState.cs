namespace Prisstyrning.Data.Entities;

public class FlexibleScheduleState
{
    public string UserId { get; set; } = string.Empty;  // PK
    /// <summary>
    /// The UTC time of the most recently scheduled eco hour.
    /// NOTE: This is set when the schedule is composed and persisted,
    /// regardless of whether the Daikin apply step succeeds. The scheduled
    /// hour (e.g., 14:00) is recorded to compute the next eco window correctly.
    /// </summary>
    public DateTimeOffset? LastEcoRunUtc { get; set; }
    /// <summary>
    /// The UTC time when the last comfort run was marked as completed.
    /// Unlike LastEcoRunUtc, this is only set when the scheduled comfort hour
    /// has actually passed (state == "already_ran").
    /// </summary>
    public DateTimeOffset? LastComfortRunUtc { get; set; }
    /// <summary>
    /// The next comfort run that has been scheduled but not yet executed.
    /// Used for re-optimization: if new price data reveals a cheaper hour,
    /// the scheduled comfort run can be postponed to that cheaper hour
    /// as long as we remain within the comfort window.
    /// </summary>
    public DateTimeOffset? NextScheduledComfortUtc { get; set; }
    /// <summary>
    /// The next eco run that has been scheduled but not yet executed.
    /// Mirrors NextScheduledComfortUtc: prevents premature deletion of a pending eco
    /// run when the batch re-runs before the scheduled hour arrives.
    /// LastEcoRunUtc is only advanced once this time passes (state == "already_ran").
    /// </summary>
    public DateTimeOffset? NextScheduledEcoUtc { get; set; }
}
