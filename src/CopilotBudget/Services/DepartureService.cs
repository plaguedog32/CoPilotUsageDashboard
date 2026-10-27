using CopilotBudget.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CopilotBudget.Services;

/// <summary>Someone who has left, and what they still hold.</summary>
/// <param name="CreditsLeft">This month's budget they haven't used.</param>
public record Departure(
    AppUser User,
    decimal CreditsLeft,
    IReadOnlyList<PoolView> Reserves,
    IReadOnlyList<PoolView> SharedPools,
    IReadOnlyList<string> SeatOrganizations,
    AppRole Roles)
{
    public bool Handled => User.DepartureHandledUtc is not null;
    public bool HasSomethingToReassign => CreditsLeft > 0 || Reserves.Count > 0 || SharedPools.Count > 0 || Roles != AppRole.None;
}

/// <summary>
/// People who leave: their Entra account is disabled or deleted (checked through Microsoft Graph), or an admin marks
/// them. When that happens they stop getting credits, disappear from everyday lists, and everything they had pending is
/// cancelled. Admins then reassign what's left (credits, reserves, pools, roles) in one step.
/// </summary>
public class DepartureService(
    IDbContextFactory<AppDbContext> dbFactory,
    IDirectoryClient directory,
    BudgetService budgets,
    PoolService pools,
    AuditLog audit,
    AlertService alerts,
    Notifications notifications,
    ChangeNotifier notifier,
    IOptions<AppOptions> appOptions,
    ILogger<DepartureService> logger)
{
    private string Unit => appOptions.Value.UnitLabel;

    public IDirectoryClient Directory => directory;

    /// <summary>Asks Entra which linked accounts are disabled or gone, and marks or restores people to match.</summary>
    public async Task<OpResult> CheckDirectoryAsync(int? actorUserId = null, CancellationToken ct = default)
    {
        if (!directory.IsAvailable) return OpResult.Fail(directory.Description);

        List<AppUser> linked;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            linked = await db.Users.AsNoTracking().Where(u => u.EntraObjectId != null).ToListAsync(ct);

        Dictionary<string, bool?> states;
        try
        {
            states = await directory.GetAccountStatesAsync(linked.Select(u => u.EntraObjectId!).ToList(), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or Azure.Identity.AuthenticationFailedException or Azure.RequestFailedException)
        {
            await alerts.RaiseAsync("directory-check", "directory", AlertSeverity.Warning, "Couldn't check Entra accounts", ex.Message);
            return OpResult.Fail("Couldn't check Entra accounts: " + ex.Message);
        }
        await alerts.ResolveAsync("directory-check");

        int left = 0, back = 0;
        foreach (var user in linked)
        {
            if (!states.TryGetValue(user.EntraObjectId!, out var enabled)) continue; // Not answered this round.
            if (enabled != true && !user.IsDisabled)
            {
                await DisableAsync(user.Id, enabled is null ? "Entra account deleted" : "Entra account disabled", actorUserId);
                left++;
            }
            else if (enabled == true && user.IsDisabled && user.DisabledReason?.StartsWith("Entra") == true)
            {
                await ReactivateAsync(actorUserId, user.Id, "Entra account enabled again");
                back++;
            }
        }
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var ids = linked.Where(u => states.ContainsKey(u.EntraObjectId!)).Select(u => u.Id).ToList();
            await db.Users.Where(u => ids.Contains(u.Id)).ExecuteUpdateAsync(s => s.SetProperty(u => u.DirectoryCheckedUtc, DateTime.UtcNow), ct);
        }
        return OpResult.Success($"Checked {states.Count} Entra accounts: {left} newly disabled, {back} enabled again.");
    }

    /// <summary>An admin marks someone as left (e.g. when Entra isn't connected, or before the account is disabled).</summary>
    public async Task<OpResult> MarkLeftAsync(int actorUserId, int userId)
    {
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            if (!budgets.IsAppAdmin(await db.Users.FindAsync(actorUserId))) return OpResult.Fail("Only app admins can mark people as left.");
            if (actorUserId == userId) return OpResult.Fail("You can't mark yourself as left.");
        }
        return await DisableAsync(userId, "Marked as left by an admin", actorUserId);
    }

    /// <summary>
    /// Marks someone as left and cleans up: open requests cancelled; transfers they sent or were due to receive
    /// cancelled (held credits go back, a request someone else was filling for them closes); requests they were filling
    /// reopen for others; their budget proposals and exceptions cancelled. Their remaining credits wait for an admin.
    /// </summary>
    public async Task<OpResult> DisableAsync(int userId, string reason, int? actorUserId)
    {
        AppUser user;
        var cleanup = new List<string>();
        await BudgetService.WriteLock.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var found = await db.Users.FindAsync(userId);
            if (found is null) return OpResult.Fail("Person not found.");
            user = found;
            if (user.IsDisabled) return OpResult.Fail($"{user.Login} is already marked as left.");

            var now = DateTime.UtcNow;
            user.IsDisabled = true;
            user.DisabledUtc = now;
            user.DisabledReason = reason;
            user.DepartureHandledUtc = null;

            var requests = await db.Requests.Where(r => r.RequesterId == userId && (r.Status == RequestStatus.Pending || r.Status == RequestStatus.AwaitingApproval)).ToListAsync();
            foreach (var r in requests)
            {
                r.Status = RequestStatus.Cancelled;
                r.ResolvedUtc = now;
                r.ResponseNote = $"Cancelled: {user.Login} has left.";
            }
            if (requests.Count > 0) cleanup.Add($"{requests.Count} open request{(requests.Count == 1 ? "" : "s")} cancelled");

            var transfers = await db.PendingTransfers.Include(t => t.BudgetRequest)
                .Where(t => t.Status == TransferStatus.Pending && (t.FromUserId == userId || t.ToUserId == userId)).ToListAsync();
            foreach (var t in transfers)
            {
                t.Status = TransferStatus.Cancelled;
                t.ResolvedUtc = now;
                t.ResponseNote = $"Cancelled: {user.Login} has left.";
                // A request they were filling goes back to open for someone else.
                if (t.FromUserId == userId && t.BudgetRequest is { Status: RequestStatus.AwaitingApproval } req)
                {
                    req.Status = RequestStatus.Pending;
                    req.ResolvedById = null;
                    req.ResolvedUtc = null;
                    req.ResponseNote = $"{user.Login} left before this was approved.";
                }
            }
            if (transfers.Count > 0) cleanup.Add($"{transfers.Count} transfer{(transfers.Count == 1 ? "" : "s")} waiting for approval cancelled");

            var proposals = await db.Proposals.Include(p => p.BudgetException)
                .Where(p => p.Status == ProposalStatus.Pending && (p.UserId == userId || p.ProposedById == userId)).ToListAsync();
            foreach (var p in proposals)
            {
                p.Status = ProposalStatus.Cancelled;
                p.DecidedUtc = now;
                p.DecisionNote = $"Cancelled: {user.Login} has left.";
                if (p.BudgetException is { IsApproved: false } e) e.IsCancelled = true;
            }
            if (proposals.Count > 0) cleanup.Add($"{proposals.Count} budget proposal{(proposals.Count == 1 ? "" : "s")} cancelled");

            var exceptions = await db.BudgetExceptions.Where(e => e.UserId == userId && !e.IsCancelled && e.EndDate >= now.Date).ToListAsync();
            foreach (var e in exceptions) e.IsCancelled = true;
            if (exceptions.Count > 0) cleanup.Add($"{exceptions.Count} exception{(exceptions.Count == 1 ? "" : "s")} ended");

            await db.SaveChangesAsync();
        }
        finally
        {
            BudgetService.WriteLock.Release();
        }

        var left = (await budgets.GetBudgetsAsync(Period.Current)).FirstOrDefault(b => b.User.Id == userId)?.Available ?? 0;
        var summary = $"{user.Login} marked as left ({reason})" + (cleanup.Count > 0 ? ": " + string.Join(", ", cleanup) : "");
        await audit.RecordAsync(actorUserId, "people.left", summary, "user", userId, user.Login);
        logger.LogInformation("{Summary}", summary);
        await notifications.PersonLeftAsync(user, Math.Max(0, left), cleanup.Count == 0 ? "Nothing was pending." : string.Join(". ", cleanup) + ".");
        await notifier.NotifyAsync();
        return OpResult.Success(summary + ".");
    }

    /// <summary>An admin brings back someone who was marked as left by mistake (or rejoined).</summary>
    public async Task<OpResult> RestoreAsync(int actorUserId, int userId)
    {
        await using (var db = await dbFactory.CreateDbContextAsync())
            if (!budgets.IsAppAdmin(await db.Users.FindAsync(actorUserId))) return OpResult.Fail("Only app admins can do this.");
        return await ReactivateAsync(actorUserId, userId, "restored by an admin");
    }

    /// <summary>Undoes "left": they get their monthly budget again (this month too, if they hadn't been allocated).</summary>
    private async Task<OpResult> ReactivateAsync(int? actorUserId, int userId, string why)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var user = await db.Users.FindAsync(userId);
        if (user is null || !user.IsDisabled) return OpResult.Fail("That person isn't marked as left.");
        user.IsDisabled = false;
        user.DisabledUtc = null;
        user.DisabledReason = null;
        user.DepartureHandledUtc = null;
        await db.SaveChangesAsync();
        await audit.RecordAsync(actorUserId, "people.reactivate", $"{user.Login} is active again ({why})", "user", userId, user.Login);
        await budgets.EnsureMonthlyAllocationsAsync(Period.Current);
        await notifier.NotifyAsync();
        return OpResult.Success($"{user.Login} is active again.");
    }

    public async Task<List<Departure>> GetDeparturesAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var users = await db.Users.AsNoTracking().Include(u => u.Memberships).ThenInclude(m => m.Organization)
            .Where(u => u.IsDisabled).ToListAsync();
        if (users.Count == 0) return [];
        var budgetsNow = (await budgets.GetBudgetsAsync(Period.Current)).ToDictionary(b => b.User.Id);
        var allPools = await pools.GetPoolsAsync();
        return users.OrderBy(u => u.DepartureHandledUtc is not null).ThenByDescending(u => u.DisabledUtc).Select(u => new Departure(
                u,
                Math.Max(0, budgetsNow.GetValueOrDefault(u.Id)?.Available ?? 0),
                allPools.Where(p => !p.Pool.IsShared && p.HasMember(u.Id)).ToList(),
                allPools.Where(p => p.Pool.IsShared && p.HasMember(u.Id)).ToList(),
                u.Memberships.Where(m => m.HasSeat).Select(m => m.Organization.Name).ToList(),
                u.AssignedRoles))
            .ToList();
    }

    /// <summary>
    /// Hands over everything someone who left still holds, in one step:
    /// <list type="bullet">
    /// <item>Credits left this month go to <paramref name="recipientId"/>, or are returned (taken off their budget) if null.</item>
    /// <item>Personal reserves move to the recipient (or are archived if there's none).</item>
    /// <item>They're taken out of shared pools, and their assigned roles are removed.</item>
    /// </list>
    /// Moving credits to someone doesn't change total spend, so it needs no approval; returning them lowers spend.
    /// </summary>
    public async Task<OpResult> ReassignAsync(int actorUserId, int userId, int? recipientId)
    {
        var done = new List<string>();
        AppUser user;
        await BudgetService.WriteLock.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            if (!budgets.IsAppAdmin(await db.Users.FindAsync(actorUserId))) return OpResult.Fail("Only app admins can reassign credits.");
            var found = await db.Users.FindAsync(userId);
            if (found is null || !found.IsDisabled) return OpResult.Fail("Only people marked as left can be reassigned.");
            user = found;
            AppUser? recipient = null;
            if (recipientId is int rid)
            {
                recipient = await db.Users.FindAsync(rid);
                if (recipient is null || recipient.IsDisabled || rid == userId) return OpResult.Fail("Pick someone who's still here.");
                if (!await db.Memberships.AnyAsync(m => m.UserId == rid && m.HasSeat)) return OpResult.Fail($"{recipient.Login} doesn't have a Copilot seat.");
            }

            var available = await BudgetService.GetAvailableAsync(db, userId, Period.Current);
            var credits = Math.Max(0, Math.Floor(available));
            if (credits > 0)
            {
                if (recipient is not null)
                {
                    BudgetService.WriteTransfer(db, userId, recipient.Id, credits, $"Reassigned: {user.Login} has left", null, actorUserId);
                    done.Add($"{Format.Amount(credits)} {Unit} moved to {recipient.Login}");
                }
                else
                {
                    db.Ledger.Add(new LedgerEntry
                    {
                        UserId = userId, Period = Period.Current, Amount = -credits, Type = LedgerEntryType.AdminAdjustment,
                        Note = $"Returned: {user.Login} has left", CreatedByUserId = actorUserId,
                    });
                    done.Add($"{Format.Amount(credits)} {Unit} returned (taken off their budget)");
                }
            }

            var memberships = await db.CreditPoolMembers.Include(m => m.CreditPool).Where(m => m.UserId == userId && !m.CreditPool.IsArchived).ToListAsync();
            foreach (var m in memberships)
            {
                if (!m.CreditPool.IsShared)
                {
                    if (recipient is not null && !await db.CreditPoolMembers.AnyAsync(x => x.CreditPoolId == m.CreditPoolId && x.UserId == recipient.Id))
                    {
                        m.UserId = recipient.Id;
                        m.CreditPool.Name = $"{m.CreditPool.Name} (from {user.Login})";
                        done.Add($"reserve {m.CreditPool.Name} moved to {recipient.Login}");
                    }
                    else
                    {
                        m.CreditPool.IsArchived = true;
                        done.Add($"reserve {m.CreditPool.Name} archived");
                    }
                }
                else
                {
                    db.CreditPoolMembers.Remove(m);
                    done.Add($"removed from shared pool {m.CreditPool.Name}");
                }
            }

            if (user.AssignedRoles != AppRole.None)
            {
                done.Add($"roles removed ({BudgetService.RoleNames(user.AssignedRoles)})");
                user.AssignedRoles = AppRole.None;
            }
            user.DepartureHandledUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        finally
        {
            BudgetService.WriteLock.Release();
        }

        var summary = $"Reassigned what {user.Login} held: " + (done.Count == 0 ? "nothing left to move" : string.Join("; ", done));
        await audit.RecordAsync(actorUserId, "people.reassign", summary, "user", userId, user.Login, new { recipientId });
        await notifier.NotifyAsync();
        return OpResult.Success(summary + ".");
    }
}

/// <summary>Checks Entra accounts every few hours (Directory:CheckIntervalHours, default 6).</summary>
public class DirectoryCheckBackgroundService(IServiceProvider services, IDirectoryClient directory, IConfiguration configuration,
    ILogger<DirectoryCheckBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!directory.IsAvailable) return;
        var hours = Math.Max(1, configuration.GetValue("Directory:CheckIntervalHours", 6));
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(hours));
        do
        {
            try
            {
                using var scope = services.CreateScope();
                var result = await scope.ServiceProvider.GetRequiredService<DepartureService>().CheckDirectoryAsync(ct: stoppingToken);
                logger.LogInformation("Entra account check: {Result}", result.Message);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Entra account check failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
