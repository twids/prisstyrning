namespace Prisstyrning.Thermal.Domain;

public enum ControlMode
{
    Legacy,
    Shadow,
    LwtActive,
    FullActive
}

public enum DhwWriter
{
    Legacy,
    Joint
}

public enum DataQuality
{
    Valid,
    Stale,
    Invalid,
    Unavailable
}

public enum ThermalEventSeverity
{
    Information,
    Warning,
    ActionRequired
}
