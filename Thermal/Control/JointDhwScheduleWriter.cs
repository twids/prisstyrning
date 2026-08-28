using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Domain;

namespace Prisstyrning.Thermal.Control;

internal sealed class JointDhwScheduleWriter
{
    private readonly PrisstyrningDbContext _db;
    private readonly BatchRunner _batchRunner;
    private readonly IConfiguration _configuration;
    private readonly DhwWriterLeaseService _lease;
    private readonly WriterLeaseIdentity _leaseIdentity;

    public JointDhwScheduleWriter(
        PrisstyrningDbContext db,
        BatchRunner batchRunner,
        IConfiguration configuration,
        DhwWriterLeaseService lease,
        WriterLeaseIdentity leaseIdentity)
    {
        _db = db;
        _batchRunner = batchRunner;
        _configuration = configuration;
        _lease = lease;
        _leaseIdentity = leaseIdentity;
    }

    public async Task<bool> ApplyAsync(long cycleId, CancellationToken cancellationToken = default)
    {
        if (!_configuration.GetValue("Thermal:AllowFullActive", false) ||
            !_configuration.GetValue("Thermal:EnableDhwWriterCoordination", false))
            throw new InvalidOperationException("Joint ONECTA writes are disabled by the deployment kill switches.");

        var cycle = await _db.DhwCycles.SingleAsync(x => x.Id == cycleId, cancellationToken);
        var site = await _db.ThermalSiteConfigs.AsNoTracking().SingleAsync(x => x.UserId == cycle.UserId, cancellationToken);
        if (ThermalEnumParser.ControlModeOrLegacy(site.ControlMode) != ControlMode.FullActive ||
            ThermalEnumParser.DhwWriterOrLegacy(site.DhwWriter) != DhwWriter.Joint)
            throw new InvalidOperationException("Joint ONECTA writes require FullActive and the Joint DHW writer.");

        var leaseOwner = $"joint:{_leaseIdentity.Owner}:{cycle.Id}";
        if (!await _lease.TryAcquireAsync(cycle.UserId, DhwWriter.Joint, leaseOwner, TimeSpan.FromMinutes(5), cancellationToken))
        {
            _db.ThermalControlCommands.Add(Command(cycle, "Skipped", "En annan DHW-skrivning håller databassleasen."));
            await _db.SaveChangesAsync(cancellationToken);
            return false;
        }

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(site.TimeZone);
        var payload = ScheduleAlgorithm.ComposeJointDhwSchedule(cycle.PlannedStartUtc, cycle.Kind, timeZone)
            .ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        try
        {
            var applied = await _batchRunner.ApplyScheduleToDaikinAsync(_configuration, payload, cycle.UserId);
            _db.ThermalControlCommands.Add(Command(cycle, applied ? "Accepted" : "Rejected", applied ? string.Empty : "ONECTA accepterade inte schemat."));
            if (applied)
            {
                cycle.ScheduleAcceptedUtc = DateTimeOffset.UtcNow;
                cycle.Status = "Accepted";
                _db.ThermalEvents.Add(new ThermalEvent
                {
                    UserId = cycle.UserId,
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Severity = "Information",
                    Category = "DhwSchedule",
                    Message = $"ONECTA accepterade {cycle.Kind.ToLowerInvariant()}-start {cycle.PlannedStartUtc:O}."
                });
            }
            else
            {
                _db.ThermalEvents.Add(new ThermalEvent
                {
                    UserId = cycle.UserId,
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Severity = "ActionRequired",
                    Category = "DhwSchedule",
                    Message = "ONECTA accepterade inte det gemensamma DHW-schemat."
                });
            }
            await _db.SaveChangesAsync(cancellationToken);
            return applied;
        }
        catch (Exception exception)
        {
            _db.ThermalControlCommands.Add(Command(cycle, "Rejected", exception.Message));
            _db.ThermalEvents.Add(new ThermalEvent
            {
                UserId = cycle.UserId,
                TimestampUtc = DateTimeOffset.UtcNow,
                Severity = "ActionRequired",
                Category = "DhwSchedule",
                Message = "ONECTA-skrivningen för DHW misslyckades.",
                DetailsJson = JsonSerializer.Serialize(new { error = Safe(exception.Message, 1000) })
            });
            await _db.SaveChangesAsync(cancellationToken);
            throw;
        }
        finally
        {
            await _lease.ReleaseAsync(cycle.UserId, leaseOwner, CancellationToken.None);
        }
    }

    private static ThermalControlCommand Command(DhwCycle cycle, string outcome, string error) => new()
    {
        UserId = cycle.UserId,
        TimestampUtc = DateTimeOffset.UtcNow,
        CommandType = "DhwSchedule",
        Target = "ONECTA",
        RequestedValue = null,
        PreviousValue = null,
        Outcome = outcome,
        Reason = $"{cycle.Kind}-start {cycle.PlannedStartUtc:O}",
        Error = Safe(error, 1000)
    };

    private static string Safe(string? value, int maximumLength) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Length <= maximumLength ? value : value[..maximumLength];
}
