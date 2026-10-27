using System.Globalization;
using CopilotBudget.Data;

namespace CopilotBudget.Services;

public static class Format
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    /// <summary>Whole numbers without decimals, fractional usage (model multipliers) to one place.</summary>
    public static string Amount(decimal value) =>
        value % 1 == 0 ? value.ToString("#,0", Culture) : value.ToString("#,0.#", Culture);

    public static string Signed(decimal value) => (value > 0 ? "+" : value < 0 ? "−" : "") + Amount(Math.Abs(value));

    /// <summary>"$19", "$19.25", "-$4.10".</summary>
    public static string Dollars(decimal value)
    {
        var abs = Math.Abs(Math.Round(value, 2));
        var text = abs % 1 == 0 ? abs.ToString("#,0", Culture) : abs.ToString("#,0.00", Culture);
        return (value < 0 ? "−$" : "$") + text;
    }

    public static string SignedDollars(decimal value) => (value > 0 ? "+" : value < 0 ? "−" : "") + Dollars(Math.Abs(value));

    public static string Percent(double value) => $"{Math.Round(value):0}%";

    public static string Relative(DateTime? utc)
    {
        if (utc is null) return "never";
        var span = DateTime.UtcNow - DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc);
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalMinutes switch
        {
            < 1 => "just now",
            < 60 => $"{(int)span.TotalMinutes}m ago",
            < 60 * 24 => $"{(int)span.TotalHours}h ago",
            < 60 * 24 * 30 => $"{(int)span.TotalDays}d ago",
            _ => utc.Value.ToString("d MMM yyyy", Culture),
        };
    }

    public static string Timestamp(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("d MMM yyyy, HH:mm 'UTC'", Culture);

    public static string EntryType(LedgerEntryType type) => type switch
    {
        LedgerEntryType.MonthlyAllocation => "Monthly budget",
        LedgerEntryType.BudgetChange => "Monthly budget change",
        LedgerEntryType.PoolDraw => "From a credit pool",
        LedgerEntryType.TransferIn => "Received",
        LedgerEntryType.TransferOut => "Sent",
        LedgerEntryType.AdminAdjustment => "Admin adjustment",
        _ => type.ToString(),
    };
}
