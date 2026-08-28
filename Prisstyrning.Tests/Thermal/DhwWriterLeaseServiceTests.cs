using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Thermal.Control;
using Prisstyrning.Thermal.Domain;

namespace Prisstyrning.Tests.Thermal;

public sealed class DhwWriterLeaseServiceTests
{
    [Fact]
    public async Task Lease_CreatesLegacyGuardAndPreventsWriterHandoffWhileWriteRuns()
    {
        await using var db = new PrisstyrningDbContext(
            new DbContextOptionsBuilder<PrisstyrningDbContext>()
                .UseInMemoryDatabase($"dhw-writer-lease-{Guid.NewGuid():N}")
                .Options);
        var lease = new DhwWriterLeaseService(db);

        Assert.True(await lease.TryAcquireAsync("legacy-owner", DhwWriter.Legacy, "legacy-job-1", TimeSpan.FromMinutes(5)));
        Assert.False(await lease.TryAcquireAsync("legacy-owner", DhwWriter.Legacy, "legacy-job-2", TimeSpan.FromMinutes(5)));
        Assert.False(await lease.TrySwitchWriterAsync("legacy-owner", DhwWriter.Legacy, DhwWriter.Joint));

        await lease.ReleaseAsync("legacy-owner", "legacy-job-1");
        Assert.True(await lease.TrySwitchWriterAsync("legacy-owner", DhwWriter.Legacy, DhwWriter.Joint));
        Assert.True(await lease.TryAcquireAsync("legacy-owner", DhwWriter.Joint, "joint-job", TimeSpan.FromMinutes(5)));
    }
}
