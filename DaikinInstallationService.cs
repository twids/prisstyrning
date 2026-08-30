using System.Text.Json;
using Prisstyrning.Data.Entities;
using Prisstyrning.Data.Repositories;

internal sealed class DaikinInstallationService
{
    private readonly DaikinInstallationRepository _repository;

    public DaikinInstallationService(DaikinInstallationRepository repository) => _repository = repository;

    public Task<DaikinInstallation?> GetAsync(string userId, CancellationToken cancellationToken = default) =>
        _repository.GetAsync(userId, cancellationToken);

    public Task<DaikinInstallation> SaveAsync(
        string userId,
        string siteId,
        string deviceId,
        string dhwEmbeddedId,
        string scheduleMode,
        CancellationToken cancellationToken = default) =>
        _repository.SaveAsync(userId, siteId, deviceId, dhwEmbeddedId, scheduleMode: scheduleMode, cancellationToken: cancellationToken);

    public async Task<DaikinInstallation> GetOrDiscoverAsync(
        string userId,
        DaikinApiClient client,
        CancellationToken cancellationToken = default)
    {
        var existing = await _repository.GetAsync(userId, cancellationToken);
        if (existing is not null) return existing;
        var sitesJson = await client.GetSitesAsync();
        var siteId = DeviceAutoDetection.GetFirstSiteId(sitesJson)
                     ?? throw new InvalidOperationException("No Daikin site was found for the signed-in account.");
        var devicesJson = await client.GetDevicesAsync(siteId);
        var (deviceId, rawDevice) = DeviceAutoDetection.GetFirstDevice(devicesJson);
        if (deviceId is null || rawDevice is null) throw new InvalidOperationException("No Daikin gateway device was found for the signed-in account.");
        var dhwEmbeddedId = DeviceAutoDetection.FindDhwEmbeddedId(rawDevice)
                            ?? throw new InvalidOperationException("No domestic hot-water management point was found for the signed-in account.");
        return await _repository.SaveAsync(userId, siteId, deviceId, dhwEmbeddedId, cancellationToken: cancellationToken);
    }
}
