using CopilotBudget.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CopilotBudget.Services;

public record OpResult(bool Ok, string Message)
{
    public static OpResult Success(string message) => new(true, message);
    public static OpResult Fail(string message) => new(false, message);
}

/// <summary>One person's budget for a month, across every organization they belong to.</summary>
/// <param name="MonthlyBudget">What they get each month: their override, or their org default, plus active exceptions.</param>
/// <param name="Held">Credits the person has sent that are waiting for an approver.</param>
/// <param name="ExceptionCredits">Part of <paramref name="MonthlyBudget"/> that comes from temporary exceptions.</param>
public record MemberBudget(AppUser User, IReadOnlyList<Membership> Memberships, decimal Allocated, decimal Used,
    decimal MonthlyBudget, decimal Held = 0, decimal ExceptionCredits = 0)
{
    public decimal Available => Allocated - Used - Held;
    public double PercentUsed => Allocated <= 0 ? (Used > 0 ? 100 : 0) : (double)(Used / Allocated * 100);
    public bool HasSeat => Memberships.Any(m => m.HasSeat);
    public IEnumerable<Organization> Organizations => Memberships.Select(m => m.Organization);
    public Membership? LatestActivity => Memberships.Where(m => m.LastActivityUtc is not null).MaxBy(m => m.LastActivityUtc);

    /// <summary>The org default, before any override or exception.</summary>
    public decimal DefaultBudget => BudgetService.DefaultBudgetFor(Memberships).Amount;
}

public record BudgetSummary(int People, decimal Allocated, decimal Used, int PendingRequests, int OverBudget)
{
    public decimal Available => Allocated - Used;
    public double PercentUsed => Allocated <= 0 ? 0 : (double)(Used / Allocated * 100);
}

/// <summary>Who would gain and lose what if a transfer went ahead.</summary>
public record TransferPreview(decimal SenderBefore, decimal SenderAfter, decimal RecipientBefore, decimal RecipientAfter,
    bool NeedsApproval, string? Problem)
{
    public bool CanSend => Problem is null;
}

/// <summary>
/// Owns the budget rules. Organizations set default monthly budgets, but every budget belongs to a person, and
/// anyone with a seat can send credits to or ask anyone else regardless of organization.
/// Every write checks that the acting user is allowed to do it.
/// </summary>
public class BudgetService(
    IDbContextFactory<AppDbContext> dbFactory,
    ChangeNotifier notifier,
    Notifications notifications,
    AuditLog audit,
    IOptions<AppOptions> appOptions,
    ILogger<BudgetService> logger)
{
    // SQLite has one writer and this app runs as a single instance, so a process-wide lock
    // is enough to stop two approvals from spending the same budget twice.
    internal static readonly SemaphoreSlim WriteLock = new(1, 1);

    private string Unit => appOptions.Value.UnitLabel;

    /// <summary>
    /// The org default: the largest budget among the organizations where the person has a seat. It isn't the total,
    /// because GitHub bills one Copilot seat per person however many orgs assign them one.
    /// </summary>
    internal static (decimal Amount, int? OrganizationId) DefaultBudgetFor(IEnumerable<Membership> memberships)
    {
        var best = memberships.Where(m => m.HasSeat)
            .OrderByDescending(m => m.Organization.DefaultMonthlyAllocation)
            .ThenBy(m => m.OrganizationId)
            .FirstOrDefault();
        return best is null ? (0, null) : (best.Organization.DefaultMonthlyAllocation, best.OrganizationId);
    }

    /// <summary>
    /// A person's monthly budget on a given day: an admin's override if they set one, otherwise the org default,
    /// plus any temporary exceptions active that day. People without a seat get nothing.
    /// </summary>
    internal static (decimal Amount, int? OrganizationId, decimal Exceptions) MonthlyBudgetFor(
        AppUser user, IEnumerable<Membership> memberships, IEnumerable<BudgetException> exceptions, DateTime day)
    {
        var list = memberships as IReadOnlyCollection<Membership> ?? memberships.ToList();
        var (orgDefault, orgId) = DefaultBudgetFor(list);
        // People who have left get nothing new.
        if (!list.Any(m => m.HasSeat) || user.IsDisabled) return (0, null, 0);
        var extra = exceptions.Where(e => e.UserId == user.Id && e.IsActiveOn(day)).Sum(e => e.Amount);
        return (Math.Max(0, (user.MonthlyBudgetOverride ?? orgDefault) + extra), orgId, extra);
    }

    /// <summary>The day budgets are worked out for: today in the current month, otherwise the month's first day.</summary>
    internal static DateTime BudgetDay(string period) =>
        period == Period.Current ? DateTime.UtcNow.Date : Period.StartUtc(period);

    /// <summary>
    /// Gives every seat holder their monthly budget for the period, keeps this month's budgets in line with overrides
    /// and exceptions that start or end, and expires anything left pending from earlier months.
    /// </summary>
    public async Task EnsureMonthlyAllocationsAsync(string period)
    {
        bool changed;
        await WriteLock.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            changed = await AllocateAsync(db, period);
            if (period == Period.Current)
            {
                changed |= await RebaseAsync(db, null, period, "Budget updated: an exception or override started or ended", null, null) > 0;
                changed |= await ExpireStaleAsync(db, period);
            }
            if (changed) await db.SaveChangesAsync();
        }
        finally
        {
            WriteLock.Release();
        }
        if (changed) await notifier.NotifyAsync();
    }

    private async Task<bool> AllocateAsync(AppDbContext db, string period)
    {
        var seats = await db.Memberships.Include(m => m.Organization).Include(m => m.User)
            .Where(m => m.HasSeat && !m.User.IsDisabled)
            .ToListAsync();
        var alreadyAllocated = (await db.Ledger
            .Where(l => l.Period == period && l.Type == LedgerEntryType.MonthlyAllocation)
            .Select(l => l.UserId)
            .ToListAsync()).ToHashSet();
        var exceptions = await db.BudgetExceptions.Where(e => e.IsApproved && !e.IsCancelled).ToListAsync();
        var day = BudgetDay(period);

        var added = 0;
        foreach (var person in seats.GroupBy(m => m.UserId).Where(g => !alreadyAllocated.Contains(g.Key)))
        {
            var user = person.First().User;
            var (amount, orgId, extra) = MonthlyBudgetFor(user, person, exceptions, day);
            db.Ledger.Add(new LedgerEntry
            {
                UserId = person.Key,
                Period = period,
                Amount = amount,
                Type = LedgerEntryType.MonthlyAllocation,
                OrganizationId = orgId,
                Note = $"Monthly budget for {Period.Display(period)}" +
                       (user.MonthlyBudgetOverride is not null ? " (set for this person)" : "") +
                       (extra != 0 ? $", including {Format.Signed(extra)} from exceptions" : ""),
            });
            added++;
        }
        if (added > 0) logger.LogInformation("Allocated {Count} monthly budgets for {Period}", added, period);
        return added > 0;
    }

    /// <summary>
    /// Brings people's budgets for the period in line with what they should get now (after an org budget,
    /// override or exception changed), by adding the difference as a budget change. Transfers and adjustments
    /// are kept. Only people already allocated for the period are touched. Caller holds the lock and saves.
    /// </summary>
    internal static async Task<int> RebaseAsync(AppDbContext db, IReadOnlyCollection<int>? userIds, string period,
        string note, int? actorUserId, int? organizationId)
    {
        var memberships = await db.Memberships.Include(m => m.Organization).Include(m => m.User)
            .Where(m => (userIds == null || userIds.Contains(m.UserId)) && !m.User.IsDisabled)
            .ToListAsync();
        var exceptions = await db.BudgetExceptions.Where(e => e.IsApproved && !e.IsCancelled).ToListAsync();
        var baseRows = await db.Ledger
            .Where(l => l.Period == period && (userIds == null || userIds.Contains(l.UserId))
                        && (l.Type == LedgerEntryType.MonthlyAllocation || l.Type == LedgerEntryType.BudgetChange))
            .Select(l => new { l.UserId, l.Type, l.Amount })
            .ToListAsync();
        // Include rows added in this context but not saved yet (e.g. a fresh allocation).
        var pending = db.ChangeTracker.Entries<LedgerEntry>()
            .Where(e => e.State == EntityState.Added && e.Entity.Period == period
                        && e.Entity.Type is LedgerEntryType.MonthlyAllocation or LedgerEntryType.BudgetChange)
            .Select(e => new { e.Entity.UserId, e.Entity.Type, e.Entity.Amount });
        var rows = baseRows.Concat(pending).ToList();
        var day = BudgetDay(period);

        var updated = 0;
        foreach (var person in memberships.GroupBy(m => m.UserId))
        {
            var mine = rows.Where(r => r.UserId == person.Key).ToList();
            if (!mine.Any(r => r.Type == LedgerEntryType.MonthlyAllocation)) continue; // Allocated later at the new amount.

            var current = mine.Sum(r => r.Amount);
            var (target, orgId, _) = MonthlyBudgetFor(person.First().User, person, exceptions, day);
            if (target == current) continue;

            db.Ledger.Add(new LedgerEntry
            {
                UserId = person.Key,
                Period = period,
                Amount = target - current,
                Type = LedgerEntryType.BudgetChange,
                OrganizationId = organizationId ?? orgId,
                Note = note,
                CreatedByUserId = actorUserId,
            });
            updated++;
        }
        return updated;
    }

    /// <summary>
    /// Requests, transfers and budget proposals still pending from an earlier month expire, so last month's credits
    /// don't move in a new month. Held credits go back to the sender. Caller saves.
    /// </summary>
    private static async Task<bool> ExpireStaleAsync(AppDbContext db, string period)
    {
        var start = Period.StartUtc(period);
        var now = DateTime.UtcNow;
        var expired = 0;

        foreach (var t in await db.PendingTransfers.Where(t => t.Status == TransferStatus.Pending && t.CreatedUtc < start).ToListAsync())
        {
            t.Status = TransferStatus.Expired;
            t.ResolvedUtc = now;
            t.ResponseNote = "Not approved before the month ended.";
            expired++;
        }
        foreach (var r in await db.Requests
                     .Where(r => (r.Status == RequestStatus.Pending || r.Status == RequestStatus.AwaitingApproval) && r.CreatedUtc < start)
                     .ToListAsync())
        {
            r.Status = RequestStatus.Expired;
            r.ResolvedUtc = now;
            r.ResponseNote = "Not filled before the month ended.";
            expired++;
        }
        foreach (var p in await db.Proposals.Include(p => p.BudgetException)
                     .Where(p => p.Status == ProposalStatus.Pending && p.CreatedUtc < start).ToListAsync())
        {
            p.Status = ProposalStatus.Expired;
            p.DecidedUtc = now;
            p.DecisionNote = "Not decided before the month ended.";
            if (p.BudgetException is { IsApproved: false } e) e.IsCancelled = true;
            expired++;
        }
        return expired > 0;
    }

    /// <summary>Everyone in any tracked organization, with their budget for the period.</summary>
    public async Task<List<MemberBudget>> GetBudgetsAsync(string period)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var users = await db.Users
            .Include(u => u.Memberships).ThenInclude(m => m.Organization)
            .Where(u => u.Memberships.Any())
            .AsNoTracking()
            .ToListAsync();

        // SQLite can't aggregate decimals server-side, so sum in memory. Data per month is small.
        var ledger = await db.Ledger.Where(l => l.Period == period)
            .Select(l => new { l.UserId, l.Amount })
            .ToListAsync();
        var usage = await db.Usage.Where(u => u.Period == period)
            .Select(u => new { u.Membership.UserId, u.Used })
            .ToListAsync();
        var holds = period == Period.Current
            ? await db.PendingTransfers.Where(t => t.Status == TransferStatus.Pending)
                .Select(t => new { t.FromUserId, t.Amount })
                .ToListAsync()
            : [];
        var exceptions = await db.BudgetExceptions.AsNoTracking().Where(e => e.IsApproved && !e.IsCancelled).ToListAsync();
        var day = BudgetDay(period);

        var allocated = ledger.GroupBy(l => l.UserId).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
        var used = usage.GroupBy(u => u.UserId).ToDictionary(g => g.Key, g => g.Sum(x => x.Used));
        var held = holds.GroupBy(h => h.FromUserId).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));

        return users
            .Select(u =>
            {
                var (monthly, _, extra) = MonthlyBudgetFor(u, u.Memberships, exceptions, day);
                return new MemberBudget(u, u.Memberships, allocated.GetValueOrDefault(u.Id), used.GetValueOrDefault(u.Id),
                    monthly, held.GetValueOrDefault(u.Id), extra);
            })
            .OrderBy(b => b.User.Login, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>What the person can still send: budget minus usage minus credits held for pending transfers.</summary>
    internal static async Task<decimal> GetAvailableAsync(AppDbContext db, int userId, string period, int? ignoreTransferId = null)
    {
        var amounts = await db.Ledger.Where(l => l.UserId == userId && l.Period == period)
            .Select(l => l.Amount).ToListAsync();
        var used = await db.Usage.Where(u => u.Membership.UserId == userId && u.Period == period)
            .Select(u => u.Used).ToListAsync();
        var held = await db.PendingTransfers
            .Where(t => t.FromUserId == userId && t.Status == TransferStatus.Pending && t.Id != ignoreTransferId)
            .Select(t => t.Amount).ToListAsync();
        return amounts.Sum() - used.Sum() - held.Sum();
    }

    public static BudgetSummary Summarize(IReadOnlyCollection<MemberBudget> budgets, int pendingRequests) => new(
        budgets.Count(b => b.HasSeat),
        budgets.Sum(b => b.Allocated),
        budgets.Sum(b => b.Used),
        pendingRequests,
        budgets.Count(b => b.HasSeat && b.Available < 0));

    // ---------- Sending credits ----------

    /// <summary>
    /// What a transfer would do to both people's balances, and whether it can happen. Used by the send and
    /// fill-request dialogs so people see both sides before confirming.
    /// </summary>
    public async Task<TransferPreview> PreviewTransferAsync(int senderId, int recipientId, decimal amount)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var settings = await GetSettingsAsync(db);
        var senderBefore = await GetAvailableAsync(db, senderId, Period.Current);
        var recipientBefore = await GetAvailableAsync(db, recipientId, Period.Current);
        var problem = await CheckTransferAsync(db, settings, senderId, recipientId, amount, senderBefore);
        var held = NeedsApproval(settings, amount);
        return new TransferPreview(senderBefore, senderBefore - amount, recipientBefore,
            held ? recipientBefore : recipientBefore + amount, held, problem);
    }

    /// <summary>Sends credits from the acting user to anyone else with a seat.</summary>
    public async Task<OpResult> SendAsync(int actorUserId, int toUserId, decimal amount, string? note)
    {
        if (amount <= 0) return OpResult.Fail("Amount must be greater than zero.");

        OpResult result;
        bool held;
        await WriteLock.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            (result, held) = await TransferAsync(db, actorUserId, toUserId, amount, note, requestId: null, actorUserId);
            if (!result.Ok) return result;
            await db.SaveChangesAsync();
        }
        finally
        {
            WriteLock.Release();
        }

        await notifier.NotifyAsync();
        await notifications.CreditsSentAsync(actorUserId, toUserId, amount, held);
        return result;
    }

    /// <summary>Asks everyone for credits. Anyone else with a seat can fill the request.</summary>
    public async Task<OpResult> CreateRequestAsync(int actorUserId, decimal amount, string reason)
    {
        if (amount <= 0) return OpResult.Fail("Amount must be greater than zero.");
        if (string.IsNullOrWhiteSpace(reason)) return OpResult.Fail("Please give a reason for the request.");

        await using var db = await dbFactory.CreateDbContextAsync();
        if (!await HasSeatAsync(db, actorUserId)) return OpResult.Fail("You need a Copilot seat to request credits.");
        if (await db.Users.AnyAsync(u => u.Id == actorUserId && u.IsDisabled)) return OpResult.Fail("Your account has been disabled.");

        db.Requests.Add(new BudgetRequest
        {
            RequesterId = actorUserId,
            Amount = amount,
            Reason = reason.Trim(),
        });
        await db.SaveChangesAsync();
        await notifier.NotifyAsync();
        return OpResult.Success("Request sent. It stays open until someone fills it or the month ends.");
    }

    /// <summary>
    /// Fills a request from the filler's own budget. Anyone except the requester can fill it.
    /// (Requests made before requests became open-only may still name a target; only they can fill those.)
    /// </summary>
    public async Task<OpResult> FillRequestAsync(int actorUserId, int requestId, string? note)
    {
        BudgetRequest? request;
        OpResult transfer;
        bool held;
        await WriteLock.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            request = await db.Requests.FirstOrDefaultAsync(r => r.Id == requestId);
            if (request is null) return OpResult.Fail("Request not found.");
            if (request.Status != RequestStatus.Pending) return OpResult.Fail("This request has already been resolved.");
            if (actorUserId == request.RequesterId) return OpResult.Fail("You can't fill your own request.");
            if (request.TargetId is int t && t != actorUserId) return OpResult.Fail("This request was sent to someone else.");

            (transfer, held) = await TransferAsync(db, actorUserId, request.RequesterId, request.Amount,
                $"Request #{request.Id}: {request.Reason}", request.Id, actorUserId);
            if (!transfer.Ok) return transfer;

            // When approval is needed the request stays open until an approver signs off on the transfer.
            request.Status = held ? RequestStatus.AwaitingApproval : RequestStatus.Approved;
            request.ResolvedById = actorUserId;
            request.ResolvedUtc = DateTime.UtcNow;
            request.ResponseNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            await db.SaveChangesAsync();
        }
        finally
        {
            WriteLock.Release();
        }

        await notifier.NotifyAsync();
        if (held) await notifications.CreditsSentAsync(actorUserId, request.RequesterId, request.Amount, true);
        else await notifications.RequestFilledAsync(request, actorUserId);
        return transfer;
    }

    /// <summary>Admins can decline any request (as can the target of an older, targeted request).</summary>
    public async Task<OpResult> DeclineRequestAsync(int actorUserId, int requestId, string? note)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var request = await db.Requests.FirstOrDefaultAsync(r => r.Id == requestId);
        if (request is null) return OpResult.Fail("Request not found.");
        if (request.Status != RequestStatus.Pending) return OpResult.Fail("This request has already been resolved.");

        var isTarget = request.TargetId == actorUserId;
        if (!isTarget && !await IsManagerAsync(db, actorUserId))
            return OpResult.Fail("Only an admin can decline a request.");

        request.Status = RequestStatus.Declined;
        request.ResolvedById = actorUserId;
        request.ResolvedUtc = DateTime.UtcNow;
        request.ResponseNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        await db.SaveChangesAsync();
        if (!isTarget)
            await audit.RecordAsync(actorUserId, "request.decline", $"Declined request #{request.Id} for {Format.Amount(request.Amount)} {Unit}" +
                (request.ResponseNote is null ? "" : $": {request.ResponseNote}"), "request", request.Id);
        await notifier.NotifyAsync();
        return OpResult.Success("Request declined.");
    }

    public async Task<OpResult> CancelRequestAsync(int actorUserId, int requestId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var request = await db.Requests.FirstOrDefaultAsync(r => r.Id == requestId);
        if (request is null) return OpResult.Fail("Request not found.");
        if (request.RequesterId != actorUserId) return OpResult.Fail("Only the requester can cancel a request.");
        if (request.Status != RequestStatus.Pending) return OpResult.Fail("This request has already been resolved.");

        request.Status = RequestStatus.Cancelled;
        request.ResolvedUtc = DateTime.UtcNow;
        request.ResolvedById = actorUserId;
        await db.SaveChangesAsync();
        await notifier.NotifyAsync();
        return OpResult.Success("Request cancelled.");
    }

    public async Task<List<BudgetRequest>> GetRequestsAsync(RequestStatus? status = null, int take = 200)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var query = db.Requests
            .Include(r => r.Requester)
            .Include(r => r.Target)
            .Include(r => r.ResolvedBy)
            .AsQueryable();
        if (status is not null) query = query.Where(r => r.Status == status);
        return await query.OrderByDescending(r => r.CreatedUtc).Take(take).AsNoTracking().ToListAsync();
    }

    /// <param name="involving">Only entries this person is part of: on either side, or the one who made the change.</param>
    public async Task<List<LedgerEntry>> GetHistoryAsync(int? userId = null, int take = 200, Guid? transferId = null, int? involving = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var query = db.Ledger
            .Include(l => l.User)
            .Include(l => l.Counterparty)
            .Include(l => l.CreatedBy)
            .Include(l => l.Organization)
            .Include(l => l.CreditPool)
            .AsQueryable();
        if (userId is not null) query = query.Where(l => l.UserId == userId);
        if (transferId is not null) query = query.Where(l => l.TransferId == transferId);
        if (involving is not null)
            query = query.Where(l => l.UserId == involving || l.CounterpartyUserId == involving || l.CreatedByUserId == involving);
        return await query.OrderByDescending(l => l.CreatedUtc).ThenByDescending(l => l.Id)
            .Take(take).AsNoTracking().ToListAsync();
    }

    // ---------- Permissions ----------

    /// <summary>
    /// Every role the person has: assigned in the app, from Entra (through the role mappings), and Admin for people
    /// listed in App:Admins. People who have left have none.
    /// </summary>
    public AppRole RolesOf(AppUser? user) =>
        user is null || user.IsDisabled ? AppRole.None : user.Roles | (IsConfiguredAdmin(user) ? AppRole.Admin : AppRole.None);

    public bool Has(AppUser? user, AppRole role) => (RolesOf(user) & role) != 0;

    /// <summary>Admins listed in App:Admins, plus anyone with the Admin role.</summary>
    public bool IsAppAdmin(AppUser? user) => Has(user, AppRole.Admin);

    /// <summary>Admins listed in App:Admins (by GitHub login or work email) can't be demoted in the app.</summary>
    public bool IsConfiguredAdmin(AppUser user) => appOptions.Value.IsAdmin(user.Login) || appOptions.Value.IsAdmin(user.Email);

    public bool CanApproveTransfers(AppUser? user) => Has(user, AppRole.Approver | AppRole.Admin);

    /// <summary>Read access to everyone's budgets, history and reports.</summary>
    public bool CanSeeEveryone(AppUser? user) => Has(user, AppRole.Admin | AppRole.BudgetVisibility | AppRole.Auditor);

    /// <summary>GitHub credentials, organizations, write-back, account links, alerts and the API log.</summary>
    public bool CanManageIntegration(AppUser? user) => Has(user, AppRole.Admin | AppRole.IntegrationAdmin);

    public bool CanViewAudit(AppUser? user) => Has(user, AppRole.Admin | AppRole.Auditor);

    internal async Task<bool> CanManageOrgAsync(AppDbContext db, int userId, int organizationId) =>
        IsAppAdmin(await db.Users.FindAsync(userId))
        || await db.Memberships.AnyAsync(m => m.UserId == userId && m.OrganizationId == organizationId && m.IsOrgAdmin);

    /// <summary>App admins, and owners of any tracked organization.</summary>
    internal async Task<bool> IsManagerAsync(AppDbContext db, int userId) =>
        IsAppAdmin(await db.Users.FindAsync(userId))
        || await db.Memberships.AnyAsync(m => m.UserId == userId && m.IsOrgAdmin);

    /// <summary>App admins can change anyone's budget; org owners can change people in their organizations.</summary>
    internal async Task<bool> CanAdjustAsync(AppDbContext db, int actorUserId, int targetUserId)
    {
        if (IsAppAdmin(await db.Users.FindAsync(actorUserId))) return true;
        var ownedOrgs = db.Memberships.Where(m => m.UserId == actorUserId && m.IsOrgAdmin).Select(m => m.OrganizationId);
        return await db.Memberships.AnyAsync(m => m.UserId == targetUserId && ownedOrgs.Contains(m.OrganizationId));
    }

    private static Task<bool> HasSeatAsync(AppDbContext db, int userId) =>
        db.Memberships.AnyAsync(m => m.UserId == userId && m.HasSeat);

    // ---------- Transfers ----------

    private static bool NeedsApproval(AppSettings settings, decimal amount) =>
        settings.RequireTransferApproval && (settings.ApprovalThreshold is not { } threshold || amount >= threshold);

    /// <summary>Why a transfer can't happen, or null if it can.</summary>
    private async Task<string?> CheckTransferAsync(AppDbContext db, AppSettings settings, int senderId, int recipientId,
        decimal amount, decimal available)
    {
        if (amount <= 0) return "Amount must be greater than zero.";
        if (recipientId == senderId) return "You can't send credits to yourself.";
        if (await db.Users.AsNoTracking().AnyAsync(u => u.Id == recipientId && u.IsDisabled)) return "That person has left, so they can't receive credits.";
        if (!await HasSeatAsync(db, recipientId)) return "That person doesn't have a Copilot seat.";
        if (!await HasSeatAsync(db, senderId)) return "You need a Copilot seat to send credits.";
        if (amount > available)
            return $"You only have {Format.Amount(Math.Max(0, available))} {Unit} available this month.";
        if (settings.MinimumKeep > 0 && available - amount < settings.MinimumKeep)
            return $"You need to keep at least {Format.Amount(settings.MinimumKeep)} {Unit} for yourself, so you can send up to " +
                   $"{Format.Amount(Math.Max(0, available - settings.MinimumKeep))} right now.";
        return null;
    }

    /// <summary>Most a person can send right now, after the minimum they must keep.</summary>
    public static decimal SendableFrom(decimal available, AppSettings settings) =>
        Math.Max(0, Math.Floor(available - settings.MinimumKeep));

    /// <summary>
    /// Starts a transfer. Normally it happens straight away; when approval is needed it's held for an approver.
    /// Caller holds the write lock and saves.
    /// </summary>
    private async Task<(OpResult Result, bool Held)> TransferAsync(AppDbContext db, int senderId, int recipientId, decimal amount,
        string? note, int? requestId, int actorUserId)
    {
        var settings = await GetSettingsAsync(db);
        var available = await GetAvailableAsync(db, senderId, Period.Current);
        if (await CheckTransferAsync(db, settings, senderId, recipientId, amount, available) is { } problem)
            return (OpResult.Fail(problem), false);

        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (NeedsApproval(settings, amount))
        {
            db.PendingTransfers.Add(new PendingTransfer
            {
                FromUserId = senderId, ToUserId = recipientId, Amount = amount, Note = trimmed,
                BudgetRequestId = requestId, CreatedByUserId = actorUserId,
            });
            return (OpResult.Success($"Sent for approval. {Format.Amount(amount)} {Unit} are held from your budget until an approver signs off" +
                                     " (or the month ends)."), true);
        }

        WriteTransfer(db, senderId, recipientId, amount, trimmed, requestId, actorUserId);
        return (OpResult.Success($"Sent {Format.Amount(amount)} {Unit}."), false);
    }

    /// <summary>Adds the paired ledger rows for a transfer, tied together by one transfer ID.</summary>
    internal static Guid WriteTransfer(AppDbContext db, int senderId, int recipientId, decimal amount,
        string? note, int? requestId, int actorUserId)
    {
        var period = Period.Current;
        var transferId = Guid.NewGuid();
        db.Ledger.Add(new LedgerEntry
        {
            UserId = senderId, CounterpartyUserId = recipientId, Period = period,
            Amount = -amount, Type = LedgerEntryType.TransferOut, Note = note,
            BudgetRequestId = requestId, CreatedByUserId = actorUserId, TransferId = transferId,
        });
        db.Ledger.Add(new LedgerEntry
        {
            UserId = recipientId, CounterpartyUserId = senderId, Period = period,
            Amount = amount, Type = LedgerEntryType.TransferIn, Note = note,
            BudgetRequestId = requestId, CreatedByUserId = actorUserId, TransferId = transferId,
        });
        return transferId;
    }

    // ---------- Admin transfers ----------

    /// <summary>
    /// An admin moves credits from one person to another. It happens straight away: no transfer approval, and the
    /// sender's minimum-to-keep doesn't apply. Only unused credits can move. Total spend doesn't change, so no budget
    /// authority is needed. Both people are told, and it's audited.
    /// </summary>
    public async Task<OpResult> ForceTransferAsync(int actorUserId, int fromUserId, int toUserId, decimal amount, string reason)
    {
        if (amount <= 0) return OpResult.Fail("Amount must be greater than zero.");
        if (amount != Math.Floor(amount)) return OpResult.Fail("Use a whole number of credits.");
        if (string.IsNullOrWhiteSpace(reason)) return OpResult.Fail("Please give a reason. Both people see it.");
        if (fromUserId == toUserId) return OpResult.Fail("Pick two different people.");

        AppUser from, to;
        Guid transferId;
        await WriteLock.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            if (!IsAppAdmin(await db.Users.FindAsync(actorUserId))) return OpResult.Fail("Only app admins can move credits between people.");
            var f = await db.Users.FindAsync(fromUserId);
            var t = await db.Users.FindAsync(toUserId);
            if (f is null || t is null) return OpResult.Fail("Person not found.");
            (from, to) = (f, t);
            if (to.IsDisabled) return OpResult.Fail($"{to.Login} has left, so they can't receive credits.");
            if (!await HasSeatAsync(db, toUserId)) return OpResult.Fail($"{to.Login} doesn't have a Copilot seat.");

            var available = await GetAvailableAsync(db, fromUserId, Period.Current);
            if (amount > available)
                return OpResult.Fail($"{from.Login} only has {Format.Amount(Math.Max(0, available))} {Unit} unused this month" +
                                     (available < amount && await db.PendingTransfers.AnyAsync(x => x.FromUserId == fromUserId && x.Status == TransferStatus.Pending)
                                         ? " (some are held for transfers waiting for approval)." : "."));

            transferId = WriteTransfer(db, fromUserId, toUserId, amount, $"Moved by an admin: {reason.Trim()}", null, actorUserId);
            await db.SaveChangesAsync();
        }
        finally
        {
            WriteLock.Release();
        }

        var summary = $"Moved {Format.Amount(amount)} {Unit} from {from.Login} to {to.Login}: {reason.Trim()}";
        await audit.RecordAsync(actorUserId, "transfer.force", summary, "transfer", transferId, $"{from.Login} → {to.Login}",
            new { from = from.Login, to = to.Login, amount, reason = reason.Trim() });
        await notifier.NotifyAsync();
        await notifications.ForcedTransferAsync(from, to, amount, reason.Trim());
        return OpResult.Success(summary + ".");
    }

    // ---------- Settings ----------

    internal static async Task<AppSettings> GetSettingsAsync(AppDbContext db) =>
        await db.Settings.AsNoTracking().FirstOrDefaultAsync() ?? new AppSettings();

    public async Task<AppSettings> GetSettingsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await GetSettingsAsync(db);
    }

    private async Task<OpResult> UpdateSettingsAsync(int actorUserId, Action<AppSettings> change, string message,
        string auditAction, AppRole allowed = AppRole.Admin)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!Has(await db.Users.FindAsync(actorUserId), allowed))
            return OpResult.Fail(allowed == AppRole.Admin ? "Only app admins can change this." : "You don't have permission to change this.");

        var settings = await db.Settings.FirstOrDefaultAsync();
        if (settings is null) db.Settings.Add(settings = new AppSettings());
        var before = new { settings.RequireTransferApproval, settings.ApprovalThreshold, settings.MinimumKeep, settings.RequireBudgetAuthority, settings.GitHubWriteBackEnabled };
        change(settings);
        settings.UpdatedUtc = DateTime.UtcNow;
        settings.UpdatedByUserId = actorUserId;
        await db.SaveChangesAsync();
        var after = new { settings.RequireTransferApproval, settings.ApprovalThreshold, settings.MinimumKeep, settings.RequireBudgetAuthority, settings.GitHubWriteBackEnabled };
        await audit.RecordAsync(actorUserId, auditAction, message, "settings", null, null, new { before, after });
        await notifier.NotifyAsync();
        return OpResult.Success(message);
    }

    /// <summary>
    /// Transfer rules: whether transfers need an approver, from what size (null threshold means all of them), and how
    /// many credits a sender must keep.
    /// </summary>
    public async Task<OpResult> SetTransferRulesAsync(int actorUserId, bool requireApproval, decimal? threshold, decimal minimumKeep)
    {
        if (threshold is < 0) return OpResult.Fail("The approval threshold can't be negative.");
        if (minimumKeep < 0) return OpResult.Fail("The minimum to keep can't be negative.");
        if (threshold == 0) threshold = null;

        string message;
        if (!requireApproval) message = "Approval is off. Transfers now happen straight away. Anything already waiting still needs an approver.";
        else
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var approvers = (await db.Users.AsNoTracking().ToListAsync()).Count(u => u.IsApprover && !u.IsDisabled);
            message = (threshold is null ? "Every transfer now needs an approver." : $"Transfers of {Format.Amount(threshold.Value)} {Unit} or more now need an approver.")
                      + (approvers == 0 ? " No one is an approver yet, so app admins will approve them." : "");
        }
        if (minimumKeep > 0) message += $" Senders keep at least {Format.Amount(minimumKeep)} {Unit}.";

        return await UpdateSettingsAsync(actorUserId, s =>
        {
            s.RequireTransferApproval = requireApproval;
            s.ApprovalThreshold = threshold;
            s.MinimumKeep = minimumKeep;
        }, message, "settings.transfer-rules");
    }

    public Task<OpResult> SetBudgetAuthorityRequiredAsync(int actorUserId, bool required) =>
        UpdateSettingsAsync(actorUserId, s => s.RequireBudgetAuthority = required, required
            ? "Increases to total spend now need a budget authority's approval."
            : "Admins and org owners can now raise budgets without a budget authority.", "settings.budget-authority");

    /// <summary>The write-back off switch. Integration admins can flip it: it controls the connection, not anyone's budget.</summary>
    public Task<OpResult> SetWriteBackAsync(int actorUserId, bool enabled) =>
        UpdateSettingsAsync(actorUserId, s => s.GitHubWriteBackEnabled = enabled, enabled
            ? "Writing to GitHub is on. Budgets are checked before and after every write."
            : "Writing to GitHub is off. Nothing is written; the app still reads GitHub and flags differences.",
            "writeback.toggle", AppRole.Admin | AppRole.IntegrationAdmin);

    // ---------- Approvals ----------

    public async Task<List<PendingTransfer>> GetTransfersAsync(TransferStatus? status = null, int take = 200)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var query = db.PendingTransfers
            .Include(t => t.From).Include(t => t.To).Include(t => t.ResolvedBy).Include(t => t.BudgetRequest)
            .AsQueryable();
        if (status is not null) query = query.Where(t => t.Status == status);
        return await query.OrderByDescending(t => t.CreatedUtc).Take(take).AsNoTracking().ToListAsync();
    }

    /// <summary>Approvers (and app admins) sign off a held transfer, as long as they aren't part of it.</summary>
    public async Task<OpResult> ApproveTransferAsync(int actorUserId, int transferId, string? note)
    {
        PendingTransfer t;
        await WriteLock.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var found = await db.PendingTransfers.Include(x => x.From).Include(x => x.To).FirstOrDefaultAsync(x => x.Id == transferId);
            if (found is null) return OpResult.Fail("Transfer not found.");
            t = found;
            if (t.Status != TransferStatus.Pending) return OpResult.Fail("This transfer has already been resolved.");
            if (!CanApproveTransfers(await db.Users.FindAsync(actorUserId))) return OpResult.Fail("Only approvers can approve transfers.");
            if (actorUserId == t.FromUserId || actorUserId == t.ToUserId) return OpResult.Fail("You can't approve a transfer you're part of.");

            var available = await GetAvailableAsync(db, t.FromUserId, Period.Current, ignoreTransferId: t.Id);
            if (t.Amount > available)
                return OpResult.Fail($"{t.From.Login} only has {Format.Amount(Math.Max(0, available))} {Unit} available now. Reject it instead.");

            WriteTransfer(db, t.FromUserId, t.ToUserId, t.Amount, t.Note, t.BudgetRequestId, t.CreatedByUserId);
            t.Status = TransferStatus.Approved;
            t.ResolvedById = actorUserId;
            t.ResolvedUtc = DateTime.UtcNow;
            t.ResponseNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

            if (t.BudgetRequestId is int requestId && await db.Requests.FindAsync(requestId) is { } request)
            {
                request.Status = RequestStatus.Approved;
                request.ResolvedUtc = DateTime.UtcNow;
            }
            await db.SaveChangesAsync();
        }
        finally
        {
            WriteLock.Release();
        }
        await audit.RecordAsync(actorUserId, "transfer.approve", $"Approved {t.From.Login} → {t.To.Login}, {Format.Amount(t.Amount)} {Unit}", "transfer", t.Id);
        await notifier.NotifyAsync();
        await notifications.TransferDecidedAsync(t, approved: true);
        return OpResult.Success($"Approved. {t.From.Login} sent {Format.Amount(t.Amount)} {Unit} to {t.To.Login}.");
    }

    public async Task<OpResult> RejectTransferAsync(int actorUserId, int transferId, string? note)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var t = await db.PendingTransfers.Include(x => x.From).FirstOrDefaultAsync(x => x.Id == transferId);
        if (t is null) return OpResult.Fail("Transfer not found.");
        if (t.Status != TransferStatus.Pending) return OpResult.Fail("This transfer has already been resolved.");
        if (!CanApproveTransfers(await db.Users.FindAsync(actorUserId))) return OpResult.Fail("Only approvers can reject transfers.");
        if (actorUserId == t.FromUserId || actorUserId == t.ToUserId) return OpResult.Fail("You can't reject a transfer you're part of.");

        t.Status = TransferStatus.Rejected;
        t.ResolvedById = actorUserId;
        t.ResolvedUtc = DateTime.UtcNow;
        t.ResponseNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        await ReopenRequestAsync(db, t, $"{t.From.Login}'s transfer wasn't approved" + (t.ResponseNote is null ? "." : $": {t.ResponseNote}"));
        await db.SaveChangesAsync();
        await audit.RecordAsync(actorUserId, "transfer.reject", $"Rejected {t.From.Login}'s transfer of {Format.Amount(t.Amount)} {Unit}" +
            (t.ResponseNote is null ? "" : $": {t.ResponseNote}"), "transfer", t.Id);
        await notifier.NotifyAsync();
        await notifications.TransferDecidedAsync(t, approved: false);
        return OpResult.Success("Transfer rejected. The credits are back with the sender.");
    }

    /// <summary>The sender can withdraw a transfer that hasn't been approved yet.</summary>
    public async Task<OpResult> CancelTransferAsync(int actorUserId, int transferId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var t = await db.PendingTransfers.Include(x => x.From).FirstOrDefaultAsync(x => x.Id == transferId);
        if (t is null) return OpResult.Fail("Transfer not found.");
        if (t.Status != TransferStatus.Pending) return OpResult.Fail("This transfer has already been resolved.");
        if (t.FromUserId != actorUserId) return OpResult.Fail("Only the sender can cancel a transfer.");

        t.Status = TransferStatus.Cancelled;
        t.ResolvedById = actorUserId;
        t.ResolvedUtc = DateTime.UtcNow;
        await ReopenRequestAsync(db, t, $"{t.From.Login} withdrew their offer.");
        await db.SaveChangesAsync();
        await notifier.NotifyAsync();
        return OpResult.Success("Transfer cancelled. The credits are back in your budget.");
    }

    /// <summary>A request whose transfer didn't go through goes back to open, so someone else can fill it.</summary>
    private static async Task ReopenRequestAsync(AppDbContext db, PendingTransfer t, string note)
    {
        if (t.BudgetRequestId is not int requestId || await db.Requests.FindAsync(requestId) is not { } request) return;
        if (request.Status != RequestStatus.AwaitingApproval) return;
        request.Status = RequestStatus.Pending;
        request.ResolvedById = null;
        request.ResolvedUtc = null;
        request.ResponseNote = note;
    }

    // ---------- Roles ----------

    /// <summary>Everyone who has signed in or belongs to a tracked org, for the roles list.</summary>
    public async Task<List<AppUser>> GetPeopleAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return (await db.Users.AsNoTracking().ToListAsync())
            .OrderBy(u => u.Login, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Sets the roles an admin gives someone in the app. Roles from Entra are managed through role mappings.</summary>
    public async Task<OpResult> SetRolesAsync(int actorUserId, int userId, AppRole assigned)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var actor = await db.Users.FindAsync(actorUserId);
        if (!IsAppAdmin(actor)) return OpResult.Fail("Only app admins can change roles.");
        var user = await db.Users.FindAsync(userId);
        if (user is null) return OpResult.Fail("Person not found.");
        if (user.IsDisabled && assigned != AppRole.None) return OpResult.Fail($"{user.Login} has left, so they can't have roles.");

        var removingAdmin = user.AssignedRoles.HasFlag(AppRole.Admin) && !assigned.HasFlag(AppRole.Admin);
        if (removingAdmin && !user.DirectoryRoles.HasFlag(AppRole.Admin) && !IsConfiguredAdmin(user))
        {
            var others = (await db.Users.Where(u => u.Id != userId).ToListAsync()).Count(IsAppAdmin);
            if (others == 0) return OpResult.Fail("There has to be at least one admin.");
        }

        var before = user.AssignedRoles;
        if (before == assigned) return OpResult.Success("No changes.");
        user.AssignedRoles = assigned;
        await db.SaveChangesAsync();

        var added = RoleNames(assigned & ~before);
        var removed = RoleNames(before & ~assigned);
        var summary = $"{user.Login}: " + string.Join("; ", new[]
        {
            added.Length > 0 ? "now " + added : null,
            removed.Length > 0 ? "no longer " + removed : null,
        }.Where(x => x is not null));
        await audit.RecordAsync(actorUserId, "roles.change", summary, "user", user.Id, user.Login, new { before = before.ToString(), after = assigned.ToString() });
        await notifier.NotifyAsync();
        return OpResult.Success(summary + ".");
    }

    public static string RoleName(AppRole role) => role switch
    {
        AppRole.Admin => "admin",
        AppRole.Approver => "approver",
        AppRole.BudgetAuthority => "budget authority",
        AppRole.BudgetVisibility => "budget visibility",
        AppRole.IntegrationAdmin => "integration admin",
        AppRole.Auditor => "auditor",
        _ => role.ToString(),
    };

    public static readonly AppRole[] AllRoles =
        [AppRole.Admin, AppRole.Approver, AppRole.BudgetAuthority, AppRole.BudgetVisibility, AppRole.IntegrationAdmin, AppRole.Auditor];

    public static string RoleNames(AppRole roles) =>
        string.Join(", ", AllRoles.Where(r => roles.HasFlag(r)).Select(RoleName));

    /// <summary>Remembers whether this person wants amounts in dollars.</summary>
    public async Task SetPrefersDollarsAsync(int userId, bool dollars)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.PrefersDollars, dollars));
    }
}
