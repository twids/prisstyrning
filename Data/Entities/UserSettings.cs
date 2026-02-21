namespace Prisstyrning.Data.Entities;

public class UserSettings
{
    public string UserId { get; set; } = string.Empty;  // PK
    public int ComfortHours { get; set; } = 3;
    public double TurnOffPercentile { get; set; } = 0.9;
    public int MaxComfortGapHours { get; set; } = 28;
    public bool AutoApplySchedule { get; set; } = false;
    public string Zone { get; set; } = "SE3";
    public string SchedulingMode { get; set; } = "Classic";  // "Classic" or "Flexible"
    public int EcoIntervalHours { get; set; } = 24;
    public int EcoFlexibilityHours { get; set; } = 12;
    public int ComfortIntervalDays { get; set; } = 21;
    public int ComfortFlexibilityDays { get; set; } = 7;
    public double ComfortEarlyPercentile { get; set; } = 0.10;
}
