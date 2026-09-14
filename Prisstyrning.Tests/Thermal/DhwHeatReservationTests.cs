using Prisstyrning.Thermal.Optimization;

namespace Prisstyrning.Tests.Thermal;

public sealed class DhwHeatReservationTests
{
    private static readonly DateTimeOffset Horizon = new(2026, 9, 14, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, 45, 0, 3)]
    [InlineData(10, 45, 0, 4)]
    [InlineData(20, 60, 1, 5)]
    [InlineData(30, 60, 2, 4)]
    [InlineData(-10, 30, 0, 2)]
    public void Rasterize_ReservesEveryOverlappingQuarter(int startMinute, int duration, int first, int steps)
    {
        Assert.Equal((first, steps), DhwHeatReservation.Rasterize(
            Horizon.AddMinutes(startMinute), Horizon.AddMinutes(startMinute + duration), Horizon, 15, 192));
    }

    [Theory]
    [InlineData(14.4, 60, false)]
    [InlineData(14.4, 45, true)]
    [InlineData(17, 60, true)]
    public void CanCoast_UsesFullReservedDurationAndSolverCooling(double outside, int minutes, bool expected)
    {
        var thermal = new EmhassThermalConfig(5, .175, 0, 21.5,
            Enumerable.Repeat(21d, 192).ToArray(), Enumerable.Repeat(22.2, 192).ToArray());
        var start = Horizon.AddHours(2);
        var candidate = new DhwCandidate(start, start.AddMinutes(minutes), 1, 0, 1, true);
        Assert.Equal(expected, DhwHeatReservation.CanCoast(candidate, Horizon, 15, thermal,
            Enumerable.Repeat(outside, 192).ToArray()));
    }

    [Fact]
    public void CanCoast_UsesObservedTemperatureForAlreadyStartingReservation()
    {
        var thermal = new EmhassThermalConfig(5, .175, 0, 21,
            Enumerable.Repeat(21d, 192).ToArray(), Enumerable.Repeat(22.2, 192).ToArray());
        var candidate = new DhwCandidate(Horizon, Horizon.AddMinutes(15), 1, 0, 1, true);
        Assert.False(DhwHeatReservation.CanCoast(candidate, Horizon, 15, thermal,
            Enumerable.Repeat(14d, 192).ToArray()));
    }
}
