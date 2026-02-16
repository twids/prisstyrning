namespace Prisstyrning.Data.Entities;

public class UserSettings
{
    public string UserId { get; set; } = string.Empty;  // PK
    public int ComfortHours { get; set; } = 3;
    public double TurnOffPercentile { get; set; } = 0.9;
    public int MaxComfortGapHours { get; set; } = 28;
    public bool AutoApplySchedule { get; set; } = false;
    public string Zone { get; set; } = "SE3";
}
