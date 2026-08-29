using System.Collections.Concurrent;
using Prisstyrning.Thermal.Domain;

namespace Prisstyrning.Thermal.HomeAssistant;

public sealed record SensorValidationRules(
    double? Minimum,
    double? Maximum,
    double? MaximumRatePerHour,
    TimeSpan StaleAfter);

public sealed record SensorAssessment(
    DataQuality Quality,
    double? Value,
    bool? BooleanValue,
    string? Reason,
    bool Excluded,
    bool BecameExcluded,
    bool BecameRecovered,
    double? LastValidValue,
    DateTimeOffset? LastValidUtc);

public sealed class SensorQualityTracker
{
    private sealed class State
    {
        public int ConsecutiveInvalid { get; set; }
        public int ConsecutiveValid { get; set; }
        public bool Excluded { get; set; }
        public double? LastValidValue { get; set; }
        public DateTimeOffset? LastValidUtc { get; set; }
    }

    private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);

    public SensorAssessment Assess(
        string entityId,
        HomeAssistantState? rawState,
        NormalizedSensorValue normalized,
        SensorValidationRules rules,
        DateTimeOffset nowUtc)
    {
        var state = _states.GetOrAdd(entityId, _ => new State());
        lock (state)
        {
            var quality = normalized.Quality;
            var reason = normalized.Reason;
            if (quality == DataQuality.Valid &&
                rawState?.LastUpdatedUtc is { } lastUpdated &&
                nowUtc - lastUpdated > rules.StaleAfter)
            {
                quality = DataQuality.Stale;
                reason = $"Senast uppdaterad för {(nowUtc - lastUpdated).TotalMinutes:0} minuter sedan.";
            }

            if (quality == DataQuality.Valid && normalized.Value is { } value)
            {
                if (rules.Minimum is { } minimum && value < minimum ||
                    rules.Maximum is { } maximum && value > maximum)
                {
                    quality = DataQuality.Invalid;
                    reason = "Värdet ligger utanför tillåtet intervall.";
                }
                else if (rules.MaximumRatePerHour is { } maxRate &&
                         state.LastValidValue is { } previous &&
                         state.LastValidUtc is { } previousUtc &&
                         nowUtc > previousUtc &&
                         Math.Abs(value - previous) / (nowUtc - previousUtc).TotalHours > maxRate)
                {
                    quality = DataQuality.Invalid;
                    reason = "Värdet ändras snabbare än tillåtet.";
                }
            }

            var wasExcluded = state.Excluded;
            if (quality == DataQuality.Valid)
            {
                state.ConsecutiveInvalid = 0;
                state.ConsecutiveValid++;
                if (!state.Excluded || state.ConsecutiveValid >= 3)
                {
                    state.Excluded = false;
                    state.LastValidValue = normalized.Value;
                    state.LastValidUtc = nowUtc;
                }
            }
            else
            {
                state.ConsecutiveValid = 0;
                state.ConsecutiveInvalid++;
                if (state.ConsecutiveInvalid >= 3) state.Excluded = true;
            }

            return new SensorAssessment(
                quality,
                normalized.Value,
                normalized.BooleanValue,
                reason,
                state.Excluded,
                !wasExcluded && state.Excluded,
                wasExcluded && !state.Excluded,
                state.LastValidValue,
                state.LastValidUtc);
        }
    }
}
