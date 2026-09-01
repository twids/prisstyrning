using System.Text.Json;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Data;
using static Prisstyrning.Thermal.Data.ThermalEvidenceJson;

namespace Prisstyrning.Thermal.Optimization;

public sealed record ThermalModelValidation(
    bool Passed, string Status, string Reason, DateTimeOffset CheckedAtUtc,
    double? TwoHourMaeC = null, double? DayMaeC = null, double? CopMae = null,
    int? TwoHourValidationWindows = null, int? DayValidationWindows = null);

/// <summary>Read-only evidence assessment shared by readiness, training and the model UI.</summary>
internal static class ThermalModelEvidence
{
    internal static ThermalModelValidation AssessCurrent(
        ThermalModelVersion? model,
        ThermalModelSourceValidation? source,
        DateTimeOffset now)
    {
        var assessment = Assess(model, now);
        if (!assessment.Passed) return assessment;
        if (source is null || source.CheckedAtUtc == default || source.CheckedAtUtc > now ||
            now - source.CheckedAtUtc > TimeSpan.FromMinutes(5))
            return new(false, "Unproven", "Modellens historiska källunderlag har inte omvaliderats nyligen. Hämta underlaget igen eller träna om modellen.", now);
        if (source.Passed && source.Status == "Current") return assessment;
        var reason = string.IsNullOrWhiteSpace(source.Reason)
            ? "Modellens historiska källunderlag kan inte verifieras. Hämta underlaget igen eller träna om modellen."
            : source.Reason;
        return new(false, !source.Passed && source.Status == "Changed" ? "SourceChanged" : "Unproven", reason, now);
    }

    internal static ThermalModelValidation Assess(ThermalModelVersion? model, DateTimeOffset now)
    {
        ThermalModelValidation Block(string status, string reason) => new(false, status, reason, now);
        if (model is null) return Block("Missing", "Ingen modellversion finns ännu. Samla och validera mätdata.");
        if (model.TrainingFromUtc == default || model.TrainingToUtc <= model.TrainingFromUtc ||
            model.CreatedAtUtc < model.TrainingToUtc || model.CreatedAtUtc > now)
            return Block("Invalid", "Modellens träningsperiod eller tidsstämpel är ogiltig. Träna en ny version med verifierade tider.");
        using var metrics = Object(model.MetricsJson);
        using var parameters = Object(model.ParametersJson);
        if (metrics is null || parameters is null)
            return Block("Invalid", "Modellens mått eller parametrar kan inte tolkas säkert. Träna en ny version.");
        var values = metrics.RootElement;
        if (Count(values, "validationVersion") != 1)
            return Block("Unproven", "Den äldre versionen saknar verifierbart valideringsunderlag. Träna om modellen; en aktivmarkering räcker inte.");
        var training = Count(values, "trainingSamples");
        var validation = Count(values, "validationSamples");
        var capacity = Math.Floor((model.TrainingToUtc - model.TrainingFromUtc).TotalMinutes / 5) + 1;
        if (training is null || validation is null || (long)training + validation > capacity)
            return Block("Invalid", "Antalet mätpunkter stämmer inte med träningsperioden. Träna en ny modellversion.");
        var provenance = ThermalModelProvenance.Read(model);
        if (provenance is null)
            return Block("Unproven", "Modellversionen saknar ett verifierbart fingeravtryck för exakt träningsurval och kodversion. Träna om modellen.");
        if (provenance.TrainingSamples != training || provenance.ValidationSamples != validation ||
            provenance.ObservationCount > capacity)
            return Block("Invalid", "Modellens källbevis stämmer inte med träningsperioden eller valideringsmåtten. Träna en ny version.");

        if (model.ModelType == "2R2C")
        {
            if (!PhysicalThermalParameters(parameters.RootElement))
                return Block("Invalid", "2R2C-parametrarna är ogiltiga eller saknar fysisk mening. Träna en ny version.");
            var twoHour = Number(values, "twoHourMaeC");
            var day = Number(values, "dayMaeC");
            var twoHourWindows = Count(values, "twoHourValidationWindows");
            var dayWindows = Count(values, "dayValidationWindows");
            if (training < 200 || validation < 289 || twoHourWindows is not > 0 || dayWindows is not > 0 ||
                twoHourWindows > validation - 24 || dayWindows > validation - 288)
                return Block("Insufficient", "Sammanhängande undanhållna mätningar för hela två timmar och 24 timmar saknas. Samla mer data utan luckor och träna om.");
            if (twoHour is not >= 0 || day is not >= 0)
                return Block("Invalid", "Modellens prognosfel måste vara ändliga, icke-negativa tal. Träna en ny version.");
            var passed = twoHour <= .3 && day <= .6;
            return new(passed, passed ? "Validated" : "ThresholdExceeded", passed
                ? "Hela tvåtimmars- och dygnsfönster på undanhållen data klarar MAE-kraven. Källurval och träningskod är versionsbundna; detta godkänner inte aktiv styrning."
                : "Prognosfelet överskrider 0,30 °C för två timmar eller 0,60 °C för ett dygn. Fortsätt i Shadow och granska modellen.",
                now, twoHour, day, TwoHourValidationWindows: twoHourWindows, DayValidationWindows: dayWindows);
        }
        if (model.ModelType == "COP")
        {
            if (!PhysicalCopParameters(parameters.RootElement))
                return Block("Invalid", "COP-parametrarna är ogiltiga. Kontrollera effektmätningen och träna om modellen.");
            var mae = Number(values, "mae");
            if (training < 80 || validation < 20)
                return Block("Insufficient", "COP-modellen saknar tillräckligt med separata tränings- och valideringspunkter.");
            if (mae is not >= 0)
                return Block("Invalid", "COP-felet måste vara ett ändligt, icke-negativt tal. Träna en ny version.");
            return new(mae <= .5, mae <= .5 ? "Validated" : "ThresholdExceeded", mae <= .5
                ? "Separata COP-valideringspunkter klarar MAE-kravet och källurvalet är versionsbundet. Effektmätning och övriga aktiveringskrav måste också verifieras."
                : "COP-modellens fel överstiger 0,50. Kontrollera mätdata och träna om modellen.", now, CopMae: mae);
        }
        return Block("Invalid", "Modelltypen stöds inte för styrning.");
    }

    private static bool PhysicalThermalParameters(JsonElement root) =>
        Number(root, "airCapacityKwhPerC") is > .2 and < 20 && Number(root, "massCapacityKwhPerC") is > 2 and < 500 &&
        Number(root, "envelopeConductanceKwPerC") is > .02 and < 5 && Number(root, "massCouplingKwPerC") is > .02 and < 10 &&
        Number(root, "heatingGain") is > .2 and <= 1.2 && Number(root, "baseCurveInterceptC") is not null &&
        Number(root, "baseCurveSlope") is not null && Number(root, "windLossCoefficientKwPerCPerMps") is >= 0 and < 1 &&
        Number(root, "solarGainKwPerWm2") is >= 0 and < .1 && PhysicalRoomAdjustments(root);

    private static bool PhysicalRoomAdjustments(JsonElement root)
    {
        var rooms = Property(root, "roomAdjustments");
        if (rooms.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return true;
        return rooms.ValueKind == JsonValueKind.Object && rooms.EnumerateObject().All(room =>
            Number(room.Value, "offsetC") is >= -100 and <= 100 &&
            Number(room.Value, "inertiaHours") is >= .08 and <= 72 &&
            Number(room.Value, "disturbanceStdDevC") is >= 0 and <= 100 &&
            Count(room.Value, "samples") is >= 12);
    }

    private static bool PhysicalCopParameters(JsonElement root) =>
        Number(root, "intercept") is >= 1.2 and <= 8 && Number(root, "brineCoefficient") is >= 0 and <= .3 &&
        Number(root, "lwtCoefficient") is >= -.3 and <= 0 && Number(root, "loadCoefficient") is >= -.2 and <= .2;
}
