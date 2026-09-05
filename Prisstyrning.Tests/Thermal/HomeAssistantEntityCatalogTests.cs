using System.Text.Json.Nodes;
using Prisstyrning.Thermal.Domain;
using Prisstyrning.Thermal.HomeAssistant;

namespace Prisstyrning.Tests.Thermal;

public sealed class HomeAssistantEntityCatalogTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 4, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("unknown")]
    [InlineData("unavailable")]
    [InlineData(" UNAVAILABLE ")]
    [InlineData("Unknown")]
    [InlineData("")]
    [InlineData("  ")]
    public void Project_RecentlyReceivedUnavailableState_IsNeverValid(string state)
    {
        var result = Project(State(state));
        Assert.Equal(DataQuality.Unavailable, result.Quality);
        Assert.Empty(result.CompatibleUnits!);
        Assert.NotNull(result.QualityReason);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("1e999")]
    public void Project_NonFiniteValue_IsInvalid(string state)
    {
        var result = Project(State(state));
        Assert.Equal(DataQuality.Invalid, result.Quality);
        Assert.Empty(result.CompatibleUnits!);
    }

    [Theory]
    [InlineData("21.5", "°C", "°C")]
    [InlineData("0", "C", "°C")]
    [InlineData("68", "°F", "°C")]
    [InlineData("295.15", "K", "°C")]
    [InlineData("1500", "W", "kW")]
    [InlineData("1.5", "kW", "kW")]
    [InlineData("0.0015", "MW", "kW")]
    [InlineData("12", "lpm", "l/min")]
    [InlineData("0.2", "l/s", "l/min")]
    [InlineData("0.72", "m³/h", "l/min")]
    [InlineData("1.2", "kWh", "kWh")]
    [InlineData("-25", "öre/kWh", "SEK/kWh")]
    [InlineData("50", "SEK/MWh", "SEK/kWh")]
    [InlineData("0.7", "SEK/kWh", "SEK/kWh")]
    [InlineData("36", "km/h", "m/s")]
    [InlineData("10", "mph", "m/s")]
    [InlineData("100", "W/m2", "W/m²")]
    [InlineData("off", null, "bool")]
    [InlineData("false", "bool", "bool")]
    [InlineData("0", null, "bool")]
    [InlineData("ON", "boolean", "bool")]
    public void Project_SupportedConversion_OffersOnlyCompatibleUnit(string state, string? unit, string expected)
    {
        var result = Project(State(state, unit));
        Assert.Equal(DataQuality.Valid, result.Quality);
        Assert.Equal([expected], result.CompatibleUnits);
        Assert.Equal(Now, result.CheckedAtUtc);
        Assert.Equal(Now.AddMinutes(9), result.ValidUntilUtc);
    }

    [Theory]
    [InlineData("abc", "°C")]
    [InlineData("1", "furlongs")]
    [InlineData("21.5", null)]
    [InlineData("inactive", "°C")]
    [InlineData("heating", "kW")]
    [InlineData(" off ", null)]
    [InlineData("1e308", "MW")]
    public void Project_UnreadableOrUnsupportedValue_DoesNotAdvertiseUnitCompatibility(string state, string? unit)
    {
        var result = Project(State(state, unit));
        Assert.Empty(result.CompatibleUnits!);
    }

    [Fact]
    public void Project_TemperatureZero_IsNotABooleanOperatingSignal()
    {
        var result = Project(State("0"));
        Assert.Equal(["°C"], result.CompatibleUnits);
    }

    [Fact]
    public void Project_MalformedAttributes_DoesNotThrowOrReturnArbitraryObjects()
    {
        var state = State("21.5") with { Attributes = new JsonObject
        {
            ["friendly_name"] = new JsonObject { ["private"] = "do-not-display" },
            ["unit_of_measurement"] = new JsonArray("°C")
        } };
        var result = Project(state);
        Assert.Equal(DataQuality.Invalid, result.Quality);
        Assert.Equal(state.EntityId, result.FriendlyName);
        Assert.Null(result.Unit);
        Assert.DoesNotContain("do-not-display", result.QualityReason);
        var nameOnly = Project(State("21.5") with { Attributes = new JsonObject { ["friendly_name"] = 42, ["unit_of_measurement"] = "°C" } });
        Assert.Equal(DataQuality.Valid, nameOnly.Quality);
        Assert.Equal(state.EntityId, nameOnly.FriendlyName);
    }

    [Theory]
    [InlineData("missing_updated", DataQuality.Unavailable)]
    [InlineData("missing_received", DataQuality.Unavailable)]
    [InlineData("future_updated", DataQuality.Invalid)]
    [InlineData("future_received", DataQuality.Invalid)]
    [InlineData("received_before_update", DataQuality.Invalid)]
    [InlineData("stale", DataQuality.Stale)]
    public void Project_UnverifiableTimes_NeverReportValid(string scenario, DataQuality quality)
    {
        var state = State("21.5");
        state = scenario switch
        {
            "missing_updated" => state with { LastUpdatedUtc = null },
            "missing_received" => state with { ReceivedAtUtc = default },
            "future_updated" => state with { LastUpdatedUtc = Now.AddMinutes(2) },
            "future_received" => state with { ReceivedAtUtc = Now.AddMinutes(2) },
            "received_before_update" => state with { ReceivedAtUtc = Now.AddMinutes(-3) },
            _ => state with { LastChangedUtc = Now.AddMinutes(-11), LastUpdatedUtc = Now.AddMinutes(-11) }
        };
        var result = Project(state);
        Assert.Equal(quality, result.Quality);
        if (quality == DataQuality.Stale) Assert.Contains("°C", result.CompatibleUnits!);
        else Assert.Empty(result.CompatibleUnits!);
        Assert.NotNull(result.QualityReason);
    }

    [Fact]
    public void Project_AccountAgeLimit_IsUsedInQualityReasonAndExpiry()
    {
        var state = State("21.5") with { LastChangedUtc = Now.AddMinutes(-4), LastUpdatedUtc = Now.AddMinutes(-4) };
        var stale = HomeAssistantEntityCatalog.Project(state, Now, 3);
        Assert.Equal(DataQuality.Stale, stale.Quality);
        Assert.Contains("3 minuter", stale.QualityReason);
        var valid = HomeAssistantEntityCatalog.Project(state, Now, 12);
        Assert.Equal(DataQuality.Valid, valid.Quality);
        Assert.Equal(Now.AddMinutes(8), valid.ValidUntilUtc);
    }

    [Fact]
    public void Project_DisconnectedCache_CannotOfferPreliminaryApproval()
    {
        var result = HomeAssistantEntityCatalog.Project(State("21.5"), Now, 10, "Liveanslutningen är bruten.");
        Assert.Equal(DataQuality.Unavailable, result.Quality);
        Assert.Empty(result.CompatibleUnits!);
        Assert.Null(result.ValidUntilUtc);
    }

    [Theory]
    [InlineData("future", true)]
    [InlineData("past", false)]
    [InlineData("missing", false)]
    [InlineData("unknown_unit", false)]
    [InlineData("malformed_unit", false)]
    public void Project_Forecast_RequiresReadableFuturePointsAndKnownUnits(string scenario, bool compatible)
    {
        var state = State("sunny", null);
        if (scenario != "missing")
            state.Attributes["forecast"] = new JsonArray(Enumerable.Range(1, 2).Select(index => (JsonNode)new JsonObject
            {
                ["datetime"] = Now.AddHours(scenario == "past" ? -index : index).ToString("O"),
                ["temperature"] = 10 + index
            }).ToArray());
        if (scenario == "unknown_unit") state.Attributes["temperature_unit"] = "furlongs";
        if (scenario == "malformed_unit") state.Attributes["temperature_unit"] = new JsonArray("°C");
        var result = Project(state);
        Assert.Equal(compatible, result.CompatibleUnits!.Contains("forecast"));
        Assert.Equal(DataQuality.Valid, result.Quality); // availability is not role compatibility
    }

    private static ThermalEntityStateDto Project(HomeAssistantState state) => HomeAssistantEntityCatalog.Project(state, Now, 10);

    private static HomeAssistantState State(string state, string? unit = "°C") => new(
        "sensor.room", state, new JsonObject { ["friendly_name"] = "Vardagsrum", ["unit_of_measurement"] = unit },
        Now.AddMinutes(-1), Now.AddMinutes(-1), Now);
}
