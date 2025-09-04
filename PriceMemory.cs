using System.Text.Json.Nodes;

internal static class PriceMemory
{
    private static readonly object _lock = new();
    public static JsonArray? Today { get; private set; }
    public static JsonArray? Tomorrow { get; private set; }
    public static DateTimeOffset? LastUpdatedUtc { get; private set; }

    public static void Set(JsonArray? today, JsonArray? tomorrow)
    {
        lock (_lock)
        {
            // Only clone if we're actually storing new data
            Today = today?.DeepClone() as JsonArray;
            Tomorrow = tomorrow?.DeepClone() as JsonArray;
            LastUpdatedUtc = DateTimeOffset.UtcNow;
        }
    }

    public static (JsonArray? today, JsonArray? tomorrow, DateTimeOffset? lastUpdatedUtc) Get()
    {
        lock (_lock)
        {
            // Return defensive copies to prevent external modification
            return (
                Today?.DeepClone() as JsonArray,
                Tomorrow?.DeepClone() as JsonArray,
                LastUpdatedUtc
            );
        }
    }

    // Get without defensive copying for read-only access (performance optimization)
    public static (JsonArray? today, JsonArray? tomorrow, DateTimeOffset? lastUpdatedUtc) GetReadOnly()
    {
        lock (_lock)
        {
            return (Today, Tomorrow, LastUpdatedUtc);
        }
    }
}
