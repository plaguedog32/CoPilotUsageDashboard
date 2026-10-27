namespace CopilotBudget.Services;

/// <summary>
/// Month rollover without waiting for someone to visit: new budgets, pool top-ups, expiring last month's pending
/// requests, transfers and proposals, exceptions starting or ending, and the month-end close email to admins.
/// </summary>
public class HousekeepingBackgroundService(IServiceProvider services, ILogger<HousekeepingBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            try
            {
                using var scope = services.CreateScope();
                var sp = scope.ServiceProvider;
                await sp.GetRequiredService<BudgetService>().EnsureMonthlyAllocationsAsync(Period.Current);
                await sp.GetRequiredService<PoolService>().EnsureMonthlyRefillsAsync(Period.Current);
                await sp.GetRequiredService<MonthEndService>().CloseIfDueAsync();
                await CheckStaleSyncAsync(sp);
                await sp.GetRequiredService<ExpiryWatch>().CheckAsync();
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Housekeeping failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>An org that hasn't synced successfully for three sync intervals (at least 30 minutes) gets an alert.</summary>
    private static async Task CheckStaleSyncAsync(IServiceProvider sp)
    {
        if (!sp.GetRequiredService<GitHubCredentials>().IsConfigured) return;
        var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GitHubOptions>>().Value;
        var alerts = sp.GetRequiredService<AlertService>();
        var limit = TimeSpan.FromMinutes(Math.Max(30, 3 * Math.Max(2, options.SyncIntervalMinutes)));
        await using var db = await sp.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<Data.AppDbContext>>().CreateDbContextAsync();
        var orgs = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(db.Organizations.Where(o => !o.IsDemo));
        foreach (var org in orgs)
        {
            var lastGood = org.LastSyncFailed ? null : org.LastSyncedUtc;
            var age = DateTime.UtcNow - (lastGood ?? org.CreatedUtc);
            if (age > limit)
                await alerts.RaiseAsync($"sync-stale:{org.Login}", "sync", Data.AlertSeverity.Critical, $"{org.Login} hasn't synced for {(int)age.TotalMinutes} minutes",
                    lastGood is null ? "It hasn't synced successfully since it was added, or the last sync failed." : $"Last good sync {lastGood:u}. Usage and budgets are going stale.");
        }
    }
}
