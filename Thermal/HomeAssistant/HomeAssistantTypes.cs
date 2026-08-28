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
    public string FriendlyName => Attributes["friendly_name"]?.GetValue<string>() ?? EntityId;
    public string? Unit => Attributes["unit_of_measurement"]?.GetValue<string>();
}

public sealed record NormalizedSensorValue(
    double? Value,
    bool? BooleanValue,
    string Unit,
    DataQuality Quality,
    string? Reason);

public interface IHomeAssistantTelemetryClient
{
    Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
    Task<HomeAssistantState?> GetStateAsync(string entityId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HomeAssistantState>> GetStatesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HomeAssistantState>> GetHistoryAsync(
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
    DateTimeOffset? LastSnapshotUtc { get; }
    DateTimeOffset? LastActivityUtc { get; }
    bool Connected { get; }
    void Replace(IEnumerable<HomeAssistantState> states);
    void Upsert(HomeAssistantState state);
    void MarkConnected();
    void MarkDisconnected();
    bool TryGet(string entityId, out HomeAssistantState? state);
    IReadOnlyList<HomeAssistantState> Snapshot();
}
