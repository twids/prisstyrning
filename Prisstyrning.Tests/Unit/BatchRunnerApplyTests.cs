using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Prisstyrning.Tests.Fixtures;
using Xunit;

namespace Prisstyrning.Tests.Unit;

public class BatchRunnerApplyTests
{
    private const string TestAccessToken = "test-token-12345";

    private static IConfiguration CreateTestConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Daikin:AccessToken"] = TestAccessToken,
                ["Daikin:ApplySchedule"] = "true",
                ["Daikin:ScheduleMode"] = "heating",
            })
            .Build();

    private static string CreateDeviceJson(string deviceId = "device1", string embeddedId = "2") =>
        JsonSerializer.Serialize(new[]
        {
            new
            {
                id = deviceId,
                managementPoints = new[]
                {
                    new
                    {
                        managementPointType = "domesticHotWaterTank",
                        embeddedId = embeddedId,
                    }
                }
            }
        });

    private static MockHttpMessageHandler CreateMockHandler(
        HttpStatusCode putSchedulesStatus = HttpStatusCode.NoContent,
        HttpStatusCode setCurrentStatus = HttpStatusCode.NoContent,
        string? setCurrentBody = null)
    {
        var handler = new MockHttpMessageHandler();
        // Add more-specific routes FIRST — MockHttpMessageHandler uses first substring match.
        handler.AddRoute("schedule/heating/schedules", putSchedulesStatus, "");
        handler.AddRoute("schedule/heating/current", setCurrentStatus,
            setCurrentBody ?? "");
        handler.AddRoute("/v1/sites", HttpStatusCode.OK,
            JsonSerializer.Serialize(new[] { new { id = "site1" } }));
        handler.AddRoute("gateway-devices", HttpStatusCode.OK,
            CreateDeviceJson());
        return handler;
    }

    [Fact]
    public async Task ApplySchedule_PutSucceeds_SetCurrentFails_StillReturnsTrue()
    {
        // Arrange: PutSchedules → 204, SetCurrentSchedule → 400 READ_ONLY_CHARACTERISTIC
        var handler = CreateMockHandler(
            putSchedulesStatus: HttpStatusCode.NoContent,
            setCurrentStatus: HttpStatusCode.BadRequest,
            setCurrentBody: "{\"error\":\"READ_ONLY_CHARACTERISTIC\"}");

        var factory = MockServiceFactory.CreateMockHttpClientFactory(handler);
        var oauth = MockServiceFactory.CreateMockDaikinOAuthService(factory);
        var runner = new BatchRunner(factory, oauth);

        var config = CreateTestConfig();
        var payload = "{\"schedules\":[]}";

        // Act
        var result = await runner.ApplyScheduleToDaikinAsync(config, payload, "test-user");

        // Assert: should succeed because PutSchedules worked
        Assert.True(result, "ApplyScheduleToDaikinAsync should return true when PutSchedules succeeds even if SetCurrentSchedule fails");
    }

    [Fact]
    public async Task ApplySchedule_PutFails_ReturnsFalse()
    {
        // Arrange: PutSchedules → 500 (fails)
        var handler = CreateMockHandler(
            putSchedulesStatus: HttpStatusCode.InternalServerError);

        var factory = MockServiceFactory.CreateMockHttpClientFactory(handler);
        var oauth = MockServiceFactory.CreateMockDaikinOAuthService(factory);
        var runner = new BatchRunner(factory, oauth);

        var config = CreateTestConfig();
        var payload = "{\"schedules\":[]}";

        // Act
        var result = await runner.ApplyScheduleToDaikinAsync(config, payload, "test-user");

        // Assert: should fail because PutSchedules failed
        Assert.False(result, "ApplyScheduleToDaikinAsync should return false when PutSchedules fails");
    }
}
