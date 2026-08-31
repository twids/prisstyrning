using System.Collections.Concurrent;

namespace Prisstyrning.Thermal.HomeAssistant;

public sealed class HomeAssistantStateCache : IHomeAssistantStateCache
{
    private const string CompatibilityAccount = "default";
    private readonly ConcurrentDictionary<string, AccountCache> _accounts = new(StringComparer.Ordinal);

    public DateTimeOffset? LastSnapshotUtc => LastSnapshotUtcFor(CompatibilityAccount);
    public DateTimeOffset? LastActivityUtc => LastActivityUtcFor(CompatibilityAccount);
    public bool Connected => IsConnected(CompatibilityAccount);

    public HomeAssistantCacheSnapshot ReadAccount(string userId)
    {
        var account = Account(userId);
        lock (account.Gate) return new(account.Phase, account.Revision, account.LastSnapshotUtc,
            account.LastActivityUtc, account.States.Values.OrderBy(x => x.EntityId, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    // Called after a committed settings change. All old callbacks immediately lose
    // their lease, even if cancelling their network operation takes a little longer.
    public void Invalidate(string userId, DateTimeOffset configurationUpdatedAtUtc, bool telemetryEnabled)
    {
        var account = Account(userId);
        lock (account.Gate)
        {
            if (account.Revision > configurationUpdatedAtUtc) return;
            account.Generation++;
            account.Revision = configurationUpdatedAtUtc;
            account.TelemetryEnabled = telemetryEnabled;
            Clear(account);
            account.Phase = telemetryEnabled ? HomeAssistantLivePhase.Reloading : HomeAssistantLivePhase.Disabled;
        }
    }

    public HomeAssistantCacheSession? BeginSession(string userId, DateTimeOffset configurationUpdatedAtUtc)
    {
        var account = Account(userId);
        lock (account.Gate)
        {
            if (account.Revision > configurationUpdatedAtUtc ||
                account.Revision == configurationUpdatedAtUtc && !account.TelemetryEnabled) return null;
            account.Generation++;
            account.Revision = configurationUpdatedAtUtc;
            account.TelemetryEnabled = true;
            Clear(account);
            account.Phase = HomeAssistantLivePhase.Connecting;
            return new(userId, account.Generation, configurationUpdatedAtUtc);
        }
    }

    public void RetireRevision(string userId, DateTimeOffset configurationUpdatedAtUtc)
    {
        var account = Account(userId);
        lock (account.Gate)
        {
            // A stale database poll must never retire a newer saved connection.
            if (account.Revision != configurationUpdatedAtUtc) return;
            account.Generation++;
            account.TelemetryEnabled = false;
            Clear(account);
            account.Phase = HomeAssistantLivePhase.Disabled;
        }
    }

    public bool BeginSnapshot(HomeAssistantCacheSession session)
    {
        var account = Account(session.UserId);
        lock (account.Gate)
        {
            if (!IsCurrent(account, session) || account.Phase != HomeAssistantLivePhase.Connecting) return false;
            account.Phase = HomeAssistantLivePhase.Synchronizing;
            return true;
        }
    }

    // Publish once, atomically. Events received while REST was in flight are merged
    // by HA time so neither a late snapshot nor an older queued event rolls data back.
    public bool PublishSnapshot(HomeAssistantCacheSession session, IEnumerable<HomeAssistantState> states)
    {
        var replacement = states.ToDictionary(x => x.EntityId, StringComparer.OrdinalIgnoreCase);
        var account = Account(session.UserId);
        lock (account.Gate)
        {
            if (!IsCurrent(account, session) || account.Phase != HomeAssistantLivePhase.Synchronizing) return false;
            foreach (var change in account.Pending.Values) Apply(replacement, change);
            account.States = replacement;
            account.LastSnapshotUtc = DateTimeOffset.UtcNow;
            account.LastActivityUtc = account.LastSnapshotUtc;
            account.Phase = HomeAssistantLivePhase.Connected;
            return true;
        }
    }

    public bool ApplyEvent(HomeAssistantCacheSession session, HomeAssistantStateChange change)
    {
        var account = Account(session.UserId);
        lock (account.Gate)
        {
            if (!IsCurrent(account, session)) return false;
            if (account.Phase is not (HomeAssistantLivePhase.Synchronizing or HomeAssistantLivePhase.Connected)) return false;
            if (account.Pending.TryGetValue(change.EntityId, out var previous) &&
                IsOlder(change.State?.LastUpdatedUtc ?? change.OccurredAtUtc, previous.State?.LastUpdatedUtc ?? previous.OccurredAtUtc)) return true;
            if (!account.Pending.ContainsKey(change.EntityId) && account.Pending.Count >= 20_000)
                throw new InvalidOperationException("Home Assistant event buffer exceeded its capacity.");
            // Retain tombstone watermarks after publishing; a delayed older update
            // must not resurrect an entity removed while REST was in flight.
            account.Pending[change.EntityId] = change;
            if (account.Phase == HomeAssistantLivePhase.Connected) Apply(account.States, change);
            account.LastActivityUtc = DateTimeOffset.UtcNow;
            return true;
        }
    }

    public void EndSession(HomeAssistantCacheSession session)
    {
        var account = Account(session.UserId);
        lock (account.Gate)
        {
            if (!IsCurrent(account, session)) return;
            account.Generation++;
            account.Pending.Clear();
            account.Phase = HomeAssistantLivePhase.Reconnecting;
            // Retain the last complete snapshot only for diagnostics; Connected is false.
        }
    }

    private static bool IsCurrent(AccountCache account, HomeAssistantCacheSession session) =>
        account.TelemetryEnabled && account.Generation == session.Generation && account.Revision == session.ConfigurationUpdatedAtUtc;

    private static void Apply(Dictionary<string, HomeAssistantState> states, HomeAssistantStateChange change)
    {
        if (states.TryGetValue(change.EntityId, out var current) &&
            IsOlder(change.State?.LastUpdatedUtc ?? change.OccurredAtUtc, current.LastUpdatedUtc)) return;
        if (change.State is null) states.Remove(change.EntityId);
        else states[change.EntityId] = change.State;
    }

    private static bool IsOlder(DateTimeOffset? incoming, DateTimeOffset? current) =>
        incoming is not null && current is not null && incoming < current;

    private static void Clear(AccountCache account)
    {
        account.States.Clear();
        account.Pending.Clear();
        account.LastSnapshotUtc = null;
        account.LastActivityUtc = null;
    }

    // Compatibility helpers for existing consumers/tests. Live ingestion uses the
    // revision-bound operations above, never an unguarded account-id write.
    public void Replace(IEnumerable<HomeAssistantState> states) => Replace(CompatibilityAccount, states);
    public void Replace(string userId, IEnumerable<HomeAssistantState> states)
    {
        var replacement = states.ToDictionary(x => x.EntityId, StringComparer.OrdinalIgnoreCase);
        var account = Account(userId);
        lock (account.Gate)
        {
            account.States = replacement;
            account.LastSnapshotUtc = DateTimeOffset.UtcNow;
            account.LastActivityUtc = account.LastSnapshotUtc;
        }
    }

    public void Upsert(HomeAssistantState state) => Upsert(CompatibilityAccount, state);
    public void Upsert(string userId, HomeAssistantState state)
    {
        var account = Account(userId);
        lock (account.Gate)
        {
            account.States[state.EntityId] = state;
            if (account.LastActivityUtc is null || state.ReceivedAtUtc > account.LastActivityUtc) account.LastActivityUtc = state.ReceivedAtUtc;
        }
    }

    public void MarkConnected() => MarkConnected(CompatibilityAccount);
    public void MarkConnected(string userId)
    {
        var account = Account(userId);
        lock (account.Gate)
        {
            account.Phase = HomeAssistantLivePhase.Connected;
            account.LastActivityUtc = DateTimeOffset.UtcNow;
        }
    }

    public void MarkDisconnected() => MarkDisconnected(CompatibilityAccount);
    public void MarkDisconnected(string userId)
    {
        var account = Account(userId);
        lock (account.Gate) account.Phase = HomeAssistantLivePhase.Disconnected;
    }

    public bool TryGet(string entityId, out HomeAssistantState? state) => TryGet(CompatibilityAccount, entityId, out state);
    public bool TryGet(string userId, string entityId, out HomeAssistantState? state)
    {
        var account = Account(userId);
        lock (account.Gate) return account.States.TryGetValue(entityId, out state);
    }

    public IReadOnlyList<HomeAssistantState> Snapshot() => Snapshot(CompatibilityAccount);
    public IReadOnlyList<HomeAssistantState> Snapshot(string userId) => ReadAccount(userId).States;
    public DateTimeOffset? LastSnapshotUtcFor(string userId)
    {
        var account = Account(userId);
        lock (account.Gate) return account.LastSnapshotUtc;
    }

    public DateTimeOffset? LastActivityUtcFor(string userId)
    {
        var account = Account(userId);
        lock (account.Gate) return account.LastActivityUtc;
    }

    public bool IsConnected(string userId)
    {
        var account = Account(userId);
        lock (account.Gate) return account.Phase == HomeAssistantLivePhase.Connected;
    }

    private AccountCache Account(string userId)
    {
        if (!AdminService.IsValidUserId(userId)) throw new ArgumentException("Invalid account identifier.", nameof(userId));
        return _accounts.GetOrAdd(userId, static _ => new AccountCache());
    }

    private sealed class AccountCache
    {
        public object Gate { get; } = new();
        public Dictionary<string, HomeAssistantState> States { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, HomeAssistantStateChange> Pending { get; } = new(StringComparer.OrdinalIgnoreCase);
        public long Generation { get; set; }
        public DateTimeOffset? Revision { get; set; }
        public bool TelemetryEnabled { get; set; }
        public DateTimeOffset? LastSnapshotUtc { get; set; }
        public DateTimeOffset? LastActivityUtc { get; set; }
        public HomeAssistantLivePhase Phase { get; set; }
    }
}
