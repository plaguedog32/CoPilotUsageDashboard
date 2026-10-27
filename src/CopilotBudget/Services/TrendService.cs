using CopilotBudget.Data;
using Microsoft.EntityFrameworkCore;

namespace CopilotBudget.Services;

/// <summary>Credits one person used through one organization's seat in one month.</summary>
public record UsagePoint(int UserId, int OrganizationId, string Period, decimal Used);

/// <summary>Monthly usage and budgets for a range of months, used by the Trends page.</summary>
public record TrendData(
    IReadOnlyList<string> Periods,
    IReadOnlyList<Organization> Organizations,
    IReadOnlyDictionary<int, AppUser> Users,
    IReadOnlyDictionary<int, IReadOnlyList<int>> UserOrganizations,
    IReadOnlyList<UsagePoint> Usage,
    IReadOnlyDictionary<(int UserId, string Period), decimal> Allocated)
{
    /// <summary>Credits used per period, optionally limited to one org or one person. Null when nothing was recorded.</summary>
    public decimal? UsedIn(string period, int? organizationId = null, int? userId = null)
    {
        var points = Usage.Where(u => u.Period == period
                                      && (organizationId is null || u.OrganizationId == organizationId)
                                      && (userId is null || u.UserId == userId)).ToList();
        return points.Count == 0 ? null : points.Sum(p => p.Used);
    }

    public decimal? AllocatedTo(int userId, string period) =>
        Allocated.TryGetValue((userId, period), out var amount) ? amount : null;
}

/// <summary>Reads stored history. Nothing here calls GitHub; sync keeps the history filled in.</summary>
public class TrendService(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<TrendData> GetAsync(int months)
    {
        var periods = Period.LastMonths(months);
        await using var db = await dbFactory.CreateDbContextAsync();

        var orgs = (await db.Organizations.AsNoTracking().ToListAsync()).OrderBy(o => o.Id).ToList();
        var usage = await db.Usage.Where(u => periods.Contains(u.Period))
            .Select(u => new UsagePoint(u.Membership.UserId, u.Membership.OrganizationId, u.Period, u.Used))
            .ToListAsync();
        var ledger = await db.Ledger.Where(l => periods.Contains(l.Period))
            .Select(l => new { l.UserId, l.Period, l.Amount })
            .ToListAsync();
        var memberships = await db.Memberships.AsNoTracking()
            .Select(m => new { m.UserId, m.OrganizationId })
            .ToListAsync();

        var userIds = usage.Select(u => u.UserId).Concat(memberships.Select(m => m.UserId)).ToHashSet();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id);

        return new TrendData(
            periods,
            orgs,
            users,
            memberships.GroupBy(m => m.UserId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<int>)g.Select(m => m.OrganizationId).ToList()),
            usage,
            ledger.GroupBy(l => (l.UserId, l.Period)).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount)));
    }
}
