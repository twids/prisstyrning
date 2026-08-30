
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Linq;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Antiforgery;
using Hangfire;
using Hangfire.InMemory;
using Hangfire.Dashboard;
using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data;
using Prisstyrning.Data.Repositories;
using Prisstyrning.Jobs;
using Prisstyrning.Thermal.HomeAssistant;
using Prisstyrning.Thermal.Jobs;
using Prisstyrning.Thermal;
using Prisstyrning.Thermal.Control;
using Prisstyrning.Thermal.Data;
using Prisstyrning.Thermal.Optimization;
using Prisstyrning.Security;

// Constants for maintainability
const int MaxUserIdLength = 100;
const int MaxScheduleRawDisplayLength = 400;
const int DefaultListenPort = 5000;
string[] ValidTimezones = ["auto", "Europe/Stockholm", "Europe/Oslo", "Europe/Copenhagen", "Europe/Helsinki"];

// Register /api/user/settings endpoints after app is declared

var builder = WebApplication.CreateBuilder(args);
// Läser in en extra lokal override-fil (gemener) om den finns
builder.Configuration.AddJsonFile("appsettings.development.json", optional: true, reloadOnChange: true);
// Miljövariabler (tar över appsettings). Stöd både utan prefix och med prefix PRISSTYRNING_
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddEnvironmentVariables(prefix: "PRISSTYRNING_");
// Konfigurera så att appen kan lyssna på alla interfaces (0.0.0.0) istället för endast localhost
var portValue = Environment.GetEnvironmentVariable("PORT") ?? builder.Configuration["PORT"] ?? builder.Configuration["App:Port"];
if (!int.TryParse(portValue, out var listenPort)) listenPort = DefaultListenPort;
builder.WebHost.ConfigureKestrel(o =>
{
    // Rensar ev. default endpoints och lyssnar på angiven port på alla IP
    o.ListenAnyIP(listenPort);
});
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var configuredStorageDirectory = builder.Configuration["Storage:Directory"] ?? "data";
var configuredKeyDirectory = builder.Configuration["Security:DataProtectionKeysPath"];
var dataProtectionKeyDirectory = string.IsNullOrWhiteSpace(configuredKeyDirectory)
    ? Path.Combine(configuredStorageDirectory, "data-protection-keys")
    : configuredKeyDirectory;
if (!Path.IsPathRooted(dataProtectionKeyDirectory))
    dataProtectionKeyDirectory = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, dataProtectionKeyDirectory));
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeyDirectory))
    .SetApplicationName("Prisstyrning");
builder.Services.Configure<CredentialEncryptionOptions>(
    builder.Configuration.GetSection(CredentialEncryptionOptions.SectionName));
builder.Services.AddSingleton<IAccountSecretProtector, AccountSecretProtector>();
builder.Services.AddScoped<AccountSessionService>();
builder.Services.AddScoped<AccountCookieEvents>();
builder.Services.AddAuthentication(AccountAuthentication.Scheme)
    .AddCookie(AccountAuthentication.Scheme, options =>
    {
        options.Cookie.Name = "__Host-prisstyrning-session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.Cookie.Path = "/";
        options.ExpireTimeSpan = AccountAuthentication.InactivityTimeout;
        options.SlidingExpiration = true;
        options.EventsType = typeof(AccountCookieEvents);
    });
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "__Host-prisstyrning-csrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
    options.HeaderName = "X-CSRF-TOKEN";
});

// PostgreSQL + EF Core
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=localhost;Database=prisstyrning;Username=prisstyrning;Password=prisstyrning";
builder.Services.AddDbContext<PrisstyrningDbContext>(options =>
    options.UseNpgsql(connectionString));

// Configure HttpClientFactory with named clients
builder.Services.AddHttpClient("Nordpool", client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Prisstyrning/1.0");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json, */*;q=0.8");
    client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.8");
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AutomaticDecompression = System.Net.DecompressionMethods.All
});

builder.Services.AddHttpClient("Daikin", client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Prisstyrning/1.0");
});

builder.Services.AddHttpClient("HomeAssistantTelemetry", client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Prisstyrning/1.0");
    client.Timeout = TimeSpan.FromSeconds(30);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

builder.Services.AddHttpClient("HomeAssistantControl", client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Prisstyrning/1.0");
    client.Timeout = TimeSpan.FromSeconds(15);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

builder.Services.AddHttpClient("Entsoe", client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Prisstyrning/1.0");
});

builder.Services.AddHttpClient("Emhass", (services, client) =>
{
    var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<EmhassOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
    client.Timeout = Timeout.InfiniteTimeSpan;
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Prisstyrning/1.0");
});

// Register application services
builder.Services.AddScoped<BatchRunner>();

// Configure Hangfire with in-memory storage
builder.Services.AddHangfire(config => config
    .UseInMemoryStorage());
builder.Services.AddHangfireServer();

// Register repositories
builder.Services.AddScoped<UserSettingsRepository>();
builder.Services.AddScoped<AdminRepository>();
builder.Services.AddScoped<PriceRepository>();
builder.Services.AddScoped<ScheduleHistoryRepository>();
builder.Services.AddScoped<DaikinTokenRepository>();
builder.Services.AddScoped<DaikinInstallationRepository>();
builder.Services.AddScoped<DaikinInstallationService>();
builder.Services.AddScoped<FlexibleScheduleStateRepository>();
builder.Services.AddScoped<DaikinOAuthService>();
builder.Services.AddHostedService<JsonMigrationService>();

// Thermal orchestration is additive and remains read-only while ControlMode=Legacy/Shadow.
builder.Services.Configure<EmhassOptions>(builder.Configuration.GetSection(EmhassOptions.SectionName));
builder.Services.Configure<ThermalOptimizationQueueOptions>(
    builder.Configuration.GetSection(ThermalOptimizationQueueOptions.SectionName));
builder.Services.AddSingleton<IHomeAssistantStateCache, HomeAssistantStateCache>();
builder.Services.AddSingleton<SensorQualityTracker>();
builder.Services.AddSingleton<IHomeAssistantEndpointValidator, HomeAssistantEndpointValidator>();
builder.Services.AddScoped<HomeAssistantConnectionService>();
builder.Services.AddScoped<IHomeAssistantTelemetryClient, HomeAssistantTelemetryClient>();
builder.Services.AddScoped<IHomeAssistantControlClient, HomeAssistantControlClient>();
builder.Services.AddHostedService<HomeAssistantWebSocketWorker>();
builder.Services.AddHostedService<HomeAssistantTelemetryCollector>();
builder.Services.AddScoped<ThermalDataService>();
builder.Services.AddScoped<ThermalInstallationRegistry>();
builder.Services.AddScoped<ThermalDiagnosticsService>();
builder.Services.AddScoped<HomeAssistantHistoryImportService>();
builder.Services.AddScoped<DhwWriterGuard>();
builder.Services.AddScoped<DhwWriterLeaseService>();
builder.Services.AddScoped<ThermalReadinessService>();
builder.Services.AddScoped<ThermalModeService>();
builder.Services.AddScoped<WriterLeaseService>();
builder.Services.AddSingleton<WriterLeaseIdentity>();
builder.Services.AddScoped<JointDhwScheduleWriter>();
builder.Services.AddScoped<DhwProfileEstimator>();
builder.Services.AddSingleton<DhwCyclePlanner>();
builder.Services.AddSingleton<LwtRegulator>();
builder.Services.AddSingleton<GreyBoxThermalModel>();
builder.Services.AddSingleton<CopModel>();
builder.Services.AddSingleton<EmhassHealthState>();
builder.Services.AddScoped<IEmhassClient, EmhassClient>();
builder.Services.AddSingleton<ThermalOptimizationQueue>();
builder.Services.AddSingleton<IEmhassOptimizationDispatcher>(services =>
    services.GetRequiredService<ThermalOptimizationQueue>());
builder.Services.AddHostedService<LwtControlWorker>();
builder.Services.AddHostedService<EmhassOptimizationWorker>();
builder.Services.AddHostedService<JointPlanCoordinator>();
builder.Services.AddHostedService<DhwLifecycleWorker>();
builder.Services.AddTransient<ThermalModelTrainingJob>();
builder.Services.AddTransient<CopModelTrainingJob>();
builder.Services.AddTransient<ThermalRetentionJob>();

// Register job classes for dependency injection
builder.Services.AddTransient<NordpoolPriceHangfireJob>();
builder.Services.AddTransient<DaikinTokenRefreshHangfireJob>();
builder.Services.AddTransient<DailyPriceHangfireJob>();
builder.Services.AddTransient<InitialBatchHangfireJob>();
builder.Services.AddTransient<ScheduleUpdateHangfireJob>();

// CORS: same-origin by default, with an explicit exact-origin allowlist when needed.
var configuredOrigins = builder.Configuration.GetSection("Security:AllowedOrigins").Get<string[]>() ?? [];
var publicOrigin = builder.Configuration["PublicBaseUrl"];
var allowedOrigins = configuredOrigins
    .Concat(Uri.TryCreate(publicOrigin, UriKind.Absolute, out var publicUri)
        ? [publicUri.GetLeftPart(UriPartial.Authority)]
        : Array.Empty<string>())
    .Where(x => Uri.TryCreate(x, UriKind.Absolute, out _))
    .Select(x => x.TrimEnd('/'))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToHashSet(StringComparer.OrdinalIgnoreCase);
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.SetIsOriginAllowed(origin => allowedOrigins.Contains(origin.TrimEnd('/')))
        .AllowAnyMethod()
        .AllowAnyHeader()
        .AllowCredentials();
    });
});

// Rate limiting for admin login endpoint (partitioned per remote IP)
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("admin-login", httpContext =>
    {
        var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: remoteIp,
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });
    });
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { error = "Too many attempts. Please try again later." }, cancellationToken);
    };
});

var app = builder.Build();

// Apply EF Core migrations on startup (with retry for container orchestration)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
    for (var attempt = 1; attempt <= 5; attempt++)
    {
        try
        {
            db.Database.Migrate();
            var tokenRepository = scope.ServiceProvider.GetRequiredService<DaikinTokenRepository>();
            var credentialStorage = await tokenRepository.ReconcileCredentialStorageAsync();
            if (credentialStorage.EncryptedCount > 0 || credentialStorage.PlaintextClearedCount > 0)
            {
                Console.WriteLine($"[Startup] Reconciled Daikin credential storage: encrypted={credentialStorage.EncryptedCount}, plaintextCleared={credentialStorage.PlaintextClearedCount}, rollbackCompatibility={credentialStorage.LegacyPlaintextPreserved}.");
            }
            var legacySiteId = builder.Configuration["Daikin:SiteId"];
            var legacyDeviceId = builder.Configuration["Daikin:DeviceId"];
            var legacyEmbeddedId = builder.Configuration["Daikin:ManagementPointEmbeddedId"];
            if (!string.IsNullOrWhiteSpace(legacySiteId) &&
                !string.IsNullOrWhiteSpace(legacyDeviceId) &&
                !string.IsNullOrWhiteSpace(legacyEmbeddedId) &&
                !await db.DaikinInstallations.AnyAsync())
            {
                var legacyOwners = await db.DaikinTokens.AsNoTracking().Select(x => x.UserId).Distinct().ToListAsync();
                if (legacyOwners.Count == 1)
                {
                    var installations = scope.ServiceProvider.GetRequiredService<DaikinInstallationRepository>();
                    await installations.SaveAsync(
                        legacyOwners[0], legacySiteId, legacyDeviceId, legacyEmbeddedId,
                        scheduleMode: builder.Configuration["Daikin:ScheduleMode"] ?? "heating");
                    Console.WriteLine("[Startup] Migrated the single legacy Daikin target into its account-owned installation record.");
                }
                else
                {
                    Console.WriteLine("[Startup] Legacy Daikin target was not migrated because ownership is ambiguous.");
                }
            }
            Console.WriteLine("[Startup] Database migrations applied successfully.");
            break;
        }
        catch (Exception ex) when (attempt < 5)
        {
            Console.WriteLine($"[Startup] Database migration attempt {attempt}/5 failed: {ex.Message}. Retrying in {attempt * 2}s...");
            Thread.Sleep(attempt * 2000);
        }
    }
}

var hangfirePassword = builder.Configuration["Hangfire:DashboardPassword"];

// Schedule recurring jobs
RecurringJob.AddOrUpdate<NordpoolPriceHangfireJob>("nordpool-price-job", 
    job => job.ExecuteAsync(), 
    "0 13 * * *", // Daily at 13:00 Europe/Stockholm local time (CET/CEST, when Nordpool publishes day-ahead prices)
    new RecurringJobOptions { TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm") });

RecurringJob.AddOrUpdate<ScheduleUpdateHangfireJob>("schedule-update-job-midnight",
    job => job.ExecuteAsync(),
    "35 1 * * *", // Daily at 01:35 (1.5h after midnight, allows for price data availability)
    new RecurringJobOptions { TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm") });

RecurringJob.AddOrUpdate<ScheduleUpdateHangfireJob>("schedule-update-job-noon",
    job => job.ExecuteAsync(),
    "35 13 * * *", // Daily at 13:35 (1.5h after noon, ensures tomorrow's prices are available)
    new RecurringJobOptions { TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm") });

RecurringJob.AddOrUpdate<DaikinTokenRefreshHangfireJob>("daikin-token-refresh-job",
    job => job.ExecuteAsync(),
    "*/5 * * * *"); // Every 5 minutes

RecurringJob.AddOrUpdate<DailyPriceHangfireJob>("daily-price-job",
    job => job.ExecuteAsync(),
    "*/10 * * * *"); // Every 10 minutes

// Schedule initial batch job to run daily at 14:30
RecurringJob.AddOrUpdate<InitialBatchHangfireJob>("initial-batch-job",
    job => job.ExecuteAsync(),
    "30 14 * * *", // Daily at 14:30
    new RecurringJobOptions { TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm") });

RecurringJob.AddOrUpdate<ThermalModelTrainingJob>("thermal-model-training-job",
    job => job.ExecuteAsync(CancellationToken.None),
    "20 2 * * *",
    new RecurringJobOptions { TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm") });

RecurringJob.AddOrUpdate<CopModelTrainingJob>("cop-model-training-job",
    job => job.ExecuteAsync(CancellationToken.None),
    "40 2 * * *",
    new RecurringJobOptions { TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm") });

RecurringJob.AddOrUpdate<ThermalRetentionJob>("thermal-retention-job",
    job => job.ExecuteAsync(CancellationToken.None),
    "10 3 * * *",
    new RecurringJobOptions { TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm") });

// User settings endpoints
// Schedule history endpoint for frontend visualization
app.MapGet("/api/user/schedule-history", async (HttpContext ctx, ScheduleHistoryRepository historyRepo) =>
{
    var userId = GetUserId(ctx) ?? "default";
    var entries = await historyRepo.LoadAsync(userId);
    var result = entries.Select(e => new {
        timestamp = e.Timestamp.ToString("o"),
        date = e.Timestamp.ToString("yyyy-MM-dd"),
        schedule = (JsonNode?)JsonNode.Parse(e.SchedulePayloadJson)
    });
    return Results.Json(result);
});
app.MapGet("/api/user/settings", async (HttpContext ctx, UserSettingsRepository settingsRepo) =>
{
    var userId = GetUserId(ctx) ?? "default";
    var entity = await settingsRepo.GetOrCreateAsync(userId);
    return Results.Json(new { 
        ComfortHours = entity.ComfortHours, 
        TurnOffPercentile = entity.TurnOffPercentile, 
        AutoApplySchedule = entity.AutoApplySchedule, 
        MaxComfortGapHours = entity.MaxComfortGapHours,
        SchedulingMode = entity.SchedulingMode,
        EcoIntervalHours = entity.EcoIntervalHours,
        EcoFlexibilityHours = entity.EcoFlexibilityHours,
        ComfortIntervalDays = entity.ComfortIntervalDays,
        ComfortFlexibilityDays = entity.ComfortFlexibilityDays,
        ComfortEarlyPercentile = entity.ComfortEarlyPercentile,
        Timezone = entity.Timezone
    }, new JsonSerializerOptions { PropertyNamingPolicy = null });
});

app.MapPost("/api/user/settings", async (HttpContext ctx, UserSettingsRepository settingsRepo) =>
{
    var userId = GetUserId(ctx) ?? "default";
    var body = await JsonNode.ParseAsync(ctx.Request.Body) as JsonObject;
    if (body == null) return Results.BadRequest(new { error = "Missing body" });
    string? rawCh = body["ComfortHours"]?.ToString();
    string? rawTp = body["TurnOffPercentile"]?.ToString();
    string? rawAas = body["AutoApplySchedule"]?.ToString();
    string? rawMcgh = body["MaxComfortGapHours"]?.ToString();
    string? rawMode = body["SchedulingMode"]?.ToString();
    string? rawEih = body["EcoIntervalHours"]?.ToString();
    string? rawEfh = body["EcoFlexibilityHours"]?.ToString();
    string? rawCid = body["ComfortIntervalDays"]?.ToString();
    string? rawCfd = body["ComfortFlexibilityDays"]?.ToString();
    string? rawCep = body["ComfortEarlyPercentile"]?.ToString();
    string? rawTz = body["Timezone"]?.ToString();
    var errors = new List<string>();
    int comfortHours = 3;
    if (!string.IsNullOrWhiteSpace(rawCh))
    { if (!int.TryParse(rawCh, out comfortHours) || comfortHours < 1 || comfortHours > 12) { errors.Add("ComfortHours must be an integer between 1 and 12"); comfortHours = 3; } }
    double turnOffPercentile = 0.9;
    if (!string.IsNullOrWhiteSpace(rawTp))
    { if (!double.TryParse(rawTp, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out turnOffPercentile) || turnOffPercentile < 0.5 || turnOffPercentile > 0.99) { errors.Add("TurnOffPercentile must be a number between 0.5 and 0.99"); turnOffPercentile = 0.9; } }
    bool autoApplySchedule = false;
    if (!string.IsNullOrWhiteSpace(rawAas))
    { if (!bool.TryParse(rawAas, out autoApplySchedule)) { errors.Add("AutoApplySchedule must be true or false"); autoApplySchedule = false; } }
    int maxComfortGapHours = 28;
    if (!string.IsNullOrWhiteSpace(rawMcgh))
    { if (!int.TryParse(rawMcgh, out maxComfortGapHours) || maxComfortGapHours < 1 || maxComfortGapHours > 72) { errors.Add("MaxComfortGapHours must be an integer between 1 and 72"); maxComfortGapHours = 28; } }
    // Flexible scheduling fields
    string? schedulingMode = null;
    if (!string.IsNullOrWhiteSpace(rawMode))
    {
        if (rawMode != "Classic" && rawMode != "Flexible") { errors.Add("SchedulingMode must be 'Classic' or 'Flexible'"); }
        else { schedulingMode = rawMode; }
    }
    int? ecoIntervalHours = null;
    if (!string.IsNullOrWhiteSpace(rawEih))
    { if (!int.TryParse(rawEih, out var eih) || eih < 6 || eih > 36) { errors.Add("EcoIntervalHours must be an integer between 6 and 36"); } else { ecoIntervalHours = eih; } }
    int? ecoFlexibilityHours = null;
    if (!string.IsNullOrWhiteSpace(rawEfh))
    { if (!int.TryParse(rawEfh, out var efh) || efh < 1 || efh > 18) { errors.Add("EcoFlexibilityHours must be an integer between 1 and 18"); } else { ecoFlexibilityHours = efh; } }
    int? comfortIntervalDays = null;
    if (!string.IsNullOrWhiteSpace(rawCid))
    { if (!int.TryParse(rawCid, out var cid) || cid < 7 || cid > 90) { errors.Add("ComfortIntervalDays must be an integer between 7 and 90"); } else { comfortIntervalDays = cid; } }
    int? comfortFlexibilityDays = null;
    if (!string.IsNullOrWhiteSpace(rawCfd))
    { if (!int.TryParse(rawCfd, out var cfd) || cfd < 1 || cfd > 30) { errors.Add("ComfortFlexibilityDays must be an integer between 1 and 30"); } else { comfortFlexibilityDays = cfd; } }
    double? comfortEarlyPercentile = null;
    if (!string.IsNullOrWhiteSpace(rawCep))
    { if (!double.TryParse(rawCep, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var cep) || cep < 0.01 || cep > 0.50) { errors.Add("ComfortEarlyPercentile must be a number between 0.01 and 0.50"); } else { comfortEarlyPercentile = cep; } }
    string? timezone = null;
    if (!string.IsNullOrWhiteSpace(rawTz))
    {
        if (!ValidTimezones.Contains(rawTz)) { errors.Add("Timezone must be one of: auto, Europe/Stockholm, Europe/Oslo, Europe/Copenhagen, Europe/Helsinki"); }
        else { timezone = rawTz; }
    }
    if (errors.Count > 0) return Results.BadRequest(new { error = "Validation failed", errors });
    await settingsRepo.SaveSettingsAsync(userId, comfortHours, turnOffPercentile, autoApplySchedule, maxComfortGapHours,
        schedulingMode, ecoIntervalHours, ecoFlexibilityHours, comfortIntervalDays, comfortFlexibilityDays, comfortEarlyPercentile,
        timezone);
    return Results.Ok(new { saved = true });
});

app.MapGet("/api/user/flexible-state", async (HttpContext ctx, FlexibleScheduleStateRepository flexRepo, UserSettingsRepository settingsRepo, PriceRepository priceRepo, IConfiguration cfg) =>
{
    var userId = GetUserId(ctx) ?? "default";
    var state = await flexRepo.GetOrCreateAsync(userId);
    var settings = await settingsRepo.GetOrCreateAsync(userId);
    
    // Compute window info
    var now = DateTimeOffset.UtcNow;
    DateTimeOffset? ecoWindowStart = null, ecoWindowEnd = null;
    DateTimeOffset? comfortWindowStart = null, comfortWindowEnd = null;
    double? comfortWindowProgress = null;

    if (state.LastEcoRunUtc.HasValue)
    {
        ecoWindowStart = state.LastEcoRunUtc.Value.AddHours(settings.EcoIntervalHours - settings.EcoFlexibilityHours);
        ecoWindowEnd = state.LastEcoRunUtc.Value.AddHours(settings.EcoIntervalHours + settings.EcoFlexibilityHours);
    }
    if (state.LastComfortRunUtc.HasValue)
    {
        comfortWindowStart = state.LastComfortRunUtc.Value.AddDays(settings.ComfortIntervalDays - settings.ComfortFlexibilityDays);
        comfortWindowEnd = state.LastComfortRunUtc.Value.AddDays(settings.ComfortIntervalDays + settings.ComfortFlexibilityDays);
        if (comfortWindowStart.Value < comfortWindowEnd.Value)
        {
            comfortWindowProgress = Math.Clamp(
                (now - comfortWindowStart.Value).TotalHours / (comfortWindowEnd.Value - comfortWindowStart.Value).TotalHours,
                0.0, 1.0);
        }
    }

    // Compute threshold data
    decimal? currentThreshold = null;
    decimal? baseThreshold = null;
    double trendFactor = 1.0;
    string currency = cfg["Price:Nordpool:Currency"] ?? "SEK";

    var zone = await settingsRepo.GetUserZoneAsync(userId);
    var histStats = await HistoricalPriceAnalyzer.GetHistoricalStatsAsync(priceRepo, zone, settings.ComfortEarlyPercentile);
    if (histStats.PercentileThreshold.HasValue && histStats.MaxPrice.HasValue)
    {
        trendFactor = histStats.TrendFactor;
        baseThreshold = histStats.PercentileThreshold.Value;
        var adjustedBase = HistoricalPriceAnalyzer.ApplyTrendFactor(baseThreshold.Value, trendFactor);
        if (comfortWindowProgress.HasValue)
        {
            currentThreshold = HistoricalPriceAnalyzer.ComputeSlidingThreshold(
                adjustedBase, histStats.MaxPrice.Value, comfortWindowProgress.Value);
        }
        else
        {
            currentThreshold = adjustedBase; // Window not open yet, show strict threshold
        }
    }

    return Results.Json(new
    {
        LastEcoRunUtc = state.LastEcoRunUtc,
        LastComfortRunUtc = state.LastComfortRunUtc,
        NextScheduledComfortUtc = state.NextScheduledComfortUtc,
        EcoWindow = new { Start = ecoWindowStart, End = ecoWindowEnd },
        ComfortWindow = new { Start = comfortWindowStart, End = comfortWindowEnd, Progress = comfortWindowProgress },
        SchedulingMode = settings.SchedulingMode,
        CurrentThreshold = currentThreshold,
        BaseThreshold = baseThreshold,
        TrendFactor = trendFactor,
        Currency = currency
    }, new JsonSerializerOptions { PropertyNamingPolicy = null });
});

// Preload price memory from database
try
{
    using var preloadScope = app.Services.CreateScope();
    var priceRepo = preloadScope.ServiceProvider.GetRequiredService<PriceRepository>();
    var defaultZone = builder.Configuration["Price:Nordpool:DefaultZone"] ?? "SE3";
    var latestSnapshot = await priceRepo.GetLatestAsync(defaultZone);
    if (latestSnapshot != null)
    {
        var todayArr = JsonSerializer.Deserialize<JsonArray>(latestSnapshot.TodayPricesJson);
        var tomorrowArr = JsonSerializer.Deserialize<JsonArray>(latestSnapshot.TomorrowPricesJson);
        if (todayArr != null || tomorrowArr != null)
        {
            PriceMemory.Set(todayArr, tomorrowArr);
            Console.WriteLine($"[Startup] Preloaded price memory from database (zone={defaultZone}, date={latestSnapshot.Date})");
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"[Startup] DB preload failed: {ex.Message}");
}

// Security headers middleware (skip CSP for Swagger in Development to allow inline scripts)
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    ctx.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    // Skip CSP for Swagger UI paths — Swagger injects inline scripts that CSP would block
    if (!ctx.Request.Path.StartsWithSegments("/swagger"))
    {
        ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self'; frame-ancestors 'none'";
    }
    await next();
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
// Static files (from wwwroot)
app.UseDefaultFiles();
app.UseStaticFiles();

// CORS middleware
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = new[] { new HangfirePasswordAuthorizationFilter(hangfirePassword, builder.Configuration) }
});

// Rate limiter middleware
app.UseRateLimiter();

// Fail closed for account APIs. Static login assets and the OAuth entry/callback
// remain public; no anonymous browser identity is manufactured.
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path;
    var publicApi = path.Equals("/api/session", StringComparison.OrdinalIgnoreCase);
    var publicOAuth = path.Equals("/auth/daikin/start", StringComparison.OrdinalIgnoreCase) ||
                      path.Equals("/auth/daikin/callback", StringComparison.OrdinalIgnoreCase);
    if ((path.StartsWithSegments("/api") && !publicApi || path.StartsWithSegments("/auth/daikin") && !publicOAuth) &&
        ctx.User.Identity?.IsAuthenticated != true)
    {
        await Results.Json(new { error = "Authentication required." }, statusCode: StatusCodes.Status401Unauthorized)
            .ExecuteAsync(ctx);
        return;
    }
    await next();
});

// Double-submit antiforgery protection for every authenticated state-changing API call.
app.Use(async (ctx, next) =>
{
    if (ctx.User.Identity?.IsAuthenticated == true &&
        (HttpMethods.IsPost(ctx.Request.Method) || HttpMethods.IsPut(ctx.Request.Method) ||
         HttpMethods.IsPatch(ctx.Request.Method) || HttpMethods.IsDelete(ctx.Request.Method)))
    {
        if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/auth/daikin"))
        {
            try
            {
                await ctx.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(ctx);
            }
            catch (AntiforgeryValidationException)
            {
                await Results.Json(new { error = "Invalid or missing CSRF token." }, statusCode: StatusCodes.Status400BadRequest)
                    .ExecuteAsync(ctx);
                return;
            }
        }
    }
    await next();
});

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (PrisstyrningDbContext db, CancellationToken cancellationToken) =>
{
    try
    {
        if (!await db.Database.CanConnectAsync(cancellationToken))
            return Results.Json(new { status = "not-ready" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        var pendingMigrations = await db.Database.GetPendingMigrationsAsync(cancellationToken);
        return pendingMigrations.Any()
            ? Results.Json(new { status = "not-ready", reason = "pending-migrations" }, statusCode: StatusCodes.Status503ServiceUnavailable)
            : Results.Ok(new { status = "ready" });
    }
    catch
    {
        return Results.Json(new { status = "not-ready" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/api/session", (HttpContext context, IAntiforgery antiforgery, IConfiguration configuration) =>
{
    var tokens = antiforgery.GetAndStoreTokens(context);
    var userId = AccountAuthentication.UserId(context.User);
    return Results.Ok(new
    {
        authenticated = userId is not null,
        userId,
        isAdmin = userId is not null && AdminService.IsAdmin(configuration, userId),
        csrfToken = tokens.RequestToken
    });
});

app.MapPost("/api/session/logout", async (
    HttpContext context,
    AccountSessionService sessions,
    CancellationToken cancellationToken) =>
{
    await sessions.SignOutAsync(context, cancellationToken);
    return Results.NoContent();
}).RequireAuthorization();

string? GetUserId(HttpContext c)
{
    var userId = AccountAuthentication.UserId(c.User);
    return userId is { Length: <= MaxUserIdLength } ? userId : null;
}

// Prices group
var pricesGroup = app.MapGroup("/api/prices").WithTags("Prices");
pricesGroup.MapGet("/latest", () =>
{
    var dir = builder.Configuration["Storage:Directory"] ?? "data";
    if (!Directory.Exists(dir)) return Results.NotFound();
    var file = Directory.GetFiles(dir, "prices-*.json").OrderByDescending(f => f).FirstOrDefault();
    if (file == null) return Results.NotFound();
    return Results.File(file, "application/json");
});

// Diagnostic endpoint for Nordpool fetch debugging
app.MapGet("/api/prices/_debug/fetch", async (IHttpClientFactory httpClientFactory, HttpContext ctx, IConfiguration cfg, UserSettingsRepository settingsRepo) =>
{
    if (!IsAdminRequest(ctx, cfg)) return Results.Json(new { error = "Unauthorized" }, statusCode: 401);
    var userId = GetUserId(ctx);
    var zone = await settingsRepo.GetUserZoneAsync(userId) ?? "SE3";
    var dateStr = ctx.Request.Query["date"].FirstOrDefault();
    DateTime date = DateTime.TryParse(dateStr, out var d) ? d : DateTime.Today;
    var currency = cfg["Price:Nordpool:Currency"] ?? "SEK";
    var pageId = cfg["Price:Nordpool:PageId"];
    var client = new NordpoolClient(httpClientFactory.CreateClient("Nordpool"), currency, pageId);
    var (prices, attempts) = await client.GetDailyPricesDetailedAsync(date, zone);
    return Results.Json(new { date = date.ToString("yyyy-MM-dd"), zone, priceCount = prices.Count, prices, attempts, currency, pageId, userId });
});
app.MapGet("/api/prices/_debug/raw", async (IHttpClientFactory httpClientFactory, HttpContext ctx, IConfiguration cfg) =>
{
    if (!IsAdminRequest(ctx, cfg)) return Results.Json(new { error = "Unauthorized" }, statusCode: 401);
    var dateStr = ctx.Request.Query["date"].FirstOrDefault();
    DateTime date = DateTime.TryParse(dateStr, out var d) ? d : DateTime.Today;
    var currency = cfg["Price:Nordpool:Currency"] ?? "SEK";
    var pageId = cfg["Price:Nordpool:PageId"];
    var client = new NordpoolClient(httpClientFactory.CreateClient("Nordpool"), currency, pageId);
    return Results.Json(await client.GetRawCandidateResponsesAsync(date));
});
pricesGroup.MapGet("/memory", () =>
{
    var (today, tomorrow, updated) = PriceMemory.Get();
    if (today == null && tomorrow == null) return Results.NotFound(new { message = "No prices in memory yet" });
    return Results.Json(new { updated, today, tomorrow });
});
// Per-user zone get/set
pricesGroup.MapGet("/zone", async (HttpContext c, UserSettingsRepository settingsRepo) => {
    var userId = GetUserId(c); var zone = await settingsRepo.GetUserZoneAsync(userId); return Results.Json(new { zone });
});
pricesGroup.MapPost("/zone", async (HttpContext c, UserSettingsRepository settingsRepo) => {
    try {
        using var doc = await JsonDocument.ParseAsync(c.Request.Body);
        if (!doc.RootElement.TryGetProperty("zone", out var zEl)) return Results.BadRequest(new { error = "Missing zone" });
        var zone = zEl.GetString();
        if (!UserSettingsRepository.IsValidZone(zone)) return Results.BadRequest(new { error = "Invalid zone" });
        var userId = GetUserId(c);
        await settingsRepo.SetUserZoneAsync(userId, zone!);
        return Results.Ok(new { saved = true, zone });
    } catch (Exception ex) { Console.WriteLine($"[API Error] {ex}"); return Results.BadRequest(new { error = "An internal error occurred" }); }
});
// Get latest persisted Nordpool snapshot for zone
pricesGroup.MapGet("/nordpool/latest", async (HttpContext c, IConfiguration cfg, UserSettingsRepository settingsRepo, PriceRepository priceRepo, string? zone) => {
    zone ??= settingsRepo.GetUserZone(GetUserId(c));
    var snapshot = await priceRepo.GetLatestAsync(zone);
    if (snapshot == null) return Results.NotFound(new { error = "No snapshot" });
    return Results.Json(new { zone = snapshot.Zone, date = snapshot.Date.ToString("yyyy-MM-dd"), savedAt = snapshot.SavedAtUtc, today = JsonSerializer.Deserialize<JsonArray>(snapshot.TodayPricesJson), tomorrow = JsonSerializer.Deserialize<JsonArray>(snapshot.TomorrowPricesJson) });
});
pricesGroup.MapGet("/timeseries", async (HttpContext ctx, PriceRepository priceRepo, IConfiguration cfg) =>
{
    var source = ctx.Request.Query["source"].ToString();
    var (memToday, memTomorrow, updated) = PriceMemory.Get();
    JsonArray? today = memToday;
    JsonArray? tomorrow = memTomorrow;
    bool forceLatest = string.Equals(source, "latest", StringComparison.OrdinalIgnoreCase);
    if (forceLatest || today == null || (today.Count < 24 && DateTimeOffset.Now.Hour < 23))
    {
        try
        {
            var zone = cfg["Price:Nordpool:DefaultZone"] ?? "SE3";
            var snapshot = await priceRepo.GetLatestAsync(zone);
            if (snapshot != null)
            {
                if (today == null)
                    today = JsonSerializer.Deserialize<JsonArray>(snapshot.TodayPricesJson);
                if (tomorrow == null)
                    tomorrow = JsonSerializer.Deserialize<JsonArray>(snapshot.TomorrowPricesJson);
            }
        }
        catch (Exception ex) 
        { 
            Console.WriteLine($"[Timeseries] Failed to read price data from DB: {ex.Message}");
        }
    }
    var items = new List<(DateTimeOffset start, decimal value, string day)>();
    void Add(JsonArray? arr, string label)
    {
        if (arr == null) return;
        foreach (var n in arr)
        {
            if (n == null) continue;
            var startStr = n["start"]?.ToString();
            var valueStr = n["value"]?.ToString();
            if (!DateTimeOffset.TryParse(startStr, out var ts)) continue;
            if (!decimal.TryParse(valueStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var val)) continue;
            items.Add((ts, val, label));
        }
    }
    Add(today, "today");
    Add(tomorrow, "tomorrow");
    var ordered = items.OrderBy(i => i.start).Select(i => new { start = i.start, value = i.value, day = i.day }).ToList();
    return Results.Json(new { updated, count = ordered.Count, items = ordered, source = forceLatest ? "latest" : "memory" });
});

pricesGroup.MapGet("/threshold", async (HttpContext ctx, PriceRepository priceRepo, UserSettingsRepository settingsRepo, IConfiguration cfg) =>
{
    var percentileStr = ctx.Request.Query["percentile"].FirstOrDefault();
    var percentile = 0.1;
    if (double.TryParse(percentileStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var p))
        percentile = Math.Clamp(p, 0.01, 0.99);

    var userId = GetUserId(ctx);
    var zone = await settingsRepo.GetUserZoneAsync(userId);
    var currency = cfg["Price:Nordpool:Currency"] ?? "SEK";
    var histStats = await HistoricalPriceAnalyzer.GetHistoricalStatsAsync(priceRepo, zone, percentile);

    if (histStats.PercentileThreshold.HasValue)
    {
        return Results.Json(new
        {
            percentile,
            threshold = histStats.PercentileThreshold.Value,
            maxPrice = histStats.MaxPrice!.Value,
            trendFactor = histStats.TrendFactor,
            currency,
            lookbackDays = 60,
            zone
        });
    }
    return Results.Json(new
    {
        percentile,
        threshold = (decimal?)null,
        maxPrice = (decimal?)null,
        trendFactor = 1.0,
        currency,
        lookbackDays = 60,
        zone
    });
});

pricesGroup.MapGet("/trend", async (HttpContext ctx, PriceRepository priceRepo, UserSettingsRepository settingsRepo) =>
{
    var userId = GetUserId(ctx);
    var zone = await settingsRepo.GetUserZoneAsync(userId);
    var histStats = await HistoricalPriceAnalyzer.GetHistoricalStatsAsync(priceRepo, zone, 0.5);

    if (histStats.DailyAverages != null && histStats.DailyAverages.Count > 0)
    {
        return Results.Json(new
        {
            zone,
            trendFactor = histStats.TrendFactor,
            lookbackDays = 60,
            dailyAverages = histStats.DailyAverages.Select(da => new
            {
                date = da.Date.ToString("yyyy-MM-dd"),
                avgPrice = da.AvgPrice
            }).ToList()
        });
    }
    return Results.Json(new
    {
        zone,
        trendFactor = 1.0,
        lookbackDays = 60,
        dailyAverages = Array.Empty<object>()
    });
});

// Auth group
var daikinAuthGroup = app.MapGroup("/auth/daikin").WithTags("Daikin Auth");
daikinAuthGroup.MapGet("/start", (DaikinOAuthService daikinOAuth, HttpContext c) => { try { var url = daikinOAuth.GetAuthorizationUrl(c); return Results.Json(new { url }); } catch (Exception ex) { Console.WriteLine($"[API Error] {ex}"); return Results.BadRequest(new { error = "An internal error occurred" }); } });

daikinAuthGroup.MapGet("/callback", async (
    DaikinOAuthService daikinOAuth,
    AccountSessionService sessions,
    IConfiguration cfg,
    HttpContext c,
    string? code,
    string? state,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        return Results.BadRequest(new { error = "Missing code/state" });
    if (!DaikinOAuthService.ValidateBrowserCorrelation(c, state))
        return Results.BadRequest(new { error = "Invalid OAuth browser correlation" });
    var result = await daikinOAuth.HandleCallbackWithSubjectAsync(code, state, userId: null);
    var ok = result.Success;
    if (ok && !string.IsNullOrEmpty(result.Subject) && !string.IsNullOrEmpty(result.UserId))
    {
        await sessions.SignInAsync(c, result.UserId, result.Subject, cancellationToken);
    }
    // Secure redirect handling to avoid open redirect vulnerabilities.
    var configured = cfg["Daikin:PostAuthRedirect"];
    string finalBase;
    if (string.IsNullOrWhiteSpace(configured))
    {
        finalBase = "/"; // fallback
    }
    else if (configured.StartsWith('/'))
    {
        // Relative path within this application. Disallow protocol-relative '//' by forcing single leading slash.
        finalBase = configured.StartsWith("//") ? "/" : configured;
    }
    else if (Uri.TryCreate(configured, UriKind.Absolute, out var abs))
    {
        // Allow only https and hosts in optional allowlist
        var allowedHostsCfg = cfg["Daikin:AllowedRedirectHosts"] ?? string.Empty; // comma-separated
        var allowedHosts = allowedHostsCfg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                          .Select(h => h.ToLowerInvariant())
                                          .ToHashSet();
        var hostOk = allowedHosts.Count == 0 ? true : allowedHosts.Contains(abs.Host.ToLowerInvariant());
        if (abs.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) && hostOk)
        {
            finalBase = abs.GetLeftPart(UriPartial.Path); // drop any existing query to control params we add
        }
        else
        {
            finalBase = "/"; // unsafe absolute -> fallback
        }
    }
    else
    {
        finalBase = "/"; // invalid format
    }
    // Helper: append/replace daikinAuth param safely (supports relative URLs)
    static string AddOrReplaceQueryParam(string url, string key, string value)
    {
        url = url.TrimEnd('?', '&');
        var qIndex = url.IndexOf('?');
        if (qIndex < 0)
        {
            return QueryHelpers.AddQueryString(url, key, value);
        }
        var basePart = url.Substring(0, qIndex);
        var queryPart = url.Substring(qIndex + 1);
        var parsed = QueryHelpers.ParseQuery(queryPart);
        // Rebuild without existing key (case-insensitive)
        var rebuilt = basePart;
        foreach (var kv in parsed)
        {
            if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            // Preserve all values for duplicate query parameters
            foreach (var val in kv.Value)
            {
                rebuilt = QueryHelpers.AddQueryString(rebuilt, kv.Key, val ?? string.Empty);
            }
        }
        rebuilt = QueryHelpers.AddQueryString(rebuilt, key, value);
        return rebuilt;
    }
    var dest = AddOrReplaceQueryParam(finalBase, "daikinAuth", ok ? "ok" : "fail");
    Console.WriteLine($"[DaikinOAuth][Callback] Redirecting verifiedAccount={result.UserId is not null} success={ok} to={dest}");
    return Results.Redirect(dest, false);
});
daikinAuthGroup.MapGet("/status", async (DaikinOAuthService daikinOAuth, HttpContext c) => {
    var userId = GetUserId(c);
    var raw = await daikinOAuth.StatusAsync(userId); // anonymous object { authorized, expiresAtUtc, ... }
    try
    {
        // Use reflection to read properties safely
        var t = raw.GetType();
        var authProp = t.GetProperty("authorized");
        var expProp = t.GetProperty("expiresAtUtc");
        var authorized = authProp?.GetValue(raw) as bool?;
        var expiresAt = expProp?.GetValue(raw) as DateTimeOffset?;
        if (authorized == true && expiresAt != null && expiresAt < DateTimeOffset.UtcNow.AddMinutes(5))
        {
            await daikinOAuth.RefreshIfNeededAsync(userId, TimeSpan.FromMinutes(5));
            raw = await daikinOAuth.StatusAsync(userId);
        }
    }
    catch { }
    return Results.Json(raw);
});
daikinAuthGroup.MapPost("/refresh", async (DaikinOAuthService daikinOAuth, HttpContext c) => { var userId = GetUserId(c); var token = await daikinOAuth.RefreshIfNeededAsync(userId); return token == null ? Results.BadRequest(new { error = "Refresh failed or not authorized" }) : Results.Ok(new { refreshed = true }); }).RequireAuthorization();
daikinAuthGroup.MapGet("/debug", async (DaikinOAuthService daikinOAuth, HttpContext c, IConfiguration cfg) => { if (!IsAdminRequest(c, cfg)) return Results.Json(new { error = "Unauthorized" }, statusCode: 401); var userId = GetUserId(c); return Results.Json(new { status = await daikinOAuth.StatusAsync(userId), userId, now = DateTimeOffset.UtcNow }); });
daikinAuthGroup.MapPost("/revoke", async (DaikinOAuthService daikinOAuth, HttpContext c) => { var userId = GetUserId(c); var ok = await daikinOAuth.RevokeAsync(userId); return ok ? Results.Ok(new { revoked = true }) : Results.BadRequest(new { error = "Revoke failed" }); }).RequireAuthorization();
daikinAuthGroup.MapGet("/introspect", async (DaikinOAuthService daikinOAuth, HttpContext c, bool refresh) => { var userId = GetUserId(c); var result = await daikinOAuth.IntrospectAsync(userId, refresh); return result == null ? Results.BadRequest(new { error = "Not authorized" }) : Results.Json(result); });

// Schedule preview/apply
var scheduleGroup = app.MapGroup("/api/schedule").WithTags("Schedule");
scheduleGroup.MapGet("/preview", async (HttpContext c, UserSettingsRepository settingsRepo, BatchRunner batchRunner, IServiceScopeFactory scopeFactory) => {
    var cfg = (IConfiguration)builder.Configuration;
    var userId = GetUserId(c);
    
    // Preview should NOT persist to history - only apply should persist
    var (generated, schedulePayload, message) = await batchRunner.RunBatchAsync(cfg, userId, applySchedule: false, persist: false, scopeFactory);
    var zone = await settingsRepo.GetUserZoneAsync(userId);
    
    return Results.Json(new { schedulePayload, generated, message, zone });
});
scheduleGroup.MapPost("/apply", async (BatchRunner batchRunner, HttpContext ctx, IServiceScopeFactory scopeFactory) => await HandleApplyScheduleAsync(batchRunner, ctx, builder.Configuration, scopeFactory));
scheduleGroup.MapPost("/comfort", async (HttpContext ctx, BatchRunner batchRunner, IConfiguration cfg, DaikinOAuthService daikinOAuth) =>
{
    var userId = GetUserId(ctx) ?? "default";

    // Verify the user has a valid Daikin token before allowing schedule application
    var (token, _) = await daikinOAuth.TryGetValidAccessTokenAsync(userId);
    token ??= await daikinOAuth.RefreshIfNeededAsync(userId);
    if (token == null)
        return Results.Json(new { error = "Not authorized with Daikin. Please connect your Daikin account first." }, statusCode: 401);

    try
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var body = await reader.ReadToEndAsync();
        var json = System.Text.Json.JsonDocument.Parse(body);
        var comfortTimeStr = json.RootElement.GetProperty("comfortTime").GetString();
        if (string.IsNullOrEmpty(comfortTimeStr) || !DateTimeOffset.TryParse(comfortTimeStr, out var comfortTime))
            return Results.BadRequest(new { error = "Invalid or missing comfortTime" });

        var now = DateTimeOffset.UtcNow;
        if (comfortTime < now)
            return Results.BadRequest(new { error = "comfortTime must be in the future" });
        if (comfortTime > now.AddHours(48))
            return Results.BadRequest(new { error = "comfortTime must be within the next 48 hours" });

        var todayDate = now.Date;
        var tomorrowDate = todayDate.AddDays(1);
        var comfortDate = comfortTime.UtcDateTime.Date;
        if (comfortDate != todayDate && comfortDate != tomorrowDate)
            return Results.BadRequest(new { error = "comfortTime must be today or tomorrow" });

        var schedule = ScheduleAlgorithm.ComposeManualComfortSchedule(comfortTime);
        var schedulePayload = schedule.ToJsonString();

        var applied = await batchRunner.ApplyScheduleToDaikinAsync(cfg, schedulePayload, userId);

        var dayName = comfortTime.ToString("dddd");
        var hourStr = comfortTime.ToString("HH:mm");
        return Results.Json(new
        {
            applied,
            comfortHour = comfortTime.ToString("o"),
            message = applied
                ? $"Comfort scheduled at {hourStr} on {dayName} and applied to Daikin"
                : $"Comfort schedule composed for {hourStr} on {dayName} but could not apply to Daikin"
        });
    }
    catch (System.Text.Json.JsonException)
    {
        return Results.BadRequest(new { error = "Invalid JSON body" });
    }
    catch (KeyNotFoundException)
    {
        return Results.BadRequest(new { error = "Missing comfortTime field" });
    }
});

// Admin group
var adminGroup = app.MapGroup("/api/admin").WithTags("Admin");

bool IsAdminRequest(HttpContext ctx, IConfiguration cfg)
{
    var userId = GetUserId(ctx);
    var password = ctx.Request.Headers["X-Admin-Password"].FirstOrDefault();
    var (isAdmin, _) = AdminService.CheckAdminAccess(cfg, userId, password);
    return isAdmin;
}

static bool IsValidUserId(string? userId)
{
    if (string.IsNullOrWhiteSpace(userId) || userId.Length > 100)
        return false;
    return userId.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_');
}

adminGroup.MapGet("/status", (IConfiguration cfg, HttpContext c) =>
{
    var userId = GetUserId(c);
    var isAdmin = IsAdminRequest(c, cfg);
    return Results.Json(new { isAdmin, userId });
});

adminGroup.MapPost("/login", async (IConfiguration cfg, HttpContext c) =>
{
    var userId = GetUserId(c);
    var configuredPassword = cfg["Admin:Password"];
    if (string.IsNullOrEmpty(configuredPassword))
        return Results.Json(new { error = "No admin password configured" }, statusCode: 403);

    var password = c.Request.Headers["X-Admin-Password"].FirstOrDefault();
    if (string.IsNullOrEmpty(password) || !AdminService.SecureCompare(password, configuredPassword))
        return Results.Json(new { error = "Invalid admin password" }, statusCode: 401);

    if (!string.IsNullOrEmpty(userId))
        await AdminService.GrantAdmin(cfg, userId);

    return Results.Json(new { granted = true, userId });
}).RequireRateLimiting("admin-login");

adminGroup.MapGet("/users", async (IConfiguration cfg, HttpContext c, UserSettingsRepository settingsRepo, DaikinTokenRepository tokenRepo, ScheduleHistoryRepository historyRepo) =>
{
    if (!IsAdminRequest(c, cfg))
        return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

    var currentUserId = GetUserId(c);

    // Get all known user IDs from DB (tokens + settings + history)
    var tokenUserIds = await tokenRepo.GetAllUserIdsAsync();
    var userIds = new HashSet<string>(tokenUserIds);

    var adminUserIds = AdminService.GetAdminUserIds(cfg);
    var hangfireUserIds = AdminService.GetHangfireUserIds(cfg);
    var users = new List<object>();

    foreach (var uid in userIds)
    {
        var settings = settingsRepo.LoadScheduleSettings(uid);
        var zone = settingsRepo.GetUserZone(uid);
        var token = await tokenRepo.LoadAsync(uid);
        var daikinAuthorized = token != null;
        string? daikinExpiresAtUtc = token?.ExpiresAtUtc.ToString("o");
        string? daikinSubject = token?.DaikinSubject;

        var historyCount = await historyRepo.CountAsync(uid);
        var hasScheduleHistory = historyCount > 0;
        int? scheduleCount = hasScheduleHistory ? historyCount : null;
        string? lastScheduleDate = null;
        if (hasScheduleHistory)
        {
            var entries = await historyRepo.LoadAsync(uid);
            lastScheduleDate = entries.FirstOrDefault()?.Timestamp.ToString("o");
        }

        users.Add(new
        {
            userId = uid,
            settings = new { settings.ComfortHours, settings.TurnOffPercentile, settings.MaxComfortGapHours },
            zone,
            daikinAuthorized,
            daikinExpiresAtUtc,
            daikinSubject,
            hasScheduleHistory,
            scheduleCount,
            lastScheduleDate,
            isAdmin = adminUserIds.Contains(uid),
            hasHangfireAccess = hangfireUserIds.Contains(uid),
            isCurrentUser = uid == currentUserId
        });
    }

    return Results.Json(new { users }, new JsonSerializerOptions { PropertyNamingPolicy = null });
});

adminGroup.MapPost("/users/{userId}/grant", async (IConfiguration cfg, HttpContext c, string userId) =>
{
    if (!IsAdminRequest(c, cfg))
        return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

    if (!IsValidUserId(userId))
        return Results.Json(new { error = "Invalid user ID" }, statusCode: 400);

    await AdminService.GrantAdmin(cfg, userId);
    return Results.Json(new { granted = true, userId });
});

adminGroup.MapDelete("/users/{userId}/grant", async (IConfiguration cfg, HttpContext c, string userId) =>
{
    if (!IsAdminRequest(c, cfg))
        return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

    if (!IsValidUserId(userId))
        return Results.Json(new { error = "Invalid user ID" }, statusCode: 400);

    var currentUserId = GetUserId(c);
    if (userId == currentUserId)
        return Results.Json(new { error = "Cannot revoke your own admin access" }, statusCode: 400);

    await AdminService.RevokeAdmin(cfg, userId);
    return Results.Json(new { revoked = true, userId });
});

adminGroup.MapPost("/users/{userId}/hangfire", async (IConfiguration cfg, HttpContext c, string userId) =>
{
    if (!IsAdminRequest(c, cfg))
        return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

    if (!IsValidUserId(userId))
        return Results.Json(new { error = "Invalid user ID" }, statusCode: 400);

    await AdminService.GrantHangfireAccess(cfg, userId);
    return Results.Json(new { granted = true, userId });
});

adminGroup.MapDelete("/users/{userId}/hangfire", async (IConfiguration cfg, HttpContext c, string userId) =>
{
    if (!IsAdminRequest(c, cfg))
        return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

    if (!IsValidUserId(userId))
        return Results.Json(new { error = "Invalid user ID" }, statusCode: 400);

    await AdminService.RevokeHangfireAccess(cfg, userId);
    return Results.Json(new { revoked = true, userId });
});

adminGroup.MapDelete("/users/{userId}", async (IConfiguration cfg, HttpContext c, string userId, DaikinTokenRepository tokenRepo, ScheduleHistoryRepository historyRepo) =>
{
    if (!IsAdminRequest(c, cfg))
        return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

    if (!IsValidUserId(userId))
        return Results.Json(new { error = "Invalid user ID" }, statusCode: 400);

    var currentUserId = GetUserId(c);
    if (userId == currentUserId)
        return Results.Json(new { error = "Cannot delete your own user" }, statusCode: 400);

    var deleted = false;
    var warnings = new List<string>();

    // Delete tokens from database
    try
    {
        await tokenRepo.DeleteAsync(userId);
        deleted = true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Admin] Failed to delete tokens for user {userId}: {ex.Message}");
        warnings.Add("Failed to delete tokens");
    }

    // Delete schedule history from database
    try
    {
        var historyDeleted = await historyRepo.DeleteAllOlderThanAsync(DateTimeOffset.MinValue);
        deleted = true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Admin] Failed to delete schedule history for user {userId}: {ex.Message}");
        warnings.Add("Failed to delete schedule history");
    }

    // Remove from admin.json if present
    try
    {
        await AdminService.RevokeAdmin(cfg, userId);
        await AdminService.RevokeHangfireAccess(cfg, userId);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Admin] Failed to update admin.json for user {userId}: {ex.Message}");
        warnings.Add("Failed to update admin configuration");
    }

    if (!deleted)
        return Results.Json(new { error = "User not found" }, statusCode: 404);

    return Results.Json(new { deleted = true, userId, warnings });
});

// Daikin data group
var daikinGroup = app.MapGroup("/api/daikin").WithTags("Daikin");
daikinGroup.MapGet("/installation", async (
    HttpContext context,
    DaikinInstallationService installations,
    CancellationToken cancellationToken) =>
{
    var installation = await installations.GetAsync(GetUserId(context)! , cancellationToken);
    return installation is null ? Results.NoContent() : Results.Ok(installation);
});
daikinGroup.MapPost("/installation/discover", async (
    HttpContext context,
    DaikinOAuthService daikinOAuth,
    DaikinInstallationService installations,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var userId = GetUserId(context)!;
    var (token, _) = await daikinOAuth.TryGetValidAccessTokenAsync(userId);
    token ??= await daikinOAuth.RefreshIfNeededAsync(userId);
    if (token is null) return Results.Unauthorized();
    var log = configuration.GetValue("Daikin:Http:Log", false);
    var client = new DaikinApiClient(httpClientFactory.CreateClient("Daikin"), token, log, false, null, configuration["Daikin:ApiBaseUrl"]);
    try { return Results.Ok(await installations.GetOrDiscoverAsync(userId, client, cancellationToken)); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
});
// Simple proxy for sites (needed by frontend Sites button) – user-scoped
// Extracted method for /apply endpoint logic
async Task<IResult> HandleApplyScheduleAsync(BatchRunner batchRunner, HttpContext ctx, IConfiguration configuration, IServiceScopeFactory scopeFactory)
{
    var userId = GetUserId(ctx);
    var result = await batchRunner.RunBatchAsync(configuration, userId, applySchedule: false, persist: true, scopeFactory);
    return Results.Json(new { generated = result.generated, schedulePayload = result.schedulePayload, message = result.message });
}
daikinGroup.MapGet("/sites", async (IHttpClientFactory httpClientFactory, DaikinOAuthService daikinOAuth, IConfiguration cfg, HttpContext c) =>
{
    var userId = GetUserId(c);
    var (token, _) = await daikinOAuth.TryGetValidAccessTokenAsync(userId);
    token ??= await daikinOAuth.RefreshIfNeededAsync(userId);
    if (token == null) return Results.BadRequest(new { error = "Not authorized" });
    try
    {
    bool log = (cfg["Daikin:Http:Log"] ?? cfg["Daikin:HttpLog"])?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
    bool logBody = (cfg["Daikin:Http:LogBody"] ?? cfg["Daikin:HttpLogBody"])?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
    int.TryParse(cfg["Daikin:Http:BodySnippetLength"], out var bodyLen);
    var baseApi = cfg["Daikin:ApiBaseUrl"];
    var client = new DaikinApiClient(httpClientFactory.CreateClient("Daikin"), token, log, logBody, bodyLen == 0 ? null : bodyLen, baseApi);
        var sitesJson = await client.GetSitesAsync();
        return Results.Content(sitesJson, "application/json");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[API Error] {ex}");
        return Results.BadRequest(new { error = "An internal error occurred" });
    }
});
// Simplified current schedule via gateway-devices
daikinGroup.MapGet("/gateway/schedule", async (IHttpClientFactory httpClientFactory, DaikinOAuthService daikinOAuth, IConfiguration cfg, HttpContext ctx) =>
{
    var deviceId = ctx.Request.Query["deviceId"].FirstOrDefault();
    var embeddedIdQuery = ctx.Request.Query["embeddedId"].FirstOrDefault();
    Console.WriteLine($"[GatewaySchedule] start deviceId={deviceId} embeddedIdQuery={embeddedIdQuery}");
    var userId = GetUserId(ctx);
    var (token, _) = await daikinOAuth.TryGetValidAccessTokenAsync(userId);
    token ??= await daikinOAuth.RefreshIfNeededAsync(userId);
    if (token == null) return Results.Json(new { status="unauthorized", error="Not authorized" });
    try
    {
    var baseApi = cfg["Daikin:ApiBaseUrl"];
    var client = new DaikinApiClient(httpClientFactory.CreateClient("Daikin"), token, log:true, baseApiOverride:baseApi);
    var json = await client.GetDevicesCachedAsync("_ignored", TimeSpan.FromSeconds(10));
        if (string.IsNullOrWhiteSpace(json)) return Results.Json(new { status="error", error="Empty gateway-devices" });
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return Results.Json(new { status="error", error="Unexpected root" });
        JsonElement? dev = null;
        foreach (var d in doc.RootElement.EnumerateArray())
        {
            if (deviceId == null) { dev = d; break; }
            if (d.TryGetProperty("id", out var idEl) && idEl.GetString() == deviceId) { dev = d; break; }
        }
        if (dev == null) return Results.Json(new { status="error", error="Device not found", requestedDeviceId=deviceId });

        // Helper to extract schedule container and metadata from a schedule node (supports DHW schedule.value nesting)
        (bool ok, JsonElement container, string? detectedMode, string? currentScheduleId) Extract(JsonElement scheduleNode)
        {
            string? curId = null; string? mode = null; JsonElement container = default;
            if (scheduleNode.ValueKind != JsonValueKind.Object) return (false, container, null, null);
            // If node has a 'value' object (DHW often wraps data) prefer descending once
            if (scheduleNode.TryGetProperty("value", out var valueNode) && valueNode.ValueKind==JsonValueKind.Object)
            {
                scheduleNode = valueNode;
            }
            // Primary: modes collection
            if (scheduleNode.TryGetProperty("modes", out var modesRoot) && modesRoot.ValueKind==JsonValueKind.Object)
            {
                foreach (var mProp in modesRoot.EnumerateObject())
                {
                    var mVal = mProp.Value;
                    if (mode==null && mVal.TryGetProperty("schedules", out var schTest) && schTest.ValueKind==JsonValueKind.Object)
                    { mode = mProp.Name; container = schTest; }
                    if (mVal.TryGetProperty("currentSchedule", out var curObj) && curObj.TryGetProperty("value", out var curValEl))
                    { curId = curValEl.GetString(); }
                }
                if (mode!=null) return (true, container, mode, curId);
            }
            // Direct schedules property
            if (scheduleNode.TryGetProperty("schedules", out var direct) && direct.ValueKind==JsonValueKind.Object)
            {
                return (true, direct, mode ?? "heating", curId);
            }
            // Heuristic: some DHW variants expose scheduleNode.{waterHeating|domesticHotWaterHeating|dhw}.schedules
            foreach (var prop in scheduleNode.EnumerateObject())
            {
                var pVal = prop.Value;
                if (pVal.ValueKind==JsonValueKind.Object && pVal.TryGetProperty("schedules", out var scheds) && scheds.ValueKind==JsonValueKind.Object)
                {
                    // detect currentSchedule if present
                    if (pVal.TryGetProperty("currentSchedule", out var curObj) && curObj.TryGetProperty("value", out var curValEl))
                        curId = curValEl.GetString();
                    mode = prop.Name;
                    return (true, scheds, mode, curId);
                }
            }
            return (false, container, null, null);
        }

        string? embeddedId=null; string? mpType=null; JsonElement schedulesContainer=default; string? detectedMode=null; string? currentScheduleId=null; List<string> candidateEmbeddedIds=new();
        if (dev.Value.TryGetProperty("managementPoints", out var mps) && mps.ValueKind==JsonValueKind.Array)
        {
            var mpList = mps.EnumerateArray().ToList();
            foreach (var mp in mpList)
            {
                if (mp.TryGetProperty("embeddedId", out var embElAll)) { var v=embElAll.GetString(); if (v!=null) candidateEmbeddedIds.Add(v); }
            }
            Func<IEnumerable<JsonElement>, (string? emb,string? type, JsonElement container,string? mode,string? cur)> tryPick = (source) =>
            {
                foreach (var mp in source)
                {
                    if (!mp.TryGetProperty("embeddedId", out var embEl)) continue; var embVal = embEl.GetString();
                    if (embeddedIdQuery!=null && embVal != embeddedIdQuery) continue;
                    if (!mp.TryGetProperty("managementPointType", out var typeEl)) continue; var typeStr = typeEl.GetString();
                    if (!mp.TryGetProperty("schedule", out var scheduleNode)) continue;
                    var ex = Extract(scheduleNode);
                    if (!ex.ok) continue;
                    return (embVal, typeStr, ex.container, ex.detectedMode, ex.currentScheduleId);
                }
                return (null,null,default(JsonElement),null,null);
            };
            // Priority order: requested embeddedId -> domesticHotWaterTank -> climateControl -> anything with schedule
            (embeddedId, mpType, schedulesContainer, detectedMode, currentScheduleId) = tryPick(mpList.Where(mp=>mp.TryGetProperty("managementPointType", out var t1) && t1.GetString()=="domesticHotWaterTank"));
            if (embeddedId==null)
                (embeddedId, mpType, schedulesContainer, detectedMode, currentScheduleId) = tryPick(mpList.Where(mp=>mp.TryGetProperty("managementPointType", out var t1) && t1.GetString()=="climateControl"));
            if (embeddedId==null)
                (embeddedId, mpType, schedulesContainer, detectedMode, currentScheduleId) = tryPick(mpList);
            // If user explicitly requested embeddedId but we picked different, try forcing exact
            if (embeddedIdQuery!=null && embeddedId!=embeddedIdQuery)
            {
                (embeddedId, mpType, schedulesContainer, detectedMode, currentScheduleId) = tryPick(mpList.Where(mp=> mp.TryGetProperty("embeddedId", out var e2) && e2.GetString()==embeddedIdQuery));
            }
        }
        if (embeddedId==null)
        {
            return Results.Json(new { status="error", error="No schedule", requestedEmbeddedId=embeddedIdQuery, candidateEmbeddedIds });
        }
        if (schedulesContainer.ValueKind!=JsonValueKind.Object)
        {
            // Include raw schedule node (first few chars) for debugging if present
            string? scheduleRaw = null;
            try
            {
                if (dev.Value.TryGetProperty("managementPoints", out var mps2) && mps2.ValueKind==JsonValueKind.Array)
                {
                    foreach (var mp in mps2.EnumerateArray())
                    {
                        if (mp.TryGetProperty("embeddedId", out var eId) && eId.GetString()==(embeddedIdQuery??embeddedId))
                        {
                            if (mp.TryGetProperty("schedule", out var sNode)) scheduleRaw = sNode.GetRawText();
                            break;
                        }
                    }
                }
            } catch {}
            if (scheduleRaw!=null && scheduleRaw.Length > MaxScheduleRawDisplayLength) 
                scheduleRaw = scheduleRaw.Substring(0, MaxScheduleRawDisplayLength)+"...";
            return Results.Json(new { status="error", error="No schedules container", embeddedId, requestedEmbeddedId=embeddedIdQuery, candidateEmbeddedIds, scheduleRaw });
        }
        string? chosen = currentScheduleId; Dictionary<string, JsonElement> dict=new();
        foreach (var p in schedulesContainer.EnumerateObject()) { dict[p.Name]=p.Value; if (chosen==null) chosen=p.Name; }
        JsonObject? payload=null;
        if (chosen!=null && dict.TryGetValue(chosen, out var sch) && sch.TryGetProperty("actions", out var acts))
        {
            var root=new JsonObject(); var sObj=new JsonObject(); sObj["actions"]=JsonNode.Parse(acts.GetRawText()); root[chosen]=sObj; payload=root;
        }
        var id = dev.Value.TryGetProperty("id", out var idEl2)? idEl2.GetString():null;
        Console.WriteLine($"[GatewaySchedule] ok deviceId={id} embeddedId={embeddedId} chosen={chosen} detectedMode={detectedMode}");
        return Results.Json(new { status=payload==null?"warning":"ok", deviceId=id, embeddedId, mpType, currentScheduleId, chosenScheduleId=chosen, schedulePayload=payload, schedules=dict.Keys, detectedMode, requestedEmbeddedId=embeddedIdQuery, candidateEmbeddedIds });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[GatewaySchedule][Exception] {ex}");
        return Results.Json(new { status="error", error="An internal error occurred" });
    }
});
daikinGroup.MapGet("/devices", async (IHttpClientFactory httpClientFactory, DaikinOAuthService daikinOAuth, IConfiguration cfg, HttpContext c, string? siteId) =>
{
    var userId = GetUserId(c);
    var (token, _) = await daikinOAuth.TryGetValidAccessTokenAsync(userId);
    token ??= await daikinOAuth.RefreshIfNeededAsync(userId);
    if (token == null) return Results.BadRequest(new { error = "Not authorized" });
    try
    {
    bool log = (cfg["Daikin:Http:Log"] ?? cfg["Daikin:HttpLog"])?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
    bool logBody = (cfg["Daikin:Http:LogBody"] ?? cfg["Daikin:HttpLogBody"])?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
    int.TryParse(cfg["Daikin:Http:BodySnippetLength"], out var bodyLen);
    var baseApi = cfg["Daikin:ApiBaseUrl"];
    var client = new DaikinApiClient(httpClientFactory.CreateClient("Daikin"), token, log, logBody, bodyLen == 0 ? null : bodyLen, baseApi);
        if (string.IsNullOrWhiteSpace(siteId))
        {
            var sitesJson = await client.GetSitesAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(sitesJson);
            siteId = doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0 ? doc.RootElement[0].GetProperty("id").GetString() : null;
            if (siteId == null) return Results.BadRequest(new { error = "No site found" });
        }
        var devicesJson = await client.GetDevicesAsync(siteId);
        return Results.Content(devicesJson, "application/json");
    }
    catch (Exception ex) { Console.WriteLine($"[API Error] {ex}"); return Results.BadRequest(new { error = "An internal error occurred" }); }
});
// Simplified gateway devices proxy: return raw array from Daikin
daikinGroup.MapGet("/gateway", async (IHttpClientFactory httpClientFactory, DaikinOAuthService daikinOAuth, IConfiguration cfg, HttpContext c) =>
{
    var userId = GetUserId(c);
    var (token, _) = await daikinOAuth.TryGetValidAccessTokenAsync(userId);
    token ??= await daikinOAuth.RefreshIfNeededAsync(userId);
    if (token == null) return Results.BadRequest(new { error = "Not authorized" });
    try
    {
    bool log = (cfg["Daikin:Http:Log"] ?? cfg["Daikin:HttpLog"])?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
    bool logBody = (cfg["Daikin:Http:LogBody"] ?? cfg["Daikin:HttpLogBody"])?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
    int.TryParse(cfg["Daikin:Http:BodySnippetLength"], out var bodyLen);
    var baseApi = cfg["Daikin:ApiBaseUrl"];
    var client = new DaikinApiClient(httpClientFactory.CreateClient("Daikin"), token, log, logBody, bodyLen == 0 ? null : bodyLen, baseApi);
        var devicesJson = await client.GetDevicesAsync("_ignored");
        return Results.Content(devicesJson, "application/json");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[API Error] {ex}");
        return Results.BadRequest(new { error = "An internal error occurred" });
    }
});


// PUT (upload) a schedule payload to a gateway device management point + optionally activate a scheduleId (mode auto-detect if omitted or 'auto')
daikinGroup.MapPost("/gateway/schedule/put", async (IHttpClientFactory httpClientFactory, DaikinOAuthService daikinOAuth, DaikinInstallationService installations, IConfiguration cfg, HttpContext ctx, ScheduleHistoryRepository historyRepo) =>
{
    var userId = GetUserId(ctx);
    var (token, _) = await daikinOAuth.TryGetValidAccessTokenAsync(userId);
    token ??= await daikinOAuth.RefreshIfNeededAsync(userId);
    if (token == null) return Results.BadRequest(new { error = "Not authorized" });
    try
    {
        // Parse JSON body
        JsonNode? body = await JsonNode.ParseAsync(ctx.Request.Body);
        if (body == null) return Results.BadRequest(new { error = "Missing body" });
        string? gatewayDeviceId = body["gatewayDeviceId"]?.ToString();
        string? embeddedId = body["embeddedId"]?.ToString();
    string requestedMode = body["mode"]?.ToString() ?? "auto"; // 'auto' triggers detection
        JsonNode? schedulePayloadNode = body["schedulePayload"];
        string? activateScheduleId = body["activateScheduleId"]?.ToString();
        if (schedulePayloadNode == null)
            return Results.BadRequest(new { error = "schedulePayload is required" });

        // Serialize schedule payload exactly as provided
        var schedulePayloadJson = schedulePayloadNode.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

    bool log = (cfg["Daikin:Http:Log"] ?? cfg["Daikin:HttpLog"])?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
    bool logBody = (cfg["Daikin:Http:LogBody"] ?? cfg["Daikin:HttpLogBody"])?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
    int.TryParse(cfg["Daikin:Http:BodySnippetLength"], out var bodyLen);
    var baseApi = cfg["Daikin:ApiBaseUrl"];
    var client = new DaikinApiClient(httpClientFactory.CreateClient("Daikin"), token, log, logBody, bodyLen == 0 ? null : bodyLen, baseApi);
        var accountInstallation = await installations.GetAsync(userId!);

        // Auto-detect device IDs if not provided
        string? siteId = null;
        if (string.IsNullOrWhiteSpace(gatewayDeviceId) || string.IsNullOrWhiteSpace(embeddedId))
        {
            var overrideSite = accountInstallation?.SiteId;
            var overrideDevice = accountInstallation?.DeviceId;
            var overrideEmbedded = accountInstallation?.DhwManagementPointEmbeddedId;

            string? detectedSite = null;
            string? detectedDevice = null;
            string? detectedEmbedded = null;

            // Detect site
            if (!string.IsNullOrWhiteSpace(overrideSite))
                detectedSite = overrideSite;
            else
            {
                var sitesJson = await client.GetSitesAsync();
                detectedSite = DeviceAutoDetection.GetFirstSiteId(sitesJson);
                if (detectedSite != null)
                    Console.WriteLine($"[SchedulePut] Auto-detected site: {detectedSite}");
            }

            if (detectedSite == null)
                return Results.BadRequest(new { error = "Could not auto-detect site. No Daikin sites found." });

            siteId = detectedSite;

            // Detect device
            if (!string.IsNullOrWhiteSpace(overrideDevice))
                detectedDevice = overrideDevice;
            else
            {
                var devicesJson = await client.GetDevicesAsync(detectedSite);
                var (deviceId, deviceJsonRaw) = DeviceAutoDetection.GetFirstDevice(devicesJson);
                detectedDevice = deviceId;

                // Also detect embedded ID from the device
                if (!string.IsNullOrWhiteSpace(overrideEmbedded))
                    detectedEmbedded = overrideEmbedded;
                else if (deviceJsonRaw != null)
                {
                    detectedEmbedded = DeviceAutoDetection.FindDhwEmbeddedId(deviceJsonRaw);
                    if (detectedEmbedded != null)
                        Console.WriteLine($"[SchedulePut] Auto-detected DHW embeddedId: {detectedEmbedded}");
                }

                if (detectedDevice != null)
                    Console.WriteLine($"[SchedulePut] Auto-detected device: {detectedDevice}");
            }

            if (detectedDevice == null)
                return Results.BadRequest(new { error = "Could not auto-detect device. No Daikin devices found." });

            if (detectedEmbedded == null)
                return Results.BadRequest(new { error = "Could not auto-detect DHW management point. No domesticHotWaterTank found on device." });

            // Use detected values if not provided in request
            gatewayDeviceId ??= detectedDevice;
            embeddedId ??= detectedEmbedded;
        }

        string modeUsed = requestedMode;
        if (modeUsed == "auto" || string.IsNullOrWhiteSpace(modeUsed))
        {
            // Fetch devices to detect mode (need site ID)
            if (siteId == null)
            {
                // If we didn't auto-detect above, we need to get the site
                var overrideSite = accountInstallation?.SiteId;
                if (!string.IsNullOrWhiteSpace(overrideSite))
                    siteId = overrideSite;
                else
                {
                    var sitesJson = await client.GetSitesAsync();
                    using var siteDoc = JsonDocument.Parse(sitesJson);
                    if (siteDoc.RootElement.ValueKind == JsonValueKind.Array && siteDoc.RootElement.GetArrayLength() > 0)
                    {
                        siteId = siteDoc.RootElement[0].GetProperty("id").GetString();
                    }
                }
            }

            try
            {
                var devicesJson = siteId != null ? await client.GetDevicesAsync(siteId) : "[]";
                using var doc = JsonDocument.Parse(devicesJson);
                if (doc.RootElement.ValueKind==JsonValueKind.Array)
                {
                    foreach (var d in doc.RootElement.EnumerateArray())
                    {
                        if (d.TryGetProperty("id", out var idEl) && idEl.GetString()==gatewayDeviceId)
                        {
                            if (d.TryGetProperty("managementPoints", out var mps) && mps.ValueKind==JsonValueKind.Array)
                            {
                                foreach (var mp in mps.EnumerateArray())
                                {
                                    if (mp.TryGetProperty("embeddedId", out var emb2) && emb2.GetString()==embeddedId)
                                    {
                                        if (mp.TryGetProperty("schedule", out var schNode))
                                        {
                                            // DHW devices wrap schedule data in a 'value' property – unwrap it (mirrors Extract logic)
                                            var schTarget = schNode;
                                            if (schTarget.TryGetProperty("value", out var schValue) && schValue.ValueKind == JsonValueKind.Object)
                                                schTarget = schValue;
                                            if (schTarget.TryGetProperty("modes", out var modesNode) && modesNode.ValueKind==JsonValueKind.Object)
                                            {
                                                // prefer heating, waterHeating, cooling order
                                                string[] pref = new[]{"heating","waterHeating","cooling","dhw","domesticHotWaterHeating"};
                                                var available = modesNode.EnumerateObject().Select(o=>o.Name).ToList();
                                                var picked = pref.FirstOrDefault(p=>available.Contains(p)) ?? available.FirstOrDefault();
                                                if (picked!=null) modeUsed = picked; else modeUsed = "heating";
                                            }
                                            else if (schTarget.TryGetProperty("schedules", out _))
                                            {
                                                modeUsed = "heating"; // generic
                                            }
                                        }
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception exDetect)
            {
                Console.WriteLine($"[SchedulePut] mode auto-detect failed: {exDetect.Message}");
                modeUsed = "heating"; // fallback
            }

            // Final guard: 'auto' is not a valid API mode — always fall back to 'heating'
            if (modeUsed == "auto")
            {
                Console.WriteLine("[SchedulePut] mode still 'auto' after detection – falling back to 'heating'");
                modeUsed = "heating";
            }
        }

        Console.WriteLine($"[SchedulePut] PUT device={gatewayDeviceId} embedded={embeddedId} mode={modeUsed}");
        await client.PutSchedulesAsync(gatewayDeviceId, embeddedId, modeUsed, schedulePayloadJson);
    
    // Save to schedule history
    if (schedulePayloadNode is JsonObject scheduleObj && !string.IsNullOrWhiteSpace(userId))
    {
        try
        {
            await historyRepo.SaveAsync(userId, scheduleObj, DateTimeOffset.UtcNow);
            Console.WriteLine($"[SchedulePut] Saved schedule to history for user {userId}");
        }
        catch (Exception exHist)
        {
            Console.WriteLine($"[SchedulePut] Failed to save history for user {userId}: {exHist.Message}");
        }
    }
    
    // Activation step removed: only PUT schedule, do not activate
    return Results.Ok(new { put = true, activateScheduleId, modeUsed, requestedMode });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[API Error] {ex}");
        return Results.BadRequest(new { error = "An internal error occurred" });
    }
});

app.MapThermalApi();

// SPA fallback: serve index.html for client-side routes (excluding /api and /auth)
app.MapFallback(async (HttpContext ctx) =>
{
    var path = ctx.Request.Path.Value ?? "";
    
    // Don't intercept API or auth endpoints
    if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) || 
        path.StartsWith("/auth/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/hangfire", StringComparison.OrdinalIgnoreCase))
    {
        ctx.Response.StatusCode = 404;
        await ctx.Response.WriteAsync("Not Found");
        return;
    }
    
    // Serve index.html for SPA routes like /settings, /history, etc.
    var indexPath = Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "index.html");
    if (File.Exists(indexPath))
    {
        ctx.Response.ContentType = "text/html";
        await ctx.Response.SendFileAsync(indexPath);
    }
    else
    {
        ctx.Response.StatusCode = 404;
        await ctx.Response.WriteAsync("Frontend not built. Run: cd frontend && npm run build");
    }
});

await app.RunAsync();

// Hangfire dashboard authorization filter with password protection
internal sealed class HangfirePasswordAuthorizationFilter : IDashboardAuthorizationFilter
{
    private readonly string? _password;
    private readonly IConfiguration _cfg;

    public HangfirePasswordAuthorizationFilter(
        string? password,
        IConfiguration cfg)
    {
        _password = password;
        _cfg = cfg;
    }

    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();

        var userId = AccountAuthentication.UserId(httpContext.User);
        if (AdminService.IsValidUserId(userId))
        {
            if (AdminService.HasHangfireAccess(_cfg, userId))
                return true;

            // Check 2: Also allow admins
            if (AdminService.IsAdmin(_cfg, userId))
                return true;
        }

        // Check 3: Original Basic Auth password check
        if (string.IsNullOrWhiteSpace(_password))
        {
            httpContext.Response.StatusCode = 401;
            httpContext.Response.Headers["WWW-Authenticate"] = "Basic realm=\"Hangfire Dashboard\"";
            return false;
        }

        var authHeader = httpContext.Request.Headers["Authorization"].ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            httpContext.Response.StatusCode = 401;
            httpContext.Response.Headers["WWW-Authenticate"] = "Basic realm=\"Hangfire Dashboard\"";
            return false;
        }

        try
        {
            var encodedCredentials = authHeader.Substring(6).Trim();
            var decodedCredentials = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encodedCredentials));
            var parts = decodedCredentials.Split(':', 2);

            if (parts.Length == 2)
            {
                var providedPassword = parts[1];
                if (AdminService.SecureCompare(providedPassword, _password))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Invalid authorization header format
        }

        // Authentication failed
        httpContext.Response.StatusCode = 401;
        httpContext.Response.Headers["WWW-Authenticate"] = "Basic realm=\"Hangfire Dashboard\"";
        return false;
    }
}
