using System.Data;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Jobs;
using Prisstyrning.Thermal.Optimization;
using Xunit.Abstractions;

namespace Prisstyrning.Tests.Thermal;

public sealed class PostgreSqlThermalAcceptanceTests
{
    private const string Account = "postgres-acceptance-a";
    private const string ForeignAccount = "postgres-acceptance-b";
    private const string ConcurrentAccount = "postgres-acceptance-concurrent";
    private const int RetentionDays = 400;
    private const int FiveMinuteSamplesPerDay = 24 * 12;
    private readonly ITestOutputHelper _output;

    public PostgreSqlThermalAcceptanceTests(ITestOutputHelper output) => _output = output;

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlAcceptance")]
    public async Task SourceRevalidation_UsesExpectedIndexAndSerializablePlanningRejectsWriteSkew()
    {
        var connectionString = AcceptanceConnectionString();
        await AssertEmptyLocalDatabaseAsync(connectionString);
        await AssertPostgreSql17OrLaterAsync(connectionString);

        await using var db = Database(connectionString);
        await db.Database.MigrateAsync();

        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var (rooms, entities) = await SeedConfigurationAsync(db);
        await SeedRetentionSizedHistoryAsync(db, now);
        await db.Database.ExecuteSqlRawAsync("ANALYZE \"ThermalTelemetrySamples\"");

        var models = await SeedModelsAsync(db, now, rooms, entities);
        var plan = await ExplainSourceSelectionAsync(connectionString, now.AddDays(-60), now.AddMinutes(-5));
        Assert.Contains("IX_ThermalTelemetrySamples_UserId_TimestampUtc", plan, StringComparison.Ordinal);
        Assert.Contains("Index Cond", plan, StringComparison.Ordinal);

        await VerifySourceRevalidationPerformanceAsync(db, now, models, rooms, entities);
        await VerifyCommittedHistoricalChangeFailsClosedAsync(db, connectionString, now, models, rooms, entities);
        await VerifySerializableOpenCycleWriteSkewAsync(connectionString, now);

        var site = await db.ThermalSiteConfigs.AsNoTracking().SingleAsync(x => x.UserId == Account);
        Assert.Equal("Legacy", site.ControlMode);
        Assert.Equal("Legacy", site.DhwWriter);
        Assert.Empty(await db.ThermalPlans.AsNoTracking().ToListAsync());
        Assert.Empty(await db.ThermalControlCommands.AsNoTracking().ToListAsync());
    }

    private async Task VerifySourceRevalidationPerformanceAsync(
        PrisstyrningDbContext db,
        DateTimeOffset now,
        IReadOnlyCollection<ThermalModelVersion> models,
        IReadOnlyCollection<ThermalRoomConfig> rooms,
        IReadOnlyCollection<ThermalEntityConfig> entities)
    {
        var durations = new List<TimeSpan>();
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var stopwatch = Stopwatch.StartNew();
            var validation = await ThermalModelProvenance.VerifyCurrentAsync(
                db, Account, models, rooms, entities, true, now, CancellationToken.None,
                ThermalCurrentModelTestData.Build);
            stopwatch.Stop();
            Assert.All(validation.Values, value =>
            {
                Assert.True(value.Passed);
                Assert.Equal("Current", value.Status);
            });
            if (attempt > 0) durations.Add(stopwatch.Elapsed);
        }

        var ordered = durations.OrderBy(x => x).ToArray();
        var p95 = ordered[(int)Math.Ceiling(ordered.Length * .95) - 1];
        _output.WriteLine(
            "Source revalidation over {0:N0} retained rows: p95 {1:N0} ms ({2}).",
            RetentionDays * FiveMinuteSamplesPerDay * 2,
            p95.TotalMilliseconds,
            string.Join(", ", ordered.Select(x => $"{x.TotalMilliseconds:N0} ms")));
        Assert.True(p95 < TimeSpan.FromSeconds(5),
            $"Source revalidation p95 was {p95.TotalMilliseconds:N0} ms; the isolated acceptance limit is 5,000 ms.");
    }

    private static async Task VerifyCommittedHistoricalChangeFailsClosedAsync(
        PrisstyrningDbContext db,
        string connectionString,
        DateTimeOffset now,
        IReadOnlyCollection<ThermalModelVersion> models,
        IReadOnlyCollection<ThermalRoomConfig> rooms,
        IReadOnlyCollection<ThermalEntityConfig> entities)
    {
        var source = ThermalModelProvenance.Read(models.Single(x => x.ModelType == "2R2C"))!;
        await using (var writer = Database(connectionString))
        {
            var affected = await writer.ThermalTelemetrySamples
                .Where(x => x.Id == source.FirstSampleId && x.UserId == Account)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    x => x.RoomTemperaturesJson,
                    "{\"sensor.postgres_acceptance_room\":21.7}"));
            Assert.Equal(1, affected);
        }

        var validation = await ThermalModelProvenance.VerifyCurrentAsync(
            db, Account, models, rooms, entities, true, now, CancellationToken.None,
            ThermalCurrentModelTestData.Build);
        Assert.False(validation[models.Single(x => x.ModelType == "2R2C").Id].Passed);
        Assert.Equal("Changed", validation[models.Single(x => x.ModelType == "2R2C").Id].Status);
        Assert.True(validation[models.Single(x => x.ModelType == "COP").Id].Passed);
    }

    private static async Task VerifySerializableOpenCycleWriteSkewAsync(
        string connectionString,
        DateTimeOffset now)
    {
        await using var first = new NpgsqlConnection(connectionString);
        await using var second = new NpgsqlConnection(connectionString);
        await first.OpenAsync();
        await second.OpenAsync();
        await using var firstTransaction = await first.BeginTransactionAsync(IsolationLevel.Serializable);
        await using var secondTransaction = await second.BeginTransactionAsync(IsolationLevel.Serializable);

        Assert.Equal(0L, await OpenCycleCountAsync(first, firstTransaction));
        Assert.Equal(0L, await OpenCycleCountAsync(second, secondTransaction));

        var outcomes = await Task.WhenAll(
            InsertAndCommitAsync(first, firstTransaction, now.AddHours(1)),
            InsertAndCommitAsync(second, secondTransaction, now.AddHours(2)));
        Assert.Single(outcomes.Where(x => x is null));
        var serializationFailure = Assert.Single(outcomes.OfType<PostgresException>());
        Assert.Equal(PostgresErrorCodes.SerializationFailure, serializationFailure.SqlState);

        await using var verification = new NpgsqlConnection(connectionString);
        await verification.OpenAsync();
        await using var count = new NpgsqlCommand(
            "SELECT count(*) FROM \"DhwCycles\" WHERE \"UserId\" = @user_id AND \"ActualEndUtc\" IS NULL",
            verification);
        count.Parameters.AddWithValue("user_id", ConcurrentAccount);
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
    }

    private static async Task<Exception?> InsertAndCommitAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTimeOffset plannedStartUtc)
    {
        try
        {
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO "DhwCycles"
                    ("UserId", "Kind", "Source", "Status", "PlannedStartUtc", "TargetTemperatureC",
                     "PredictedDurationMinutes", "ReservedDurationMinutes", "BackupHeaterUsed",
                     "PowerProfileJson", "TargetVerificationCount")
                VALUES
                    (@user_id, 'Eco', 'Shadow', 'Planned', @planned_start, 45, 45, 60, false, '[]'::jsonb, 0)
                """,
                connection,
                transaction);
            insert.Parameters.AddWithValue("user_id", ConcurrentAccount);
            insert.Parameters.AddWithValue("planned_start", plannedStartUtc);
            await insert.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
            return null;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            return exception;
        }
    }

    private static async Task<long> OpenCycleCountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM \"DhwCycles\" WHERE \"UserId\" = @user_id AND \"ActualEndUtc\" IS NULL",
            connection,
            transaction);
        command.Parameters.AddWithValue("user_id", ConcurrentAccount);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<IReadOnlyList<ThermalModelVersion>> SeedModelsAsync(
        PrisstyrningDbContext db,
        DateTimeOffset now,
        IReadOnlyCollection<ThermalRoomConfig> rooms,
        IReadOnlyCollection<ThermalEntityConfig> entities)
    {
        var from = now.AddDays(-60);
        var to = now.AddMinutes(-5);
        var candidates = await db.ThermalTelemetrySamples.AsNoTracking()
            .Where(x => x.UserId == Account && x.TimestampUtc >= from && x.TimestampUtc <= to)
            .OrderBy(x => x.TimestampUtc)
            .ThenBy(x => x.Id)
            .ToListAsync();
        var thermal = ThermalModelTrainingData.SelectThermal(candidates, Account, from, to, rooms, entities)
            .Select(x => x.Sample).ToArray();
        var cop = ThermalModelTrainingData.SelectCop(candidates, Account, from, to, entities)
            .Select(x => x.Sample).ToArray();
        Assert.Equal(60 * FiveMinuteSamplesPerDay, thermal.Length);
        Assert.Equal(thermal.Length, cop.Length);

        var models = new[]
        {
            Model("2R2C", thermal, ThermalModelProvenance.Create(
                Account, "2R2C", from, to, thermal, rooms, entities,
                thermal.Length - FiveMinuteSamplesPerDay, FiveMinuteSamplesPerDay, true,
                ThermalCurrentModelTestData.BuildRevision), to),
            Model("COP", cop, ThermalModelProvenance.Create(
                Account, "COP", from, to, cop, rooms, entities,
                cop.Length - 100, 100, true, ThermalCurrentModelTestData.BuildRevision), to)
        };
        db.ThermalModelVersions.AddRange(models);
        await db.SaveChangesAsync();
        return models;
    }

    private static ThermalModelVersion Model(
        string type,
        IReadOnlyList<ThermalTelemetrySample> samples,
        ThermalModelSourceEvidence evidence,
        DateTimeOffset selectionToUtc) => new()
        {
            UserId = Account,
            ModelType = type,
            CreatedAtUtc = selectionToUtc.AddMinutes(1),
            TrainingFromUtc = samples[0].TimestampUtc,
            TrainingToUtc = samples[^1].TimestampUtc,
            IsActive = true,
            ParametersJson = "{}",
            MetricsJson = "{}",
            SourceEvidenceJson = ThermalModelProvenance.Serialize(evidence)
        };

    private static async Task<(IReadOnlyList<ThermalRoomConfig> Rooms, IReadOnlyList<ThermalEntityConfig> Entities)>
        SeedConfigurationAsync(PrisstyrningDbContext db)
    {
        db.ThermalSiteConfigs.Add(new ThermalSiteConfig
        {
            UserId = Account,
            ControlMode = "Legacy",
            DhwWriter = "Legacy",
            HeatPumpPowerSignVerified = true
        });
        db.ThermalRoomConfigs.Add(new ThermalRoomConfig
        {
            UserId = Account,
            Name = "PostgreSQL acceptance room",
            EntityId = "sensor.postgres_acceptance_room",
            IsCritical = true
        });
        db.ThermalEntityConfigs.AddRange(ThermalModelTrainingDataTests.Entities.Select(entity => new ThermalEntityConfig
        {
            UserId = Account,
            Role = entity.Role,
            EntityId = $"sensor.postgres_acceptance_{entity.Role}",
            ExpectedUnit = entity.ExpectedUnit
        }));
        await db.SaveChangesAsync();
        return (
            await db.ThermalRoomConfigs.AsNoTracking().Where(x => x.UserId == Account).OrderBy(x => x.Id).ToListAsync(),
            await db.ThermalEntityConfigs.AsNoTracking().Where(x => x.UserId == Account).OrderBy(x => x.Id).ToListAsync());
    }

    private static async Task SeedRetentionSizedHistoryAsync(PrisstyrningDbContext db, DateTimeOffset now)
    {
        var sample = ThermalModelTrainingDataTests.ValidSample(now.AddMinutes(-5));
        var start = now.AddDays(-RetentionDays);
        var count = RetentionDays * FiveMinuteSamplesPerDay;
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "ThermalTelemetrySamples"
                ("UserId", "TimestampUtc", "OutsideTemperatureC", "OutsideTemperatureForecastJson",
                 "WindSpeedMps", "SolarIrradianceWm2", "LeavingWaterTemperatureC", "ReturnWaterTemperatureC",
                 "FlowLitresPerMinute", "BrineInC", "BrineOutC", "TankTemperatureC", "HeatPumpPowerKw",
                 "PropertyPowerKw", "SpotPriceSekPerKwh", "HeatOutputKw", "Cop", "DhwActive",
                 "DefrostActive", "BackupHeaterActive", "RoomTemperaturesJson", "QualityJson")
            SELECT account_id,
                   {{start}} + sample_number * interval '5 minutes',
                   5, '[]'::jsonb, 3, 0, 35, 30, 12, 5, 2, 45, 2, 2.5, 1.25, 4.186, 2.093,
                   false, false, false, {{sample.RoomTemperaturesJson}}::jsonb, {{sample.QualityJson}}::jsonb
            FROM unnest(ARRAY[{{Account}}, {{ForeignAccount}}]) AS account_id
            CROSS JOIN generate_series(0, {{count - 1}}) AS sample_number
            """);
    }

    private static async Task<string> ExplainSourceSelectionAsync(
        string connectionString,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)
            SELECT * FROM "ThermalTelemetrySamples"
            WHERE "UserId" = @user_id AND "TimestampUtc" >= @from_utc AND "TimestampUtc" <= @to_utc
            ORDER BY "TimestampUtc", "Id"
            """,
            connection);
        command.Parameters.AddWithValue("user_id", Account);
        command.Parameters.AddWithValue("from_utc", fromUtc);
        command.Parameters.AddWithValue("to_utc", toUtc);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task AssertEmptyLocalDatabaseAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM pg_tables WHERE schemaname = 'public'",
            connection);
        var tableCount = (long)(await command.ExecuteScalarAsync())!;
        Assert.Equal(0, tableCount);
    }

    private static async Task AssertPostgreSql17OrLaterAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        Assert.True(connection.PostgreSqlVersion.Major >= 17,
            $"PostgreSQL 17 or later is required; connected to {connection.PostgreSqlVersion}.");
    }

    private static PrisstyrningDbContext Database(string connectionString) => new(
        new DbContextOptionsBuilder<PrisstyrningDbContext>()
            .UseNpgsql(connectionString)
            .EnableDetailedErrors()
            .Options);

    private static string AcceptanceConnectionString()
    {
        var value = Environment.GetEnvironmentVariable("PRISSTYRNING_TEST_POSTGRES");
        return GuardedConnectionString(value);
    }

    internal static string GuardedConnectionString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(
                "PRISSTYRNING_TEST_POSTGRES must name a disposable local acceptance database.");
        var builder = new NpgsqlConnectionStringBuilder(value);
        var host = builder.Host ?? string.Empty;
        var database = builder.Database ?? string.Empty;
        if (!host.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
            !host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
            !host.Equals("::1", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("PostgreSQL acceptance is restricted to a loopback host.");
        if (!database.StartsWith("prisstyrning_acceptance_", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "PostgreSQL acceptance database names must start with 'prisstyrning_acceptance_'.");
        builder.Pooling = false;
        builder.Timeout = 10;
        builder.CommandTimeout = 90;
        return builder.ConnectionString;
    }
}

public sealed class PostgreSqlAcceptanceGuardTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Host=database.internal;Database=prisstyrning_acceptance_test;Username=test")]
    [InlineData("Host=127.0.0.1;Database=prisstyrning;Username=test")]
    public void Guard_RejectsMissingRemoteOrPersistentDatabase(string? connectionString)
    {
        Assert.Throws<InvalidOperationException>(() =>
            PostgreSqlThermalAcceptanceTests.GuardedConnectionString(connectionString));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    public void Guard_AcceptsOnlyNamedLoopbackAcceptanceDatabaseAndDisablesPooling(string host)
    {
        var value = PostgreSqlThermalAcceptanceTests.GuardedConnectionString(
            $"Host={host};Database=prisstyrning_acceptance_test;Username=test;Pooling=true");
        var guarded = new NpgsqlConnectionStringBuilder(value);

        Assert.Equal(host, guarded.Host);
        Assert.Equal("prisstyrning_acceptance_test", guarded.Database);
        Assert.False(guarded.Pooling);
        Assert.Equal(10, guarded.Timeout);
        Assert.Equal(90, guarded.CommandTimeout);
    }
}

internal sealed class PostgreSqlFactAttribute : FactAttribute
{
    public PostgreSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PRISSTYRNING_TEST_POSTGRES")))
            Skip = "Requires the explicit disposable PostgreSQL acceptance harness.";
    }
}
