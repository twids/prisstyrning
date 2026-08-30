using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data.Entities;

namespace Prisstyrning.Data.Repositories;

public sealed class DaikinInstallationRepository
{
    private readonly PrisstyrningDbContext _db;

    public DaikinInstallationRepository(PrisstyrningDbContext db) => _db = db;

    public Task<DaikinInstallation?> GetAsync(string userId, CancellationToken cancellationToken = default)
    {
        EnsureUser(userId);
        return _db.DaikinInstallations.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken);
    }

    public async Task<DaikinInstallation> SaveAsync(
        string userId,
        string siteId,
        string deviceId,
        string dhwEmbeddedId,
        string? heatingEmbeddedId = null,
        string scheduleMode = "heating",
        CancellationToken cancellationToken = default)
    {
        EnsureUser(userId);
        ValidateId(siteId, nameof(siteId));
        ValidateId(deviceId, nameof(deviceId));
        ValidateId(dhwEmbeddedId, nameof(dhwEmbeddedId));
        if (heatingEmbeddedId is not null) ValidateId(heatingEmbeddedId, nameof(heatingEmbeddedId));
        if (string.IsNullOrWhiteSpace(scheduleMode) || scheduleMode.Length > 50) throw new ArgumentException("Invalid schedule mode.", nameof(scheduleMode));
        var now = DateTimeOffset.UtcNow;
        var installation = await _db.DaikinInstallations.SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        if (installation is null)
        {
            installation = new DaikinInstallation { UserId = userId, CreatedAtUtc = now };
            _db.DaikinInstallations.Add(installation);
        }
        installation.SiteId = siteId.Trim();
        installation.DeviceId = deviceId.Trim();
        installation.DhwManagementPointEmbeddedId = dhwEmbeddedId.Trim();
        installation.HeatingManagementPointEmbeddedId = heatingEmbeddedId?.Trim();
        installation.ScheduleMode = scheduleMode.Trim();
        installation.UpdatedAtUtc = now;
        await _db.SaveChangesAsync(cancellationToken);
        return installation;
    }

    private static void EnsureUser(string userId)
    {
        if (!AdminService.IsValidUserId(userId)) throw new ArgumentException("Invalid account identifier.", nameof(userId));
    }

    private static void ValidateId(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200 || value.Any(char.IsControl))
            throw new ArgumentException("Invalid Daikin installation identifier.", name);
    }
}
