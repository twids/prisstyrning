using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Prisstyrning.Data;
using Prisstyrning.Data.Entities;

namespace Prisstyrning.Tests.Unit.Data;

public class JsonMigrationServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DbContextOptions<PrisstyrningDbContext> _dbOptions;

    public JsonMigrationServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "prisstyrning-test-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
        _dbOptions = new DbContextOptionsBuilder<PrisstyrningDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        using var ctx = new PrisstyrningDbContext(_dbOptions);
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private IConfiguration BuildConfig(string? defaultZone = null)
    {
        var dict = new Dictionary<string, string?>
        {
            ["Storage:Directory"] = _tempDir
        };
        if (defaultZone != null)
            dict["Price:Nordpool:DefaultZone"] = defaultZone;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private IServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_dbOptions);
        services.AddScoped(sp =>
        {
            var opts = sp.GetRequiredService<DbContextOptions<PrisstyrningDbContext>>();
            return new PrisstyrningDbContext(opts);
        });
        return services.BuildServiceProvider();
    }

    private JsonMigrationService CreateService(IConfiguration? config = null)
    {
        var cfg = config ?? BuildConfig();
        var sp = BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        return new JsonMigrationService(cfg, scopeFactory);
    }

    // ─── User Settings ───────────────────────────────────────────────

    [Fact]
    public async Task MigratesUserSettings()
    {
        var userId = "user1";
        var userDir = Path.Combine(_tempDir, "tokens", userId);
        Directory.CreateDirectory(userDir);
        var json = JsonSerializer.Serialize(new
        {
            ComfortHours = 5,
            TurnOffPercentile = 0.75,
            MaxComfortGapHours = 20,
            zone = "SE1"
        });
        File.WriteAllText(Path.Combine(userDir, "user.json"), json);

        var svc = CreateService();
        await svc.StartAsync(CancellationToken.None);

        using var db = new PrisstyrningDbContext(_dbOptions);
        var settings = await db.UserSettings.FindAsync(userId);
        Assert.NotNull(settings);
        Assert.Equal(5, settings.ComfortHours);
        Assert.Equal(0.75, settings.TurnOffPercentile);
        Assert.Equal(20, settings.MaxComfortGapHours);
        Assert.Equal("SE1", settings.Zone);
        Assert.False(settings.AutoApplySchedule);

        // File should be deleted
        Assert.False(File.Exists(Path.Combine(userDir, "user.json")));
    }

    // ─── Daikin Tokens ───────────────────────────────────────────────

    [Fact]
    public async Task MigratesDaikinTokenForUser()
    {
        var userId = "user2";
        var userDir = Path.Combine(_tempDir, "tokens", userId);
        Directory.CreateDirectory(userDir);
        var json = JsonSerializer.Serialize(new
        {
            access_token = "atk123",
            refresh_token = "rtk456",
            expires_at_utc = "2025-06-01T12:00:00+00:00"
        });
        File.WriteAllText(Path.Combine(userDir, "daikin.json"), json);

        var svc = CreateService();
        await svc.StartAsync(CancellationToken.None);

        using var db = new PrisstyrningDbContext(_dbOptions);
        var token = await db.DaikinTokens.FindAsync(userId);
        Assert.NotNull(token);
        Assert.Equal("atk123", token.AccessToken);
        Assert.Equal("rtk456", token.RefreshToken);
        Assert.Equal(new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero), token.ExpiresAtUtc);

        Assert.False(File.Exists(Path.Combine(userDir, "daikin.json")));
    }

    [Fact]
    public async Task MigratesDaikinTokenAnonymous()
    {
        var tokensDir = Path.Combine(_tempDir, "tokens");
        Directory.CreateDirectory(tokensDir);
        var json = JsonSerializer.Serialize(new
        {
            access_token = "anon_atk",
            refresh_token = "anon_rtk",
            expires_at_utc = "2025-07-01T00:00:00+00:00"
        });
        File.WriteAllText(Path.Combine(tokensDir, "daikin.json"), json);

        var svc = CreateService();
        await svc.StartAsync(CancellationToken.None);

        using var db = new PrisstyrningDbContext(_dbOptions);
        var token = await db.DaikinTokens.FindAsync("anonymous");
        Assert.NotNull(token);
        Assert.Equal("anon_atk", token.AccessToken);

        Assert.False(File.Exists(Path.Combine(tokensDir, "daikin.json")));
    }

    // ─── Price Snapshots (new format) ────────────────────────────────

    [Fact]
    public async Task MigratesPriceSnapshotNewFormat()
    {
        var zoneDir = Path.Combine(_tempDir, "nordpool", "SE3");
        Directory.CreateDirectory(zoneDir);
        var json = JsonSerializer.Serialize(new
        {
            zone = "SE3",
            savedAt = "2025-08-24T17:14:04Z",
            today = new[]
            {
                new { start = "2025-08-24T00:00:00+02:00", end = "2025-08-24T01:00:00+02:00", value = 1.32 }
            },
            tomorrow = new[]
            {
                new { start = "2025-08-25T00:00:00+02:00", end = "2025-08-25T01:00:00+02:00", value = 2.0 }
            }
        });
        File.WriteAllText(Path.Combine(zoneDir, "prices-2025-08-24-SE3.json"), json);

        var svc = CreateService();
        await svc.StartAsync(CancellationToken.None);

        using var db = new PrisstyrningDbContext(_dbOptions);
        var snapshots = await db.PriceSnapshots.ToListAsync();
        Assert.Single(snapshots);
        var snap = snapshots[0];
        Assert.Equal("SE3", snap.Zone);
        Assert.Equal(new DateOnly(2025, 8, 24), snap.Date);
        Assert.Equal(new DateTimeOffset(2025, 8, 24, 17, 14, 4, TimeSpan.Zero), snap.SavedAtUtc);
        Assert.Contains("1.32", snap.TodayPricesJson);
        Assert.Contains("2", snap.TomorrowPricesJson);

        Assert.False(File.Exists(Path.Combine(zoneDir, "prices-2025-08-24-SE3.json")));
    }

    // ─── Price Snapshots (old format) ────────────────────────────────

    [Fact]
    public async Task MigratesPriceSnapshotOldFormat()
    {
        var json = JsonSerializer.Serialize(new
        {
            fetchedAt = "2025-08-24T17:14:04.877348+00:00",
            sensor = "sensor.nordpool_sell",
            baseUrl = "https://ha.widsell.nu",
            today = new[]
            {
                new { start = "2025-08-24T00:00:00+02:00", end = "2025-08-24T01:00:00+02:00", value = 1.32 }
            },
            tomorrow = new object[0]
        });
        File.WriteAllText(Path.Combine(_tempDir, "prices-20250824-171404.json"), json);

        var svc = CreateService(BuildConfig(defaultZone: "SE4"));
        await svc.StartAsync(CancellationToken.None);

        using var db = new PrisstyrningDbContext(_dbOptions);
        var snapshots = await db.PriceSnapshots.ToListAsync();
        Assert.Single(snapshots);
        var snap = snapshots[0];
        Assert.Equal("SE4", snap.Zone);  // uses configured default zone
        Assert.Equal(new DateOnly(2025, 8, 24), snap.Date);

        Assert.False(File.Exists(Path.Combine(_tempDir, "prices-20250824-171404.json")));
    }

    [Fact]
    public async Task OldFormatUsesDefaultZoneSE3WhenNotConfigured()
    {
        var json = JsonSerializer.Serialize(new
        {
            fetchedAt = "2025-08-24T10:00:00+00:00",
            today = new[]
            {
                new { start = "2025-08-24T00:00:00+02:00", end = "2025-08-24T01:00:00+02:00", value = 1.0 }
            },
            tomorrow = new object[0]
        });
        File.WriteAllText(Path.Combine(_tempDir, "prices-20250824-100000.json"), json);

        var svc = CreateService();  // no defaultZone configured
        await svc.StartAsync(CancellationToken.None);

        using var db = new PrisstyrningDbContext(_dbOptions);
        var snap = await db.PriceSnapshots.FirstAsync();
        Assert.Equal("SE3", snap.Zone);
    }

    // ─── Schedule History ────────────────────────────────────────────

    [Fact]
    public async Task MigratesScheduleHistory()
    {
        var userId = "user3";
        var histDir = Path.Combine(_tempDir, "schedule_history", userId);
        Directory.CreateDirectory(histDir);
        var entries = new[]
        {
            new { timestamp = "2025-08-24T10:00:00Z", schedule = new { hours = new[] { 1, 2, 3 } } },
            new { timestamp = "2025-08-24T11:00:00Z", schedule = new { hours = new[] { 4, 5, 6 } } }
        };
        File.WriteAllText(Path.Combine(histDir, "history.json"), JsonSerializer.Serialize(entries));

        var svc = CreateService();
        await svc.StartAsync(CancellationToken.None);

        using var db = new PrisstyrningDbContext(_dbOptions);
        var rows = await db.ScheduleHistory.Where(s => s.UserId == userId).OrderBy(s => s.Timestamp).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(new DateTimeOffset(2025, 8, 24, 10, 0, 0, TimeSpan.Zero), rows[0].Timestamp);
        Assert.Contains("1", rows[0].SchedulePayloadJson);
        Assert.Equal(new DateTimeOffset(2025, 8, 24, 11, 0, 0, TimeSpan.Zero), rows[1].Timestamp);

        Assert.False(File.Exists(Path.Combine(histDir, "history.json")));
    }

    // ─── Idempotent ──────────────────────────────────────────────────

    [Fact]
    public async Task IdempotentReRunDoesNotDuplicate()
    {
        // Setup user settings
        var userId = "idempotent-user";
        var userDir = Path.Combine(_tempDir, "tokens", userId);
        Directory.CreateDirectory(userDir);
        File.WriteAllText(Path.Combine(userDir, "user.json"),
            JsonSerializer.Serialize(new { ComfortHours = 7, TurnOffPercentile = 0.5, MaxComfortGapHours = 10, zone = "SE2" }));

        var svc = CreateService();
        await svc.StartAsync(CancellationToken.None);

        // File is deleted after first run, so re-create to simulate re-run with file still present
        // Actually, since the file is deleted, a second run with no files should do nothing
        var svc2 = CreateService();
        await svc2.StartAsync(CancellationToken.None);

        using var db = new PrisstyrningDbContext(_dbOptions);
        var count = await db.UserSettings.CountAsync();
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task IdempotentSkipsExistingUserSettings()
    {
        // Pre-populate DB
        using (var db = new PrisstyrningDbContext(_dbOptions))
        {
            db.UserSettings.Add(new UserSettings { UserId = "existing", ComfortHours = 99, Zone = "SE1" });
            await db.SaveChangesAsync();
        }

        // Create file with different data for same userId
        var userDir = Path.Combine(_tempDir, "tokens", "existing");
        Directory.CreateDirectory(userDir);
        File.WriteAllText(Path.Combine(userDir, "user.json"),
            JsonSerializer.Serialize(new { ComfortHours = 1, TurnOffPercentile = 0.1, MaxComfortGapHours = 5, zone = "SE4" }));

        var svc = CreateService();
        await svc.StartAsync(CancellationToken.None);

        using var db2 = new PrisstyrningDbContext(_dbOptions);
        var settings = await db2.UserSettings.FindAsync("existing");
        Assert.NotNull(settings);
        Assert.Equal(99, settings.ComfortHours); // Original value preserved
        // File should still be deleted
        Assert.False(File.Exists(Path.Combine(userDir, "user.json")));
    }

    // ─── Partial Failure ─────────────────────────────────────────────

    [Fact]
    public async Task CorruptFileDoesNotBlockOthers()
    {
        // Create a good user settings file
        var user1Dir = Path.Combine(_tempDir, "tokens", "good-user");
        Directory.CreateDirectory(user1Dir);
        File.WriteAllText(Path.Combine(user1Dir, "user.json"),
            JsonSerializer.Serialize(new { ComfortHours = 3, TurnOffPercentile = 0.9, MaxComfortGapHours = 28, zone = "SE3" }));

        // Create a corrupt user settings file
        var user2Dir = Path.Combine(_tempDir, "tokens", "bad-user");
        Directory.CreateDirectory(user2Dir);
        File.WriteAllText(Path.Combine(user2Dir, "user.json"), "{{{CORRUPT JSON!!!");

        var svc = CreateService();
        await svc.StartAsync(CancellationToken.None);

        using var db = new PrisstyrningDbContext(_dbOptions);
        // Good user should be migrated
        var good = await db.UserSettings.FindAsync("good-user");
        Assert.NotNull(good);
        Assert.Equal(3, good.ComfortHours);

        // Bad user should NOT be migrated
        var bad = await db.UserSettings.FindAsync("bad-user");
        Assert.Null(bad);
    }

    // ─── Empty directories ──────────────────────────────────────────

    [Fact]
    public async Task HandlesEmptyDirectories()
    {
        // Create empty tokens dir with an empty user subdir
        var userDir = Path.Combine(_tempDir, "tokens", "empty-user");
        Directory.CreateDirectory(userDir);

        var nordpoolDir = Path.Combine(_tempDir, "nordpool", "SE3");
        Directory.CreateDirectory(nordpoolDir);

        var historyDir = Path.Combine(_tempDir, "schedule_history", "empty-user");
        Directory.CreateDirectory(historyDir);

        var svc = CreateService();
        await svc.StartAsync(CancellationToken.None);

        using var db = new PrisstyrningDbContext(_dbOptions);
        Assert.Equal(0, await db.UserSettings.CountAsync());
        Assert.Equal(0, await db.PriceSnapshots.CountAsync());
        Assert.Equal(0, await db.ScheduleHistory.CountAsync());
    }

    // ─── No Data Directories ────────────────────────────────────────

    [Fact]
    public async Task HandlesNoDataDirectories()
    {
        // _tempDir exists but has no sub-dirs
        var svc = CreateService();
        await svc.StartAsync(CancellationToken.None);

        using var db = new PrisstyrningDbContext(_dbOptions);
        Assert.Equal(0, await db.UserSettings.CountAsync());
        Assert.Equal(0, await db.PriceSnapshots.CountAsync());
        Assert.Equal(0, await db.ScheduleHistory.CountAsync());
        Assert.Equal(0, await db.DaikinTokens.CountAsync());
    }

    // ─── Multiple Users / Files ──────────────────────────────────────

    [Fact]
    public async Task MigratesMultipleUsersAndFiles()
    {
        // User A
        var userADir = Path.Combine(_tempDir, "tokens", "userA");
        Directory.CreateDirectory(userADir);
        File.WriteAllText(Path.Combine(userADir, "user.json"),
            JsonSerializer.Serialize(new { ComfortHours = 5, TurnOffPercentile = 0.8, MaxComfortGapHours = 24, zone = "SE1" }));
        File.WriteAllText(Path.Combine(userADir, "daikin.json"),
            JsonSerializer.Serialize(new { access_token = "a_atk", refresh_token = "a_rtk", expires_at_utc = "2025-01-01T00:00:00Z" }));

        // User B
        var userBDir = Path.Combine(_tempDir, "tokens", "userB");
        Directory.CreateDirectory(userBDir);
        File.WriteAllText(Path.Combine(userBDir, "user.json"),
            JsonSerializer.Serialize(new { ComfortHours = 2, TurnOffPercentile = 0.6, MaxComfortGapHours = 12, zone = "SE2" }));

        // Schedule History for User A
        var histDir = Path.Combine(_tempDir, "schedule_history", "userA");
        Directory.CreateDirectory(histDir);
        File.WriteAllText(Path.Combine(histDir, "history.json"),
            JsonSerializer.Serialize(new[] { new { timestamp = "2025-01-01T00:00:00Z", schedule = new { x = 1 } } }));

        // Price snapshot (new format)
        var priceDir = Path.Combine(_tempDir, "nordpool", "SE1");
        Directory.CreateDirectory(priceDir);
        File.WriteAllText(Path.Combine(priceDir, "prices-2025-01-01-SE1.json"),
            JsonSerializer.Serialize(new
            {
                zone = "SE1",
                savedAt = "2025-01-01T12:00:00Z",
                today = new[] { new { start = "2025-01-01T00:00:00+01:00", value = 1.0 } },
                tomorrow = new object[0]
            }));

        var svc = CreateService();
        await svc.StartAsync(CancellationToken.None);

        using var db = new PrisstyrningDbContext(_dbOptions);
        Assert.Equal(2, await db.UserSettings.CountAsync());
        Assert.Equal(1, await db.DaikinTokens.CountAsync());
        Assert.Equal(1, await db.ScheduleHistory.CountAsync());
        Assert.Equal(1, await db.PriceSnapshots.CountAsync());
    }

    // ─── Date Extraction from Price Data ─────────────────────────────

    [Fact]
    public async Task ExtractsDateFromFirstTodayEntry()
    {
        var zoneDir = Path.Combine(_tempDir, "nordpool", "SE2");
        Directory.CreateDirectory(zoneDir);
        var json = JsonSerializer.Serialize(new
        {
            zone = "SE2",
            savedAt = "2025-12-15T14:00:00Z",
            today = new[]
            {
                new { start = "2025-12-15T00:00:00+01:00", end = "2025-12-15T01:00:00+01:00", value = 0.5 }
            },
            tomorrow = new object[0]
        });
        File.WriteAllText(Path.Combine(zoneDir, "prices-2025-12-15-SE2.json"), json);

        var svc = CreateService();
        await svc.StartAsync(CancellationToken.None);

        using var db = new PrisstyrningDbContext(_dbOptions);
        var snap = await db.PriceSnapshots.FirstAsync();
        Assert.Equal(new DateOnly(2025, 12, 15), snap.Date);
    }
}
