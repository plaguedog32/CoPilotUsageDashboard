using CopilotBudget.Data;
using Microsoft.EntityFrameworkCore;

namespace CopilotBudget.Services;

/// <summary>
/// Connects Microsoft Entra sign-ins to GitHub users, by work email.
/// <list type="number">
/// <item>An Entra account already linked (by object ID) is used as-is.</item>
/// <item>Otherwise a GitHub user with the same email (from GitHub SAML/SCIM data, or set by an admin) is linked.</item>
/// <item>Otherwise the person gets a placeholder so they can still sign in and view; it's merged into their GitHub
/// user as soon as a matching email turns up, or an admin links them by hand.</item>
/// </list>
/// </summary>
public class IdentityLinkService(IDbContextFactory<AppDbContext> dbFactory, ChangeNotifier notifier, AuditLog audit,
    BudgetService budgets, ILogger<IdentityLinkService> logger)
{
    private static readonly SemaphoreSlim Lock = new(1, 1);

    public static string? NormalizeEmail(string? email) =>
        string.IsNullOrWhiteSpace(email) || !email.Contains('@') ? null : email.Trim().ToLowerInvariant();

    /// <summary>Called on every Microsoft sign-in. Returns the person to sign in as.</summary>
    /// <param name="directoryClaims">The app role values and group IDs in the sign-in token.</param>
    public async Task<AppUser> SignInAsync(string objectId, string? email, string? displayName, IReadOnlyCollection<string>? appRoles = null,
        IReadOnlyCollection<string>? groups = null)
    {
        email = NormalizeEmail(email);
        await Lock.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var user = await db.Users.FirstOrDefaultAsync(u => u.EntraObjectId == objectId);

            // A placeholder whose GitHub user has since been found (by sync or an admin) gets merged now.
            if (user is { HasGitHubAccount: false } && email is not null
                && await FindGitHubUserByEmailAsync(db, email) is { } match)
            {
                user = await MergeAsync(db, user, match);
            }

            if (user is null && email is not null && await FindGitHubUserByEmailAsync(db, email) is { } byEmail)
            {
                user = byEmail;
                user.EntraObjectId = objectId;
                logger.LogInformation("Linked Entra account {Email} to GitHub user {Login}", email, user.Login);
            }

            if (user is null)
            {
                user = new AppUser
                {
                    Login = email ?? objectId,
                    HasGitHubAccount = false,
                    Email = email,
                    EmailSource = "entra",
                    EntraObjectId = objectId,
                };
                db.Users.Add(user);
                logger.LogInformation("Entra account {Email} has no matching GitHub user yet", email ?? objectId);
            }

            if (email is not null && user.Email is null) { user.Email = email; user.EmailSource = "entra"; }
            if (!string.IsNullOrWhiteSpace(displayName) && (string.IsNullOrWhiteSpace(user.Name) || !user.HasGitHubAccount)) user.Name = displayName;
            user.LastLoginUtc = DateTime.UtcNow;

            // Roles from Entra replace the previous ones at every sign-in, so removing someone from a group takes effect.
            var before = user.DirectoryRoles;
            user.DirectoryRoles = await RolesFromDirectoryAsync(db, appRoles ?? [], groups ?? []);
            var claims = (appRoles ?? []).Select(r => "role:" + r).Concat((groups ?? []).Select(g => "group:" + g)).ToList();
            var joined = string.Join(",", claims);
            user.DirectoryClaims = claims.Count == 0 ? null : joined.Length > 4000 ? joined[..4000] : joined;
            await db.SaveChangesAsync();
            if (before != user.DirectoryRoles)
                await audit.RecordAsync(null, "roles.directory", $"{user.Login}: roles from Entra changed from " +
                    $"{Describe(before)} to {Describe(user.DirectoryRoles)} at sign-in", "user", user.Id, user.Login);
            return user;
        }
        finally
        {
            Lock.Release();
        }
    }

    /// <summary>Roles granted by the admin's mappings for these Entra app roles and groups.</summary>
    public static async Task<AppRole> RolesFromDirectoryAsync(AppDbContext db, IReadOnlyCollection<string> appRoles, IReadOnlyCollection<string> groups)
    {
        if (appRoles.Count == 0 && groups.Count == 0) return AppRole.None;
        var mappings = await db.RoleMappings.AsNoTracking().ToListAsync();
        var roles = AppRole.None;
        foreach (var m in mappings)
        {
            var values = m.Source == RoleSource.AppRole ? appRoles : groups;
            if (values.Any(v => string.Equals(v, m.Value, StringComparison.OrdinalIgnoreCase))) roles |= m.Role;
        }
        return roles;
    }

    private static string Describe(AppRole roles) => roles == AppRole.None ? "none" : BudgetService.RoleNames(roles);

    /// <summary>
    /// Records work emails found for GitHub users (e.g. from SAML identities during sync) and merges any
    /// placeholders that now match. Never overwrites an email an admin set by hand.
    /// </summary>
    public async Task<int> ApplyGitHubEmailsAsync(IReadOnlyDictionary<string, string> emailByLogin)
    {
        if (emailByLogin.Count == 0) return 0;
        var merged = 0;
        await Lock.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var logins = emailByLogin.Keys.ToList();
            var users = await db.Users.Where(u => u.HasGitHubAccount && logins.Contains(u.Login)).ToListAsync();
            foreach (var user in users)
            {
                if (user.EmailSource == "admin") continue;
                var email = NormalizeEmail(emailByLogin.FirstOrDefault(kv => string.Equals(kv.Key, user.Login, StringComparison.OrdinalIgnoreCase)).Value);
                if (email is null) continue;
                user.Email = email;
                user.EmailSource = "github-saml";
            }
            await db.SaveChangesAsync();
            merged = await MergeMatchingPlaceholdersAsync(db);
        }
        finally
        {
            Lock.Release();
        }
        if (merged > 0) await notifier.NotifyAsync();
        return merged;
    }

    /// <summary>An admin sets (or clears) a GitHub user's work email. Merges a waiting placeholder if it matches.</summary>
    public async Task<OpResult> SetEmailAsync(int actorUserId, int userId, string? email)
    {
        var normalized = NormalizeEmail(email);
        if (!string.IsNullOrWhiteSpace(email) && normalized is null) return OpResult.Fail("That doesn't look like an email address.");

        await Lock.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            if (!budgets.CanManageIntegration(await db.Users.FindAsync(actorUserId)))
                return OpResult.Fail("Only admins and integration admins can change account links.");
            var user = await db.Users.FindAsync(userId);
            if (user is null || !user.HasGitHubAccount) return OpResult.Fail("Person not found.");
            if (normalized is not null && await db.Users.AnyAsync(u => u.Id != userId && u.HasGitHubAccount && u.Email == normalized))
                return OpResult.Fail($"{normalized} is already used by another GitHub user.");

            var previous = user.Email;
            user.Email = normalized;
            user.EmailSource = normalized is null ? null : "admin";
            await db.SaveChangesAsync();
            await audit.RecordAsync(actorUserId, "mapping.email", $"{user.Login}: work email {previous ?? "none"} → {normalized ?? "none"}", "user", user.Id, user.Login);
            var merged = await MergeMatchingPlaceholdersAsync(db);
            await notifier.NotifyAsync();
            return OpResult.Success(merged > 0 ? $"Saved and linked {user.Login} to their Microsoft account." : "Saved.");
        }
        finally
        {
            Lock.Release();
        }
    }

    /// <summary>An admin links a Microsoft sign-in (placeholder) to a GitHub user by hand.</summary>
    public async Task<OpResult> LinkAsync(int actorUserId, int placeholderId, int gitHubUserId)
    {
        await Lock.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            if (!budgets.CanManageIntegration(await db.Users.FindAsync(actorUserId)))
                return OpResult.Fail("Only admins and integration admins can link accounts.");
            var placeholder = await db.Users.FindAsync(placeholderId);
            var target = await db.Users.FindAsync(gitHubUserId);
            if (placeholder is null || placeholder.HasGitHubAccount) return OpResult.Fail("That sign-in isn't waiting to be linked.");
            if (target is null || !target.HasGitHubAccount) return OpResult.Fail("GitHub user not found.");
            if (target.EntraObjectId is not null) return OpResult.Fail($"{target.Login} is already linked to another Microsoft account.");

            var email = placeholder.Email;
            await MergeAsync(db, placeholder, target);
            if (email is not null && target.EmailSource != "admin") { target.Email = email; target.EmailSource = "admin"; }
            await db.SaveChangesAsync();
            await audit.RecordAsync(actorUserId, "mapping.link", $"Linked Microsoft sign-in {email ?? "(no email)"} to GitHub user {target.Login}", "user", target.Id, target.Login);
            await notifier.NotifyAsync();
            return OpResult.Success($"Linked {email ?? "the sign-in"} to {target.Login}.");
        }
        finally
        {
            Lock.Release();
        }
    }

    private static Task<AppUser?> FindGitHubUserByEmailAsync(AppDbContext db, string email) =>
        db.Users.FirstOrDefaultAsync(u => u.HasGitHubAccount && u.EntraObjectId == null && u.Email == email);

    private async Task<int> MergeMatchingPlaceholdersAsync(AppDbContext db)
    {
        var placeholders = await db.Users.Where(u => !u.HasGitHubAccount && u.Email != null && u.EntraObjectId != null).ToListAsync();
        var merged = 0;
        foreach (var p in placeholders)
        {
            if (await FindGitHubUserByEmailAsync(db, p.Email!) is not { } match) continue;
            await MergeAsync(db, p, match);
            merged++;
        }
        if (merged > 0) await db.SaveChangesAsync();
        return merged;
    }

    /// <summary>
    /// Moves a placeholder's sign-in, roles and anything it did (approvals, pool changes...) onto the GitHub user,
    /// then removes the placeholder.
    /// </summary>
    private async Task<AppUser> MergeAsync(AppDbContext db, AppUser placeholder, AppUser target)
    {
        var from = placeholder.Id;
        var to = target.Id;

        await db.Ledger.Where(x => x.UserId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.UserId, to));
        await db.Ledger.Where(x => x.CounterpartyUserId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.CounterpartyUserId, to));
        await db.Ledger.Where(x => x.CreatedByUserId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedByUserId, to));
        await db.Requests.Where(x => x.RequesterId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.RequesterId, to));
        await db.Requests.Where(x => x.TargetId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.TargetId, to));
        await db.Requests.Where(x => x.ResolvedById == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResolvedById, to));
        await db.PendingTransfers.Where(x => x.FromUserId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.FromUserId, to));
        await db.PendingTransfers.Where(x => x.ToUserId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.ToUserId, to));
        await db.PendingTransfers.Where(x => x.CreatedByUserId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedByUserId, to));
        await db.PendingTransfers.Where(x => x.ResolvedById == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResolvedById, to));
        await db.PoolEntries.Where(x => x.UserId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.UserId, to));
        await db.PoolEntries.Where(x => x.CreatedByUserId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedByUserId, to));
        await db.CreditPools.Where(x => x.CreatedByUserId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedByUserId, to));
        await db.Settings.Where(x => x.UpdatedByUserId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdatedByUserId, to));
        await db.BudgetExceptions.Where(x => x.UserId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.UserId, to));
        await db.BudgetExceptions.Where(x => x.CreatedByUserId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedByUserId, to));
        await db.Proposals.Where(x => x.UserId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.UserId, to));
        await db.Proposals.Where(x => x.ProposedById == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.ProposedById, to));
        await db.Proposals.Where(x => x.DecidedById == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.DecidedById, to));
        await db.GitHubOperations.Where(x => x.ActorUserId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.ActorUserId, to));
        await db.AuditLog.Where(x => x.ActorUserId == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.ActorUserId, to));
        await db.Alerts.Where(x => x.AcknowledgedById == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.AcknowledgedById, to));
        await db.GitHubBudgets.Where(x => x.UserId == from).ExecuteDeleteAsync(); // Placeholders have no GitHub budget.

        // Pool memberships: keep one per pool.
        var targetPools = await db.CreditPoolMembers.Where(m => m.UserId == to).Select(m => m.CreditPoolId).ToListAsync();
        await db.CreditPoolMembers.Where(m => m.UserId == from && targetPools.Contains(m.CreditPoolId)).ExecuteDeleteAsync();
        await db.CreditPoolMembers.Where(m => m.UserId == from).ExecuteUpdateAsync(s => s.SetProperty(m => m.UserId, to));
        await db.Memberships.Where(m => m.UserId == from).ExecuteDeleteAsync(); // Placeholders have no GitHub memberships.

        var objectId = placeholder.EntraObjectId;
        target.AssignedRoles |= placeholder.AssignedRoles;
        target.DirectoryRoles |= placeholder.DirectoryRoles;
        target.DirectoryClaims ??= placeholder.DirectoryClaims;
        target.LastLoginUtc = placeholder.LastLoginUtc ?? target.LastLoginUtc;
        db.Users.Remove(placeholder);
        await db.SaveChangesAsync(); // Free the object ID before giving it to the GitHub user.
        target.EntraObjectId = objectId;
        await db.SaveChangesAsync();

        logger.LogInformation("Merged Microsoft sign-in {Email} into GitHub user {Login}", placeholder.Email, target.Login);
        return target;
    }
}
