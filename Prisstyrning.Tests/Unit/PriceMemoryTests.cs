using System.Text.Json.Nodes;
using Prisstyrning.Tests.Fixtures;
using Xunit;

namespace Prisstyrning.Tests.Unit;

/// <summary>
/// Unit tests for PriceMemory thread-safe in-memory storage
/// </summary>
public class PriceMemoryTests
{
    [Fact]
    public void Set_StoresDataCorrectly()
    {
        var today = TestDataFactory.CreatePriceData(DateTime.Today);
        var tomorrow = TestDataFactory.CreatePriceData(DateTime.Today.AddDays(1));
        
        PriceMemory.Set(today, tomorrow);
        
        var (loadedToday, loadedTomorrow, lastUpdated) = PriceMemory.Get();
        
        Assert.NotNull(loadedToday);
        Assert.NotNull(loadedTomorrow);
        Assert.NotNull(lastUpdated);
        Assert.Equal(24, loadedToday.Count);
        Assert.Equal(24, loadedTomorrow.Count);
        Assert.True((DateTimeOffset.UtcNow - lastUpdated.Value).TotalSeconds < 5);
    }

    [Fact]
    public void Get_ReturnsDefensiveCopies()
    {
        var today = TestDataFactory.CreatePriceData(DateTime.Today);
        var tomorrow = TestDataFactory.CreatePriceData(DateTime.Today.AddDays(1));
        
        PriceMemory.Set(today, tomorrow);
        
        // Get first copy
        var (copy1Today, _, _) = PriceMemory.Get();
        
        // Modify the copy
        copy1Today!.Add(new JsonObject { ["test"] = "modified" });
        
        // Get second copy - should not be affected by modification
        var (copy2Today, _, _) = PriceMemory.Get();
        
        Assert.NotNull(copy2Today);
        Assert.Equal(24, copy2Today.Count); // Still original count, not 25
        Assert.DoesNotContain(copy2Today, item => 
            item is JsonObject obj && obj.ContainsKey("test"));
    }

    [Fact]
    public void GetReadOnly_ReturnsReferenceForPerformance()
    {
        var today = TestDataFactory.CreatePriceData(DateTime.Today);
        var tomorrow = TestDataFactory.CreatePriceData(DateTime.Today.AddDays(1));
        
        PriceMemory.Set(today, tomorrow);
        
        // GetReadOnly should return the same reference (not a copy)
        var (ref1Today, ref1Tomorrow, _) = PriceMemory.GetReadOnly();
        var (ref2Today, ref2Tomorrow, _) = PriceMemory.GetReadOnly();
        
        // These should be the exact same reference
        Assert.Same(ref1Today, ref2Today);
        Assert.Same(ref1Tomorrow, ref2Tomorrow);
    }

    [Fact]
    public void Set_ConcurrentAccess_ThreadSafe()
    {
        var prices = TestDataFactory.CreatePriceData(DateTime.Today);
        
        // Run 100 concurrent Set operations
        var tasks = Enumerable.Range(0, 100).Select(i => 
            Task.Run(() => {
                var today = TestDataFactory.CreatePriceData(DateTime.Today.AddDays(i % 3));
                var tomorrow = TestDataFactory.CreatePriceData(DateTime.Today.AddDays((i % 3) + 1));
                PriceMemory.Set(today, tomorrow);
            })
        ).ToArray();
        
        Task.WaitAll(tasks);
        
        // Should complete without corruption
        var (result, _, _) = PriceMemory.Get();
        Assert.NotNull(result);
        Assert.Equal(24, result.Count); // No corruption
    }

    [Fact]
    public void Get_AfterSet_PreservesOriginalData()
    {
        var originalToday = TestDataFactory.CreatePriceData(new DateTime(2026, 2, 7));
        var originalTomorrow = TestDataFactory.CreatePriceData(new DateTime(2026, 2, 8));
        
        // Store original JSON for comparison
        var originalTodayJson = originalToday.ToJsonString();
        var originalTomorrowJson = originalTomorrow.ToJsonString();
        
        PriceMemory.Set(originalToday, originalTomorrow);
        
        // Modify the source arrays after Set
        originalToday.Clear();
        originalTomorrow.Clear();
        
        // Get should return the stored data, not affected by source modification
        var (loadedToday, loadedTomorrow, _) = PriceMemory.Get();
        
        Assert.NotNull(loadedToday);
        Assert.NotNull(loadedTomorrow);
        Assert.Equal(24, loadedToday.Count);
        Assert.Equal(24, loadedTomorrow.Count);
        
        // Content should match original, not the cleared arrays
        Assert.Equal(originalTodayJson, loadedToday.ToJsonString());
        Assert.Equal(originalTomorrowJson, loadedTomorrow.ToJsonString());
    }
}
