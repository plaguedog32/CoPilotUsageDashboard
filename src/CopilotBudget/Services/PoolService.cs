using CopilotBudget.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CopilotBudget.Services;

/// <summary>A credit pool with its current balance and members.</summary>
public record PoolView(CreditPool Pool, decimal Balance, IReadOnlyList<AppUser> Members, decimal UsedThisMonth)
{
    public bool HasMember(int userId) => Members.Any(m => m.Id == userId);
}

/// <summary>
/// Prepaid, non-expiring credits. Credits leave a pool only when a member takes them (or auto-cover moves exactly
/// what's needed to cover overage), so whatever isn't used stays in the pool across months.
/// Draws aren't transfers between people, so they don't need approval.
/// </summary>
public class PoolService(
    IDbContextFactory<AppDbContext> dbFactory,
    BudgetService budgets,
    ChangeNotifier notifier,
    AuditLog audit,
    IOptions<AppOptions> appOptions,
    ILogger<PoolService> logger)
{
    private string Unit => appOptions.Value.UnitLabel;

    public async Task<List<PoolView>> GetPoolsAsync(bool includeArchived = false)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var pools = await db.CreditPools.Include(p => p.Members).ThenInclude(m => m.User).AsNoTracking()
            .Where(p => includeArchived || !p.IsArchived)
            .ToListAsync();
        var entries = await db.PoolEntries.Select(e => new { e.CreditPoolId, e.Amount, e.Type, e.Period }).ToListAsync();
        var period = Period.Current;

        return pools.Select(p =>
        {
            var mine = entries.Where(e => e.CreditPoolId == p.Id).ToList();
            var usedThisMonth = -mine.Where(e => e.Period == period && e.Type is PoolEntryType.Draw or PoolEntryType.AutoCover).Sum(e => e.Amount);
            return new PoolView(p, mine.Sum(e => e.Amount),
                p.Members.Select(m => m.User).OrderBy(u => u.Login, StringComparer.OrdinalIgnoreCase).ToList(), usedThisMonth);
        })
        .OrderBy(v => v.Pool.IsShared).ThenBy(v => v.Pool.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();
    }

    public async Task<List<PoolEntry>> GetEntriesAsync(int poolId, int take = 50)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.PoolEntries.Include(e => e.User).Include(e => e.CreatedBy).AsNoTracking()
            .Where(e => e.CreditPoolId == poolId)
            .OrderByDescending(e => e.CreatedUtc).ThenByDescending(e => e.Id)
            .Take(take).ToListAsync();
    }

    public async Task<OpResult> CreateAsync(int actorUserId, string name, bool shared, IReadOnlyCollection<int> memberIds,
        decimal startingCredits, decimal? monthlyRefill, bool autoCover, string? note)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name)) return OpResult.Fail("Give the pool a name.");
        if (memberIds.Count == 0) return OpResult.Fail(shared ? "Add at least one member." : "Choose who the reserve is for.");
        if (!shared && memberIds.Count != 1) return OpResult.Fail("A personal reserve is for exactly one person.");
        if (startingCredits < 0 || monthlyRefill < 0) return OpResult.Fail("Amounts can't be negative.");
        if (startingCredits != Math.Floor(startingCredits) || monthlyRefill is { } r && r != Math.Floor(r))
            return OpResult.Fail("Use whole numbers of credits.");

        await BudgetService.WriteLock.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            if (!budgets.IsAppAdmin(await db.Users.FindAsync(actorUserId))) return OpResult.Fail("Only app admins can create credit pools.");
            if (await db.Users.CountAsync(u => memberIds.Contains(u.Id)) != memberIds.Count) return OpResult.Fail("Some of those people weren't found.");

            var pool = new CreditPool
            {
                Name = name,
                IsShared = shared,
                AutoCover = autoCover,
                MonthlyRefill = shared && monthlyRefill > 0 ? monthlyRefill : null,
                Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                CreatedByUserId = actorUserId,
                Members = memberIds.Select(id => new CreditPoolMember { UserId = id }).ToList(),
            };
            db.CreditPools.Add(pool);
            var opening = Math.Max(startingCredits, pool.MonthlyRefill ?? 0);
            if (opening > 0)
            {
                db.PoolEntries.Add(new PoolEntry
                {
                    CreditPool = pool, Amount = opening, Period = Period.Current, CreatedByUserId = actorUserId,
                    Type = startingCredits >= opening ? PoolEntryType.Added : PoolEntryType.Refill,
                    Note = startingCredits >= opening ? pool.Note : $"Monthly top-up for {Period.Display(Period.Current)}",
                });
            }
            await db.SaveChangesAsync();
        }
        finally
        {
            BudgetService.WriteLock.Release();
        }

        await audit.RecordAsync(actorUserId, "pool.create", $"Created {(shared ? "shared pool" : "reserve")} {name} with {Format.Amount(startingCredits)} {Unit}" +
            (monthlyRefill > 0 ? $", topped up to {Format.Amount(monthlyRefill.Value)} monthly" : ""), "pool", null, name, new { memberIds, autoCover, note });
        await CoverOveragesAsync();
        await notifier.NotifyAsync();
        return OpResult.Success(shared ? $"Created the {name} pool." : $"Created the {name} reserve.");
    }

    /// <summary>Adds credits (a purchase or top-up) or, with a negative amount, takes unused credits back out.</summary>
    public async Task<OpResult> AddCreditsAsync(int actorUserId, int poolId, decimal amount, string? note)
    {
        if (amount == 0) return OpResult.Fail("Amount can't be zero.");
        if (amount != Math.Floor(amount)) return OpResult.Fail("Use whole numbers of credits.");

        await BudgetService.WriteLock.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            if (!budgets.IsAppAdmin(await db.Users.FindAsync(actorUserId))) return OpResult.Fail("Only app admins can change a pool's credits.");
            var pool = await db.CreditPools.FindAsync(poolId);
            if (pool is null || pool.IsArchived) return OpResult.Fail("Pool not found.");

            var balance = await BalanceAsync(db, poolId);
            if (amount < 0 && -amount > balance) return OpResult.Fail($"The pool only has {Format.Amount(balance)} {Unit} left to remove.");

            db.PoolEntries.Add(new PoolEntry
            {
                CreditPoolId = poolId, Amount = amount, Period = Period.Current, CreatedByUserId = actorUserId,
                Type = amount > 0 ? PoolEntryType.Added : PoolEntryType.Removed,
                Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            });
            await db.SaveChangesAsync();
        }
        finally
        {
            BudgetService.WriteLock.Release();
        }

        await audit.RecordAsync(actorUserId, amount > 0 ? "pool.add" : "pool.remove",
            $"{(amount > 0 ? "Added" : "Removed")} {Format.Amount(Math.Abs(amount))} {Unit} {(amount > 0 ? "to" : "from")} pool #{poolId}" +
            (string.IsNullOrWhiteSpace(note) ? "" : $" ({note.Trim()})"), "pool", poolId);
        if (amount > 0) await CoverOveragesAsync();
        await notifier.NotifyAsync();
        return OpResult.Success(amount > 0 ? $"Added {Format.Amount(amount)} {Unit}." : $"Removed {Format.Amount(-amount)} {Unit}.");
    }

    public async Task<OpResult> UpdateAsync(int actorUserId, int poolId, IReadOnlyCollection<int> memberIds, bool autoCover, decimal? monthlyRefill)
    {
        if (monthlyRefill < 0 || monthlyRefill is { } r && r != Math.Floor(r)) return OpResult.Fail("The monthly top-up must be a whole number.");

        await using var db = await dbFactory.CreateDbContextAsync();
        if (!budgets.IsAppAdmin(await db.Users.FindAsync(actorUserId))) return OpResult.Fail("Only app admins can change credit pools.");
        var pool = await db.CreditPools.Include(p => p.Members).FirstOrDefaultAsync(p => p.Id == poolId);
        if (pool is null || pool.IsArchived) return OpResult.Fail("Pool not found.");
        if (memberIds.Count == 0) return OpResult.Fail("A pool needs at least one member.");
        if (!pool.IsShared && memberIds.Count != 1) return OpResult.Fail("A personal reserve is for exactly one person.");

        db.CreditPoolMembers.RemoveRange(pool.Members.Where(m => !memberIds.Contains(m.UserId)));
        foreach (var id in memberIds.Where(id => pool.Members.All(m => m.UserId != id)))
            pool.Members.Add(new CreditPoolMember { UserId = id });
        pool.AutoCover = autoCover;
        pool.MonthlyRefill = pool.IsShared && monthlyRefill > 0 ? monthlyRefill : null;
        await db.SaveChangesAsync();
        await audit.RecordAsync(actorUserId, "pool.update", $"Changed pool {pool.Name}", "pool", pool.Id, pool.Name,
            new { memberIds, autoCover, monthlyRefill });

        await CoverOveragesAsync();
        await notifier.NotifyAsync();
        return OpResult.Success("Saved.");
    }

    /// <summary>Archives a pool. Its history stays; any remaining credits are no longer usable.</summary>
    public async Task<OpResult> ArchiveAsync(int actorUserId, int poolId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!budgets.IsAppAdmin(await db.Users.FindAsync(actorUserId))) return OpResult.Fail("Only app admins can archive credit pools.");
        var pool = await db.CreditPools.FindAsync(poolId);
        if (pool is null) return OpResult.Fail("Pool not found.");
        pool.IsArchived = true;
        await db.SaveChangesAsync();
        await audit.RecordAsync(actorUserId, "pool.archive", $"Archived pool {pool.Name}", "pool", pool.Id, pool.Name);
        await notifier.NotifyAsync();
        return OpResult.Success($"Archived {pool.Name}.");
    }

    /// <summary>A member moves credits from the pool into their budget for this month.</summary>
    public async Task<OpResult> DrawAsync(int actorUserId, int poolId, decimal amount)
    {
        if (amount <= 0) return OpResult.Fail("Amount must be greater than zero.");
        if (amount != Math.Floor(amount)) return OpResult.Fail("Use whole numbers of credits.");

        string poolName;
        await BudgetService.WriteLock.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var pool = await db.CreditPools.Include(p => p.Members).FirstOrDefaultAsync(p => p.Id == poolId);
            if (pool is null || pool.IsArchived) return OpResult.Fail("Pool not found.");
            if (pool.Members.All(m => m.UserId != actorUserId)) return OpResult.Fail("You're not a member of this pool.");

            var balance = await BalanceAsync(db, poolId);
            if (amount > balance) return OpResult.Fail($"{pool.Name} only has {Format.Amount(balance)} {Unit} left.");

            AddDraw(db, pool, actorUserId, amount, PoolEntryType.Draw, actorUserId, null);
            await db.SaveChangesAsync();
            poolName = pool.Name;
        }
        finally
        {
            BudgetService.WriteLock.Release();
        }

        await notifier.NotifyAsync();
        return OpResult.Success($"Added {Format.Amount(amount)} {Unit} from {poolName} to your budget for this month.");
    }

    /// <summary>
    /// Covers anyone who's over their monthly budget from pools with auto-cover on: personal reserves first,
    /// then shared pools. Moves exactly the shortfall, so unused credits stay in the pool. Safe to call often.
    /// </summary>
    public async Task CoverOveragesAsync()
    {
        var covered = 0;
        await BudgetService.WriteLock.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var pools = await db.CreditPools.Include(p => p.Members)
                .Where(p => p.AutoCover && !p.IsArchived)
                .ToListAsync();
            if (pools.Count == 0) return;

            var balances = (await db.PoolEntries.Select(e => new { e.CreditPoolId, e.Amount }).ToListAsync())
                .GroupBy(e => e.CreditPoolId).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
            var period = Period.Current;

            var departed = (await db.Users.Where(u => u.IsDisabled).Select(u => u.Id).ToListAsync()).ToHashSet();
            foreach (var userId in pools.SelectMany(p => p.Members).Select(m => m.UserId).Distinct().Where(id => !departed.Contains(id)))
            {
                var shortfall = -await BudgetService.GetAvailableAsync(db, userId, period);
                if (shortfall <= 0) continue;

                foreach (var pool in pools.Where(p => p.Members.Any(m => m.UserId == userId)).OrderBy(p => p.IsShared).ThenBy(p => p.Id))
                {
                    var balance = balances.GetValueOrDefault(pool.Id);
                    if (balance <= 0) continue;
                    var take = Math.Min(balance, Math.Ceiling(shortfall));
                    AddDraw(db, pool, userId, take, PoolEntryType.AutoCover, null, "Covered going over the monthly budget");
                    balances[pool.Id] = balance - take;
                    shortfall -= take;
                    covered++;
                    if (shortfall <= 0) break;
                }
            }
            if (covered > 0) await db.SaveChangesAsync();
        }
        finally
        {
            BudgetService.WriteLock.Release();
        }

        if (covered > 0)
        {
            logger.LogInformation("Auto-cover moved credits for {Count} draws", covered);
            await notifier.NotifyAsync();
        }
    }

    /// <summary>Tops pools with a monthly refill back up to their amount, once per month.</summary>
    public async Task EnsureMonthlyRefillsAsync(string period)
    {
        await BudgetService.WriteLock.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var pools = await db.CreditPools.Where(p => p.MonthlyRefill != null && !p.IsArchived).ToListAsync();
            var added = 0;
            foreach (var pool in pools)
            {
                if (await db.PoolEntries.AnyAsync(e => e.CreditPoolId == pool.Id && e.Period == period && e.Type == PoolEntryType.Refill)) continue;
                var topUp = pool.MonthlyRefill!.Value - await BalanceAsync(db, pool.Id);
                db.PoolEntries.Add(new PoolEntry
                {
                    CreditPoolId = pool.Id, Amount = Math.Max(0, topUp), Period = period, Type = PoolEntryType.Refill,
                    Note = $"Monthly top-up for {Period.Display(period)}",
                });
                added++;
            }
            if (added > 0) await db.SaveChangesAsync();
        }
        finally
        {
            BudgetService.WriteLock.Release();
        }
    }

    private static async Task<decimal> BalanceAsync(AppDbContext db, int poolId) =>
        (await db.PoolEntries.Where(e => e.CreditPoolId == poolId).Select(e => e.Amount).ToListAsync()).Sum();

    /// <summary>Takes credits out of the pool and adds them to the person's budget for this month.</summary>
    private static void AddDraw(AppDbContext db, CreditPool pool, int userId, decimal amount, PoolEntryType type, int? actorUserId, string? note)
    {
        var period = Period.Current;
        db.PoolEntries.Add(new PoolEntry
        {
            CreditPoolId = pool.Id, Amount = -amount, Period = period, Type = type,
            UserId = userId, CreatedByUserId = actorUserId, Note = note,
        });
        db.Ledger.Add(new LedgerEntry
        {
            UserId = userId, Period = period, Amount = amount, Type = LedgerEntryType.PoolDraw,
            CreditPoolId = pool.Id, CreatedByUserId = actorUserId,
            Note = type == PoolEntryType.AutoCover ? $"{pool.Name}: covered going over budget" : $"Taken from {pool.Name}",
        });
    }
}
