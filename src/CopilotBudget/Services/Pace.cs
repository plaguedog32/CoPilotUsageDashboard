namespace CopilotBudget.Services;

/// <summary>
/// How far through the monthly budget period we are, so usage can be compared with an even spend.
/// Budgets reset at midnight UTC on the 1st of each month.
/// </summary>
public static class Pace
{
    public static DateTime PeriodStartUtc(DateTime? now = null)
    {
        var n = now ?? DateTime.UtcNow;
        return new DateTime(n.Year, n.Month, 1, 0, 0, 0, DateTimeKind.Utc);
    }

    public static DateTime NextResetUtc(DateTime? now = null) => PeriodStartUtc(now).AddMonths(1);

    public static TimeSpan UntilReset(DateTime? now = null)
    {
        var left = NextResetUtc(now) - (now ?? DateTime.UtcNow);
        return left < TimeSpan.Zero ? TimeSpan.Zero : left;
    }

    /// <summary>Share of the current period that has passed, 0 to 1.</summary>
    public static double Elapsed(DateTime? now = null)
    {
        var n = now ?? DateTime.UtcNow;
        var start = PeriodStartUtc(n);
        return Math.Clamp((n - start).TotalSeconds / (NextResetUtc(n) - start).TotalSeconds, 0, 1);
    }

    /// <summary>Usage you'd expect by now if the budget were spent evenly across the month.</summary>
    public static decimal Expected(decimal allocated) => allocated * (decimal)Elapsed();

    /// <summary>Where usage ends up at the reset if it carries on at the current rate.</summary>
    public static decimal Projected(decimal used)
    {
        // Very early in the month a few credits extrapolate wildly; wait until 2% of the month has passed.
        var elapsed = Math.Max(Elapsed(), 0.02);
        return Math.Round(used / (decimal)elapsed);
    }

    /// <summary>True when usage is running faster than the budget allows, so it runs out before the reset.</summary>
    public static bool IsAhead(decimal used, decimal allocated) =>
        allocated <= 0 ? used > 0 : used > Expected(allocated) && Projected(used) > allocated;

    /// <summary>
    /// True when usage is well behind an even spend, so a noticeable part of the budget would go unused at the reset:
    /// on track to use less than <see cref="SpareThreshold"/> of it. Not judged in the first days of the month.
    /// </summary>
    public static bool IsBehind(decimal used, decimal allocated) =>
        allocated > 0 && Elapsed() >= 0.1 && !IsAhead(used, allocated) && Projected(used) < allocated * SpareThreshold;

    /// <summary>Below this share of the budget used by the reset, someone counts as having credits to spare.</summary>
    public const decimal SpareThreshold = 0.8m;

    /// <summary>What would go unused at the reset at the current rate.</summary>
    public static decimal ProjectedUnused(decimal used, decimal allocated) => Math.Max(0, allocated - Projected(used));

    /// <summary>Time left in "4D 5H 6M" form.</summary>
    public static string Countdown(TimeSpan left) => $"{(int)left.TotalDays}D {left.Hours}H {left.Minutes}M";

    /// <summary>One line describing pace, e.g. "Ahead of pace: on track to use 412 of 300 credits by the reset".</summary>
    public static string Describe(decimal used, decimal allocated, string unit) =>
        Describe(used, allocated, Format.Amount, unit);

    /// <summary>Same as above with the caller's number format (credits or dollars) and unit ("" for dollars).</summary>
    public static string Describe(decimal used, decimal allocated, Func<decimal, string> amount, string unit)
    {
        if (allocated <= 0) return used > 0 ? "No budget this month" : "Nothing used yet";
        var projected = Projected(used);
        var suffix = string.IsNullOrEmpty(unit) ? "" : " " + unit;
        if (IsBehind(used, allocated))
            return $"Credits to spare: on track to use about {amount(projected)} of {amount(allocated)}{suffix}, leaving {amount(allocated - projected)} unused at the reset";
        if (!IsAhead(used, allocated))
            return $"On pace: about {amount(Math.Min(projected, allocated))} of {amount(allocated)}{suffix} by the reset";
        var runOut = RunOutUtc(used, allocated);
        return $"Ahead of pace: on track to use {amount(projected)} of {amount(allocated)}{suffix}" +
               (runOut is { } r ? $", runs out around {r:d MMM}" : " by the reset");
    }

    /// <summary>
    /// When someone using credits at their rate so far this month reaches their budget, or null if they won't before
    /// the reset. Already out of credits returns now.
    /// </summary>
    public static DateTime? RunOutUtc(decimal used, decimal allocated, DateTime? now = null)
    {
        var n = now ?? DateTime.UtcNow;
        if (used <= 0) return null;
        if (used >= allocated) return n;
        var start = PeriodStartUtc(n);
        var elapsedSeconds = Math.Max((n - start).TotalSeconds, (NextResetUtc(n) - start).TotalSeconds * 0.02);
        var perSecond = (double)used / elapsedSeconds;
        var runOut = n.AddSeconds((double)(allocated - used) / perSecond);
        return runOut < NextResetUtc(n) ? runOut : null;
    }
}
