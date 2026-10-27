using System.Globalization;

namespace CopilotBudget.Services;

/// <summary>Budgets reset monthly, matching Copilot's monthly AI credit allotment.</summary>
public static class Period
{
    public static string Current => FromDate(DateTime.UtcNow);

    public static string FromDate(DateTime utc) => utc.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    public static (int Year, int Month) Parse(string period)
    {
        var d = DateTime.ParseExact(period, "yyyy-MM", CultureInfo.InvariantCulture);
        return (d.Year, d.Month);
    }

    /// <summary>Midnight UTC on the first day of the period.</summary>
    public static DateTime StartUtc(string period)
    {
        var (y, m) = Parse(period);
        return new DateTime(y, m, 1, 0, 0, 0, DateTimeKind.Utc);
    }

    public static DateTime EndUtc(string period) => StartUtc(period).AddMonths(1);

    public static string Display(string period)
    {
        var (y, m) = Parse(period);
        return new DateTime(y, m, 1).ToString("MMMM yyyy", CultureInfo.InvariantCulture);
    }

    public static string Previous(string period) => AddMonths(period, -1);

    public static string AddMonths(string period, int months)
    {
        var (y, m) = Parse(period);
        return FromDate(new DateTime(y, m, 1).AddMonths(months));
    }

    /// <summary>The last <paramref name="count"/> periods ending with the current one, oldest first.</summary>
    public static List<string> LastMonths(int count) =>
        Enumerable.Range(0, Math.Max(1, count)).Select(i => AddMonths(Current, i - count + 1)).ToList();

    /// <summary>"Oct", or "Oct 2026" when <paramref name="withYear"/> is set.</summary>
    public static string Short(string period, bool withYear = false)
    {
        var (y, m) = Parse(period);
        return new DateTime(y, m, 1).ToString(withYear ? "MMM yyyy" : "MMM", CultureInfo.InvariantCulture);
    }
}
