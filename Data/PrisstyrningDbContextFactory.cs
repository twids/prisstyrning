using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Prisstyrning.Data;

/// <summary>
/// Keeps EF tooling independent from the web host, background workers and live
/// database startup path. No connection is opened when migrations are scaffolded.
/// </summary>
public sealed class PrisstyrningDbContextFactory : IDesignTimeDbContextFactory<PrisstyrningDbContext>
{
    public PrisstyrningDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("PRISSTYRNING_ConnectionStrings__DefaultConnection")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Database=prisstyrning;Username=prisstyrning;Password=prisstyrning";
        var options = new DbContextOptionsBuilder<PrisstyrningDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new PrisstyrningDbContext(options);
    }
}
