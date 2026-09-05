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
        public DateTimeOffset? LastRecoveryMeasurementUtc { get; set; }
        public DateTimeOffset? LastInvalidBucketUtc { get; set; }
        public DateTimeOffset? ConfigurationRevisionUtc { get; set; }
    }

    private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);

    public SensorAssessment Assess(
        string entityId,
        HomeAssistantState? rawState,
        NormalizedSensorValue normalized,
        SensorValidationRules rules,
        DateTimeOffset nowUtc,
        DateTimeOffset? configurationRevisionUtc = null,
        DateTimeOffset? historyImportedAtUtc = null)
    {
        var state = _states.GetOrAdd(entityId, _ => new State());
        lock (state)
        {
            if (configurationRevisionUtc < state.ConfigurationRevisionUtc)
                return new(DataQuality.Unavailable, null, null, "Insamlingen tillhör en äldre konfiguration.", false, false, false, null, null);
            if (configurationRevisionUtc != state.ConfigurationRevisionUtc)
            {
                state.ConsecutiveInvalid = state.ConsecutiveValid = 0;
                state.Excluded = false;
                state.LastValidValue = null;
                state.LastValidUtc = state.LastRecoveryMeasurementUtc = state.LastInvalidBucketUtc = null;
                state.ConfigurationRevisionUtc = configurationRevisionUtc;
            }
            var quality = normalized.Quality;
            var reason = normalized.Reason;
            var sourceTime = historyImportedAtUtc is null
                ? rawState?.LastReportedUtc ?? rawState?.LastUpdatedUtc
                : rawState?.LastUpdatedUtc;
            if (quality == DataQuality.Valid &&
                (normalized.Value is { } number && !double.IsFinite(number) ||
                 normalized.Value.HasValue == normalized.BooleanValue.HasValue))
            {
                quality = DataQuality.Invalid;
                reason = "Givaren saknar ett entydigt, ändligt mätvärde eller av/på-värde.";
            }
            if (quality == DataQuality.Valid && normalized.Value is { } value)
            {
                if (rules.Minimum is { } minimum && (!double.IsFinite(minimum) || value < minimum) ||
                    rules.Maximum is { } maximum && (!double.IsFinite(maximum) || value > maximum))
                {
                    quality = DataQuality.Invalid;
                    reason = "Värdet ligger utanför tillåtet intervall.";
                }
                else if (rules.MaximumRatePerHour is { } maxRate &&
                         state.LastValidValue is { } previous &&
                         state.LastValidUtc is { } previousUtc &&
                         sourceTime is { } measuredAt &&
                         (measuredAt < previousUtc || measuredAt == previousUtc && value != previous ||
                          measuredAt > previousUtc && Math.Abs(value - previous) / (measuredAt - previousUtc).TotalHours > maxRate))
                {
                    quality = DataQuality.Invalid;
                    reason = "Värdet ändras snabbare än tillåtet.";
                }
            }

            if (quality == DataQuality.Valid)
                (quality, reason) = SensorTimestampValidator.Assess(rawState, nowUtc, rules.StaleAfter, historyImportedAtUtc);

            var wasExcluded = state.Excluded;
            if (quality == DataQuality.Valid)
            {
                state.ConsecutiveInvalid = 0;
                state.LastInvalidBucketUtc = null;
                if (state.LastRecoveryMeasurementUtc is null || sourceTime > state.LastRecoveryMeasurementUtc)
                {
                    state.ConsecutiveValid = Math.Min(3, state.ConsecutiveValid + 1);
                    state.LastRecoveryMeasurementUtc = sourceTime;
                }
                if (!state.Excluded || state.ConsecutiveValid >= 3)
                {
                    state.Excluded = false;
                    state.LastValidValue = normalized.Value;
                    state.LastValidUtc = sourceTime;
                }
            }
            else if (quality == DataQuality.Invalid)
            {
                state.ConsecutiveValid = 0;
                var bucket = nowUtc.ToUniversalTime().Ticks / TimeSpan.TicksPerMinute / 5;
                var bucketUtc = new DateTimeOffset(bucket * TimeSpan.TicksPerMinute * 5, TimeSpan.Zero);
                if (state.LastInvalidBucketUtc != bucketUtc)
                {
                    state.ConsecutiveInvalid = Math.Min(3, state.ConsecutiveInvalid + 1);
                    state.LastInvalidBucketUtc = bucketUtc;
                }
                if (state.ConsecutiveInvalid >= 3) state.Excluded = true;
            }
            else
            {
                // Missing reports are not evidence of three erroneous measurements.
                // Keep an already excluded sensor excluded until real recovery.
                state.ConsecutiveValid = 0;
                state.ConsecutiveInvalid = 0;
                state.LastInvalidBucketUtc = null;
            }

            return new SensorAssessment(
                quality,
                normalized.Value is { } finite && double.IsFinite(finite) ? finite : null,
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
