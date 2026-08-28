using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Domain;
using Prisstyrning.Thermal.HomeAssistant;

namespace Prisstyrning.Thermal.Control;

internal sealed class ThermalModeService
{
    private readonly PrisstyrningDbContext _db;
    private readonly ThermalReadinessService _readiness;
    private readonly IHomeAssistantControlClient _controlClient;
    private readonly BatchRunner _batchRunner;
    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly string _heatingDeviationEntityId;
    private readonly DhwWriterLeaseService _dhwLease;

    public ThermalModeService(
        PrisstyrningDbContext db,
        ThermalReadinessService readiness,
        IHomeAssistantControlClient controlClient,
        BatchRunner batchRunner,
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        IOptions<HomeAssistantControlOptions> controlOptions,
        DhwWriterLeaseService dhwLease)
    {
        _db = db;
        _readiness = readiness;
        _controlClient = controlClient;
        _batchRunner = batchRunner;
        _configuration = configuration;
        _scopeFactory = scopeFactory;
        _heatingDeviationEntityId = controlOptions.Value.HeatingDeviationEntityId;
        _dhwLease = dhwLease;
    }

    public async Task<(bool Success, string Message)> ChangeModeAsync(
        string userId,
        ThermalModeRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!request.Confirmed) return (false, "Lägesbytet måste bekräftas i den guidade checklistan.");
        if (request.Mode is ControlMode.LwtActive or ControlMode.FullActive &&
            !_configuration.GetValue("Thermal:AllowLwtActive", false))
            return (false, "Aktiv LWT-styrning är spärrad i driftsättningskonfigurationen.");
        if (request.Mode == ControlMode.FullActive &&
            !_configuration.GetValue("Thermal:AllowFullActive", false))
            return (false, "FullActive är spärrat i driftsättningskonfigurationen.");
        if (request.Mode == ControlMode.FullActive &&
            !_configuration.GetValue("Thermal:EnableDhwWriterCoordination", false))
            return (false, "FullActive kräver aktiverad DHW-writerkoordinering.");
        var site = await _db.ThermalSiteConfigs.SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        if (site is null)
        {
            site = new ThermalSiteConfig { UserId = userId };
            _db.ThermalSiteConfigs.Add(site);
        }
        var current = ThermalEnumParser.ControlModeOrLegacy(site.ControlMode);
        if (current == request.Mode) return (true, "Driftläget är redan aktivt.");
        if (!IsAllowedTransition(current, request.Mode)) return (false, $"Otillåtet lägesbyte från {current} till {request.Mode}.");

        if (request.Mode is not ControlMode.Legacy)
        {
            var checks = await _readiness.EvaluateAsync(userId, request.Mode, cancellationToken);
            if (checks.Any(x => !x.Passed)) return (false, "Alla readiness-kontroller måste vara godkända före lägesbytet.");
        }

        var controlState = await _db.ThermalControlStates.SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken)
            ?? new ThermalControlState { UserId = userId };
        if (_db.Entry(controlState).State == EntityState.Detached) _db.ThermalControlStates.Add(controlState);

        if (current is ControlMode.LwtActive or ControlMode.FullActive && request.Mode is ControlMode.Legacy or ControlMode.Shadow)
        {
            var previousDeviation = controlState.CurrentDeviationC;
            try
            {
                await _controlClient.SetHeatingDeviationAsync(userId, 0, cancellationToken);
                controlState.CurrentDeviationC = 0;
                controlState.LastDeviationWriteUtc = DateTimeOffset.UtcNow;
                controlState.FallbackReason = string.Empty;
                _db.ThermalControlCommands.Add(Command(
                    userId,
                    "LwtDeviation",
                    _heatingDeviationEntityId,
                    0,
                    previousDeviation,
                    "Accepted",
                    "Säker rollback till Daikins grundkurva."));
            }
            catch (Exception exception)
            {
                controlState.FallbackReason = "LWT-avvikelsen kunde inte nollställas automatiskt.";
                _db.ThermalControlCommands.Add(Command(
                    userId,
                    "LwtDeviation",
                    _heatingDeviationEntityId,
                    0,
                    previousDeviation,
                    "Rejected",
                    "Säker rollback till Daikins grundkurva.",
                    exception.Message));
                _db.ThermalEvents.Add(Event(userId, "ActionRequired", "Fallback", controlState.FallbackReason, exception.Message));
                await _db.SaveChangesAsync(cancellationToken);
                return (false, "Rollback avbröts eftersom LWT-avvikelsen inte kunde nollställas. Nollställ den manuellt och försök igen.");
            }
        }

        var currentWriter = ThermalEnumParser.DhwWriterOrLegacy(site.DhwWriter);
        var targetWriter = request.Mode == ControlMode.FullActive ? DhwWriter.Joint : DhwWriter.Legacy;
        if (currentWriter != targetWriter)
        {
            if (!await _dhwLease.TrySwitchWriterAsync(userId, currentWriter, targetWriter, cancellationToken))
            {
                await _db.SaveChangesAsync(cancellationToken);
                return (false, "DHW-writern kunde inte flyttas eftersom en schemaskrivning fortfarande håller databassleasen. Försök igen när den är klar.");
            }
            await _db.Entry(site).ReloadAsync(cancellationToken);
        }

        site.ControlMode = request.Mode.ToString();
        site.DhwWriter = targetWriter.ToString();
        site.UpdatedAtUtc = DateTimeOffset.UtcNow;
        if (request.Mode == ControlMode.FullActive)
        {
            // The active worker acquires its own renewable instance lease on the next evaluation.
            controlState.LeaseOwner = null;
            controlState.LeaseExpiresUtc = null;
        }
        else
        {
            controlState.LeaseOwner = null;
            controlState.LeaseExpiresUtc = null;
        }

        _db.ThermalEvents.Add(Event(userId, "Information", "ControlMode", $"Driftläget ändrades från {current} till {request.Mode}.", null));
        await _db.SaveChangesAsync(cancellationToken);

        if (current == ControlMode.FullActive && request.Mode != ControlMode.FullActive)
        {
            try
            {
                var (generated, _, message) = await _batchRunner.RunBatchAsync(
                    _configuration,
                    userId,
                    applySchedule: true,
                    persist: true,
                    _scopeFactory);
                if (!generated) throw new InvalidOperationException(message);
                _db.ThermalControlCommands.Add(Command(userId, "DhwSchedule", "ONECTA", null, null, "Accepted", "Legacy-schemat återställdes vid rollback."));
                _db.ThermalEvents.Add(Event(userId, "Information", "DhwWriter", "Legacy-schemat återställdes direkt i ONECTA.", message));
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                _db.ThermalControlCommands.Add(Command(userId, "DhwSchedule", "ONECTA", null, null, "Rejected", "Legacy-schemat skulle återställas vid rollback.", exception.Message));
                _db.ThermalEvents.Add(Event(userId, "ActionRequired", "DhwWriter", "Legacy äger åter DHW, men ONECTA-schemat kunde inte återställas direkt. Kör legacy-jobbet eller återställ schemat manuellt.", exception.Message));
                await _db.SaveChangesAsync(cancellationToken);
                return (true, "Driftläget ändrades, men ONECTA-schemat måste återställas manuellt eller av nästa legacy-jobb.");
            }
        }

        return (true, "Driftläget har ändrats.");
    }

    public async Task SetOverrideAsync(string userId, ThermalOverrideRequest request, CancellationToken cancellationToken = default)
    {
        if (request.UntilUtc is null || request.UntilUtc <= DateTimeOffset.UtcNow || request.UntilUtc > DateTimeOffset.UtcNow.AddDays(7))
            throw new ArgumentException("Override måste ha en sluttid inom sju dagar.");
        if (request.LwtDeviationC is < -3 or > 3) throw new ArgumentException("Manuell LWT-avvikelse måste vara inom ±3 °C.");
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new ArgumentException("Ange varför override behövs.");

        var state = await _db.ThermalControlStates.SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken)
            ?? new ThermalControlState { UserId = userId };
        if (_db.Entry(state).State == EntityState.Detached) _db.ThermalControlStates.Add(state);
        state.ManualOverrideUntilUtc = request.UntilUtc;
        state.ManualOverrideDeviationC = request.LwtDeviationC;
        state.ManualOverrideReason = request.Reason.Trim();
        _db.ThermalEvents.Add(Event(userId, "Warning", "ManualOverride", $"Manuell override är aktiv till {request.UntilUtc:O}.", request.Reason));
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task ClearOverrideAsync(string userId, CancellationToken cancellationToken = default)
    {
        var state = await _db.ThermalControlStates.SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        if (state is null) return;
        state.ManualOverrideUntilUtc = null;
        state.ManualOverrideDeviationC = null;
        state.ManualOverrideReason = string.Empty;
        _db.ThermalEvents.Add(Event(userId, "Information", "ManualOverride", "Manuell override avslutades.", null));
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static bool IsAllowedTransition(ControlMode current, ControlMode target) =>
        target == ControlMode.Legacy ||
        (current, target) is
            (ControlMode.Legacy, ControlMode.Shadow) or
            (ControlMode.Shadow, ControlMode.LwtActive) or
            (ControlMode.LwtActive, ControlMode.Shadow) or
            (ControlMode.LwtActive, ControlMode.FullActive) or
            (ControlMode.FullActive, ControlMode.LwtActive);

    private static ThermalEvent Event(string userId, string severity, string category, string message, string? detail) => new()
    {
        UserId = userId,
        TimestampUtc = DateTimeOffset.UtcNow,
        Severity = severity,
        Category = category,
        Message = message,
        DetailsJson = detail is null ? "{}" : System.Text.Json.JsonSerializer.Serialize(new { detail })
    };

    private static ThermalControlCommand Command(
        string userId,
        string type,
        string target,
        double? requested,
        double? previous,
        string outcome,
        string reason,
        string? error = null) => new()
    {
        UserId = userId,
        TimestampUtc = DateTimeOffset.UtcNow,
        CommandType = type,
        Target = string.IsNullOrWhiteSpace(target) ? "not-configured" : target,
        RequestedValue = requested,
        PreviousValue = previous,
        Outcome = outcome,
        Reason = reason,
        Error = string.IsNullOrWhiteSpace(error) ? string.Empty : error.Length <= 1000 ? error : error[..1000]
    };
}
