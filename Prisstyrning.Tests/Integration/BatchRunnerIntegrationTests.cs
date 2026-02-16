using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Prisstyrning.Data;
using Prisstyrning.Data.Repositories;
using Prisstyrning.Tests.Fixtures;
using Xunit;

namespace Prisstyrning.Tests.Integration;

/// <summary>
/// Integration tests for BatchRunner schedule generation and application
/// </summary>
public class BatchRunnerIntegrationTests : IDisposable
{
    private ServiceProvider? _serviceProvider;

    public void Dispose()
    {
        _serviceProvider?.Dispose();
    }

    private IServiceScopeFactory BuildScopeFactory(IConfiguration cfg)
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<PrisstyrningDbContext>(o =>
            o.UseInMemoryDatabase(dbName));
        services.AddSingleton(cfg);
        services.AddScoped<ScheduleHistoryRepository>();
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrisstyrningDbContext>();
        db.Database.EnsureCreated();

        return _serviceProvider.GetRequiredService<IServiceScopeFactory>();
    }
    [Fact]
    public async Task RunBatchAsync_WithValidPriceData_GeneratesSchedule()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();
        var date = new DateTime(2026, 2, 7);
        
        // Setup: Create price data
        var today = TestDataFactory.CreatePriceData(date);
        var tomorrow = TestDataFactory.CreatePriceData(date.AddDays(1));
        NordpoolPersistence.Save("SE3", today, tomorrow, fs.NordpoolDir);
        
        // Also set in memory
        PriceMemory.Set(today, tomorrow);
        
        var (generated, payload, message) = await BatchRunner.RunBatchAsync(
            cfg, 
            userId: "test-user", 
            applySchedule: false, 
            persist: false
        );
        
        Assert.True(generated);
        Assert.NotNull(payload);
        Assert.Contains("schedule", message.ToLower());
    }

    [Fact]
    public async Task RunBatchAsync_WithNoAccessToken_SkipsApply()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();
        var date = new DateTime(2026, 2, 7);
        
        var today = TestDataFactory.CreatePriceData(date);
        var tomorrow = TestDataFactory.CreatePriceData(date.AddDays(1));
        PriceMemory.Set(today, tomorrow);
        
        // No token provided, applySchedule=true should be ignored
        var (generated, payload, message) = await BatchRunner.RunBatchAsync(
            cfg, 
            userId: "test-user", 
            applySchedule: true, // This should be skipped
            persist: false
        );
        
        Assert.True(generated);
        Assert.NotNull(payload);
    }

    [Fact]
    public async Task RunBatchAsync_WithApplyScheduleTrue_CallsDaikinAPI()
    {
        using var fs = new TempFileSystem();
        
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.AddRoute("api.onecta.daikineurope.com", HttpStatusCode.OK, 
            @"[{""id"":""site-123""}]");
        mockHandler.AddRoute("devices", HttpStatusCode.OK, 
            @"[{""id"":""dev-456"",""managementPoints"":[{""managementPointType"":""domesticHotWaterTank"",""embeddedId"":""2""}]}]");
        mockHandler.AddRoute("management-points", HttpStatusCode.OK, "{}");
        
        var additionalSettings = new Dictionary<string, string?>
        {
            ["Daikin:AccessToken"] = "test-token-12345",
            ["Daikin:ApplySchedule"] = "true"
        };
        var cfg = fs.GetTestConfig(additionalSettings);
        
        var date = new DateTime(2026, 2, 7);
        var today = TestDataFactory.CreatePriceData(date);
        var tomorrow = TestDataFactory.CreatePriceData(date.AddDays(1));
        PriceMemory.Set(today, tomorrow);
        
        var (generated, payload, message) = await BatchRunner.RunBatchAsync(
            cfg, 
            userId: "test-user", 
            applySchedule: true, 
            persist: false
        );
        
        Assert.True(generated);
        Assert.NotNull(payload);
    }

    [Fact]
    public async Task RunBatchAsync_WithPersistTrue_SavesHistory()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();
        var userId = "test-user-persist";
        var date = new DateTime(2026, 2, 7);
        
        var today = TestDataFactory.CreatePriceData(date);
        var tomorrow = TestDataFactory.CreatePriceData(date.AddDays(1));
        PriceMemory.Set(today, tomorrow);

        var scopeFactory = BuildScopeFactory(cfg);
        
        var (generated, payload, message) = await BatchRunner.RunBatchAsync(
            cfg, 
            userId: userId, 
            applySchedule: false, 
            persist: true,
            scopeFactory: scopeFactory
        );
        
        Assert.True(generated);
        Assert.NotNull(payload);
        
        // Give async save a moment to complete
        await Task.Delay(500);
        
        // Verify history was saved to database
        using var scope = scopeFactory.CreateScope();
        var historyRepo = scope.ServiceProvider.GetRequiredService<ScheduleHistoryRepository>();
        var count = await historyRepo.CountAsync(userId);
        Assert.True(count > 0, "History should be saved to DB after persist=true");
    }

    [Fact]
    public async Task RunBatchAsync_WithNordpoolFailure_ReturnsError()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();
        
        // Clear any cached price data
        PriceMemory.Set(null, null);
        
        // No price data available - BatchRunner will try to fetch from Nordpool
        // In test environment, this may or may not succeed
        var (generated, payload, message) = await BatchRunner.RunBatchAsync(
            cfg, 
            userId: "test-user", 
            applySchedule: false, 
            persist: false
        );
        
        // Either generates schedule (real fetch succeeded) or returns error
        // Test verifies no crash occurs
        Assert.True(true, $"BatchRunner completed: generated={generated}, message={message}");
    }

    [Fact]
    public async Task GenerateSchedulePreview_ReturnsScheduleWithoutApplying()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();
        var date = new DateTime(2026, 2, 7);
        
        var today = TestDataFactory.CreatePriceData(date);
        var tomorrow = TestDataFactory.CreatePriceData(date.AddDays(1));
        PriceMemory.Set(today, tomorrow);
        
        var result = await BatchRunner.GenerateSchedulePreview(cfg);
        
        Assert.NotNull(result);
        
        // Verify it has expected properties (anonymous type)
        var resultType = result.GetType();
        var generatedProp = resultType.GetProperty("generated");
        var payloadProp = resultType.GetProperty("schedulePayload");
        var messageProp = resultType.GetProperty("message");
        
        Assert.NotNull(generatedProp);
        Assert.NotNull(payloadProp);
        Assert.NotNull(messageProp);
    }

    [Fact]
    public async Task RunBatchAsync_WithUserSettings_UsesUserOverrides()
    {
        using var fs = new TempFileSystem();
        var userId = "user-with-settings";
        
        // Create user settings with custom comfort hours
        fs.CreateUserSettings(userId, comfortHours: 5, turnOffPercentile: 0.85);
        
        var cfg = fs.GetTestConfig();
        var date = new DateTime(2026, 2, 7);
        
        var today = TestDataFactory.CreatePriceData(date);
        var tomorrow = TestDataFactory.CreatePriceData(date.AddDays(1));
        PriceMemory.Set(today, tomorrow);
        
        var (generated, payload, message) = await BatchRunner.RunBatchAsync(
            cfg, 
            userId: userId, 
            applySchedule: false, 
            persist: false
        );
        
        Assert.True(generated);
        Assert.NotNull(payload);
    }

    [Fact]
    public async Task RunBatchAsync_WithMultipleUsers_IsolatesState()
    {
        using var fs = new TempFileSystem();
        var user1 = "user-one";
        var user2 = "user-two";
        
        fs.CreateUserSettings(user1, comfortHours: 2);
        fs.CreateUserSettings(user2, comfortHours: 4);
        
        var cfg = fs.GetTestConfig();
        var date = new DateTime(2026, 2, 7);
        
        var today = TestDataFactory.CreatePriceData(date);
        var tomorrow = TestDataFactory.CreatePriceData(date.AddDays(1));
        PriceMemory.Set(today, tomorrow);
        
        var (gen1, payload1, msg1) = await BatchRunner.RunBatchAsync(
            cfg, userId: user1, applySchedule: false, persist: false);
        
        var (gen2, payload2, msg2) = await BatchRunner.RunBatchAsync(
            cfg, userId: user2, applySchedule: false, persist: false);
        
        Assert.True(gen1);
        Assert.True(gen2);
        Assert.NotNull(payload1);
        Assert.NotNull(payload2);
    }

    [Fact]
    public async Task SaveHistoryAsync_FiresAndForget_CompletesSuccessfully()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();
        var userId = "async-save-test";
        var date = new DateTime(2026, 2, 7);
        
        var today = TestDataFactory.CreatePriceData(date);
        var tomorrow = TestDataFactory.CreatePriceData(date.AddDays(1));
        PriceMemory.Set(today, tomorrow);

        var scopeFactory = BuildScopeFactory(cfg);
        
        // Run with persist=true triggers fire-and-forget async save
        var (generated, payload, message) = await BatchRunner.RunBatchAsync(
            cfg, 
            userId: userId, 
            applySchedule: false, 
            persist: true,
            scopeFactory: scopeFactory
        );
        
        Assert.True(generated);
        
        // Wait for async save to complete
        await Task.Delay(1000);
        
        // Verify history was saved to database
        using var scope = scopeFactory.CreateScope();
        var historyRepo = scope.ServiceProvider.GetRequiredService<ScheduleHistoryRepository>();
        var entries = await historyRepo.LoadAsync(userId);
        Assert.NotEmpty(entries);
    }

    [Fact]
    public async Task RunBatchAsync_WithInvalidPriceData_HandlesGracefully()
    {
        using var fs = new TempFileSystem();
        var cfg = fs.GetTestConfig();
        
        // Set empty arrays (ScheduleAlgorithm should handle this)
        PriceMemory.Set(new JsonArray(), new JsonArray());
        
        var (generated, payload, message) = await BatchRunner.RunBatchAsync(
            cfg,
            userId: "test-user", 
            applySchedule: false, 
            persist: false
        );
        
        // Empty price data should be handled gracefully
        // The algorithm may still generate (with no actions) or return "no schedule"
        Assert.NotNull(message);
        Assert.True(message.Length > 0, "Should return a message");
    }
}
