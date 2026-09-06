using System.Net.Http.Headers;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Thermal.Domain;

namespace Prisstyrning.Thermal.HomeAssistant;

public sealed class HomeAssistantControlClient : IHomeAssistantControlClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HomeAssistantConnectionService _connections;
    private readonly IHomeAssistantStateCache _cache;
    private readonly PrisstyrningDbContext _db;
    private readonly IConfiguration _configuration;

    public HomeAssistantControlClient(
        IHttpClientFactory httpClientFactory,
        HomeAssistantConnectionService connections,
        IHomeAssistantStateCache cache,
        PrisstyrningDbContext db,
        IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _connections = connections;
        _cache = cache;
        _db = db;
        _configuration = configuration;
    }

    public async Task SetHeatingDeviationAsync(string userId, double deviationC, CancellationToken cancellationToken = default)
    {
        if (!AdminService.IsValidUserId(userId)) throw new ArgumentException("Invalid thermal installation user id.", nameof(userId));
        if (!double.IsFinite(deviationC)) throw new ArgumentOutOfRangeException(nameof(deviationC));
        var site = await _db.ThermalSiteConfigs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        var mode = ThermalEnumParser.ControlModeOrLegacy(site?.ControlMode);
        if (mode is not (ControlMode.LwtActive or ControlMode.FullActive))
        {
            throw new InvalidOperationException("Home Assistant control is disabled outside active control modes.");
        }
        if (Math.Abs(deviationC) >= 0.01 &&
            !_configuration.GetValue("Thermal:AllowLwtActive", false))
        {
            throw new InvalidOperationException("LWT writes are disabled by the deployment kill switch.");
        }

        if (site is null || !double.IsFinite(site.ActiveDeviationLimitC) || site.ActiveDeviationLimitC is < 0 or > 3)
            throw new InvalidOperationException("LWT-säkerhetsgränsen är ogiltig.");
        var configuredLimit = site.ActiveDeviationLimitC;
        if (Math.Abs(deviationC) > configuredLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(deviationC), $"Deviation exceeds the configured ±{configuredLimit:0.0} °C limit.");
        }

        var connection = await _connections.ResolveAsync(userId, cancellationToken);
        var entityId = connection?.HeatingDeviationEntityId ?? string.Empty;
        var token = connection is { ControlEnabled: true } ? connection.ControlToken : null;
        var baseUri = connection?.BaseUri;
        if (!LwtControlBinding.IsActuator(entityId) ||
            string.IsNullOrWhiteSpace(token) ||
            baseUri is null)
        {
            throw new InvalidOperationException("Home Assistant control is not safely configured.");
        }

        var client = _httpClientFactory.CreateClient("HomeAssistantControl");
        var sentAtUtc = DateTimeOffset.UtcNow;
        var mappings = await _db.ThermalEntityConfigs.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(cancellationToken);
        var feedbackId = LwtControlBinding.FeedbackEntity(entityId, mappings)
            ?? throw new InvalidOperationException("P1P2 kräver en separat numerisk återkoppling i °C under Entities.");
        var climate = LwtControlBinding.IsEntity(entityId, "climate");
        if (climate)
        {
            // Loss of feedback must stop optimization, not the attempt to restore the base curve.
            // An exact zero still requires a valid actuator and fresh feedback AFTER the command
            // before it can be reported as accepted; HTTP success alone is never sufficient.
            if (deviationC != 0 && (!_cache.TryGet(userId, feedbackId, out var numericFeedback) || !LwtControlBinding.NumericFeedback(numericFeedback, sentAtUtc)))
                throw new InvalidOperationException("LWT-återkopplingen saknar ett aktuellt numeriskt värde i °C.");
            _cache.TryGet(userId, entityId, out var actuator);
            var step = LwtControlBinding.Step(entityId, actuator, sentAtUtc, configuredLimit);
            if (step is null || Math.Abs(deviationC / step.Value - Math.Round(deviationC / step.Value)) > 1e-6)
                throw new InvalidOperationException("LWT-reglagets aktuella intervall eller temperatursteg tillåter inte värdet.");
        }
        var alreadyAtRequestedValue = _cache.TryGet(userId, feedbackId, out var beforeWrite) &&
                                      IsRecentMatchingState(beforeWrite, deviationC, sentAtUtc);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(baseUri, climate ? "/api/services/climate/set_temperature" : "/api/services/number/set_value"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = climate
            ? JsonContent.Create(new { entity_id = entityId, temperature = deviationC })
            : JsonContent.Create(new { entity_id = entityId, value = Math.Round(deviationC, 1) });
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (alreadyAtRequestedValue) return;

        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (_cache.TryGet(userId, feedbackId, out var observed) && IsVerifiedState(observed, deviationC, sentAtUtc)) return;
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }
        throw new InvalidOperationException("Home Assistant accepterade anropet men P1P2-värdet kunde inte verifieras inom tio sekunder.");
    }

    internal static bool IsAllowedNumberEntity(string? entityId) =>
        LwtControlBinding.IsEntity(entityId, "number");

    internal static bool IsVerifiedState(HomeAssistantState? state, double requestedValue, DateTimeOffset sentAtUtc) =>
        state is not null &&
        state.ReceivedAtUtc >= sentAtUtc &&
        LwtControlBinding.Recent(state, sentAtUtc.AddSeconds(10)) && state.Unit == "°C" &&
        IsMatchingValue(state.State, requestedValue);

    private static bool IsRecentMatchingState(HomeAssistantState? state, double requestedValue, DateTimeOffset nowUtc) =>
        state is not null &&
        LwtControlBinding.Recent(state, nowUtc) && state.Unit == "°C" &&
        IsMatchingValue(state.State, requestedValue);

    private static bool IsMatchingValue(string value, double requestedValue) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var observedValue) &&
        double.IsFinite(observedValue) && double.IsFinite(requestedValue) &&
        Math.Abs(observedValue - requestedValue) <= 0.11;
}
