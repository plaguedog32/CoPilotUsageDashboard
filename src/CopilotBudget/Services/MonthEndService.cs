using System.Globalization;
using System.Text;
using CopilotBudget.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CopilotBudget.Services;

/// <summary>One person's month, from monthly budget to what expired unused.</summary>
/// <param name="MonthlyBudget">Monthly budget plus budget changes (org budget, override, exceptions) during the month.</param>
public record CloseRow(
    AppUser User, string Organizations,
    decimal MonthlyBudget, decimal Received, decimal Sent, decimal Adjustments, decimal FromPools, decimal Allocated, decimal Used)
{
    /// <summary>Credits nobody used. Monthly credits don't carry over, so these are lost at the reset.</summary>
    public decimal Expired => Math.Max(0, Allocated - Used);

    /// <summary>Usage beyond the person's budget.</summary>
    public decimal Over => Math.Max(0, Used - Allocated);
}

public record CloseReport(string Period, IReadOnlyList<CloseRow> Rows, bool IsFinal, decimal PoolCreditsLeft)
{
    public decimal Allocated => Rows.Sum(r => r.Allocated);
    public decimal Used => Rows.Sum(r => r.Used);
    public decimal Expired => Rows.Sum(r => r.Expired);
    public decimal Over => Rows.Sum(r => r.Over);
    public decimal Moved => Rows.Sum(r => r.Received);
    public double UsedPercent => Allocated <= 0 ? 0 : (double)(Used / Allocated * 100);
    public double ExpiredPercent => Allocated <= 0 ? 0 : (double)(Expired / Allocated * 100);
}

/// <summary>
/// Month-end close: for a finished month, how many credits each person had, used and let expire. Admins can view and
/// export it any time; it's emailed to admins automatically once a month ends.
/// </summary>
public class MonthEndService(
    IDbContextFactory<AppDbContext> dbFactory,
    BudgetService budgets,
    Notifications notifications,
    AuditLog audit,
    IOptions<AppOptions> appOptions,
    ILogger<MonthEndService> logger)
{
    private decimal Rate => appOptions.Value.DollarsPerCredit;

    public async Task<CloseReport> BuildAsync(string period)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var ledger = await db.Ledger.AsNoTracking().Where(l => l.Period == period)
            .Select(l => new { l.UserId, l.Type, l.Amount }).ToListAsync();
        var usage = await db.Usage.AsNoTracking().Where(u => u.Period == period)
            .Select(u => new { u.Membership.UserId, u.Used }).ToListAsync();
        var userIds = ledger.Select(l => l.UserId).Concat(usage.Select(u => u.UserId)).ToHashSet();
        var users = await db.Users.AsNoTracking().Include(u => u.Memberships).ThenInclude(m => m.Organization)
            .Where(u => userIds.Contains(u.Id)).ToListAsync();

        decimal Sum(int userId, params LedgerEntryType[] types) =>
            ledger.Where(l => l.UserId == userId && types.Contains(l.Type)).Sum(l => l.Amount);

        var rows = users.Select(u => new CloseRow(
                u,
                string.Join(", ", u.Memberships.Select(m => m.Organization.Name).Order()),
                Sum(u.Id, LedgerEntryType.MonthlyAllocation, LedgerEntryType.BudgetChange),
                Sum(u.Id, LedgerEntryType.TransferIn),
                -Sum(u.Id, LedgerEntryType.TransferOut),
                Sum(u.Id, LedgerEntryType.AdminAdjustment),
                Sum(u.Id, LedgerEntryType.PoolDraw),
                ledger.Where(l => l.UserId == u.Id).Sum(l => l.Amount),
                usage.Where(x => x.UserId == u.Id).Sum(x => x.Used)))
            .OrderBy(r => r.User.Login, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var poolLeft = (await db.PoolEntries.AsNoTracking().Where(e => !e.CreditPool.IsArchived).Select(e => e.Amount).ToListAsync()).Sum();
        return new CloseReport(period, rows, string.CompareOrdinal(period, Period.Current) < 0, poolLeft);
    }

    /// <summary>The report as CSV, in credits and dollars, with a totals row.</summary>
    public string ToCsv(CloseReport report)
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("Period,Login,Name,Email,Organizations,Monthly budget,Received,Sent,Adjustments,From pools," +
                      "Total budget,Used,Expired unused,Over budget,Total budget ($),Used ($),Expired ($)");
        foreach (var r in report.Rows)
        {
            sb.AppendJoin(',', [
                report.Period, Csv(r.User.Login), Csv(r.User.Name), Csv(r.User.Email), Csv(r.Organizations),
                N(r.MonthlyBudget), N(r.Received), N(r.Sent), N(r.Adjustments), N(r.FromPools),
                N(r.Allocated), N(r.Used), N(r.Expired), N(r.Over),
                D(r.Allocated), D(r.Used), D(r.Expired),
            ]);
            sb.AppendLine();
        }
        sb.AppendJoin(',', [
            report.Period, "TOTAL", "", "", "",
            N(report.Rows.Sum(r => r.MonthlyBudget)), N(report.Rows.Sum(r => r.Received)), N(report.Rows.Sum(r => r.Sent)),
            N(report.Rows.Sum(r => r.Adjustments)), N(report.Rows.Sum(r => r.FromPools)),
            N(report.Allocated), N(report.Used), N(report.Expired), N(report.Over),
            D(report.Allocated), D(report.Used), D(report.Expired),
        ]);
        sb.AppendLine();
        return sb.ToString();

        string N(decimal v) => v.ToString("0.##", c);
        string D(decimal v) => (v * Rate).ToString("0.00", c);
        static string Csv(string? v)
        {
            if (string.IsNullOrEmpty(v)) return "";
            // Quote, and stop spreadsheet apps from treating a value as a formula.
            if (v[0] is '=' or '+' or '-' or '@') v = "'" + v;
            return v.IndexOfAny([',', '"', '\n', '\r']) >= 0 || v[0] == '\'' ? $"\"{v.Replace("\"", "\"\"")}\"" : v;
        }
    }

    public string Summarize(CloseReport r)
    {
        var unit = appOptions.Value.UnitLabel;
        return $"Month-end close for {Period.Display(r.Period)}.\n\n" +
               $"Budget: {Format.Amount(r.Allocated)} {unit} ({Format.Dollars(r.Allocated * Rate)})\n" +
               $"Used: {Format.Amount(r.Used)} {unit} ({Format.Dollars(r.Used * Rate)}, {Format.Percent(r.UsedPercent)})\n" +
               $"Expired unused: {Format.Amount(r.Expired)} {unit} ({Format.Dollars(r.Expired * Rate)}, {Format.Percent(r.ExpiredPercent)})\n" +
               $"Moved between people: {Format.Amount(r.Moved)} {unit}\n" +
               $"People over budget: {r.Rows.Count(x => x.Over > 0)}\n\n" +
               "The full report is attached as CSV.";
    }

    private async Task<int> SendAsync(string period)
    {
        var recipients = await RecipientsAsync();
        if (recipients.Count == 0) return 0;
        var report = await BuildAsync(period);
        var csv = Encoding.UTF8.GetBytes(ToCsv(report));
        await notifications.MonthEndReportAsync(recipients, period, Summarize(report), csv);
        return recipients.Count;
    }

    /// <summary>Who gets the month-end email: every admin (unless switched off) plus the extra addresses admins set.</summary>
    public async Task<List<string>> RecipientsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var settings = await BudgetService.GetSettingsAsync(db);
        var list = new List<string>();
        if (settings.MonthEndToAdmins)
            list.AddRange((await notifications.AdminEmailsAsync()).Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e!));
        list.AddRange(ParseRecipients(settings.MonthEndRecipients));
        return list.Select(e => e.Trim().ToLowerInvariant()).Distinct().ToList();
    }

    public static List<string> ParseRecipients(string? value) =>
        (value ?? "").Split([',', ';', '\n', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.ToLowerInvariant()).Distinct().ToList();

    /// <summary>Admins choose who gets the month-end email.</summary>
    public async Task<OpResult> SetRecipientsAsync(int actorUserId, bool toAdmins, IEnumerable<string> extra)
    {
        var emails = extra.Select(e => e.Trim().ToLowerInvariant()).Where(e => e.Length > 0).Distinct().ToList();
        var bad = emails.Where(e => !System.Net.Mail.MailAddress.TryCreate(e, out var a) || a.Address != e).ToList();
        if (bad.Count > 0) return OpResult.Fail($"These don't look like email addresses: {string.Join(", ", bad)}");

        await using var db = await dbFactory.CreateDbContextAsync();
        if (!budgets.IsAppAdmin(await db.Users.FindAsync(actorUserId))) return OpResult.Fail("Only app admins can change who gets the report.");
        var settings = await db.Settings.FirstOrDefaultAsync();
        if (settings is null) db.Settings.Add(settings = new AppSettings());
        var before = new { settings.MonthEndToAdmins, Recipients = ParseRecipients(settings.MonthEndRecipients) };
        settings.MonthEndToAdmins = toAdmins;
        settings.MonthEndRecipients = emails.Count == 0 ? null : string.Join(",", emails);
        settings.UpdatedUtc = DateTime.UtcNow;
        settings.UpdatedByUserId = actorUserId;
        await db.SaveChangesAsync();

        var who = new List<string>();
        if (toAdmins) who.Add("all admins");
        who.AddRange(emails);
        var summary = who.Count == 0 ? "Nobody gets the month-end email now" : $"Month-end email goes to {string.Join(", ", who)}";
        await audit.RecordAsync(actorUserId, "settings.month-end-recipients", summary, "settings", null, null,
            new { before, after = new { MonthEndToAdmins = toAdmins, Recipients = emails } });
        return OpResult.Success(summary + ".");
    }

    /// <summary>Once a month has ended, sends its report to admins (once).</summary>
    public async Task CloseIfDueAsync()
    {
        var previous = Period.Previous(Period.Current);
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!await db.Ledger.AnyAsync(l => l.Period == previous)) return; // Nothing happened that month.
        var settings = await db.Settings.FirstOrDefaultAsync();
        if (settings is null) db.Settings.Add(settings = new AppSettings());
        if (settings.LastClosedPeriod is { } closed && string.CompareOrdinal(closed, previous) >= 0) return;

        settings.LastClosedPeriod = previous;
        await db.SaveChangesAsync();
        var sent = await SendAsync(previous);
        await audit.RecordAsync(null, "report.month-end-email", sent == 0
            ? $"Month-end close for {Period.Display(previous)} not emailed: nobody is set to receive it"
            : $"Emailed the month-end close for {Period.Display(previous)} to {sent} {(sent == 1 ? "person" : "people")} automatically");
        logger.LogInformation("Month-end close report for {Period} sent to admins", previous);
    }
}
