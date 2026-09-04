using System.Text.Json;
using Prisstyrning.Thermal.Domain;

namespace Prisstyrning.Tests.Thermal;

public sealed class ThermalWireContractTests
{
    [Theory]
    [InlineData(0, ControlMode.Legacy)]
    [InlineData(1, ControlMode.Shadow)]
    [InlineData(2, ControlMode.LwtActive)]
    [InlineData(3, ControlMode.FullActive)]
    public void NumericBrowserModeRequestMatchesUnchangedAspNetJsonContract(int value, ControlMode expected)
    {
        // DTO/JSON compatibility only: never invoke a mode service or change an installation.
        var request = JsonSerializer.Deserialize<ThermalModeRequest>(
            $"{{\"mode\":{value},\"confirmed\":true}}", new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(request);
        Assert.Equal(expected, request!.Mode);
        Assert.True(request.Confirmed);
    }
}
