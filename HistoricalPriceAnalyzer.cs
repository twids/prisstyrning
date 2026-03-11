using System.Collections.Concurrent;
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
    /// Result of historical price analysis containing the percentile threshold,
    /// the maximum observed price, a trend factor, and daily averages.
    /// </summary>
    public sealed record HistoricalPriceStats(
        decimal? PercentileThreshold,
        decimal? MaxPrice,
        double TrendFactor,
        List<(DateOnly Date, decimal AvgPrice)>? DailyAverages);

    private sealed record CachedZoneData(
        decimal[] SortedPrices,
        (DateOnly Date, decimal Price)[] DatedPrices,
        decimal MaxPrice,
        double TrendFactor,
        List<(DateOnly Date, decimal AvgPrice)> DailyAverages,
        DateTimeOffset ComputedAtUtc);

    private static readonly ConcurrentDictionary<string, CachedZoneData> _zoneCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

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
        var cached = await GetOrComputeZoneCacheAsync(repo, zone, lookbackDays);
        if (cached == null)
            return new HistoricalPriceStats(null, null, 1.0, null);

        var threshold = ComputePercentile(cached.SortedPrices, percentile);
        return new HistoricalPriceStats(threshold, cached.MaxPrice, cached.TrendFactor, cached.DailyAverages);
    }

    /// <summary>
    /// Computes the sliding price threshold at the given comfort window progress.
    /// At progress=0 (window just opened), returns baseThreshold (strict, only cheap prices trigger).
    /// At progress=1 (deadline), returns maxPrice (accept any available price).
    /// Cubic interpolation (x³) between base and max — stays low early, rises steeply near deadline.
    /// </summary>
    /// <param name="baseThreshold">The base percentile threshold (strict, historically cheap).</param>
    /// <param name="maxPrice">The maximum observed historical price.</param>
    /// <param name="windowProgress">Progress through the comfort window (0.0 to 1.0).</param>
    /// <returns>The effective threshold at the current window position.</returns>
    public static decimal ComputeSlidingThreshold(decimal baseThreshold, decimal maxPrice, double windowProgress)
    {
        var clamped = Math.Clamp(windowProgress, 0.0, 1.0);
        return baseThreshold + (maxPrice - baseThreshold) * (decimal)(clamped * clamped * clamped);
    }

    /// <summary>
    /// Computes a trend factor from dated price data.
    /// Returns avg(recent N days) / avg(baseline M days), clamped to [0.5, 2.0].
    /// &gt; 1.0 = prices rising, &lt; 1.0 = prices falling, ~1.0 = stable.
    /// </summary>
    public static double ComputeTrendFactor(
        IEnumerable<(DateOnly Date, decimal Price)> datedPrices,
        int recentDays = 7, int baselineDays = 30)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var recentCutoff = today.AddDays(-recentDays);
        var baselineCutoff = today.AddDays(-baselineDays);

        var grouped = datedPrices
            .GroupBy(p => p.Date)
            .Select(g => (Date: g.Key, Avg: g.Average(p => (double)p.Price)))
            .ToList();

        var recentAvgs = grouped.Where(g => g.Date >= recentCutoff && g.Date <= today).ToList();
        var baselineAvgs = grouped.Where(g => g.Date >= baselineCutoff && g.Date <= today).ToList();

        if (recentAvgs.Count == 0 || baselineAvgs.Count == 0)
            return 1.0;

        var recentAvg = recentAvgs.Average(g => g.Avg);
        var baselineAvg = baselineAvgs.Average(g => g.Avg);

        if (baselineAvg == 0.0)
            return 1.0;

        return Math.Clamp(recentAvg / baselineAvg, 0.5, 2.0);
    }

    /// <summary>
    /// Applies a trend factor to a base threshold, clamping the factor to [0.5, 2.0].
    /// </summary>
    public static decimal ApplyTrendFactor(decimal baseThreshold, double trendFactor)
    {
        return baseThreshold * (decimal)Math.Clamp(trendFactor, 0.5, 2.0);
    }

    /// <summary>
    /// Clears the zone price cache. Intended for testing.
    /// </summary>
    internal static void ClearCache() => _zoneCache.Clear();

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

    /// <summary>
    /// Extracts price values from a TodayPricesJson string, tagged with the snapshot date.
    /// </summary>
    private static void ExtractDatedPricesFromJson(string json, DateOnly date, List<(DateOnly Date, decimal Price)> datedPrices)
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
                        datedPrices.Add((date, price));
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Skip malformed JSON silently
        }
    }

    private static async Task<CachedZoneData?> GetOrComputeZoneCacheAsync(
        PriceRepository repo, string zone, int lookbackDays = 60)
    {
        var cacheKey = zone.Trim().ToUpperInvariant();

        if (_zoneCache.TryGetValue(cacheKey, out var existing) &&
            DateTimeOffset.UtcNow - existing.ComputedAtUtc < CacheTtl)
        {
            return existing;
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = today.AddDays(-lookbackDays);

        var snapshots = await repo.GetByDateRangeAsync(zone, from, today);

        var datedPrices = new List<(DateOnly Date, decimal Price)>();
        foreach (var snapshot in snapshots)
        {
            ExtractDatedPricesFromJson(snapshot.TodayPricesJson, snapshot.Date, datedPrices);
        }

        if (datedPrices.Count == 0)
            return null;

        var sortedPrices = datedPrices.Select(dp => dp.Price).OrderBy(p => p).ToArray();
        var maxPrice = sortedPrices[^1];
        var trendFactor = ComputeTrendFactor(datedPrices);
        var dailyAverages = datedPrices
            .GroupBy(dp => dp.Date)
            .Select(g => (Date: g.Key, AvgPrice: Math.Round(g.Average(p => p.Price), 4)))
            .OrderBy(g => g.Date)
            .ToList();

        var cached = new CachedZoneData(
            sortedPrices, datedPrices.ToArray(), maxPrice,
            trendFactor, dailyAverages, DateTimeOffset.UtcNow);

        _zoneCache[cacheKey] = cached;
        return cached;
    }
}
