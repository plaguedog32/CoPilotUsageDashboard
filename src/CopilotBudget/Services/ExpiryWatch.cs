using CopilotBudget.Data;
using Microsoft.Extensions.Options;

namespace CopilotBudget.Services;

/// <summary>Credits that will be lost at the reset unless someone moves them.</summary>
/// <param name="Unused">Credits on track to go unused at the reset, at current pace (people who left included).</param>
/// <param name="Departed">People who left whose credits haven't been reassigned, with what they still hold.</param>
/// <param name="Idle">People with the most credits they won't use, most first.</param>
/// <param name="RunningOut">People on track to run out before the reset, and how much more they'd use.</param>
public record ExpiryOutlook(
    decimal Unused,
    TimeSpan UntilReset,
    IReadOnlyList<(AppUser User, decimal Credits)> Departed,
    IReadOnlyList<(AppUser User, decimal Credits)> Idle,
    IReadOnlyList<(AppUser User, decimal Shortfall)> RunningOut,
    bool InWarningWindow)
{
    public decimal DepartedCredits => Departed.Sum(d => d.Credits);
    public decimal Shortfall => RunningOut.Sum(r => r.Shortfall);

    /// <summary>Worth telling admins about: credits expire soon, or someone who left still holds credits.</summary>
    public bool NeedsAttention => (InWarningWindow && Unused >= 1) || DepartedCredits >= 1;
}

/// <summary>
/// Watches for credits that will expire unused at the month's end, so admins can move them to people who need them
/// while there's still time. Raises one alert per month in the last <c>App:ExpiryWarningDays</c> days (default 7), and
/// one whenever someone who left still holds credits.
/// </summary>
public class ExpiryWatch(BudgetService budgets, AlertService alerts, IOptions<AppOptions> appOptions)
{
    private int WarningDays => Math.Max(1, appOptions.Value.ExpiryWarningDays);

    public async Task<ExpiryOutlook> GetAsync()
    {
        var all = await budgets.GetBudgetsAsync(Period.Current);
        var forecast = Forecast.Summarize(all);
        var departed = all.Where(b => b.User.IsDisabled && b.Available >= 1)
            .Select(b => (b.User, Math.Floor(b.Available))).OrderByDescending(d => d.Item2).ToList();
        var idle = forecast.People.Where(p => !p.Budget.User.IsDisabled && p.Unused >= 1)
            .OrderByDescending(p => p.Unused).Take(5).Select(p => (p.Budget.User, Math.Floor(p.Unused))).ToList();
        var runningOut = forecast.RunningOut.Where(p => !p.Budget.User.IsDisabled && p.Shortfall > 0)
            .Select(p => (p.Budget.User, Math.Ceiling(p.Shortfall))).ToList();
        var until = Pace.UntilReset();
        return new ExpiryOutlook(Math.Floor(forecast.Unused), until, departed, idle, runningOut, until <= TimeSpan.FromDays(WarningDays));
    }

    /// <summary>Raises or clears this month's alerts. Called by housekeeping every few minutes.</summary>
    public async Task CheckAsync()
    {
        var o = await GetAsync();
        var unit = appOptions.Value.UnitLabel;
        var rate = appOptions.Value.DollarsPerCredit;
        var period = Period.Current;
        var reset = Pace.NextResetUtc();

        var expiringKey = $"expiring:{period}";
        if (o.InWarningWindow && o.Unused >= 1)
        {
            var detail = $"{Format.Amount(o.Unused)} {unit} ({Format.Dollars(o.Unused * rate)}) are on track to go unused when budgets reset on {reset:d MMM}.";
            if (o.RunningOut.Count > 0)
                detail += $" {o.RunningOut.Count} {(o.RunningOut.Count == 1 ? "person is" : "people are")} on track to run out first and could use about " +
                          $"{Format.Amount(o.Shortfall)} more: {string.Join(", ", o.RunningOut.Take(5).Select(r => $"{r.User.Login} ({Format.Amount(r.Shortfall)})"))}.";
            if (o.Idle.Count > 0)
                detail += $" Most unused: {string.Join(", ", o.Idle.Select(i => $"{i.User.Login} ({Format.Amount(i.Credits)})"))}.";
            detail += " Move credits on the Members page.";
            await alerts.RaiseAsync(expiringKey, "expiry", AlertSeverity.Warning,
                $"{Format.Amount(o.Unused)} {unit} will expire unused on {reset:d MMM}", detail);
        }
        else
        {
            await alerts.ResolveAsync(expiringKey);
        }
        // Last month's warning is over once the month has reset.
        await alerts.ResolveAllExceptAsync("expiring:", [expiringKey]);

        var departedKey = $"departed-credits:{period}";
        if (o.DepartedCredits >= 1)
        {
            await alerts.RaiseAsync(departedKey, "expiry", AlertSeverity.Warning,
                $"{Format.Amount(o.DepartedCredits)} {unit} are held by people who left",
                $"{string.Join(", ", o.Departed.Select(d => $"{d.User.Login} ({Format.Amount(d.Credits)})"))}. " +
                $"Nobody can use them, and they expire on {reset:d MMM}. Reassign them on the People & access page.");
        }
        else
        {
            await alerts.ResolveAsync(departedKey);
        }
        await alerts.ResolveAllExceptAsync("departed-credits:", [departedKey]);
    }
}
