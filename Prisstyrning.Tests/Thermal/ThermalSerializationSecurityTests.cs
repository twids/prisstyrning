using System.Text.Json;
using Prisstyrning.Data.Entities;
using Prisstyrning.Thermal.Domain;

namespace Prisstyrning.Tests.Thermal;

public sealed class ThermalSerializationSecurityTests
{
    [Fact]
    public void ApiEntities_DoNotSerializeInstallationIdentityOrLeaseOwner()
    {
        var config = new ThermalConfigDto(
            new ThermalSiteConfig
            {
                UserId = "installation-owner",
                DhwLeaseOwner = "writer-instance",
                DhwLeaseExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5)
            },
            [new ThermalRoomConfig { UserId = "installation-owner" }],
            [new ThermalEntityConfig { UserId = "installation-owner" }]);
        var plan = new ThermalPlan
        {
            UserId = "installation-owner",
            Steps = [new ThermalPlanStep { ThermalPlan = new ThermalPlan { UserId = "nested-owner" } }]
        };
        var payload = JsonSerializer.Serialize(new object[]
        {
            config,
            plan,
            new ThermalTelemetrySample { UserId = "installation-owner" },
            new ThermalEvent { UserId = "installation-owner" },
            new DhwCycle { UserId = "installation-owner" },
            new ThermalModelVersion { UserId = "installation-owner" }
        });

        Assert.DoesNotContain("installation-owner", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("nested-owner", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("writer-instance", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("UserId", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("DhwLeaseOwner", payload, StringComparison.Ordinal);
    }
}
