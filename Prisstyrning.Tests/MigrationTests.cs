using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Prisstyrning.Data;
using Xunit;

namespace Prisstyrning.Tests;

public class MigrationTests
{
    [Fact]
    public void No_Pending_Model_Changes()
    {
        var options = new DbContextOptionsBuilder<PrisstyrningDbContext>()
            .UseNpgsql("Host=localhost;Database=migration_check")
            .Options;

        using var context = new PrisstyrningDbContext(options);

        var migrationsAssembly = context.GetService<IMigrationsAssembly>();
        var modelDiffer = context.GetService<IMigrationsModelDiffer>();

        var snapshotModel = migrationsAssembly.ModelSnapshot?.Model;

        if (snapshotModel is IMutableModel mutableModel)
            snapshotModel = mutableModel.FinalizeModel();

        if (snapshotModel != null)
            snapshotModel = context.GetService<IModelRuntimeInitializer>().Initialize(snapshotModel);

        var designTimeModel = context.GetService<IDesignTimeModel>().Model;

        var differences = modelDiffer.GetDifferences(
            snapshotModel?.GetRelationalModel(),
            designTimeModel.GetRelationalModel());

        Assert.True(differences.Count == 0,
            "The database model has pending changes that are not covered by a migration. " +
            "Run 'dotnet ef migrations add <MigrationName>' to generate the missing migration.");
    }
}
