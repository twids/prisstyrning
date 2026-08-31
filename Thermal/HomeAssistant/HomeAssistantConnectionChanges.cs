using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Prisstyrning.Thermal.HomeAssistant;

/// <summary>Coalesced wake-up, not a queue of credentials or account data. Polling also recovers missed notifications.</summary>
public sealed class HomeAssistantConnectionChanges
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _settingsGates = new(StringComparer.Ordinal);
    private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false
    });

    public void Notify() => _changes.Writer.TryWrite(true);

    internal async Task<IDisposable> LockSettingsAsync(string userId, CancellationToken cancellationToken)
    {
        var gate = _settingsGates.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new SettingsLease(gate);
    }

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { await _changes.Reader.ReadAsync(timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
    }

    private sealed class SettingsLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
