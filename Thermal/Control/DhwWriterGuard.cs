using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Thermal.Domain;

namespace Prisstyrning.Thermal.Control;

public sealed class DhwWriterGuard
{
    private readonly PrisstyrningDbContext _db;

    public DhwWriterGuard(PrisstyrningDbContext db) => _db = db;

    public async Task<bool> IsLegacyWriterAsync(string userId, CancellationToken cancellationToken = default)
    {
        var writer = await _db.ThermalSiteConfigs.AsNoTracking()
            .Where(x => x.UserId == userId)
            .Select(x => x.DhwWriter)
            .SingleOrDefaultAsync(cancellationToken);
        return ThermalEnumParser.DhwWriterOrLegacy(writer) == DhwWriter.Legacy;
    }
}
