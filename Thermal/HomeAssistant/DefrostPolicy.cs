using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Domain;

namespace Prisstyrning.Thermal.HomeAssistant;

/// <summary>Explicit installation declaration, never an inference from a missing sensor.</summary>
internal static class DefrostPolicy
{
    internal const string Reason = "Ej tillämpligt: anläggningen är uttryckligen konfigurerad utan avfrostning. Inte en sensormätning.";
    internal static bool IsDeclared(ThermalEntityConfig config) => config.Enabled && config.NotApplicable &&
        config.Role == ThermalEntityRoles.DefrostActive && config.EntityId == "" && config.ExpectedUnit == "bool";

    internal static SensorAssessment Assessment() => new(DataQuality.Valid, 0, false, Reason, false, false, false, null, null);
}
