namespace Prisstyrning.Thermal.Control;

public sealed class WriterLeaseIdentity
{
    public string Owner { get; } = $"orchestrator-{Environment.MachineName}-{Guid.NewGuid():N}";
}
