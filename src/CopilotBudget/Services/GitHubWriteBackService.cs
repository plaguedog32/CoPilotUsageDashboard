using System.Net;
using CopilotBudget.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CopilotBudget.Services;

/// <summary>Outcome of one pass over everyone's GitHub budgets.</summary>
public record WriteBackRun(int Checked, int InSync, int Written, int Paused, int NeedReconciling, int Errors, string? Problem)
{
    public string Describe() => Problem
        ?? $"Checked {Checked}: {InSync} in sync, {Written} written, {Paused} waiting (writing is off), " +
           $"{NeedReconciling} need reconciling, {Errors} failed.";
}

/// <summary>
/// Keeps each person's user-level budget in GitHub (their hard stop) equal to their budget in this app.
/// <list type="number">
/// <item><b>Read before write.</b> Every pass reads GitHub first, and the budget is read again right before it's written.</item>
/// <item><b>Only overwrite our own value.</b> If GitHub holds anything other than the value the app last wrote (someone
/// changed it in GitHub, or a budget existed before the app), the person goes to <i>reconciliation required</i> and
/// nothing is written until an admin decides.</item>
/// <item><b>Check afterwards.</b> After a write the budget is read back; if it doesn't match, it's flagged for reconciliation.</item>
/// <item><b>Off switch.</b> With write-back off (the default) the app still reads and compares, but never writes.</item>
/// </list>
/// Every read, write and check is logged in <see cref="GitHubOperation"/>.
/// </summary>
public class GitHubWriteBackService(
    IDbContextFactory<AppDbContext> dbFactory,
    IGitHubBudgetStore store,
    BudgetService budgets,
    ChangeNotifier notifier,
    Notifications notifications,
    LiveStatus live,
    AlertService alerts,
    AuditLog audit,
    IOptions<AppOptions> appOptions,
    ILogger<GitHubWriteBackService> logger)
{
    private static readonly SemaphoreSlim RunLock = new(1, 1);

    public IGitHubBudgetStore Store => store;

    /// <summary>GitHub budgets are whole dollars, so round down: nobody is allowed more than they were given.</summary>
    public int ToDollars(decimal credits) => (int)Math.Floor(Math.Max(0, credits) * appOptions.Value.DollarsPerCredit);

    public async Task<List<GitHubBudgetLink>> GetLinksAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.GitHubBudgets.Include(l => l.User).AsNoTracking().ToListAsync();
    }

    public async Task<List<GitHubOperation>> GetOperationsAsync(int take = 100)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.GitHubOperations.Include(o => o.User).AsNoTracking()
            .OrderByDescending(o => o.CreatedUtc).ThenByDescending(o => o.Id).Take(take).ToListAsync();
    }

    /// <summary>Compares everyone's budget with GitHub, and writes the differences if write-back is on.</summary>
    public async Task<WriteBackRun> RunAsync(int? actorUserId = null, CancellationToken ct = default)
    {
        if (!store.IsAvailable) return new WriteBackRun(0, 0, 0, 0, 0, 0, store.Description);
        if (!await RunLock.WaitAsync(0, ct)) return new WriteBackRun(0, 0, 0, 0, 0, 0, "A check is already running.");
        try
        {
            return await RunCoreAsync(actorUserId, ct);
        }
        finally
        {
            RunLock.Release();
        }
    }

    private async Task<WriteBackRun> RunCoreAsync(int? actorUserId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var settings = await BudgetService.GetSettingsAsync(db);
        var people = (await budgets.GetBudgetsAsync(Period.Current)).Where(b => b.HasSeat && b.User.HasGitHubAccount).ToList();
        var links = await db.GitHubBudgets.ToDictionaryAsync(l => l.UserId, ct);
        var runId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        List<GitHubUserBudget> remote;
        try
        {
            remote = await store.ListAsync(ct);
        }
        catch (Exception ex) when (ex is GitHubApiException or HttpRequestException or TaskCanceledException)
        {
            Log(db, runId, null, "read", false, null, null, "Couldn't list budgets: " + ex.Message, actorUserId);
            await db.SaveChangesAsync(CancellationToken.None);
            live.WriteBackFinished(now, ex.Message);
            await alerts.RaiseAsync("writeback-read", "write-back", AlertSeverity.Critical, "Couldn't read user budgets from GitHub", ex.Message);
            return new WriteBackRun(0, 0, 0, 0, 0, 1, "Couldn't read budgets from GitHub: " + ex.Message);
        }
        await alerts.ResolveAsync("writeback-read");
        var access = await store.GetWriteAccessAsync(ct);
        var byLogin = remote.GroupBy(b => b.Login, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var byId = remote.GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First());

        int inSync = 0, written = 0, paused = 0, reconcile = 0, errors = 0, changed = 0;
        var newlyFlagged = new List<string>();
        foreach (var person in people)
        {
            if (!links.TryGetValue(person.User.Id, out var link))
            {
                link = new GitHubBudgetLink { UserId = person.User.Id };
                db.GitHubBudgets.Add(link);
                links[person.User.Id] = link;
            }
            var before = (link.State, link.GitHubDollars, link.DesiredDollars, link.Message);
            var desired = ToDollars(person.Allocated);
            // By budget ID first, so a renamed GitHub account still finds its budget.
            var current = (link.BudgetId is { } knownId ? byId.GetValueOrDefault(knownId) : null) ?? byLogin.GetValueOrDefault(person.User.Login);
            link.DesiredDollars = desired;
            link.GitHubDollars = current?.Dollars;
            link.BudgetId = current?.Id ?? link.BudgetId;
            link.LastCheckedUtc = now;

            var wasFlagged = link.State == GitHubBudgetState.ReconciliationRequired;
            if (current?.Dollars == desired)
            {
                // Matches already (perhaps someone fixed it by hand); treat it as ours from now on.
                link.State = GitHubBudgetState.InSync;
                link.LastWrittenDollars = desired;
                link.Message = null;
                inSync++;
            }
            else if (wasFlagged)
            {
                reconcile++; // Waits for an admin, whatever has changed since.
            }
            else if (OutsideChange(link, current) is { } reason)
            {
                link.State = GitHubBudgetState.ReconciliationRequired;
                link.Message = reason;
                Log(db, runId, person.User.Id, "read", true, link.LastWrittenDollars, current?.Dollars, reason, actorUserId);
                newlyFlagged.Add(person.User.Login);
                reconcile++;
            }
            else if (!settings.GitHubWriteBackEnabled || !access.CanWrite)
            {
                link.State = GitHubBudgetState.WritePaused;
                link.Message = $"GitHub has {Dollars(current?.Dollars)}; the app wants ${desired}. {(access.CanWrite ? "Writing is off." : "The GitHub connection is read-only.")}";
                paused++;
            }
            else
            {
                var outcome = await WriteAsync(db, runId, person.User.Login, person.User.Id, link, current, desired, actorUserId, ct);
                switch (outcome)
                {
                    case GitHubBudgetState.InSync: written++; break;
                    case GitHubBudgetState.ReconciliationRequired: reconcile++; newlyFlagged.Add(person.User.Login); break;
                    default: errors++; break;
                }
            }
            if (before != (link.State, link.GitHubDollars, link.DesiredDollars, link.Message)) changed++;
        }

        await db.SaveChangesAsync(CancellationToken.None);
        var run = new WriteBackRun(people.Count, inSync, written, paused, reconcile, errors, null);

        // One alert per person whose write failed or needs reconciling; it closes once they're back in sync.
        var problems = new List<string>();
        foreach (var link in links.Values.Where(l => l.State is GitHubBudgetState.Error or GitHubBudgetState.ReconciliationRequired))
        {
            var login = people.FirstOrDefault(p => p.User.Id == link.UserId)?.User.Login ?? $"user #{link.UserId}";
            var key = $"writeback:{link.UserId}";
            problems.Add(key);
            var failed = link.State == GitHubBudgetState.Error;
            await alerts.RaiseAsync(key, "write-back", failed ? AlertSeverity.Critical : AlertSeverity.Warning,
                failed ? $"GitHub budget write failed for {login}" : $"GitHub budget for {login} needs reconciling", link.Message);
        }
        await alerts.ResolveAllExceptAsync("writeback:", problems);
        if (errors > 0 || newlyFlagged.Count > 0)
            Telemetry.BudgetWriteFailures.Add(errors + newlyFlagged.Count);
        live.WriteBackFinished(now, errors > 0 ? $"{errors} budget writes failed" : null);
        if (written > 0 || newlyFlagged.Count > 0) logger.LogInformation("GitHub budgets: {Summary}", run.Describe());
        if (changed > 0) await notifier.NotifyAsync();
        await notifications.ReconciliationRequiredAsync(newlyFlagged);
        return run;
    }

    /// <summary>Why GitHub's value isn't one the app can safely overwrite, or null if it is.</summary>
    private static string? OutsideChange(GitHubBudgetLink link, GitHubUserBudget? current)
    {
        if (link.LastWrittenDollars is int ours)
        {
            if (current is null) return $"The ${ours} budget the app set was deleted in GitHub.";
            if (current.Dollars != ours) return $"Changed in GitHub from ${ours} to ${current.Dollars}, outside the app.";
            return null;
        }
        return current is null ? null : $"GitHub already has a ${current.Dollars} budget the app didn't set.";
    }

    /// <summary>Read, write, then read back. Returns the resulting state.</summary>
    private async Task<GitHubBudgetState> WriteAsync(AppDbContext db, Guid runId, string login, int userId, GitHubBudgetLink link,
        GitHubUserBudget? listed, int desired, int? actorUserId, CancellationToken ct)
    {
        try
        {
            // Read again right before writing, in case it changed since the list was fetched.
            GitHubUserBudget? fresh = null;
            if (listed is not null)
            {
                fresh = await store.GetAsync(listed.Id, ct);
                Log(db, runId, userId, "read", true, null, fresh?.Dollars, null, actorUserId);
                if (fresh?.Dollars != listed.Dollars)
                {
                    link.State = GitHubBudgetState.ReconciliationRequired;
                    link.Message = $"Changed in GitHub while the app was updating it (now {Dollars(fresh?.Dollars)}).";
                    link.GitHubDollars = fresh?.Dollars;
                    return link.State;
                }
            }

            var result = fresh is null
                ? await store.CreateAsync(login, desired, ct)
                : await store.UpdateAsync(fresh.Id, desired, ct);
            Log(db, runId, userId, fresh is null ? "create" : "update", true, fresh?.Dollars, desired, null, actorUserId);

            var id = result?.Id ?? fresh?.Id;
            var check = id is null ? null : await store.GetAsync(id, ct);
            link.BudgetId = id ?? link.BudgetId;
            link.GitHubDollars = check?.Dollars;
            if (check?.Dollars == desired)
            {
                Log(db, runId, userId, "verify", true, null, check.Dollars, null, actorUserId);
                link.State = GitHubBudgetState.InSync;
                link.LastWrittenDollars = desired;
                link.LastWrittenUtc = DateTime.UtcNow;
                link.Message = null;
            }
            else
            {
                var message = $"Wrote ${desired}, but GitHub shows {Dollars(check?.Dollars)} afterwards.";
                Log(db, runId, userId, "verify", false, desired, check?.Dollars, message, actorUserId);
                link.State = GitHubBudgetState.ReconciliationRequired;
                link.Message = message;
            }
            return link.State;
        }
        catch (Exception ex) when (ex is GitHubApiException or HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            var status = ex is GitHubApiException g ? $" ({(int)g.Status})" : "";
            Log(db, runId, userId, "update", false, listed?.Dollars, desired, ex.Message, actorUserId);
            link.State = GitHubBudgetState.Error;
            link.Message = $"Write failed{status}: {ex.Message}";
            if (ex is GitHubApiException { Status: HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized })
                logger.LogWarning("GitHub refused a budget write: {Message}", ex.Message);
            return link.State;
        }
    }

    /// <summary>
    /// Resolves reconciliation by keeping the app's value: GitHub's current value becomes the baseline, so the next pass
    /// overwrites it (if write-back is on).
    /// </summary>
    public async Task<OpResult> UseAppValueAsync(int actorUserId, int userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!budgets.CanManageIntegration(await db.Users.FindAsync(actorUserId)))
            return OpResult.Fail("Only admins and integration admins can resolve GitHub budgets.");
        var link = await db.GitHubBudgets.Include(l => l.User).FirstOrDefaultAsync(l => l.UserId == userId);
        if (link is null) return OpResult.Fail("Nothing to resolve.");

        Log(db, Guid.NewGuid(), userId, "resolve", true, link.GitHubDollars, link.DesiredDollars,
            "Admin chose the app's value; GitHub will be overwritten.", actorUserId);
        link.LastWrittenDollars = link.GitHubDollars; // Baseline: what's there now is what we expect to replace.
        link.State = GitHubBudgetState.Unknown;
        link.Message = null;
        await db.SaveChangesAsync();
        await audit.RecordAsync(actorUserId, "writeback.use-app-value",
            $"{link.User.Login}: keep the app's ${link.DesiredDollars} over GitHub's {Dollars(link.GitHubDollars)}", "user", userId, link.User.Login);
        await notifier.NotifyAsync();
        var settings = await BudgetService.GetSettingsAsync(db);
        var run = await RunAsync(actorUserId);
        return OpResult.Success(settings.GitHubWriteBackEnabled
            ? $"Using the app's value for {link.User.Login}. {run.Describe()}"
            : $"Using the app's value for {link.User.Login}. Writing is off, so GitHub changes once it's switched on.");
    }

    /// <summary>
    /// The adjustment (in credits) that would make the app match GitHub's value for this person, for an admin who
    /// decides GitHub is right. Applied through the normal budget change flow, so increases still need approval.
    /// </summary>
    public async Task<(decimal Credits, string Login)?> AdjustmentToMatchGitHubAsync(int userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var link = await db.GitHubBudgets.Include(l => l.User).AsNoTracking().FirstOrDefaultAsync(l => l.UserId == userId);
        if (link?.GitHubDollars is not int dollars) return null;
        var mine = (await budgets.GetBudgetsAsync(Period.Current)).FirstOrDefault(b => b.User.Id == userId);
        if (mine is null) return null;
        var target = dollars / appOptions.Value.DollarsPerCredit;
        return (target - mine.Allocated, link.User.Login);
    }

    /// <summary>Demo only: pretend someone edited a budget directly in GitHub.</summary>
    public async Task<OpResult> SimulateOutsideChangeAsync(int actorUserId, int userId, int dollars)
    {
        if (store is not SimulatedBudgetStore simulated) return OpResult.Fail("Only the simulated GitHub can be edited like this.");
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!budgets.CanManageIntegration(await db.Users.FindAsync(actorUserId))) return OpResult.Fail("Only admins and integration admins can do this.");
        var user = await db.Users.FindAsync(userId);
        if (user is null) return OpResult.Fail("Person not found.");
        simulated.SimulateOutsideChange(user.Login, dollars);
        await audit.RecordAsync(actorUserId, "writeback.simulate", $"Demo: simulated a GitHub edit of {user.Login}'s budget to ${dollars}", "user", userId, user.Login);
        var run = await RunAsync(actorUserId);
        return OpResult.Success($"Simulated someone setting {user.Login}'s budget to ${dollars} in GitHub. {run.Describe()}");
    }

    private static string Dollars(int? dollars) => dollars is int d ? $"${d}" : "no budget";

    private static void Log(AppDbContext db, Guid runId, int? userId, string action, bool ok, int? before, int? after, string? message, int? actor) =>
        db.GitHubOperations.Add(new GitHubOperation
        {
            RunId = runId, UserId = userId, Action = action, Succeeded = ok, Before = before, After = after,
            Message = message is { Length: > 500 } ? message[..500] : message, ActorUserId = actor,
        });
}

/// <summary>
/// Checks GitHub budgets on a timer, and soon after anything changes in the app (a transfer, a budget change),
/// so GitHub's hard stops follow the app within seconds.
/// </summary>
public class GitHubWriteBackBackgroundService(
    IServiceProvider services,
    ChangeNotifier notifier,
    IGitHubBudgetStore store,
    IOptions<GitHubOptions> options,
    ILogger<GitHubWriteBackBackgroundService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _wake = new(0, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!store.IsAvailable)
            logger.LogInformation("No GitHub enterprise configured yet; user budgets are checked once one is.");

        notifier.Changed += OnChanged;
        try
        {
            var interval = TimeSpan.FromSeconds(Math.Max(15, options.Value.WriteBackIntervalSeconds));
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); // Let startup (allocations, first sync) settle.
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (store.IsAvailable)
                    {
                        using var scope = services.CreateScope();
                        await scope.ServiceProvider.GetRequiredService<GitHubWriteBackService>().RunAsync(ct: stoppingToken);
                    }
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "GitHub budget check failed");
                }

                // Sleep until the next tick, or until something changes (then wait a moment to batch bursts).
                if (await _wake.WaitAsync(interval, stoppingToken))
                    await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            notifier.Changed -= OnChanged;
        }
    }

    private Task OnChanged()
    {
        if (_wake.CurrentCount == 0)
        {
            try { _wake.Release(); }
            catch (SemaphoreFullException) { }
        }
        return Task.CompletedTask;
    }
}
