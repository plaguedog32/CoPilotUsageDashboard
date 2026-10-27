using CopilotBudget.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CopilotBudget.Services;

/// <summary>One slice of usage: a model, SKU, product or cost center.</summary>
public record UsageSlice(string Name, decimal Credits, decimal Cost, int People = 0);

/// <summary>One day of an organization's (or all organizations') usage, split by model.</summary>
public record DailyUsage(int Day, decimal Credits, IReadOnlyDictionary<string, decimal> ByModel);

public record UsageBreakdown(
    string Period,
    decimal Total,
    IReadOnlyList<UsageSlice> ByModel,
    IReadOnlyList<UsageSlice> BySku,
    IReadOnlyList<UsageSlice> ByProduct,
    IReadOnlyList<UsageSlice> ByCostCenter,
    IReadOnlyList<DailyUsage> Daily,
    int DaysInMonth,
    bool HasModelDetail)
{
    /// <summary>Models in order of use, for consistent colors across the charts.</summary>
    public IReadOnlyList<string> Models => ByModel.Select(m => m.Name).ToList();
}

/// <summary>
/// Usage broken down by day, model, SKU and cost center, from what sync stores. GitHub's usage report returns model and
/// SKU per item; per-person rows give the model/SKU/cost center split, per-org daily rows give the daily trend.
/// </summary>
public class UsageBreakdownService(IDbContextFactory<AppDbContext> dbFactory, IOptions<AppOptions> appOptions)
{
    public const string NoCostCenter = "No cost center";

    public async Task<UsageBreakdown> GetAsync(string period, int? organizationId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var rate = appOptions.Value.DollarsPerCredit;

        var details = await db.UsageDetails.AsNoTracking()
            .Where(d => d.Period == period && (organizationId == null || d.OrganizationId == organizationId))
            .Select(d => new { d.UserId, d.Day, d.Model, d.Sku, d.Product, d.Quantity, d.Cost })
            .ToListAsync();
        var people = details.Where(d => d.UserId != null).ToList();
        var daily = details.Where(d => d.UserId == null && d.Day != null).ToList();
        // Model/SKU split from per-person rows; fall back to daily org rows if that's all there is.
        var source = people.Count > 0 ? people : daily;

        decimal Cost(decimal quantity, decimal cost) => cost != 0 ? cost : quantity * rate;

        var byModel = source.GroupBy(d => d.Model)
            .Select(g => new UsageSlice(g.Key, g.Sum(x => x.Quantity), g.Sum(x => Cost(x.Quantity, x.Cost)),
                g.Where(x => x.UserId != null).Select(x => x.UserId!.Value).Distinct().Count()))
            .OrderByDescending(s => s.Credits).ToList();
        var bySku = source.GroupBy(d => d.Sku)
            .Select(g => new UsageSlice(g.Key, g.Sum(x => x.Quantity), g.Sum(x => Cost(x.Quantity, x.Cost)),
                g.Where(x => x.UserId != null).Select(x => x.UserId!.Value).Distinct().Count()))
            .OrderByDescending(s => s.Credits).ToList();
        var byProduct = source.GroupBy(d => d.Product ?? "Copilot")
            .Select(g => new UsageSlice(g.Key, g.Sum(x => x.Quantity), g.Sum(x => Cost(x.Quantity, x.Cost))))
            .OrderByDescending(s => s.Credits).ToList();

        // Cost centers come from per-person totals, which exist even when the model split doesn't.
        var totals = await db.Usage.AsNoTracking()
            .Where(u => u.Period == period && (organizationId == null || u.Membership.OrganizationId == organizationId))
            .Select(u => new { u.Membership.UserId, u.Membership.User.CostCenter, u.Used })
            .ToListAsync();
        var byCostCenter = totals.GroupBy(t => t.CostCenter ?? NoCostCenter)
            .Select(g => new UsageSlice(g.Key, g.Sum(x => x.Used), g.Sum(x => x.Used) * rate, g.Select(x => x.UserId).Distinct().Count()))
            .OrderBy(s => s.Name == NoCostCenter).ThenByDescending(s => s.Credits).ToList();

        var days = DateTime.DaysInMonth(Period.Parse(period).Year, Period.Parse(period).Month);
        var dailyUsage = daily.GroupBy(d => d.Day!.Value).OrderBy(g => g.Key)
            .Select(g => new DailyUsage(g.Key, g.Sum(x => x.Quantity),
                g.GroupBy(x => x.Model).ToDictionary(m => m.Key, m => m.Sum(x => x.Quantity))))
            .ToList();

        return new UsageBreakdown(period, totals.Sum(t => t.Used), byModel, bySku, byProduct, byCostCenter, dailyUsage, days, source.Count > 0);
    }
}
