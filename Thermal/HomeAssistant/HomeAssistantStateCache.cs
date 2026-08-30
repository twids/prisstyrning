using System.Collections.Concurrent;

namespace Prisstyrning.Thermal.HomeAssistant;

public sealed class HomeAssistantStateCache : IHomeAssistantStateCache
{
    private const string CompatibilityAccount = "default";
    private readonly ConcurrentDictionary<string, AccountCache> _accounts = new(StringComparer.Ordinal);

    public DateTimeOffset? LastSnapshotUtc => LastSnapshotUtcFor(CompatibilityAccount);
    public DateTimeOffset? LastActivityUtc => LastActivityUtcFor(CompatibilityAccount);
    public bool Connected => IsConnected(CompatibilityAccount);

    public void Replace(IEnumerable<HomeAssistantState> states)
    {
        Replace(CompatibilityAccount, states);
    }

    public void Replace(string userId, IEnumerable<HomeAssistantState> states)
    {
        var account = Account(userId);
        var replacement = states.ToDictionary(x => x.EntityId, StringComparer.OrdinalIgnoreCase);
        account.States.Clear();
        foreach (var pair in replacement)
        {
            account.States[pair.Key] = pair.Value;
        }

        lock (account.StatusGate)
        {
            account.LastSnapshotUtc = DateTimeOffset.UtcNow;
            account.LastActivityUtc = account.LastSnapshotUtc;
        }
    }

    public void Upsert(HomeAssistantState state)
    {
        Upsert(CompatibilityAccount, state);
    }

    public void Upsert(string userId, HomeAssistantState state)
    {
        var account = Account(userId);
        account.States[state.EntityId] = state;
        lock (account.StatusGate)
        {
            if (account.LastActivityUtc is null || state.ReceivedAtUtc > account.LastActivityUtc)
                account.LastActivityUtc = state.ReceivedAtUtc;
        }
    }

    public void MarkConnected()
    {
        MarkConnected(CompatibilityAccount);
    }

    public void MarkConnected(string userId)
    {
        var account = Account(userId);
        lock (account.StatusGate)
        {
            account.Connected = true;
            account.LastActivityUtc = DateTimeOffset.UtcNow;
        }
    }

    public void MarkDisconnected()
    {
        MarkDisconnected(CompatibilityAccount);
    }

    public void MarkDisconnected(string userId)
    {
        var account = Account(userId);
        lock (account.StatusGate) account.Connected = false;
    }

    public bool TryGet(string entityId, out HomeAssistantState? state) =>
        TryGet(CompatibilityAccount, entityId, out state);

    public bool TryGet(string userId, string entityId, out HomeAssistantState? state) =>
        Account(userId).States.TryGetValue(entityId, out state);

    public IReadOnlyList<HomeAssistantState> Snapshot() =>
        Snapshot(CompatibilityAccount);

    public IReadOnlyList<HomeAssistantState> Snapshot(string userId) =>
        Account(userId).States.Values.OrderBy(x => x.EntityId, StringComparer.OrdinalIgnoreCase).ToArray();

    public DateTimeOffset? LastSnapshotUtcFor(string userId)
    {
        var account = Account(userId);
        lock (account.StatusGate) return account.LastSnapshotUtc;
    }

    public DateTimeOffset? LastActivityUtcFor(string userId)
    {
        var account = Account(userId);
        lock (account.StatusGate) return account.LastActivityUtc;
    }

    public bool IsConnected(string userId)
    {
        var account = Account(userId);
        lock (account.StatusGate) return account.Connected;
    }

    private AccountCache Account(string userId)
    {
        if (!AdminService.IsValidUserId(userId)) throw new ArgumentException("Invalid account identifier.", nameof(userId));
        return _accounts.GetOrAdd(userId, static _ => new AccountCache());
    }

    private sealed class AccountCache
    {
        public ConcurrentDictionary<string, HomeAssistantState> States { get; } = new(StringComparer.OrdinalIgnoreCase);
        public object StatusGate { get; } = new();
        public DateTimeOffset? LastSnapshotUtc { get; set; }
        public DateTimeOffset? LastActivityUtc { get; set; }
        public bool Connected { get; set; }
    }
}
