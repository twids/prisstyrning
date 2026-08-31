using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Prisstyrning.Data;
using Prisstyrning.Thermal.Control;
using Prisstyrning.Thermal.Data;
using Prisstyrning.Thermal.Domain;
using Prisstyrning.Thermal.HomeAssistant;
using Prisstyrning.Thermal.Optimization;
using Prisstyrning.Security;

namespace Prisstyrning.Thermal;

public static class ThermalApiEndpoints
{
    public static IEndpointRouteBuilder MapThermalApi(this IEndpointRouteBuilder app)
    {
        var thermal = app.MapThermalStatusApi();

        thermal.MapGet("/config", async (
            HttpContext context,
            ThermalDataService data,
            CancellationToken cancellationToken) =>
        {
            return Results.Ok(await data.GetConfigAsync(UserId(context), cancellationToken));
        });

        thermal.MapPut("/config", async (
            HttpContext context,
            ThermalConfigDto config,
            ThermalDataService data,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await data.UpdateConfigAsync(UserId(context), config, cancellationToken));
            }
            catch (ArgumentException exception)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["config"] = [exception.Message] });
            }
        });

        thermal.MapGet("/readiness", async (
            HttpContext context,
            ControlMode? targetMode,
            ThermalReadinessService readiness,
            CancellationToken cancellationToken) =>
        {
            var mode = targetMode ?? ControlMode.FullActive;
            var checks = await readiness.EvaluateAsync(UserId(context), mode, cancellationToken);
            return Results.Ok(new { targetMode = mode, ready = checks.All(x => x.Passed), checks });
        });

        thermal.MapGet("/plan", async (HttpContext context, PrisstyrningDbContext db, CancellationToken cancellationToken) =>
        {
            var userId = UserId(context);
            var plan = await db.ThermalPlans.AsNoTracking().Include(x => x.Steps)
                .Where(x => x.UserId == userId)
                .OrderByDescending(x => x.CreatedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);
            return plan is null ? Results.NoContent() : Results.Ok(plan);
        });

        thermal.MapGet("/history", async (
            HttpContext context,
            DateTimeOffset? from,
            DateTimeOffset? to,
            PrisstyrningDbContext db,
            CancellationToken cancellationToken) =>
        {
            var end = (to ?? DateTimeOffset.UtcNow).ToUniversalTime();
            var start = (from ?? end.AddDays(-7)).ToUniversalTime();
            if (end <= start || end - start > TimeSpan.FromDays(400)) return Results.BadRequest(new { error = "Tidsintervallet måste vara 1 minut–400 dagar." });
            var samples = await db.ThermalTelemetrySamples.AsNoTracking()
                .Where(x => x.UserId == UserId(context) && x.TimestampUtc >= start && x.TimestampUtc <= end)
                .OrderBy(x => x.TimestampUtc)
                .ToListAsync(cancellationToken);
            return Results.Ok(samples);
        });

        thermal.MapGet("/events", async (
            HttpContext context,
            int? limit,
            PrisstyrningDbContext db,
            CancellationToken cancellationToken) =>
        {
            var take = Math.Clamp(limit ?? 100, 1, 500);
            var events = await db.ThermalEvents.AsNoTracking()
                .Where(x => x.UserId == UserId(context))
                .OrderByDescending(x => x.TimestampUtc)
                .Take(take)
                .ToListAsync(cancellationToken);
            return Results.Ok(events);
        });

        thermal.MapGet("/dhw", async (HttpContext context, PrisstyrningDbContext db, CancellationToken cancellationToken) =>
        {
            var cycles = await db.DhwCycles.AsNoTracking()
                .Where(x => x.UserId == UserId(context))
                .OrderByDescending(x => x.PlannedStartUtc)
                .Take(100)
                .ToListAsync(cancellationToken);
            return Results.Ok(cycles);
        });

        thermal.MapPost("/mode", async (
            HttpContext context,
            ThermalModeRequest request,
            ThermalModeService modes,
            CancellationToken cancellationToken) =>
        {
            var result = await modes.ChangeModeAsync(UserId(context), request, cancellationToken);
            return result.Success ? Results.Ok(new { message = result.Message }) : Results.BadRequest(new { error = result.Message });
        });

        thermal.MapPost("/override", async (
            HttpContext context,
            ThermalOverrideRequest request,
            ThermalModeService modes,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await modes.SetOverrideAsync(UserId(context), request, cancellationToken);
                return Results.Ok();
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        thermal.MapDelete("/override", async (
            HttpContext context,
            ThermalModeService modes,
            CancellationToken cancellationToken) =>
        {
            await modes.ClearOverrideAsync(UserId(context), cancellationToken);
            return Results.NoContent();
        });

        var homeAssistant = app.MapHomeAssistantEntityCatalogApi();
        homeAssistant.MapGet("/config", async (
            HttpContext context,
            HomeAssistantConnectionService connections,
            CancellationToken cancellationToken) =>
        {
            var config = await connections.GetAsync(UserId(context), cancellationToken);
            return config is null ? Results.NoContent() : Results.Ok(config);
        });
        homeAssistant.MapPut("/config", async (
            HttpContext context,
            UpdateHomeAssistantConnectionRequest request,
            HomeAssistantConnectionService connections,
            CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await connections.SaveAsync(UserId(context), request, cancellationToken)); }
            catch (ArgumentException exception)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["connection"] = [exception.Message] });
            }
            catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
        });
        homeAssistant.MapDelete("/config", async (
            HttpContext context,
            HomeAssistantConnectionService connections,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await connections.DeleteAsync(UserId(context), cancellationToken);
                return Results.NoContent();
            }
            catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
        });
        homeAssistant.MapPost("/test", async (
            HttpContext context,
            IHomeAssistantTelemetryClient client,
            CancellationToken cancellationToken) =>
        {
            return (await client.TestConnectionAsync(UserId(context), cancellationToken))
                ? Results.Ok(new { connected = true })
                : Results.BadRequest(new { connected = false, error = "Home Assistant kunde inte nås med telemetriidentiteten." });
        });
        homeAssistant.MapPost("/import-history", async (
            HttpContext context,
            HomeAssistantHistoryImportRequest request,
            HomeAssistantHistoryImportService importer,
            ThermalInstallationRegistry registry,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var userId = await registry.ResolveUserAsync(SessionUserId(context), cancellationToken);
                return Results.Ok(await importer.ImportAsync(userId, request.FromUtc, request.ToUtc, cancellationToken));
            }
            catch (ArgumentException exception)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["interval"] = [exception.Message] });
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = "Home Assistant-historiken kunde inte hämtas." });
            }
        });
        return app;
    }

    // Isolated HTTP tests host these exact account-scoped read routes with no
    // integration clients, background workers, or control endpoints registered.
    internal static RouteGroupBuilder MapHomeAssistantEntityCatalogApi(this IEndpointRouteBuilder app)
    {
        var homeAssistant = app.MapGroup("/api/home-assistant");
        homeAssistant.MapGet("/status", async (
            HttpContext context,
            IHomeAssistantStateCache cache,
            HomeAssistantConnectionService connections,
            CancellationToken cancellationToken) =>
        {
            var userId = UserId(context);
            var config = await connections.GetAsync(userId, cancellationToken);
            var live = cache.ReadAccount(userId);
            var configured = config is { TelemetryEnabled: true, TelemetryTokenConfigured: true };
            var current = configured && live.ConfigurationUpdatedAtUtc == config!.UpdatedAtUtc;
            var verified = current && live.Connected && live.LastSnapshotUtc >= config!.UpdatedAtUtc;
            var phase = config is null || !config.TelemetryTokenConfigured ? "NotConfigured"
                : !config.TelemetryEnabled ? "Disabled"
                : !current ? "Reloading"
                : live.Connected && !verified ? "Synchronizing" : live.Phase.ToString();
            return Results.Ok(new
            {
                configured,
                connected = verified,
                lastSnapshotUtc = current ? live.LastSnapshotUtc : null,
                lastActivityUtc = current ? live.LastActivityUtc : null,
                cachedEntities = current ? live.States.Count : 0,
                phase,
                configurationUpdatedAtUtc = config?.UpdatedAtUtc
            });
        });
        homeAssistant.MapGet("/entities", async (
            HttpContext context,
            IHomeAssistantStateCache cache,
            HomeAssistantConnectionService connections,
            CancellationToken cancellationToken) =>
        {
            var userId = UserId(context);
            var config = await connections.GetAsync(userId, cancellationToken);
            if (config is not { TelemetryEnabled: true, TelemetryTokenConfigured: true })
                return Results.Ok(Array.Empty<ThermalEntityStateDto>());

            var now = DateTimeOffset.UtcNow;
            var live = cache.ReadAccount(userId);
            var connectionIssue = !live.Connected
                ? "Liveanslutningen till Home Assistant är bruten. Visade värden är inte verifierade."
                : live.ConfigurationUpdatedAtUtc != config.UpdatedAtUtc || live.LastSnapshotUtc is null || live.LastSnapshotUtc < config.UpdatedAtUtc
                    ? "Startbilden saknas eller är äldre än anslutningsinställningarna. Telemetrin behöver läsa in en ny startbild."
                    : null;
            var result = live.States.Select(state => HomeAssistantEntityCatalog.Project(
                state, now, config.StaleAfterMinutes, connectionIssue)).ToArray();
            return Results.Ok(result);
        });
        return homeAssistant;
    }

    // The same read-only route and account boundary can be hosted in HTTP tests
    // without registering workers or any heat-pump/integration write clients.
    internal static RouteGroupBuilder MapThermalStatusApi(this IEndpointRouteBuilder app)
    {
        var thermal = app.MapGroup("/api/thermal");
        thermal.AddEndpointFilter(async (invocation, next) =>
        {
            try
            {
                var registry = invocation.HttpContext.RequestServices.GetRequiredService<ThermalInstallationRegistry>();
                invocation.HttpContext.Items[InstallationUserItem] = await registry.ResolveUserAsync(
                    SessionUserId(invocation.HttpContext),
                    invocation.HttpContext.RequestAborted);
                return await next(invocation);
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
        });
        thermal.MapGet("/status", GetStatusAsync);
        thermal.MapGet("/models", GetModelsAsync);
        return thermal;
    }

    private static async Task<IResult> GetModelsAsync(
        HttpContext context, PrisstyrningDbContext db, CancellationToken cancellationToken)
    {
        var models = await db.ThermalModelVersions.AsNoTracking()
            .Where(x => x.UserId == UserId(context)).OrderByDescending(x => x.CreatedAtUtc)
            .Take(100).ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        // Existing fields keep their meaning; this read-only assessment neither
        // edits the stored active marker nor approves a mode transition.
        return Results.Ok(models.Select(model => new
        {
            model.Id, model.ModelType, model.CreatedAtUtc, model.TrainingFromUtc, model.TrainingToUtc,
            model.IsActive, model.ParametersJson, model.MetricsJson,
            validation = ThermalModelEvidence.Assess(model, now)
        }));
    }

    private static async Task<IResult> GetStatusAsync(
        HttpContext context,
        PrisstyrningDbContext db,
        EmhassHealthState emhass,
        CancellationToken cancellationToken)
    {
        var userId = UserId(context);
        var site = await db.ThermalSiteConfigs.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        var state = await db.ThermalControlStates.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        var latestTelemetry = await db.ThermalTelemetrySamples.AsNoTracking()
            .Where(x => x.UserId == userId).OrderByDescending(x => x.TimestampUtc)
            .FirstOrDefaultAsync(cancellationToken);
        var rooms = await db.ThermalRoomConfigs.AsNoTracking()
            .Where(x => x.UserId == userId && x.Enabled).ToListAsync(cancellationToken);
        var entities = await db.ThermalEntityConfigs.AsNoTracking()
            .Where(x => x.UserId == userId && x.Enabled).ToListAsync(cancellationToken);
        var plan = await db.ThermalPlans.AsNoTracking().Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var quality = ThermalStatusQuality.Assess(latestTelemetry, rooms, entities, now, site?.UpdatedAtUtc);
        var next = plan is null ? null : await db.ThermalPlanSteps.AsNoTracking()
            .Where(x => x.ThermalPlanId == plan.Id && x.StartUtc > now && (x.DhwReserved || Math.Abs(x.DesiredLwtDeviationC - (state == null ? 0 : state.CurrentDeviationC)) >= 0.5))
            .OrderBy(x => x.StartUtc).Select(x => (DateTimeOffset?)x.StartUtc).FirstOrDefaultAsync(cancellationToken);

        return Results.Ok(new ThermalStatusDto(
            ThermalEnumParser.ControlModeOrLegacy(site?.ControlMode),
            ThermalEnumParser.DhwWriterOrLegacy(site?.DhwWriter),
            latestTelemetry?.TimestampUtc,
            quality.Quality,
            emhass.Available,
            plan?.CreatedAtUtc,
            plan is null ? null : (int)(now - plan.CreatedAtUtc).TotalMinutes,
            state?.CurrentDeviationC ?? 0,
            string.IsNullOrWhiteSpace(state?.FallbackReason) ? null : state.FallbackReason,
            next,
            state?.ManualOverrideUntilUtc > now,
            quality.Reason));
    }

    private static string UserId(HttpContext context)
    {
        if (context.Items.TryGetValue(InstallationUserItem, out var resolved) && resolved is string installationUserId &&
            AdminService.IsValidUserId(installationUserId))
            return installationUserId;
        return SessionUserId(context);
    }

    private static string SessionUserId(HttpContext context)
    {
        return AccountAuthentication.UserId(context.User)
               ?? throw new InvalidOperationException("Authenticated account identity is missing.");
    }

    private const string InstallationUserItem = "thermal_installation_user";
}
