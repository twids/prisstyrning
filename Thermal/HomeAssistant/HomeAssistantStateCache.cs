using System.Collections.Concurrent;

namespace Prisstyrning.Thermal.HomeAssistant;

public sealed class HomeAssistantStateCache : IHomeAssistantStateCache
{
    private readonly ConcurrentDictionary<string, HomeAssistantState> _states =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _statusGate = new();
    private DateTimeOffset? _lastSnapshotUtc;
    private DateTimeOffset? _lastActivityUtc;
    private bool _connected;

    public DateTimeOffset? LastSnapshotUtc { get { lock (_statusGate) return _lastSnapshotUtc; } }
    public DateTimeOffset? LastActivityUtc { get { lock (_statusGate) return _lastActivityUtc; } }
    public bool Connected { get { lock (_statusGate) return _connected; } }

    public void Replace(IEnumerable<HomeAssistantState> states)
    {
        var replacement = states.ToDictionary(x => x.EntityId, StringComparer.OrdinalIgnoreCase);
        _states.Clear();
        foreach (var pair in replacement)
        {
            _states[pair.Key] = pair.Value;
        }

        lock (_statusGate)
        {
            _lastSnapshotUtc = DateTimeOffset.UtcNow;
            _lastActivityUtc = _lastSnapshotUtc;
        }
    }

    public void Upsert(HomeAssistantState state)
    {
        _states[state.EntityId] = state;
        lock (_statusGate)
        {
            if (_lastActivityUtc is null || state.ReceivedAtUtc > _lastActivityUtc)
                _lastActivityUtc = state.ReceivedAtUtc;
        }
    }

    public void MarkConnected()
    {
        lock (_statusGate)
        {
            _connected = true;
            _lastActivityUtc = DateTimeOffset.UtcNow;
        }
    }

    public void MarkDisconnected()
    {
        lock (_statusGate) _connected = false;
    }

    public bool TryGet(string entityId, out HomeAssistantState? state) =>
        _states.TryGetValue(entityId, out state);

    public IReadOnlyList<HomeAssistantState> Snapshot() =>
        _states.Values.OrderBy(x => x.EntityId, StringComparer.OrdinalIgnoreCase).ToArray();
}
