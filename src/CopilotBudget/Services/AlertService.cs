using System.Diagnostics;
using CopilotBudget.Data;
using Microsoft.EntityFrameworkCore;

namespace CopilotBudget.Services;

/// <summary>
/// Opens, updates and closes alerts. Each problem has a key; repeats bump the count on the open alert instead of
/// piling up, and the alert closes when the condition clears. New alerts are logged at warning or error level (so they
/// reach Application Insights), counted as a metric, and sent to admins and integration admins through the email hook.
/// </summary>
public class AlertService(
    IDbContextFactory<AppDbContext> dbFactory,
    Notifications notifications,
    ChangeNotifier notifier,
    AuditLog audit,
    ILogger<AlertService> logger)
{
    public async Task RaiseAsync(string key, string kind, AlertSeverity severity, string title, string? detail = null)
    {
        Alert alert;
        bool isNew;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var open = await db.Alerts.FirstOrDefaultAsync(a => a.Key == key && a.ResolvedUtc == null);
            isNew = open is null;
            if (open is null)
            {
                open = new Alert { Key = key, Kind = kind, Severity = severity, Title = title, Detail = Trim(detail) };
                db.Alerts.Add(open);
            }
            else
            {
                open.Count++;
                open.LastSeenUtc = DateTime.UtcNow;
                open.Title = title;
                open.Detail = Trim(detail);
                if (severity > open.Severity) open.Severity = severity;
            }
            await db.SaveChangesAsync();
            alert = open;
        }

        if (!isNew) return;
        var level = severity == AlertSeverity.Critical ? LogLevel.Error : LogLevel.Warning;
        logger.Log(level, "Alert {AlertKind} {AlertKey}: {AlertTitle}. {AlertDetail}", kind, key, title, detail);
        Telemetry.AlertsRaised.Add(1, new TagList { { "kind", kind }, { "severity", severity.ToString() } });
        await notifier.NotifyAsync();
        await notifications.AlertRaisedAsync(alert);
    }

    /// <summary>Closes the open alert with this key, if any (the problem went away).</summary>
    public async Task ResolveAsync(string key) => await ResolveWhereAsync(a => a.Key == key);

    /// <summary>Closes every open alert whose key starts with the prefix, except the ones still happening.</summary>
    public async Task ResolveAllExceptAsync(string prefix, IReadOnlyCollection<string> stillOpen) =>
        await ResolveWhereAsync(a => a.Key.StartsWith(prefix) && !stillOpen.Contains(a.Key));

    private async Task ResolveWhereAsync(System.Linq.Expressions.Expression<Func<Alert, bool>> match)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var open = await db.Alerts.Where(a => a.ResolvedUtc == null).Where(match).ToListAsync();
        if (open.Count == 0) return;
        foreach (var a in open) a.ResolvedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        foreach (var a in open) logger.LogInformation("Alert {AlertKey} resolved: {AlertTitle}", a.Key, a.Title);
        await notifier.NotifyAsync();
    }

    public async Task<List<Alert>> GetAsync(bool includeResolved, int take = 200)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var query = db.Alerts.Include(a => a.AcknowledgedBy).AsNoTracking().AsQueryable();
        if (!includeResolved) query = query.Where(a => a.ResolvedUtc == null);
        return await query.OrderByDescending(a => a.LastSeenUtc).Take(take).ToListAsync();
    }

    public async Task<int> CountOpenAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Alerts.CountAsync(a => a.ResolvedUtc == null && a.AcknowledgedUtc == null);
    }

    /// <summary>Someone has seen it. It stays open until the problem clears, or they close it.</summary>
    public async Task<OpResult> AcknowledgeAsync(int actorUserId, int alertId, bool close)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var alert = await db.Alerts.FindAsync(alertId);
        if (alert is null) return OpResult.Fail("Alert not found.");
        alert.AcknowledgedUtc = DateTime.UtcNow;
        alert.AcknowledgedById = actorUserId;
        if (close) alert.ResolvedUtc ??= DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync(actorUserId, close ? "alert.close" : "alert.acknowledge", $"{(close ? "Closed" : "Acknowledged")} alert: {alert.Title}", "alert", alert.Id);
        await notifier.NotifyAsync();
        return OpResult.Success(close ? "Alert closed." : "Alert acknowledged.");
    }

    private static string? Trim(string? s) => s is { Length: > 2000 } ? s[..2000] : s;
}
