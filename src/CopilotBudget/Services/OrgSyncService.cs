using System.Net;
using CopilotBudget.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CopilotBudget.Services;

/// <summary>Pulls Copilot seats, admins and usage from GitHub into the local database.</summary>
public class OrgSyncService(
    IDbContextFactory<AppDbContext> dbFactory,
    GitHubCopilotClient github,
    BudgetService budgets,
    PoolService pools,
    IdentityLinkService identities,
    ChangeNotifier notifier,
    LiveStatus live,
    AlertService alerts,
    AuditLog audit,
    GitHubCredentials credentials,
    IOptionsMonitor<GitHubOptions> optionsMonitor,
    ILogger<OrgSyncService> logger)
{
    /// <summary>Read live, so credentials added in Key Vault work without a restart.</summary>
    public bool CanSync => credentials.IsConfigured;

    /// <summary>GitHub settings, read live (they can change in Key Vault while the app runs).</summary>
    private GitHubOptions Settings => optionsMonitor.CurrentValue;

    public async Task<OpResult> AddOrganizationAsync(int actorUserId, string login)
    {
        login = login.Trim().TrimStart('@');
        if (string.IsNullOrWhiteSpace(login) || login.Contains('/'))
            return OpResult.Fail("Enter an organization login, for example \"contoso\".");

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            if (await db.Organizations.AnyAsync(o => o.Login == login))
                return OpResult.Fail($"{login} is already tracked.");
            db.Organizations.Add(new Organization { Login = login });
            await db.SaveChangesAsync();
        }
        await audit.RecordAsync(actorUserId, "org.add", $"Started tracking {login}", "organization", null, login);

        if (!CanSync) return OpResult.Success($"Added {login}. Set up GitHub credentials to sync its seats.");
        var org = await FindAsync(login);
        var sync = await SyncAsync(org!.Id);
        return sync.Ok ? OpResult.Success($"Added and synced {login}.") : OpResult.Fail($"Added {login}, but sync failed: {sync.Message}");
    }

    /// <summary>
    /// Organizations the token can see that aren't tracked yet, with whether their Copilot billing is readable.
    /// Throws <see cref="GitHubApiException"/> if the org list itself can't be read.
    /// </summary>
    public async Task<List<AvailableOrganization>> GetAvailableOrganizationsAsync(CancellationToken ct = default)
    {
        if (!CanSync) return [];
        var orgs = await github.GetMyOrganizationsAsync(ct);

        HashSet<string> tracked;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            tracked = (await db.Organizations.Select(o => o.Login).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Check Copilot billing for each candidate so people only add orgs that can actually sync.
        var candidates = orgs.Where(o => !tracked.Contains(o.Login)).OrderBy(o => o.Login, StringComparer.OrdinalIgnoreCase).ToList();
        using var gate = new SemaphoreSlim(4);
        var checks = candidates.Select(async org =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var billing = await github.GetBillingAsync(org.Login, ct);
                return new AvailableOrganization(org.Login, org.AvatarUrl, org.Description, billing.PlanType, billing.SeatBreakdown?.Total, null);
            }
            catch (GitHubApiException ex)
            {
                var reason = ex.Status switch
                {
                    HttpStatusCode.NotFound => "Copilot Business or Enterprise isn't enabled here.",
                    HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => "The token's owner isn't an owner or billing manager here.",
                    _ => $"GitHub returned {(int)ex.Status}.",
                };
                return new AvailableOrganization(org.Login, org.AvatarUrl, org.Description, null, null, reason);
            }
            finally
            {
                gate.Release();
            }
        });
        return (await Task.WhenAll(checks)).ToList();
    }

    public async Task<OpResult> RemoveOrganizationAsync(int actorUserId, int organizationId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var org = await db.Organizations.FindAsync(organizationId);
        if (org is null) return OpResult.Fail("Organization not found.");

        // Delete dependents explicitly; SQLite cascades vary with how the schema was created.
        // Budgets belong to people, so ledger history stays; it just no longer points at this org.
        var memberIds = db.Memberships.Where(m => m.OrganizationId == organizationId).Select(m => m.Id);
        await db.Ledger.Where(l => l.OrganizationId == organizationId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.OrganizationId, (int?)null));
        await db.Usage.Where(u => memberIds.Contains(u.MembershipId)).ExecuteDeleteAsync();
        await db.UsageDetails.Where(d => d.OrganizationId == organizationId).ExecuteDeleteAsync();
        await db.Proposals.Where(p => p.OrganizationId == organizationId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.OrganizationId, (int?)null));
        await db.Memberships.Where(m => m.OrganizationId == organizationId).ExecuteDeleteAsync();
        db.Organizations.Remove(org);
        await db.SaveChangesAsync();
        await audit.RecordAsync(actorUserId, "org.remove", $"Stopped tracking {org.Login}", "organization", org.Id, org.Login);
        foreach (var kind in new[] { "failed", "stale", "partial" }) await alerts.ResolveAsync($"sync-{kind}:{org.Login}");
        await notifier.NotifyAsync();
        return OpResult.Success($"Stopped tracking {org.Login}.");
    }

    public async Task SyncAllAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ids = await db.Organizations.Where(o => !o.IsDemo).Select(o => o.Id).ToListAsync(ct);
        var failures = new List<string>();
        foreach (var id in ids)
        {
            if (ct.IsCancellationRequested) break;
            var result = await SyncAsync(id, ct);
            if (!result.Ok) failures.Add(result.Message);
        }
        live.SyncFinished(DateTime.UtcNow, failures.Count == 0 ? null : string.Join(" ", failures));
    }

    /// <summary>Syncs one org. <paramref name="actorUserId"/> is set when a person pressed Sync (it's audited).</summary>
    public async Task<OpResult> SyncAsync(int organizationId, CancellationToken ct = default, int? actorUserId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var org = await db.Organizations.Include(o => o.Memberships).ThenInclude(m => m.User)
            .FirstOrDefaultAsync(o => o.Id == organizationId, ct);
        if (org is null) return OpResult.Fail("Organization not found.");

        if (org.IsDemo)
        {
            // Demo orgs have no GitHub counterpart; just make sure this month's budgets exist.
            await budgets.EnsureMonthlyAllocationsAsync(Period.Current);
            await notifier.NotifyAsync();
            return OpResult.Success("Demo organization refreshed.");
        }
        if (!CanSync) return OpResult.Fail("No GitHub credentials configured (a GitHub App or GitHub:Token).");

        if (actorUserId is not null)
            await audit.RecordAsync(actorUserId, "org.sync", $"Synced {org.Login} by hand", "organization", org.Id, org.Login);

        var warnings = new List<string>();
        var renames = new List<string>();
        try
        {
            var billing = await github.GetBillingAsync(org.Login, ct);
            org.PlanType = billing.PlanType;
            org.TotalSeats = billing.SeatBreakdown?.Total ?? 0;
            org.ActiveSeatsThisCycle = billing.SeatBreakdown?.ActiveThisCycle ?? 0;
            org.InactiveSeatsThisCycle = billing.SeatBreakdown?.InactiveThisCycle ?? 0;

            var seats = await github.GetSeatsAsync(org.Login, ct);
            var seatUsers = new HashSet<AppUser>();
            foreach (var seat in seats.Where(s => s.Assignee is { Type: "User" or null }))
            {
                var member = await UpsertMemberAsync(db, org, seat.Assignee!, renames);
                seatUsers.Add(member.User);
                member.HasSeat = true;
                member.PlanType = seat.PlanType;
                member.SeatCreatedUtc = seat.CreatedAt;
                member.LastActivityUtc = seat.LastActivityAt;
                member.LastActivityEditor = seat.LastActivityEditor;
                member.PendingCancellationDate = seat.PendingCancellationDate;
            }
            foreach (var member in org.Memberships.Where(m => !seatUsers.Contains(m.User)))
                member.HasSeat = false;

            try
            {
                var admins = await github.GetAdminsAsync(org.Login, ct);
                var adminLogins = admins.Select(a => a.Login).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var admin in admins) await UpsertMemberAsync(db, org, admin, renames);
                foreach (var member in org.Memberships)
                    member.IsOrgAdmin = adminLogins.Contains(member.User.Login);
            }
            catch (GitHubApiException ex)
            {
                warnings.Add("Couldn't read org admins: " + ex.Message);
            }

            await db.SaveChangesAsync(ct);
            await budgets.EnsureMonthlyAllocationsAsync(Period.Current);

            if (Settings.SyncSsoEmails)
            {
                try
                {
                    // Work emails from GitHub SSO let Microsoft sign-ins find their GitHub user automatically.
                    var emails = await github.GetSsoEmailsAsync(org.Login, Settings.EnterpriseSlug, ct);
                    await identities.ApplyGitHubEmailsAsync(emails);
                }
                catch (Exception ex) when (ex is GitHubApiException or HttpRequestException or KeyNotFoundException or System.Text.Json.JsonException)
                {
                    warnings.Add("Couldn't read SSO emails for linking Microsoft sign-ins: " + ex.Message);
                }
            }

            await SyncCostCentersAsync(db, org, warnings, ct);
            if (Settings.SyncCreditUsage)
                await SyncUsageAsync(db, org, warnings, ct);

            org.LastSyncedUtc = DateTime.UtcNow;
            org.LastSyncFailed = false;
            await db.SaveChangesAsync(ct);
            // New usage may push people over budget; cover them from their pools.
            await pools.CoverOveragesAsync();
            org.LastSyncMessage = warnings.Count == 0 ? $"Synced {seats.Count} seats." : string.Join(" ", warnings);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Synced {Org}: {Seats} seats", org.Login, seats.Count);
        }
        catch (Exception ex) when (ex is GitHubApiException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Sync failed for {Org}", org.Login);
            org.LastSyncFailed = true;
            org.LastSyncMessage = ex.Message;
            org.LastSyncedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            await alerts.RaiseAsync($"sync-failed:{org.Login}", "sync", AlertSeverity.Critical, $"Sync failed for {org.Login}", ex.Message);
            return OpResult.Fail(ex.Message);
        }

        // Sync worked; anything left over is only a partial problem.
        await alerts.ResolveAsync($"sync-failed:{org.Login}");
        await alerts.ResolveAsync($"sync-stale:{org.Login}");
        if (warnings.Count > 0)
            await alerts.RaiseAsync($"sync-partial:{org.Login}", "sync", AlertSeverity.Warning, $"Sync of {org.Login} was only partly successful", string.Join(" ", warnings));
        else
            await alerts.ResolveAsync($"sync-partial:{org.Login}");
        foreach (var rename in renames)
            await audit.RecordAsync(null, "github.rename", $"GitHub account renamed: {rename} (matched by GitHub user ID)", "organization", org.Id, org.Login);

        await notifier.NotifyAsync();
        return OpResult.Success(org.LastSyncMessage ?? "Synced.");
    }

    /// <summary>
    /// Refreshes usage for this month and last month (which can still change for a few days), and backfills
    /// any older month in the history window that has never been fetched. Old months are kept forever.
    /// </summary>
    private async Task SyncUsageAsync(AppDbContext db, Organization org, List<string> warnings, CancellationToken ct)
    {
        var months = Math.Clamp(Settings.UsageHistoryMonths, 1, 24);
        var periods = Period.LastMonths(months);
        var refreshAlways = new HashSet<string> { Period.Current, Period.Previous(Period.Current) };
        var existing = await db.Usage
            .Where(u => periods.Contains(u.Period) && u.Membership.OrganizationId == org.Id)
            .ToDictionaryAsync(u => (u.MembershipId, u.Period), ct);

        var backfillBudget = Math.Max(0, Settings.MaxBackfillCallsPerSync);
        var skipped = 0;
        foreach (var member in org.Memberships.Where(m => m.HasSeat))
        {
            // Newest first, so a capped backfill still fills the most useful months.
            foreach (var period in Enumerable.Reverse(periods))
            {
                var known = existing.TryGetValue((member.Id, period), out var record);
                if (known && !refreshAlways.Contains(period)) continue;
                if (!refreshAlways.Contains(period))
                {
                    if (backfillBudget == 0) { skipped++; continue; }
                    backfillBudget--;
                }

                var (year, month) = Period.Parse(period);
                decimal used;
                try
                {
                    var items = await github.GetCreditUsageAsync(org.Login, member.User.Login, year, month, ct);
                    used = items.Sum(i => i.GrossQuantity);
                    await ReplaceDetailsAsync(db, org.Id, member.UserId, period, null, items, ct);
                }
                catch (GitHubApiException ex) when (ex.Status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
                {
                    // Usage reporting needs the enhanced billing platform; without it every call fails the same way.
                    warnings.Add("Credit usage is unavailable for this org: " + ex.Message);
                    return;
                }

                if (record is not null)
                {
                    record.Used = used;
                    record.UpdatedUtc = DateTime.UtcNow;
                }
                else
                {
                    record = new UsageRecord { MembershipId = member.Id, Period = period, Used = used };
                    db.Usage.Add(record);
                    existing[(member.Id, period)] = record;
                }
            }
        }
        if (skipped > 0)
            warnings.Add($"Usage history is still being filled in ({skipped} months left); it continues on the next sync.");

        if (Settings.SyncDailyUsage) await SyncDailyUsageAsync(db, org, warnings, ct);
    }

    /// <summary>
    /// The org's usage per day, by model and SKU, for this month and last. Days already stored are only refetched for
    /// the last two days, which can still change.
    /// </summary>
    private async Task SyncDailyUsageAsync(AppDbContext db, Organization org, List<string> warnings, CancellationToken ct)
    {
        var today = DateTime.UtcNow.Date;
        var periods = new[] { Period.Previous(Period.Current), Period.Current };
        var stored = (await db.UsageDetails
                .Where(d => d.OrganizationId == org.Id && d.UserId == null && d.Day != null && periods.Contains(d.Period))
                .Select(d => new { d.Period, d.Day })
                .ToListAsync(ct))
            .Select(d => (d.Period, d.Day!.Value)).ToHashSet();

        var calls = Math.Max(0, Settings.MaxBackfillCallsPerSync);
        foreach (var period in periods)
        {
            var start = Period.StartUtc(period);
            for (var date = start; date < Period.EndUtc(period) && date <= today; date = date.AddDays(1))
            {
                var recent = (today - date).TotalDays <= 1;
                if (stored.Contains((period, date.Day)) && !recent) continue;
                if (calls-- <= 0) return;
                try
                {
                    var items = await github.GetOrgDailyUsageAsync(org.Login, date.Year, date.Month, date.Day, ct);
                    await ReplaceDetailsAsync(db, org.Id, null, period, date.Day, items, ct);
                }
                catch (GitHubApiException ex) when (ex.Status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
                {
                    warnings.Add("Daily usage is unavailable for this org: " + ex.Message);
                    return;
                }
            }
        }
    }

    /// <summary>Replaces the stored model/SKU breakdown for one person-month or one org-day.</summary>
    private static async Task ReplaceDetailsAsync(AppDbContext db, int orgId, int? userId, string period, int? day,
        List<CreditUsageItem> items, CancellationToken ct)
    {
        var old = db.UsageDetails.Local.Where(d => d.OrganizationId == orgId && d.UserId == userId && d.Period == period && d.Day == day).ToList();
        old.AddRange(await db.UsageDetails
            .Where(d => d.OrganizationId == orgId && d.UserId == userId && d.Period == period && d.Day == day)
            .ToListAsync(ct));
        db.UsageDetails.RemoveRange(old.Distinct());
        foreach (var group in items.Where(i => i.GrossQuantity != 0)
                     .GroupBy(i => (Sku: i.Sku ?? "unknown", Model: i.Model ?? "unknown", i.Product)))
        {
            db.UsageDetails.Add(new UsageDetail
            {
                OrganizationId = orgId, UserId = userId, Period = period, Day = day,
                Product = group.Key.Product, Sku = group.Key.Sku, Model = group.Key.Model,
                Quantity = group.Sum(i => i.GrossQuantity), Cost = group.Sum(i => i.GrossAmount),
            });
        }
    }

    /// <summary>Cost center membership from the enterprise, for usage by cost center. Admin-set values are kept for anyone not in one.</summary>
    private async Task SyncCostCentersAsync(AppDbContext db, Organization org, List<string> warnings, CancellationToken ct)
    {
        if (!Settings.SyncCostCenters || !Settings.HasEnterprise) return;
        try
        {
            var centers = await github.GetCostCentersAsync(Settings.EnterpriseSlug!, ct);
            foreach (var m in org.Memberships)
                if (centers.TryGetValue(m.User.Login, out var name)) m.User.CostCenter = name;
        }
        catch (Exception ex) when (ex is GitHubApiException or HttpRequestException or System.Text.Json.JsonException)
        {
            warnings.Add("Couldn't read cost centers: " + ex.Message);
        }
    }

    /// <summary>
    /// Finds or creates the person for a GitHub account, by GitHub's numeric user ID first (it survives a renamed account),
    /// then by login for people known before their ID was. A renamed account keeps its history; the login is updated.
    /// </summary>
    private static async Task<Membership> UpsertMemberAsync(AppDbContext db, Organization org, GitHubAccount account, List<string> renames)
    {
        bool SameLogin(AppUser u) => string.Equals(u.Login, account.Login, StringComparison.OrdinalIgnoreCase);
        bool Compatible(AppUser u) => u.GitHubId is null || u.GitHubId == account.Id || account.Id == 0;

        var member = (account.Id != 0 ? org.Memberships.FirstOrDefault(m => m.User.GitHubId == account.Id) : null)
                     ?? org.Memberships.FirstOrDefault(m => SameLogin(m.User) && Compatible(m.User));
        var user = member?.User;
        if (user is null && account.Id != 0)
            user = db.Users.Local.FirstOrDefault(u => u.GitHubId == account.Id) ?? await db.Users.FirstOrDefaultAsync(u => u.GitHubId == account.Id);
        if (user is null)
        {
            user = db.Users.Local.FirstOrDefault(u => SameLogin(u) && Compatible(u));
            if (user is null)
            {
                var byLogin = await db.Users.FirstOrDefaultAsync(u => u.Login == account.Login);
                if (byLogin is not null && Compatible(byLogin)) user = byLogin;
            }
        }

        // Anyone else still holding this login is a stale record of an account that was renamed away: move it aside.
        var holder = db.Users.Local.FirstOrDefault(u => SameLogin(u) && u != user)
                     ?? await db.Users.FirstOrDefaultAsync(u => u.Login == account.Login && (user == null || u.Id != user.Id));
        if (holder is not null && holder != user) holder.Login = $"{holder.Login}~{holder.GitHubId?.ToString() ?? holder.Id.ToString()}";

        if (user is null)
        {
            user = new AppUser { Login = account.Login, GitHubId = account.Id == 0 ? null : account.Id, AvatarUrl = account.AvatarUrl };
            db.Users.Add(user);
        }
        else
        {
            if (account.Id != 0) user.GitHubId ??= account.Id;
            if (!SameLogin(user))
            {
                renames.Add($"{user.Login} → {account.Login}");
                user.Login = account.Login;
            }
            user.AvatarUrl = account.AvatarUrl ?? user.AvatarUrl;
        }

        if (member is null)
        {
            member = new Membership { User = user, Organization = org };
            org.Memberships.Add(member);
        }
        return member;
    }

    private async Task<Organization?> FindAsync(string login)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Organizations.AsNoTracking().FirstOrDefaultAsync(o => o.Login == login);
    }
}

/// <summary>An organization the token can see but that isn't tracked yet.</summary>
/// <param name="Problem">Why it can't be synced, or null when Copilot billing is readable.</param>
public record AvailableOrganization(string Login, string? AvatarUrl, string? Description, string? PlanType, int? Seats, string? Problem)
{
    public bool CanAdd => Problem is null;
}

/// <summary>Re-syncs every tracked organization on a fixed interval, once GitHub credentials are configured.</summary>
public class OrgSyncBackgroundService(IServiceProvider services, GitHubCredentials credentials, IOptions<GitHubOptions> options,
    ILogger<OrgSyncBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!credentials.IsConfigured) logger.LogInformation("No GitHub credentials yet; sync starts once they're configured.");
        var interval = TimeSpan.FromMinutes(Math.Max(2, options.Value.SyncIntervalMinutes));
        using var timer = new PeriodicTimer(interval);
        do
        {
            if (!credentials.IsConfigured) continue;
            try
            {
                using var scope = services.CreateScope();
                await scope.ServiceProvider.GetRequiredService<OrgSyncService>().SyncAllAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Background sync failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
