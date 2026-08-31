using System.Text.Json;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Domain;

namespace Prisstyrning.Thermal.Data;

internal sealed record ThermalStatusQualityResult(DataQuality Quality, string Reason);

/// <summary>
/// Read-only assessment of the latest saved snapshot for the status API.
/// This is not a readiness check and never changes sensor recovery or control decisions.
/// </summary>
internal static class ThermalStatusQuality
{
    internal static ThermalStatusQualityResult Assess(
        ThermalTelemetrySample? sample,
        IEnumerable<ThermalRoomConfig> rooms,
        IEnumerable<ThermalEntityConfig> entities,
        DateTimeOffset now,
        DateTimeOffset? configurationUpdatedUtc = null)
    {
        var enabledRooms = rooms.Where(x => x.Enabled).ToArray();
        var enabledEntities = entities.Where(x => x.Enabled).ToArray();
        var total = enabledRooms.Length + enabledEntities.Length;
        if (total == 0)
            return new(DataQuality.Unavailable, "Inga aktiverade rum eller givare är konfigurerade.");
        if (sample is null)
            return new(DataQuality.Unavailable, "Ingen femminuterstelemetri har samlats in ännu.");
        if (sample.TimestampUtc > now)
            return new(DataQuality.Invalid, "Insamlingens tidsstämpel ligger i framtiden. Kontrollera klockorna.");
        if (configurationUpdatedUtc > sample.TimestampUtc)
            return new(DataQuality.Unavailable, "Konfigurationen är nyare än senaste sparade insamlingen. Vänta på en ny femminutersinsamling.");

        using var metadata = ParseObject(sample.QualityJson);
        if (metadata is null)
            return new(DataQuality.Unavailable, "Insamlingens kvalitetsstatus saknas eller kan inte tolkas.");
        var root = metadata.RootElement;
        if (TryProperty(root, "source", out var source))
        {
            var imported = source.ValueKind == JsonValueKind.String &&
                           string.Equals(source.GetString(), "HomeAssistantHistoryImport", StringComparison.OrdinalIgnoreCase);
            return new(DataQuality.Unavailable, imported
                ? "Importerad historik bekräftar inte givarnas aktuella status. Vänta på en ny liveinsamling."
                : "Insamlingens källa kan inte bekräftas som liveinsamling.");
        }
        if (now - sample.TimestampUtc > TimeSpan.FromMinutes(10))
            return new(DataQuality.Stale, "Senaste sparade insamlingen är äldre än tio minuter. Aktuell datakvalitet kan inte bekräftas.");

        var qualities = new List<DataQuality>(total);
        using var temperatures = enabledRooms.Length == 0 ? null : ParseObject(sample.RoomTemperaturesJson);
        foreach (var room in enabledRooms)
        {
            var quality = ReadAssessment(Property(Property(root, "rooms"), room.EntityId));
            if (quality == DataQuality.Valid)
            {
                var value = Property(temperatures?.RootElement ?? default, room.EntityId);
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var temperature) ||
                    !double.IsFinite(temperature) || !double.IsFinite(room.MinimumValidC) || !double.IsFinite(room.MaximumValidC) ||
                    room.MinimumValidC >= room.MaximumValidC || temperature < room.MinimumValidC || temperature > room.MaximumValidC)
                    quality = DataQuality.Invalid;
            }
            qualities.Add(quality);
        }
        foreach (var entity in enabledEntities)
        {
            var forecast = entity.Role.Equals(ThermalEntityRoles.WeatherForecast, StringComparison.OrdinalIgnoreCase);
            var quality = ReadAssessment(forecast ? Property(root, "forecast") : Property(Property(root, "entities"), entity.Role), !forecast);
            if (quality == DataQuality.Valid && !HasRecordedValue(sample, entity)) quality = DataQuality.Invalid;
            qualities.Add(quality);
        }

        var valid = qualities.Count(x => x == DataQuality.Valid);
        var invalid = qualities.Count(x => x == DataQuality.Invalid);
        var stale = qualities.Count(x => x == DataQuality.Stale);
        var unavailable = qualities.Count(x => x == DataQuality.Unavailable);
        // Explicit precedence, not the enum's numeric order. Disabled/unmapped inputs
        // and an unmapped forecast never participate. A valid cold room stays Valid.
        var overall = invalid > 0 ? DataQuality.Invalid : unavailable > 0 ? DataQuality.Unavailable
            : stale > 0 ? DataQuality.Stale : DataQuality.Valid;
        return new(overall, valid == total
            ? $"Alla {total} aktiverade datakällor är giltiga i senaste insamlingen."
            : $"{valid}/{total} aktiverade datakällor är giltiga. {invalid} ogiltiga eller exkluderade, {stale} gamla och {unavailable} saknade eller med okänd kvalitet.");
    }

    private static DataQuality ReadAssessment(JsonElement assessment, bool requiresExclusion = true)
    {
        if (requiresExclusion)
        {
            var excluded = Property(assessment, "excluded");
            if (excluded.ValueKind == JsonValueKind.True) return DataQuality.Invalid;
            if (excluded.ValueKind != JsonValueKind.False) return DataQuality.Unavailable;
        }
        var quality = Property(assessment, "quality");
        if (quality.ValueKind == JsonValueKind.Number && quality.TryGetInt32(out var numeric) &&
            Enum.IsDefined(typeof(DataQuality), numeric)) return (DataQuality)numeric;
        if (quality.ValueKind == JsonValueKind.String)
            foreach (var candidate in Enum.GetValues<DataQuality>())
                if (candidate.ToString().Equals(quality.GetString(), StringComparison.OrdinalIgnoreCase)) return candidate;
        return DataQuality.Unavailable;
    }

    private static bool HasRecordedValue(ThermalTelemetrySample sample, ThermalEntityConfig entity)
    {
        switch (entity.Role.ToLowerInvariant())
        {
            case ThermalEntityRoles.DhwActive: return sample.DhwActive.HasValue;
            case ThermalEntityRoles.DefrostActive: return sample.DefrostActive.HasValue;
            case ThermalEntityRoles.BackupHeaterActive: return sample.BackupHeaterActive.HasValue;
            // Deviation feedback is validated by the collector but has no dedicated
            // numeric snapshot column. Do not substitute the commanded LWT value.
            case ThermalEntityRoles.HeatingDeviation: return true;
            case ThermalEntityRoles.WeatherForecast:
                try
                {
                    using var forecast = JsonDocument.Parse(sample.OutsideTemperatureForecastJson);
                    return forecast.RootElement.ValueKind == JsonValueKind.Array && forecast.RootElement.GetArrayLength() > 0;
                }
                catch (JsonException) { return false; }
        }
        double? value = entity.Role.ToLowerInvariant() switch
        {
            ThermalEntityRoles.OutsideTemperature => sample.OutsideTemperatureC,
            ThermalEntityRoles.LeavingWaterTemperature => sample.LeavingWaterTemperatureC,
            ThermalEntityRoles.ReturnWaterTemperature => sample.ReturnWaterTemperatureC,
            ThermalEntityRoles.Flow => sample.FlowLitresPerMinute,
            ThermalEntityRoles.BrineIn => sample.BrineInC,
            ThermalEntityRoles.BrineOut => sample.BrineOutC,
            ThermalEntityRoles.TankTemperature => sample.TankTemperatureC,
            ThermalEntityRoles.HeatPumpPower => sample.HeatPumpPowerKw,
            ThermalEntityRoles.PropertyPower => sample.PropertyPowerKw,
            ThermalEntityRoles.SpotPrice => (double?)sample.SpotPriceSekPerKwh,
            ThermalEntityRoles.WindSpeed => sample.WindSpeedMps,
            ThermalEntityRoles.SolarIrradiance => sample.SolarIrradianceWm2,
            _ => null
        };
        return value is { } number && double.IsFinite(number) &&
               (entity.MinimumValid is null || double.IsFinite(entity.MinimumValid.Value) && number >= entity.MinimumValid) &&
               (entity.MaximumValid is null || double.IsFinite(entity.MaximumValid.Value) && number <= entity.MaximumValid);
    }

    private static JsonDocument? ParseObject(string? json)
    {
        try
        {
            var document = JsonDocument.Parse(json ?? "null");
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
            document.Dispose();
        }
        catch (JsonException) { }
        return null;
    }

    private static JsonElement Property(JsonElement parent, string name) =>
        TryProperty(parent, name, out var value) ? value : default;

    private static bool TryProperty(JsonElement parent, string name, out JsonElement value)
    {
        if (parent.ValueKind == JsonValueKind.Object)
            foreach (var property in parent.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
        value = default;
        return false;
    }
}
