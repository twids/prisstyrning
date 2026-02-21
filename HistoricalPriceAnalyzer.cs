using System.Text.Json;
using Prisstyrning.Data.Repositories;

/// <summary>
/// Computes price percentiles from historical PriceSnapshot data.
/// Used by the comfort algorithm to determine if a current price is
/// "historically cheap" enough to trigger an early comfort run.
/// </summary>
public static class HistoricalPriceAnalyzer
{
    /// <summary>
    /// Result of historical price analysis containing both the percentile threshold
    /// and the maximum observed price.
    /// </summary>
    public sealed record HistoricalPriceStats(decimal? PercentileThreshold, decimal? MaxPrice);

    /// <summary>
    /// Computes the value at the given percentile from a collection of prices
    /// using sorted array + linear interpolation.
    /// </summary>
    /// <param name="prices">Collection of price values.</param>
    /// <param name="percentile">Percentile to compute (0.0 to 1.0).</param>
    /// <returns>The percentile value, or null if prices is empty.</returns>
    public static decimal? ComputePercentile(IEnumerable<decimal> prices, double percentile)
    {
        var sorted = prices.OrderBy(p => p).ToArray();
        if (sorted.Length == 0)
            return null;

        if (sorted.Length == 1)
            return sorted[0];

        var clamped = Math.Clamp(percentile, 0.0, 1.0);

        // Map percentile to index position using linear interpolation
        var position = clamped * (sorted.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);

        if (lower == upper)
            return sorted[lower];

        var fraction = (decimal)(position - lower);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * fraction;
    }

    /// <summary>
    /// Fetches historical price data for a zone and computes both the percentile
    /// threshold and the maximum observed price.
    /// </summary>
    /// <param name="repo">PriceRepository for data access.</param>
    /// <param name="zone">Price zone (e.g. "SE3").</param>
    /// <param name="percentile">Percentile to compute (0.0 to 1.0).</param>
    /// <param name="lookbackDays">Number of days to look back (default: 60).</param>
    /// <returns>Stats with percentile threshold and max price, both null if no data.</returns>
    public static async Task<HistoricalPriceStats> GetHistoricalStatsAsync(
        PriceRepository repo, string zone, double percentile, int lookbackDays = 60)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = today.AddDays(-lookbackDays);

        var snapshots = await repo.GetByDateRangeAsync(zone, from, today);

        var allPrices = new List<decimal>();
        foreach (var snapshot in snapshots)
        {
            ExtractPricesFromJson(snapshot.TodayPricesJson, allPrices);
        }

        if (allPrices.Count == 0)
            return new HistoricalPriceStats(null, null);

        var threshold = ComputePercentile(allPrices, percentile);
        var maxPrice = allPrices.Max();

        return new HistoricalPriceStats(threshold, maxPrice);
    }

    /// <summary>
    /// Computes the sliding price threshold at the given comfort window progress.
    /// At progress=0 (window just opened), returns baseThreshold (strict, only cheap prices trigger).
    /// At progress=1 (deadline), returns maxPrice (accept any available price).
    /// Linear interpolation between base and max.
    /// </summary>
    /// <param name="baseThreshold">The base percentile threshold (strict, historically cheap).</param>
    /// <param name="maxPrice">The maximum observed historical price.</param>
    /// <param name="windowProgress">Progress through the comfort window (0.0 to 1.0).</param>
    /// <returns>The effective threshold at the current window position.</returns>
    public static decimal ComputeSlidingThreshold(decimal baseThreshold, decimal maxPrice, double windowProgress)
    {
        var clamped = Math.Clamp(windowProgress, 0.0, 1.0);
        return baseThreshold + (maxPrice - baseThreshold) * (decimal)clamped;
    }

    /// <summary>
    /// Extracts price values from a TodayPricesJson string.
    /// Expected format: [{"start":"...","value":0.52}, ...]
    /// </summary>
    private static void ExtractPricesFromJson(string json, List<decimal> prices)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]")
            return;

        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.TryGetProperty("value", out var valueProp))
                {
                    if (valueProp.TryGetDecimal(out var price))
                    {
                        prices.Add(price);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Skip malformed JSON silently
        }
    }
}
