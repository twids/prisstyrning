using Xunit;

namespace Prisstyrning.Tests.Unit;

/// <summary>
/// Unit tests for HistoricalPriceAnalyzer percentile computation and sliding threshold logic.
/// </summary>
public class HistoricalPriceAnalyzerTests
{
    #region ComputePercentile

    [Fact]
    public void ComputePercentile_EmptyList_ReturnsNull()
    {
        var result = HistoricalPriceAnalyzer.ComputePercentile(Enumerable.Empty<decimal>(), 0.5);

        Assert.Null(result);
    }

    [Fact]
    public void ComputePercentile_SingleValue_ReturnsThatValue()
    {
        var result = HistoricalPriceAnalyzer.ComputePercentile(new[] { 42.0m }, 0.5);

        Assert.Equal(42.0m, result);
    }

    [Fact]
    public void ComputePercentile_EvenDistribution_ReturnsCorrectPercentile()
    {
        // 100 values from 1 to 100
        var prices = Enumerable.Range(1, 100).Select(i => (decimal)i);

        var result = HistoricalPriceAnalyzer.ComputePercentile(prices, 0.5);

        Assert.NotNull(result);
        // 50th percentile of 1-100 should be approximately 50.5 (linear interpolation)
        Assert.InRange(result!.Value, 49.0m, 52.0m);
    }

    [Fact]
    public void ComputePercentile_0thPercentile_ReturnsMinimum()
    {
        var prices = new[] { 10m, 20m, 30m, 40m, 50m };

        var result = HistoricalPriceAnalyzer.ComputePercentile(prices, 0.0);

        Assert.NotNull(result);
        Assert.Equal(10m, result!.Value);
    }

    [Fact]
    public void ComputePercentile_100thPercentile_ReturnsMaximum()
    {
        var prices = new[] { 10m, 20m, 30m, 40m, 50m };

        var result = HistoricalPriceAnalyzer.ComputePercentile(prices, 1.0);

        Assert.NotNull(result);
        Assert.Equal(50m, result!.Value);
    }

    [Fact]
    public void ComputePercentile_10thPercentile_ReturnsCorrectValue()
    {
        // 100 values from 1 to 100
        var prices = Enumerable.Range(1, 100).Select(i => (decimal)i);

        var result = HistoricalPriceAnalyzer.ComputePercentile(prices, 0.10);

        Assert.NotNull(result);
        // 10th percentile of 1-100 should be approximately 10.9
        Assert.InRange(result!.Value, 9.0m, 12.0m);
    }

    #endregion

    #region ComputeSlidingThreshold

    [Fact]
    public void ComputeSlidingThreshold_AtWindowStart_ReturnsBaseThreshold()
    {
        var baseThreshold = 0.25m;
        var maxPrice = 2.50m;

        var result = HistoricalPriceAnalyzer.ComputeSlidingThreshold(baseThreshold, maxPrice, 0.0);

        Assert.Equal(baseThreshold, result);
    }

    [Fact]
    public void ComputeSlidingThreshold_AtWindowEnd_ReturnsMaxPrice()
    {
        var baseThreshold = 0.25m;
        var maxPrice = 2.50m;

        var result = HistoricalPriceAnalyzer.ComputeSlidingThreshold(baseThreshold, maxPrice, 1.0);

        Assert.Equal(maxPrice, result);
    }

    [Fact]
    public void ComputeSlidingThreshold_AtMidpoint_ReturnsInterpolatedValue()
    {
        var baseThreshold = 0.25m;
        var maxPrice = 2.50m;

        var result = HistoricalPriceAnalyzer.ComputeSlidingThreshold(baseThreshold, maxPrice, 0.5);

        // Midpoint: 0.25 + (2.50 - 0.25) * 0.5 = 0.25 + 1.125 = 1.375
        Assert.Equal(1.375m, result);
    }

    [Fact]
    public void ComputeSlidingThreshold_ProgressAboveOne_ClampedToMaxPrice()
    {
        var baseThreshold = 0.25m;
        var maxPrice = 2.50m;

        var result = HistoricalPriceAnalyzer.ComputeSlidingThreshold(baseThreshold, maxPrice, 1.5);

        Assert.Equal(maxPrice, result);
    }

    [Fact]
    public void ComputeSlidingThreshold_NegativeProgress_ClampedToBaseThreshold()
    {
        var baseThreshold = 0.25m;
        var maxPrice = 2.50m;

        var result = HistoricalPriceAnalyzer.ComputeSlidingThreshold(baseThreshold, maxPrice, -0.5);

        Assert.Equal(baseThreshold, result);
    }

    #endregion
}
