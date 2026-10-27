using System.Globalization;
using System.Text;
using System.Text.Json;
using CopilotBudget.Data;
using Microsoft.EntityFrameworkCore;

namespace CopilotBudget.Services;

/// <summary>
/// Records admin actions. Entries are only ever added: the app has no code path that edits or deletes them. (For
/// tamper-evidence beyond the app, see the README: SQL Server ledger tables or exporting to Log Analytics.)
/// </summary>
public class AuditLog(IDbContextFactory<AppDbContext> dbFactory, ILogger<AuditLog> logger)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public async Task RecordAsync(int? actorUserId, string action, string summary,
        string? targetType = null, object? targetId = null, string? targetName = null, object? details = null)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var actorName = actorUserId is int id
                ? await db.Users.Where(u => u.Id == id).Select(u => u.Login).FirstOrDefaultAsync() ?? $"user #{id}"
                : "system";
            db.AuditLog.Add(new AuditEntry
            {
                ActorUserId = actorUserId,
                ActorName = actorName,
                Action = action,
                TargetType = targetType,
                TargetId = targetId?.ToString(),
                TargetName = targetName,
                Summary = summary.Length > 1000 ? summary[..1000] : summary,
                Details = details is null ? null : JsonSerializer.Serialize(details, Json),
            });
            await db.SaveChangesAsync();
            // Also to the log stream (and Application Insights), so a copy lives outside the database.
            logger.LogInformation("Audit {AuditAction} by {Actor}: {Summary}", action, actorName, summary);
        }
        catch (Exception ex)
        {
            // Never fail the action because the audit write failed, but make it loud.
            logger.LogError(ex, "Couldn't write audit entry {AuditAction}: {Summary}", action, summary);
        }
    }

    public async Task<List<AuditEntry>> SearchAsync(string? text = null, string? action = null, DateTime? fromUtc = null, int take = 500)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var query = db.AuditLog.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(action)) query = query.Where(a => a.Action.StartsWith(action));
        if (fromUtc is not null) query = query.Where(a => a.CreatedUtc >= fromUtc);
        if (!string.IsNullOrWhiteSpace(text))
        {
            var t = text.Trim();
            query = query.Where(a => a.Summary.Contains(t) || a.ActorName.Contains(t) || (a.TargetName != null && a.TargetName.Contains(t)));
        }
        return await query.OrderByDescending(a => a.Id).Take(take).ToListAsync();
    }

    public static string ToCsv(IEnumerable<AuditEntry> entries)
    {
        var sb = new StringBuilder("Time (UTC),Actor,Action,Target type,Target,Summary,Details\n");
        foreach (var e in entries)
        {
            sb.AppendJoin(',', [
                e.CreatedUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), Csv(e.ActorName), Csv(e.Action),
                Csv(e.TargetType), Csv(e.TargetName ?? e.TargetId), Csv(e.Summary), Csv(e.Details),
            ]);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Quotes a CSV value and stops spreadsheets treating it as a formula.</summary>
    public static string Csv(string? v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        if (v[0] is '=' or '+' or '-' or '@') v = "'" + v;
        return v.IndexOfAny([',', '"', '\n', '\r']) >= 0 || v[0] == '\'' ? $"\"{v.Replace("\"", "\"\"")}\"" : v;
    }
}
