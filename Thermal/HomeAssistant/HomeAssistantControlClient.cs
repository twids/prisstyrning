using System.Net.Http.Headers;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Thermal.Domain;

namespace Prisstyrning.Thermal.HomeAssistant;

public sealed class HomeAssistantControlClient : IHomeAssistantControlClient
{
    private const string AllowedDomain = "number";
    private const string AllowedService = "set_value";
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

        var configuredLimit = Math.Clamp(site?.ActiveDeviationLimitC ?? 1, 0, 3);
        if (Math.Abs(deviationC) > configuredLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(deviationC), $"Deviation exceeds the configured ±{configuredLimit:0.0} °C limit.");
        }

        var connection = await _connections.ResolveAsync(userId, cancellationToken);
        var entityId = connection?.HeatingDeviationEntityId ?? string.Empty;
        var token = connection is { ControlEnabled: true } ? connection.ControlToken : null;
        var baseUri = connection?.BaseUri;
        if (!IsAllowedNumberEntity(entityId) ||
            string.IsNullOrWhiteSpace(token) ||
            baseUri is null)
        {
            throw new InvalidOperationException("Home Assistant control is not safely configured.");
        }

        var client = _httpClientFactory.CreateClient("HomeAssistantControl");
        var sentAtUtc = DateTimeOffset.UtcNow;
        var alreadyAtRequestedValue = _cache.TryGet(userId, entityId, out var beforeWrite) &&
                                      IsRecentMatchingState(beforeWrite, deviationC, sentAtUtc);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(baseUri, $"/api/services/{AllowedDomain}/{AllowedService}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new { entity_id = entityId, value = Math.Round(deviationC, 1) });
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (alreadyAtRequestedValue) return;

        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (_cache.TryGet(userId, entityId, out var observed) && IsVerifiedState(observed, deviationC, sentAtUtc)) return;
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }
        throw new InvalidOperationException("Home Assistant accepterade anropet men P1P2-värdet kunde inte verifieras inom tio sekunder.");
    }

    internal static bool IsAllowedNumberEntity(string? entityId) =>
        !string.IsNullOrWhiteSpace(entityId) &&
        entityId.StartsWith("number.", StringComparison.Ordinal) &&
        entityId.Length <= 255 &&
        entityId.All(character => char.IsLetterOrDigit(character) || character is '_' or '.');

    internal static bool IsVerifiedState(HomeAssistantState? state, double requestedValue, DateTimeOffset sentAtUtc) =>
        state is not null &&
        state.ReceivedAtUtc >= sentAtUtc &&
        IsMatchingValue(state.State, requestedValue);

    private static bool IsRecentMatchingState(HomeAssistantState? state, double requestedValue, DateTimeOffset nowUtc) =>
        state is not null &&
        nowUtc - state.ReceivedAtUtc <= TimeSpan.FromMinutes(10) &&
        IsMatchingValue(state.State, requestedValue);

    private static bool IsMatchingValue(string value, double requestedValue) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var observedValue) &&
        Math.Abs(observedValue - requestedValue) <= 0.11;
}
