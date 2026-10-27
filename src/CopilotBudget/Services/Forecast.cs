namespace CopilotBudget.Services;

/// <summary>Where one person ends the month if they keep using credits at their rate so far.</summary>
/// <param name="ProjectedUse">Credits they'll have used by the reset. GitHub stops them at their budget, so it's capped there.</param>
/// <param name="Unused">Credits that expire unused at the reset.</param>
/// <param name="RunOutUtc">When they hit their budget (GitHub's hard stop), or null if they won't before the reset.</param>
public record PersonForecast(MemberBudget Budget, decimal ProjectedUse, decimal Unused, decimal Shortfall, DateTime? RunOutUtc)
{
    public bool RunsOut => RunOutUtc is not null;
}

/// <summary>Month-end forecast across everyone, from usage so far this month.</summary>
/// <param name="Expected">Credits everyone would have used by now if spending evenly through the month.</param>
/// <param name="ProjectedUse">Credits used by the reset at current rates (each person capped at their budget).</param>
/// <param name="Unused">Credits that will expire unused at the reset.</param>
/// <param name="Shortfall">Credits people would use beyond their budgets if nothing stopped them.</param>
public record ForecastSummary(
    decimal Allocated, decimal Used, decimal Expected, decimal ProjectedUse, decimal Unused, decimal Shortfall,
    IReadOnlyList<PersonForecast> People)
{
    /// <summary>Positive when usage is running ahead of an even spend.</summary>
    public decimal PaceDifference => Used - Expected;

    /// <summary>People who hit their hard stop before the reset, soonest first (already-out people first).</summary>
    public IEnumerable<PersonForecast> RunningOut => People.Where(p => p.RunsOut).OrderBy(p => p.RunOutUtc);

    public double UnusedPercent => Allocated <= 0 ? 0 : (double)(Unused / Allocated * 100);
}

public static class Forecast
{
    public static PersonForecast For(MemberBudget b, DateTime? now = null)
    {
        var allocated = Math.Max(0, b.Allocated);
        var uncapped = Pace.Projected(b.Used);
        var projected = Math.Min(uncapped, Math.Max(allocated, b.Used));
        return new PersonForecast(
            b,
            projected,
            Math.Max(0, allocated - projected),
            Math.Max(0, uncapped - allocated),
            allocated <= 0 ? (b.Used > 0 ? now ?? DateTime.UtcNow : null) : Pace.RunOutUtc(b.Used, allocated, now));
    }

    public static ForecastSummary Summarize(IEnumerable<MemberBudget> budgets)
    {
        var people = budgets.Where(b => b.HasSeat).Select(b => For(b)).ToList();
        var allocated = people.Sum(p => Math.Max(0, p.Budget.Allocated));
        return new ForecastSummary(
            allocated,
            people.Sum(p => p.Budget.Used),
            Pace.Expected(allocated),
            people.Sum(p => p.ProjectedUse),
            people.Sum(p => p.Unused),
            people.Sum(p => p.Shortfall),
            people);
    }
}
