using System.Text.Json.Nodes;
using Prisstyrning.Thermal.Domain;

namespace Prisstyrning.Thermal.HomeAssistant;

public sealed record HomeAssistantState(
    string EntityId,
    string State,
    JsonObject Attributes,
    DateTimeOffset? LastChangedUtc,
    DateTimeOffset? LastUpdatedUtc,
    DateTimeOffset ReceivedAtUtc)
{
    public bool AttributesMalformed { get; init; }
    public string FriendlyName => StringAttribute("friendly_name") is { Length: > 0 } name ? name : EntityId;
    public string? Unit => StringAttribute("unit_of_measurement");

    internal string? StringAttribute(string name) =>
        Attributes[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

public sealed record NormalizedSensorValue(
    double? Value,
    bool? BooleanValue,
    string Unit,
    DataQuality Quality,
    string? Reason);

public interface IHomeAssistantTelemetryClient
{
    Task<bool> TestConnectionAsync(string userId, CancellationToken cancellationToken = default);
    Task<HomeAssistantState?> GetStateAsync(string userId, string entityId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HomeAssistantState>> GetStatesAsync(string userId, CancellationToken cancellationToken = default);
    // A live subscription and its REST snapshot must use the same resolved revision.
    Task<IReadOnlyList<HomeAssistantState>> GetStatesAsync(ResolvedHomeAssistantConnection connection, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HomeAssistantState>> GetHistoryAsync(
        string userId,
        string entityId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default);
}

public interface IHomeAssistantControlClient
{
    Task SetHeatingDeviationAsync(string userId, double deviationC, CancellationToken cancellationToken = default);
}

public interface IHomeAssistantStateCache
{
    HomeAssistantCacheSnapshot ReadAccount(string userId);
    void Invalidate(string userId, DateTimeOffset configurationUpdatedAtUtc, bool telemetryEnabled);
    void RetireRevision(string userId, DateTimeOffset configurationUpdatedAtUtc);
    HomeAssistantCacheSession? BeginSession(string userId, DateTimeOffset configurationUpdatedAtUtc);
    bool BeginSnapshot(HomeAssistantCacheSession session);
    bool PublishSnapshot(HomeAssistantCacheSession session, IEnumerable<HomeAssistantState> states);
    bool ApplyEvent(HomeAssistantCacheSession session, HomeAssistantStateChange change);
    void EndSession(HomeAssistantCacheSession session);
    DateTimeOffset? LastSnapshotUtc { get; }
    DateTimeOffset? LastActivityUtc { get; }
    bool Connected { get; }
    void Replace(IEnumerable<HomeAssistantState> states);
    void Upsert(HomeAssistantState state);
    void MarkConnected();
    void MarkDisconnected();
    bool TryGet(string entityId, out HomeAssistantState? state);
    IReadOnlyList<HomeAssistantState> Snapshot();
    DateTimeOffset? LastSnapshotUtcFor(string userId);
    DateTimeOffset? LastActivityUtcFor(string userId);
    bool IsConnected(string userId);
    void Replace(string userId, IEnumerable<HomeAssistantState> states);
    void Upsert(string userId, HomeAssistantState state);
    void MarkConnected(string userId);
    void MarkDisconnected(string userId);
    bool TryGet(string userId, string entityId, out HomeAssistantState? state);
    IReadOnlyList<HomeAssistantState> Snapshot(string userId);
}

public enum HomeAssistantLivePhase
{
    Disconnected, Disabled, Reloading, Connecting, Synchronizing, Connected, Reconnecting
}

// No endpoint, credential or credential-derived identifier is stored in a cache lease.
public sealed record HomeAssistantCacheSession(string UserId, long Generation, DateTimeOffset ConfigurationUpdatedAtUtc);

public sealed record HomeAssistantStateChange(string EntityId, HomeAssistantState? State, DateTimeOffset? OccurredAtUtc);

public sealed record HomeAssistantCacheSnapshot(
    HomeAssistantLivePhase Phase,
    DateTimeOffset? ConfigurationUpdatedAtUtc,
    DateTimeOffset? LastSnapshotUtc,
    DateTimeOffset? LastActivityUtc,
    IReadOnlyList<HomeAssistantState> States)
{
    public bool Connected => Phase == HomeAssistantLivePhase.Connected;
}
