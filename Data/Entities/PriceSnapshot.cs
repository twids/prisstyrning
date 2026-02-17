namespace Prisstyrning.Data.Entities;

public class PriceSnapshot
{
    public int Id { get; set; }  // Auto-increment PK
    public string Zone { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public DateTimeOffset SavedAtUtc { get; set; }
    public string TodayPricesJson { get; set; } = "[]";   // jsonb
    public string TomorrowPricesJson { get; set; } = "[]"; // jsonb
}
