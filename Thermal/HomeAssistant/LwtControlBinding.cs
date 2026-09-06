using System.Globalization;
using System.Text.Json.Nodes;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Domain;

namespace Prisstyrning.Thermal.HomeAssistant;

// The account names the exact actuator; callers cannot supply a domain, service or payload.
internal static class LwtControlBinding
{
    internal static bool IsEntity(string? id, string domain) => id is { Length: > 0 and <= 255 } &&
        id.StartsWith(domain + ".", StringComparison.Ordinal) && id.Length > domain.Length + 1 &&
        id[(domain.Length + 1)..].All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    internal static bool IsActuator(string? id) => IsEntity(id, "number") || IsEntity(id, "climate");

    internal static string? FeedbackEntity(string actuator, IEnumerable<ThermalEntityConfig> entities)
    {
        var mappings = entities.Where(x => x.Enabled && x.Role == ThermalEntityRoles.HeatingDeviation).ToArray();
        // Preserve previously supported number configurations. Climate always needs explicit numeric feedback.
        if (mappings.Length == 0) return IsEntity(actuator, "number") ? actuator : null;
        if (mappings.Length != 1 || mappings[0].ExpectedUnit != "°C") return null;
        var id = mappings[0].EntityId;
        return IsEntity(id, "sensor") || IsEntity(id, "number") ? id : null;
    }

    internal static bool Recent(HomeAssistantState? state, DateTimeOffset now) => state is not null &&
        !state.AttributesMalformed && state.ReceivedAtUtc <= now.AddSeconds(30) &&
        now - state.ReceivedAtUtc <= TimeSpan.FromMinutes(10) && state.State is not ("unknown" or "unavailable");

    internal static bool NumericFeedback(HomeAssistantState? state, DateTimeOffset now) => Recent(state, now) &&
        state!.Unit == "°C" && double.TryParse(state.State, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
        double.IsFinite(value) && value is >= -10 and <= 10;

    internal static double? Step(string actuator, HomeAssistantState? state, DateTimeOffset now, double limit)
    {
        if (!double.IsFinite(limit) || limit is < 0 or > 3 || !IsActuator(actuator) || !Recent(state, now) || state!.EntityId != actuator) return null;
        if (IsEntity(actuator, "number")) return .5; // Existing number contract retained.
        if (state.StringAttribute("temperature_unit") is { } unit && unit != "°C") return null;
        var step = Number(state, "target_temp_step");
        var min = Number(state, "min_temp");
        var max = Number(state, "max_temp");
        // This is an offset control, not an ordinary room or water setpoint. Zero must be representable.
        if (step is not (>= .5 and <= 3) || min is null || max is null || min >= 0 || max <= 0 || min > -limit || max < limit ||
            Math.Abs(min.Value / step.Value - Math.Round(min.Value / step.Value)) > 1e-6 ||
            Number(state, "temperature") is null) return null;
        return step;
    }

    private static double? Number(HomeAssistantState state, string key) =>
        state.Attributes[key] is JsonValue value &&
        double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number : null;
}
