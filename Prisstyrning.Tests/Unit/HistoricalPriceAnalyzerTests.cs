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

    #region ComputeSlidingThreshold (Cubic)

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
    public void ComputeSlidingThreshold_AtMidpoint_ReturnsCubicValue()
    {
        var baseThreshold = 0.25m;
        var maxPrice = 2.50m;

        var result = HistoricalPriceAnalyzer.ComputeSlidingThreshold(baseThreshold, maxPrice, 0.5);

        // Cubic midpoint: 0.25 + (2.50 - 0.25) * (0.5^3) = 0.25 + 2.25 * 0.125 = 0.53125
        Assert.Equal(0.53125m, result);
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

    [Fact]
    public void ComputeSlidingThreshold_AtQuarter_ReturnsCubicValue()
    {
        var baseThreshold = 0.25m;
        var maxPrice = 2.50m;

        var result = HistoricalPriceAnalyzer.ComputeSlidingThreshold(baseThreshold, maxPrice, 0.25);

        // 0.25 + 2.25 * (0.25^3) = 0.25 + 2.25 * 0.015625 = 0.25 + 0.03515625 = 0.28515625
        Assert.Equal(0.28515625m, result);
    }

    [Fact]
    public void ComputeSlidingThreshold_At90Percent_StillBelowMax()
    {
        var baseThreshold = 0.25m;
        var maxPrice = 2.50m;

        var result = HistoricalPriceAnalyzer.ComputeSlidingThreshold(baseThreshold, maxPrice, 0.9);

        // 0.25 + 2.25 * (0.9^3) = 0.25 + 2.25 * 0.729 = 0.25 + 1.64025 = 1.89025
        Assert.Equal(1.89025m, result);
    }

    #endregion

    #region ComputeTrendFactor

    [Fact]
    public void ComputeTrendFactor_StablePrices_ReturnsNearOne()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var prices = Enumerable.Range(0, 30)
            .Select(i => (Date: today.AddDays(-i), Price: 1.0m))
            .ToList();

        var result = HistoricalPriceAnalyzer.ComputeTrendFactor(prices);

        Assert.Equal(1.0, result, 2);
    }

    [Fact]
    public void ComputeTrendFactor_FallingPrices_ReturnsBelowOne()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        // Recent 7 days: avg = 0.5, baseline 30 days includes older higher prices
        var prices = new List<(DateOnly Date, decimal Price)>();
        for (int i = 0; i < 7; i++)
            prices.Add((today.AddDays(-i), 0.50m));
        for (int i = 7; i < 30; i++)
            prices.Add((today.AddDays(-i), 2.00m));

        var result = HistoricalPriceAnalyzer.ComputeTrendFactor(prices);

        Assert.True(result < 1.0, $"Expected < 1.0, got {result}");
    }

    [Fact]
    public void ComputeTrendFactor_RisingPrices_ReturnsAboveOne()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var prices = new List<(DateOnly Date, decimal Price)>();
        for (int i = 0; i < 7; i++)
            prices.Add((today.AddDays(-i), 3.00m));
        for (int i = 7; i < 30; i++)
            prices.Add((today.AddDays(-i), 1.00m));

        var result = HistoricalPriceAnalyzer.ComputeTrendFactor(prices);

        Assert.True(result > 1.0, $"Expected > 1.0, got {result}");
    }

    [Fact]
    public void ComputeTrendFactor_NoData_ReturnsOne()
    {
        var result = HistoricalPriceAnalyzer.ComputeTrendFactor(
            Enumerable.Empty<(DateOnly Date, decimal Price)>());

        Assert.Equal(1.0, result);
    }

    [Fact]
    public void ComputeTrendFactor_ClampedAt2()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var prices = new List<(DateOnly Date, decimal Price)>();
        for (int i = 0; i < 7; i++)
            prices.Add((today.AddDays(-i), 100.00m));
        for (int i = 7; i < 30; i++)
            prices.Add((today.AddDays(-i), 1.00m));

        var result = HistoricalPriceAnalyzer.ComputeTrendFactor(prices);

        Assert.Equal(2.0, result);
    }

    [Fact]
    public void ComputeTrendFactor_ClampedAt05()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var prices = new List<(DateOnly Date, decimal Price)>();
        for (int i = 0; i < 7; i++)
            prices.Add((today.AddDays(-i), 0.01m));
        for (int i = 7; i < 30; i++)
            prices.Add((today.AddDays(-i), 10.00m));

        var result = HistoricalPriceAnalyzer.ComputeTrendFactor(prices);

        Assert.Equal(0.5, result);
    }

    #endregion

    #region ApplyTrendFactor

    [Fact]
    public void ApplyTrendFactor_MultiplyBase()
    {
        var result = HistoricalPriceAnalyzer.ApplyTrendFactor(1.00m, 1.5);

        Assert.Equal(1.50m, result);
    }

    [Fact]
    public void ApplyTrendFactor_ClampsHighFactor()
    {
        var result = HistoricalPriceAnalyzer.ApplyTrendFactor(1.00m, 5.0);

        Assert.Equal(2.00m, result);
    }

    [Fact]
    public void ApplyTrendFactor_ClampsLowFactor()
    {
        var result = HistoricalPriceAnalyzer.ApplyTrendFactor(1.00m, 0.1);

        Assert.Equal(0.50m, result);
    }

    #endregion
}
