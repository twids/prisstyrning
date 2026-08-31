using System.Net;
using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Security;
using Prisstyrning.Thermal.Domain;

namespace Prisstyrning.Thermal.HomeAssistant;

public sealed record HomeAssistantConnectionDto(
    string BaseUrl,
    bool TelemetryEnabled,
    bool ControlEnabled,
    string HeatingDeviationEntityId,
    int StaleAfterMinutes,
    bool TelemetryTokenConfigured,
    bool ControlTokenConfigured,
    DateTimeOffset UpdatedAtUtc);

public sealed record UpdateHomeAssistantConnectionRequest(
    string BaseUrl,
    string? TelemetryToken,
    string? ControlToken,
    bool TelemetryEnabled,
    bool ControlEnabled,
    string HeatingDeviationEntityId,
    int StaleAfterMinutes,
    bool ClearControlToken = false);

public sealed record ResolvedHomeAssistantConnection(
    string UserId,
    Uri BaseUri,
    string TelemetryToken,
    string? ControlToken,
    bool TelemetryEnabled,
    bool ControlEnabled,
    string HeatingDeviationEntityId,
    int StaleAfterMinutes,
    DateTimeOffset UpdatedAtUtc = default);

public sealed class HomeAssistantConnectionService
{
    private readonly PrisstyrningDbContext _db;
    private readonly IAccountSecretProtector _protector;
    private readonly IHomeAssistantEndpointValidator _endpointValidator;
    private readonly IHomeAssistantStateCache _cache;
    private readonly HomeAssistantConnectionChanges _changes;
    private static readonly object RevisionGate = new();
    private static long _lastRevisionTicks;

    public HomeAssistantConnectionService(
        PrisstyrningDbContext db,
        IAccountSecretProtector protector,
        IHomeAssistantEndpointValidator endpointValidator,
        IHomeAssistantStateCache cache,
        HomeAssistantConnectionChanges changes)
    {
        _db = db;
        _protector = protector;
        _endpointValidator = endpointValidator;
        _cache = cache;
        _changes = changes;
    }

    public async Task<HomeAssistantConnectionDto?> GetAsync(string userId, CancellationToken cancellationToken = default)
    {
        EnsureUser(userId);
        var entity = await _db.HomeAssistantConnections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        return entity is null ? null : ToDto(entity);
    }

    public async Task<ResolvedHomeAssistantConnection?> ResolveAsync(string userId, CancellationToken cancellationToken = default)
    {
        EnsureUser(userId);
        var entity = await _db.HomeAssistantConnections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        if (entity is null || entity.EncryptionVersion != 1 || string.IsNullOrWhiteSpace(entity.TelemetryTokenCiphertext)) return null;
        var baseUri = await _endpointValidator.ValidateAsync(entity.BaseUrl, cancellationToken);
        var telemetry = _protector.Unprotect(entity.TelemetryTokenCiphertext, userId, "ha-telemetry");
        var control = string.IsNullOrWhiteSpace(entity.ControlTokenCiphertext)
            ? null
            : _protector.Unprotect(entity.ControlTokenCiphertext, userId, "ha-control");
        return new ResolvedHomeAssistantConnection(
            userId,
            baseUri,
            telemetry,
            control,
            entity.TelemetryEnabled,
            entity.ControlEnabled,
            entity.HeatingDeviationEntityId,
            Math.Clamp(entity.StaleAfterMinutes, 1, 60),
            entity.UpdatedAtUtc);
    }

    public async Task<HomeAssistantConnectionDto> SaveAsync(
        string userId,
        UpdateHomeAssistantConnectionRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureUser(userId);
        if (!_protector.IsConfigured) throw new InvalidOperationException("Credential encryption is not configured.");
        // Serialize account settings on this single application host so commit and
        // cache invalidation order cannot be reversed by two browser tabs.
        using var settingsLease = await _changes.LockSettingsAsync(userId, cancellationToken);
        var baseUri = await _endpointValidator.ValidateAsync(request.BaseUrl, cancellationToken);
        if (request.StaleAfterMinutes is < 1 or > 60) throw new ArgumentException("Stale-gränsen måste vara 1–60 minuter.");
        if (!string.IsNullOrWhiteSpace(request.HeatingDeviationEntityId) &&
            !HomeAssistantControlClient.IsAllowedNumberEntity(request.HeatingDeviationEntityId))
            throw new ArgumentException("P1P2-avvikelsen måste vara ett giltigt number-entity-ID.");
        if (request.ControlEnabled && (string.IsNullOrWhiteSpace(request.HeatingDeviationEntityId)))
            throw new ArgumentException("Styrning kräver ett tillåtet P1P2 entity-ID.");

        var entity = await _db.HomeAssistantConnections.SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        var existingControlConfigured = !string.IsNullOrWhiteSpace(entity?.ControlTokenCiphertext);
        var resultingTelemetryConfigured = !string.IsNullOrWhiteSpace(request.TelemetryToken) || !string.IsNullOrWhiteSpace(entity?.TelemetryTokenCiphertext);
        var resultingControlConfigured = !request.ClearControlToken &&
                                         (!string.IsNullOrWhiteSpace(request.ControlToken) || existingControlConfigured);
        if (!resultingTelemetryConfigured) throw new ArgumentException("En separat telemetritoken krävs.");
        if (request.ControlEnabled && !resultingControlConfigured) throw new ArgumentException("Aktiv styrning kräver en separat styrtoken.");

        if (entity is not null && await IsActiveAsync(userId, cancellationToken) && ControlBoundaryChanged(entity, request, baseUri))
            throw new InvalidOperationException("Byt till Legacy eller Shadow och nollställ LWT-avvikelsen innan HA:s styranslutning ändras.");

        var now = NextRevision(entity?.UpdatedAtUtc);
        if (entity is null)
        {
            entity = new HomeAssistantConnection { UserId = userId, CreatedAtUtc = now };
            _db.HomeAssistantConnections.Add(entity);
        }
        entity.BaseUrl = baseUri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        entity.TelemetryEnabled = request.TelemetryEnabled;
        entity.ControlEnabled = request.ControlEnabled;
        entity.HeatingDeviationEntityId = request.HeatingDeviationEntityId.Trim();
        entity.StaleAfterMinutes = request.StaleAfterMinutes;
        entity.EncryptionVersion = 1;
        entity.UpdatedAtUtc = now;
        if (!string.IsNullOrWhiteSpace(request.TelemetryToken))
            entity.TelemetryTokenCiphertext = _protector.Protect(request.TelemetryToken.Trim(), userId, "ha-telemetry");
        if (request.ClearControlToken) entity.ControlTokenCiphertext = null;
        else if (!string.IsNullOrWhiteSpace(request.ControlToken))
            entity.ControlTokenCiphertext = _protector.Protect(request.ControlToken.Trim(), userId, "ha-control");
        await _db.SaveChangesAsync(cancellationToken);
        _cache.Invalidate(userId, entity.UpdatedAtUtc, entity.TelemetryEnabled);
        _changes.Notify();
        return ToDto(entity);
    }

    public async Task DeleteAsync(string userId, CancellationToken cancellationToken = default)
    {
        EnsureUser(userId);
        using var settingsLease = await _changes.LockSettingsAsync(userId, cancellationToken);
        if (await IsActiveAsync(userId, cancellationToken))
            throw new InvalidOperationException("HA-anslutningen kan bara tas bort i Legacy eller Shadow.");
        var entity = await _db.HomeAssistantConnections.SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        if (entity is null) return;
        _db.HomeAssistantConnections.Remove(entity);
        await _db.SaveChangesAsync(cancellationToken);
        // Retire the removed revision, but never a concurrently recreated newer one.
        _cache.Invalidate(userId, entity.UpdatedAtUtc, telemetryEnabled: false);
        _changes.Notify();
    }

    internal static DateTimeOffset NextRevision(DateTimeOffset? previous)
    {
        // PostgreSQL stores microseconds. Use the same precision in the response,
        // cache and subsequent database reads, and stay monotonic within this host.
        lock (RevisionGate)
        {
            var ticks = DateTimeOffset.UtcNow.UtcTicks / 10 * 10;
            _lastRevisionTicks = Math.Max(ticks, Math.Max(_lastRevisionTicks, previous?.UtcTicks ?? 0) / 10 * 10 + 10);
            return new DateTimeOffset(_lastRevisionTicks, TimeSpan.Zero);
        }
    }

    private async Task<bool> IsActiveAsync(string userId, CancellationToken cancellationToken)
    {
        var mode = await _db.ThermalSiteConfigs.AsNoTracking().Where(x => x.UserId == userId)
            .Select(x => x.ControlMode).SingleOrDefaultAsync(cancellationToken);
        return ThermalEnumParser.ControlModeOrLegacy(mode) is ControlMode.LwtActive or ControlMode.FullActive;
    }

    private static bool ControlBoundaryChanged(HomeAssistantConnection current, UpdateHomeAssistantConnectionRequest request, Uri baseUri) =>
        !string.Equals(current.BaseUrl.TrimEnd('/'), baseUri.GetLeftPart(UriPartial.Path).TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
        current.ControlEnabled != request.ControlEnabled || request.ClearControlToken ||
        !string.IsNullOrWhiteSpace(request.ControlToken) ||
        !string.Equals(current.HeatingDeviationEntityId, request.HeatingDeviationEntityId.Trim(), StringComparison.Ordinal);

    private static HomeAssistantConnectionDto ToDto(HomeAssistantConnection entity) => new(
        entity.BaseUrl,
        entity.TelemetryEnabled,
        entity.ControlEnabled,
        entity.HeatingDeviationEntityId,
        entity.StaleAfterMinutes,
        !string.IsNullOrWhiteSpace(entity.TelemetryTokenCiphertext),
        !string.IsNullOrWhiteSpace(entity.ControlTokenCiphertext),
        entity.UpdatedAtUtc);

    private static void EnsureUser(string userId)
    {
        if (!AdminService.IsValidUserId(userId)) throw new ArgumentException("Invalid account identifier.", nameof(userId));
    }
}

public interface IHomeAssistantEndpointValidator
{
    Task<Uri> ValidateAsync(string value, CancellationToken cancellationToken = default);
}

public sealed class HomeAssistantEndpointValidator : IHomeAssistantEndpointValidator
{
    public Task<Uri> ValidateAsync(string value, CancellationToken cancellationToken = default) =>
        HomeAssistantEndpointGuard.ValidatePublicHttpsAsync(value, cancellationToken);
}

public static class HomeAssistantEndpointGuard
{
    public static async Task<Uri> ValidatePublicHttpsAsync(string value, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Home Assistant-adressen måste vara en publik HTTPS-URL utan användaruppgifter, query eller fragment.");
        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken); }
        catch (Exception exception) when (exception is System.Net.Sockets.SocketException or ArgumentException)
        {
            throw new ArgumentException("Home Assistant-adressen kunde inte DNS-verifieras.", exception);
        }
        if (addresses.Length == 0 || addresses.Any(IsNonPublic))
            throw new ArgumentException("Home Assistant-adressen får inte peka på loopback, privat, länk-lokal eller reserverad adress.");
        return uri;
    }

    internal static bool IsNonPublic(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.IsIPv6Multicast || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
            return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] is 0 or 10 or 127 ||
               bytes[0] == 169 && bytes[1] == 254 ||
               bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
               bytes[0] == 192 && bytes[1] == 168 ||
               bytes[0] >= 224;
    }
}
