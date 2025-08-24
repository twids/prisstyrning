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
            Today = today?.DeepClone() as JsonArray; // defensiv kopia
            Tomorrow = tomorrow?.DeepClone() as JsonArray;
            LastUpdatedUtc = DateTimeOffset.UtcNow;
        }
    }

    public static (JsonArray? today, JsonArray? tomorrow, DateTimeOffset? lastUpdatedUtc) Get()
    {
        lock (_lock)
        {
            return (Today, Tomorrow, LastUpdatedUtc);
        }
    }
}
