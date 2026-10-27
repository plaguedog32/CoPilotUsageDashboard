using CopilotBudget.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CopilotBudget.Services;

/// <summary>A change to budgets that an admin or org owner wants to make.</summary>
/// <param name="Value">New org budget or override (null clears an override), adjustment amount, or exception amount.</param>
public record BudgetChange(
    BudgetChangeKind Kind,
    int? OrganizationId = null,
    int? UserId = null,
    decimal? Value = null,
    string? Note = null,
    DateTime? StartDate = null,
    DateTime? EndDate = null);

/// <summary>One person's budget before and after a change.</summary>
public record BudgetDelta(AppUser User, decimal Before, decimal After)
{
    public decimal Change => After - Before;
}

/// <summary>What a change does to spend, worked out before it's applied.</summary>
/// <param name="ThisMonthBefore">Total budget across everyone this month now.</param>
/// <param name="ThisMonthAfter">Total budget this month once the change is applied.</param>
/// <param name="MonthlyChange">Change to every future month's total (org budgets and overrides; exceptions end).</param>
/// <param name="ExceptionMonths">Months an exception touches, for its total cost.</param>
public record SpendPreview(
    decimal ThisMonthBefore,
    decimal ThisMonthAfter,
    decimal MonthlyChange,
    IReadOnlyList<BudgetDelta> People,
    bool NeedsBudgetAuthority,
    int ExceptionMonths = 0,
    decimal ExceptionAmount = 0)
{
    public decimal ThisMonthChange => ThisMonthAfter - ThisMonthBefore;
    public bool RaisesSpend => ThisMonthChange > 0 || MonthlyChange > 0 || ExceptionAmount * ExceptionMonths > 0;
    public decimal ExceptionTotal => ExceptionAmount * ExceptionMonths;
}

/// <summary>
/// Changes to how many credits people get: org budgets, per-person overrides, temporary exceptions and one-off
/// adjustments. Any admin or org owner can propose them, and each one can be previewed first. A change that raises
/// total spend waits for a budget authority (unless the proposer is one, or the setting is off).
/// </summary>
public class BudgetChangeService(
    IDbContextFactory<AppDbContext> dbFactory,
    BudgetService budgets,
    ChangeNotifier notifier,
    Notifications notifications,
    AuditLog audit,
    IOptions<AppOptions> appOptions)
{
    private string Unit => appOptions.Value.UnitLabel;

    /// <summary>Works out what a change would do without saving anything.</summary>
    public async Task<(OpResult Check, SpendPreview? Preview)> PreviewAsync(int actorUserId, BudgetChange change)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var check = await ValidateAsync(db, actorUserId, change);
        if (!check.Ok) return (check, null);
        return (check, await BuildPreviewAsync(db, actorUserId, change));
    }

    /// <summary>Applies the change, or files it for a budget authority if it raises spend.</summary>
    public async Task<OpResult> SubmitAsync(int actorUserId, BudgetChange change)
    {
        BudgetProposal? proposal = null;
        OpResult result;
        await BudgetService.WriteLock.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var check = await ValidateAsync(db, actorUserId, change);
            if (!check.Ok) return check;

            var preview = await BuildPreviewAsync(db, actorUserId, change);
            if (!preview.RaisesSpend && preview.People.Count == 0 && change.Kind != BudgetChangeKind.Exception)
                return OpResult.Success("Nothing to change.");

            if (preview.NeedsBudgetAuthority)
            {
                proposal = await ProposeAsync(db, actorUserId, change, preview);
                await db.SaveChangesAsync();
                result = OpResult.Success($"Sent to a budget authority for approval: {proposal.Summary}. Nothing changes until it's approved.");
            }
            else
            {
                result = await ApplyAsync(db, actorUserId, change, exception: null);
                if (!result.Ok) return result;
                await db.SaveChangesAsync();
            }
        }
        finally
        {
            BudgetService.WriteLock.Release();
        }
        var what = await DescribeAsync(change);
        await audit.RecordAsync(actorUserId, proposal is null ? AuditAction(change.Kind) : "budget.propose",
            proposal is null ? $"{what}{(string.IsNullOrWhiteSpace(change.Note) ? "" : $" ({change.Note.Trim()})")}" : $"Proposed: {what}",
            change.UserId is not null ? "user" : "organization", change.UserId ?? change.OrganizationId, null,
            new { change.Kind, change.Value, change.StartDate, change.EndDate, proposal = proposal?.Id });
        await notifier.NotifyAsync();
        if (proposal is not null) await notifications.ProposalCreatedAsync(proposal);
        return result;
    }

    // ---------- Proposals ----------

    public async Task<List<BudgetProposal>> GetProposalsAsync(ProposalStatus? status = null, int take = 100)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var query = db.Proposals.Include(p => p.User).Include(p => p.Organization).Include(p => p.ProposedBy)
            .Include(p => p.DecidedBy).Include(p => p.BudgetException).AsQueryable();
        if (status is not null) query = query.Where(p => p.Status == status);
        return await query.OrderByDescending(p => p.CreatedUtc).Take(take).AsNoTracking().ToListAsync();
    }

    public async Task<OpResult> ApproveProposalAsync(int actorUserId, int proposalId, string? note)
    {
        BudgetProposal? p;
        OpResult result;
        await BudgetService.WriteLock.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var actor = await db.Users.FindAsync(actorUserId);
            if (!budgets.Has(actor, AppRole.BudgetAuthority)) return OpResult.Fail("Only a budget authority can approve increases.");
            p = await db.Proposals.Include(x => x.BudgetException).FirstOrDefaultAsync(x => x.Id == proposalId);
            if (p is null) return OpResult.Fail("Proposal not found.");
            if (p.Status != ProposalStatus.Pending) return OpResult.Fail("This proposal has already been decided.");
            if (p.ProposedById == actorUserId) return OpResult.Fail("Someone else has to approve your own proposal.");

            var change = new BudgetChange(p.Kind, p.OrganizationId, p.UserId, p.Value, p.Note,
                p.BudgetException?.StartDate, p.BudgetException?.EndDate);
            result = await ApplyAsync(db, p.ProposedById, change, p.BudgetException);
            if (!result.Ok) return result;

            p.Status = ProposalStatus.Applied;
            p.DecidedById = actorUserId;
            p.DecidedUtc = DateTime.UtcNow;
            p.DecisionNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            await db.SaveChangesAsync();
        }
        finally
        {
            BudgetService.WriteLock.Release();
        }
        await audit.RecordAsync(actorUserId, "budget.approve", $"Approved: {p.Summary}", "proposal", p.Id, null, new { p.SpendChange, p.ProposedById });
        await notifier.NotifyAsync();
        await notifications.ProposalDecidedAsync(p);
        return OpResult.Success("Approved. " + result.Message);
    }

    public async Task<OpResult> RejectProposalAsync(int actorUserId, int proposalId, string? note)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var actor = await db.Users.FindAsync(actorUserId);
        var p = await db.Proposals.Include(x => x.BudgetException).FirstOrDefaultAsync(x => x.Id == proposalId);
        if (p is null) return OpResult.Fail("Proposal not found.");
        if (p.Status != ProposalStatus.Pending) return OpResult.Fail("This proposal has already been decided.");

        var own = p.ProposedById == actorUserId;
        if (!own && !budgets.Has(actor, AppRole.BudgetAuthority)) return OpResult.Fail("Only a budget authority can reject proposals.");

        p.Status = own ? ProposalStatus.Cancelled : ProposalStatus.Rejected;
        p.DecidedById = actorUserId;
        p.DecidedUtc = DateTime.UtcNow;
        p.DecisionNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (p.BudgetException is { IsApproved: false } e) e.IsCancelled = true;
        await db.SaveChangesAsync();
        await audit.RecordAsync(actorUserId, own ? "budget.withdraw" : "budget.reject", $"{(own ? "Withdrew" : "Rejected")}: {p.Summary}" +
            (p.DecisionNote is null ? "" : $" ({p.DecisionNote})"), "proposal", p.Id);
        await notifier.NotifyAsync();
        if (!own) await notifications.ProposalDecidedAsync(p);
        return OpResult.Success(own ? "Proposal withdrawn." : "Proposal rejected. Nothing was changed.");
    }

    // ---------- Exceptions ----------

    public async Task<List<BudgetException>> GetExceptionsAsync(bool includeEnded = false)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var today = DateTime.UtcNow.Date;
        var query = db.BudgetExceptions.Include(e => e.User).Include(e => e.CreatedBy).AsQueryable();
        if (!includeEnded) query = query.Where(e => !e.IsCancelled && e.EndDate >= today);
        return await query.OrderBy(e => e.StartDate).AsNoTracking().ToListAsync();
    }

    /// <summary>Ends an exception early. Lowering spend never needs approval.</summary>
    public async Task<OpResult> CancelExceptionAsync(int actorUserId, int exceptionId)
    {
        await BudgetService.WriteLock.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var e = await db.BudgetExceptions.Include(x => x.User).FirstOrDefaultAsync(x => x.Id == exceptionId);
            if (e is null) return OpResult.Fail("Exception not found.");
            if (!await budgets.CanAdjustAsync(db, actorUserId, e.UserId)) return OpResult.Fail("You can't change this person's budget.");
            if (e.IsCancelled) return OpResult.Fail("This exception was already cancelled.");

            e.IsCancelled = true;
            foreach (var p in await db.Proposals.Where(p => p.BudgetExceptionId == e.Id && p.Status == ProposalStatus.Pending).ToListAsync())
            {
                p.Status = ProposalStatus.Cancelled;
                p.DecidedById = actorUserId;
                p.DecidedUtc = DateTime.UtcNow;
            }
            await db.SaveChangesAsync();
            var changed = await BudgetService.RebaseAsync(db, [e.UserId], Period.Current, $"Exception ended early: {e.Reason}", actorUserId, null);
            await db.SaveChangesAsync();
            await audit.RecordAsync(actorUserId, "budget.exception-end", $"Ended {e.User.Login}'s exception early: {e.Reason}", "user", e.UserId, e.User.Login);
            await notifier.NotifyAsync();
            return OpResult.Success(changed > 0 ? $"Exception cancelled. {e.User.Login}'s budget for this month is back to normal." : "Exception cancelled.");
        }
        finally
        {
            BudgetService.WriteLock.Release();
        }
    }

    // ---------- Internals ----------

    private async Task<OpResult> ValidateAsync(AppDbContext db, int actorUserId, BudgetChange change)
    {
        switch (change.Kind)
        {
            case BudgetChangeKind.OrganizationBudget:
                if (change.OrganizationId is not int orgId || await db.Organizations.FindAsync(orgId) is null)
                    return OpResult.Fail("Organization not found.");
                if (!await budgets.CanManageOrgAsync(db, actorUserId, orgId))
                    return OpResult.Fail("Only admins of this organization can change its monthly budget.");
                if (change.Value is not decimal v || v < 0) return OpResult.Fail("The monthly budget can't be negative.");
                if (v != Math.Floor(v)) return OpResult.Fail("The monthly budget must be a whole number.");
                return OpResult.Success("");

            case BudgetChangeKind.PersonOverride:
            case BudgetChangeKind.Exception:
            case BudgetChangeKind.Adjustment:
                if (change.UserId is not int userId || !await db.Users.AnyAsync(u => u.Id == userId)) return OpResult.Fail("Person not found.");
                if (!await budgets.CanAdjustAsync(db, actorUserId, userId))
                    return OpResult.Fail("Only admins of this person's organization can change their budget.");
                break;
        }

        switch (change.Kind)
        {
            case BudgetChangeKind.PersonOverride when change.Value is < 0:
                return OpResult.Fail("A monthly budget can't be negative.");
            case BudgetChangeKind.PersonOverride when change.Value is decimal o && o != Math.Floor(o):
                return OpResult.Fail("The monthly budget must be a whole number.");
            case BudgetChangeKind.Adjustment when change.Value is null or 0:
                return OpResult.Fail("Amount can't be zero.");
            case BudgetChangeKind.Adjustment when string.IsNullOrWhiteSpace(change.Note):
                return OpResult.Fail("Please add a note explaining the adjustment.");
            case BudgetChangeKind.Exception:
                if (change.Value is null or 0) return OpResult.Fail("Enter how many credits to add (or remove) while the exception lasts.");
                if (change.StartDate is not { } start || change.EndDate is not { } end) return OpResult.Fail("Pick a start and end date.");
                if (end.Date < start.Date) return OpResult.Fail("The end date is before the start date.");
                if (end.Date < DateTime.UtcNow.Date) return OpResult.Fail("That exception would already be over.");
                if (string.IsNullOrWhiteSpace(change.Note)) return OpResult.Fail("Please give a reason for the exception.");
                break;
        }
        return OpResult.Success("");
    }

    /// <summary>
    /// Loads the inputs to everyone's budget, applies the change in memory only, and compares this month's budgets
    /// before and after. The context is never saved here.
    /// </summary>
    private async Task<SpendPreview> BuildPreviewAsync(AppDbContext db, int actorUserId, BudgetChange change)
    {
        var period = Period.Current;
        var today = DateTime.UtcNow.Date;
        var memberships = await db.Memberships.AsNoTracking().Include(m => m.Organization).Include(m => m.User).ToListAsync();
        var exceptions = await db.BudgetExceptions.AsNoTracking().Where(e => e.IsApproved && !e.IsCancelled).ToListAsync();
        var ledger = await db.Ledger.Where(l => l.Period == period).Select(l => new { l.UserId, l.Amount }).ToListAsync();
        var allocatedUsers = (await db.Ledger.Where(l => l.Period == period && l.Type == LedgerEntryType.MonthlyAllocation)
            .Select(l => l.UserId).ToListAsync()).ToHashSet();
        var settings = await BudgetService.GetSettingsAsync(db);
        var actor = await db.Users.AsNoTracking().FirstAsync(u => u.Id == actorUserId);

        var people = memberships.GroupBy(m => m.UserId).ToDictionary(g => g.Key, g => g.ToList());
        // Budgets today, and at the start of next month for the ongoing monthly total.
        var nextMonth = Period.StartUtc(Period.AddMonths(period, 1));
        decimal Target(AppUser u, List<Membership> ms, IEnumerable<BudgetException> ex, DateTime day) =>
            BudgetService.MonthlyBudgetFor(u, ms, ex, day).Amount;

        var before = people.ToDictionary(p => p.Key, p => Target(p.Value[0].User, p.Value, exceptions, today));

        // Apply the change to the in-memory copies.
        var afterExceptions = exceptions.ToList();
        var adjustment = 0m;
        int months = 0;
        switch (change.Kind)
        {
            case BudgetChangeKind.OrganizationBudget:
                foreach (var m in memberships.Where(m => m.OrganizationId == change.OrganizationId))
                    m.Organization.DefaultMonthlyAllocation = change.Value!.Value;
                break;
            case BudgetChangeKind.PersonOverride:
                foreach (var m in memberships.Where(m => m.UserId == change.UserId))
                    m.User.MonthlyBudgetOverride = change.Value;
                break;
            case BudgetChangeKind.Exception:
                afterExceptions.Add(new BudgetException
                {
                    UserId = change.UserId!.Value, Amount = change.Value!.Value, StartDate = change.StartDate!.Value.Date,
                    EndDate = change.EndDate!.Value.Date, Reason = change.Note ?? "", IsApproved = true,
                });
                months = MonthsTouched(change.StartDate.Value, change.EndDate.Value);
                break;
            case BudgetChangeKind.Adjustment:
                adjustment = change.Value!.Value;
                break;
        }

        var deltas = new List<BudgetDelta>();
        decimal thisMonthChange = adjustment, monthlyChange = 0;
        foreach (var (userId, ms) in people)
        {
            var user = ms[0].User;
            var after = Target(user, ms, afterExceptions, today);
            // Exceptions are temporary, so they don't change the ongoing monthly total.
            if (change.Kind != BudgetChangeKind.Exception)
                monthlyChange += Target(user, ms, afterExceptions, nextMonth) - Target(user, ms, exceptions, nextMonth);
            if (allocatedUsers.Contains(userId) && after != before[userId]) thisMonthChange += after - before[userId];
            var userAdjust = change.Kind == BudgetChangeKind.Adjustment && userId == change.UserId ? adjustment : 0;
            if (after != before[userId] || userAdjust != 0)
                deltas.Add(new BudgetDelta(user, before[userId], after + userAdjust));
        }

        var total = ledger.Sum(l => l.Amount);
        var raises = thisMonthChange > 0 || monthlyChange > 0 || (change.Kind == BudgetChangeKind.Exception && change.Value > 0);
        var needsAuthority = raises && settings.RequireBudgetAuthority && !budgets.Has(actor, AppRole.BudgetAuthority);
        return new SpendPreview(total, total + thisMonthChange, monthlyChange,
            deltas.OrderByDescending(d => Math.Abs(d.Change)).ThenBy(d => d.User.Login).ToList(), needsAuthority,
            months, change.Kind == BudgetChangeKind.Exception ? change.Value!.Value : 0);
    }

    private static string AuditAction(BudgetChangeKind kind) => kind switch
    {
        BudgetChangeKind.OrganizationBudget => "budget.org",
        BudgetChangeKind.PersonOverride => "budget.override",
        BudgetChangeKind.Exception => "budget.exception",
        _ => "budget.adjust",
    };

    private async Task<string> DescribeAsync(BudgetChange change)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await DescribeAsync(db, change);
    }

    private static int MonthsTouched(DateTime start, DateTime end) =>
        (end.Year - start.Year) * 12 + end.Month - start.Month + 1;

    private async Task<BudgetProposal> ProposeAsync(AppDbContext db, int actorUserId, BudgetChange change, SpendPreview preview)
    {
        BudgetException? exception = null;
        if (change.Kind == BudgetChangeKind.Exception)
        {
            exception = NewException(actorUserId, change, approved: false);
            db.BudgetExceptions.Add(exception);
        }
        var proposal = new BudgetProposal
        {
            Kind = change.Kind,
            OrganizationId = change.OrganizationId,
            UserId = change.UserId,
            BudgetException = exception,
            Value = change.Value,
            Note = change.Note?.Trim(),
            SpendChange = change.Kind == BudgetChangeKind.Exception ? preview.ExceptionTotal
                : preview.ThisMonthChange != 0 ? preview.ThisMonthChange : preview.MonthlyChange,
            Summary = await DescribeAsync(db, change),
            Period = Period.Current,
            ProposedById = actorUserId,
        };
        db.Proposals.Add(proposal);
        return proposal;
    }

    private static BudgetException NewException(int actorUserId, BudgetChange change, bool approved) => new()
    {
        UserId = change.UserId!.Value,
        Amount = change.Value!.Value,
        StartDate = DateTime.SpecifyKind(change.StartDate!.Value.Date, DateTimeKind.Utc),
        EndDate = DateTime.SpecifyKind(change.EndDate!.Value.Date, DateTimeKind.Utc),
        Reason = change.Note!.Trim(),
        IsApproved = approved,
        CreatedByUserId = actorUserId,
    };

    private async Task<string> DescribeAsync(AppDbContext db, BudgetChange change)
    {
        var who = change.UserId is int id ? (await db.Users.FindAsync(id))?.Login : null;
        var org = change.OrganizationId is int oid ? (await db.Organizations.FindAsync(oid))?.Name : null;
        return change.Kind switch
        {
            BudgetChangeKind.OrganizationBudget => $"Set {org}'s monthly budget to {Format.Amount(change.Value!.Value)} {Unit} per seat",
            BudgetChangeKind.PersonOverride when change.Value is null => $"Put {who} back on their organization's budget",
            BudgetChangeKind.PersonOverride => $"Set {who}'s monthly budget to {Format.Amount(change.Value!.Value)} {Unit}",
            BudgetChangeKind.Exception => $"{Format.Signed(change.Value!.Value)} {Unit} a month for {who}, {change.StartDate:d MMM} to {change.EndDate:d MMM yyyy}",
            _ => $"Adjust {who}'s budget this month by {Format.Signed(change.Value!.Value)} {Unit}",
        };
    }

    /// <summary>Makes the change and rebalances this month's budgets. Caller holds the lock and saves.</summary>
    private async Task<OpResult> ApplyAsync(AppDbContext db, int actorUserId, BudgetChange change, BudgetException? exception)
    {
        var period = Period.Current;
        var summary = await DescribeAsync(db, change);
        switch (change.Kind)
        {
            case BudgetChangeKind.OrganizationBudget:
            {
                var org = (await db.Organizations.FindAsync(change.OrganizationId))!;
                var previous = org.DefaultMonthlyAllocation;
                org.DefaultMonthlyAllocation = change.Value!.Value;
                await db.SaveChangesAsync();
                var seatHolders = await db.Memberships.Where(m => m.OrganizationId == org.Id && m.HasSeat).Select(m => m.UserId).Distinct().ToListAsync();
                var updated = await BudgetService.RebaseAsync(db, seatHolders, period,
                    $"{org.Name} monthly budget changed from {Format.Amount(previous)} to {Format.Amount(org.DefaultMonthlyAllocation)}", actorUserId, org.Id);
                return OpResult.Success(updated switch
                {
                    0 => "Saved.",
                    1 => "Saved. 1 person's budget for this month was updated.",
                    _ => $"Saved. {updated} people's budgets for this month were updated.",
                });
            }
            case BudgetChangeKind.PersonOverride:
            {
                var user = (await db.Users.FindAsync(change.UserId))!;
                user.MonthlyBudgetOverride = change.Value;
                user.OverrideNote = change.Value is null ? null : change.Note?.Trim();
                await db.SaveChangesAsync();
                await BudgetService.RebaseAsync(db, [user.Id], period, summary, actorUserId, null);
                return OpResult.Success(change.Value is null
                    ? $"{user.Login} is back on their organization's monthly budget."
                    : $"{user.Login}'s monthly budget is now {Format.Amount(change.Value.Value)} {Unit}.");
            }
            case BudgetChangeKind.Exception:
            {
                if (exception is null) db.BudgetExceptions.Add(exception = NewException(actorUserId, change, approved: true));
                else exception.IsApproved = true;
                await db.SaveChangesAsync();
                await BudgetService.RebaseAsync(db, [exception.UserId], period, $"Exception: {exception.Reason}", actorUserId, null);
                return OpResult.Success($"Exception added: {summary}.");
            }
            default:
            {
                db.Ledger.Add(new LedgerEntry
                {
                    UserId = change.UserId!.Value,
                    Period = period,
                    Amount = change.Value!.Value,
                    Type = LedgerEntryType.AdminAdjustment,
                    Note = change.Note!.Trim(),
                    CreatedByUserId = actorUserId,
                });
                return OpResult.Success("Budget adjusted.");
            }
        }
    }
}
