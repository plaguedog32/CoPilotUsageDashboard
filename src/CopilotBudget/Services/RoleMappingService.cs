using CopilotBudget.Data;
using Microsoft.EntityFrameworkCore;

namespace CopilotBudget.Services;

/// <summary>
/// Which Entra app roles and groups grant which roles here. Only admins manage these, because a mapping can hand out
/// any role (including Admin). People pick up changes the next time they sign in.
/// </summary>
public class RoleMappingService(IDbContextFactory<AppDbContext> dbFactory, BudgetService budgets, AuditLog audit, ChangeNotifier notifier)
{
    /// <summary>App role values suggested for the Entra app registration's manifest, one per role.</summary>
    public static readonly IReadOnlyDictionary<AppRole, string> SuggestedAppRoleValues = new Dictionary<AppRole, string>
    {
        [AppRole.Admin] = "CopilotBudget.Admin",
        [AppRole.Approver] = "CopilotBudget.Approver",
        [AppRole.BudgetAuthority] = "CopilotBudget.BudgetAuthority",
        [AppRole.BudgetVisibility] = "CopilotBudget.BudgetVisibility",
        [AppRole.IntegrationAdmin] = "CopilotBudget.IntegrationAdmin",
        [AppRole.Auditor] = "CopilotBudget.Auditor",
    };

    public async Task<List<RoleMapping>> GetAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.RoleMappings.AsNoTracking().OrderBy(m => m.Role).ThenBy(m => m.Source).ThenBy(m => m.Value).ToListAsync();
    }

    public async Task<OpResult> AddAsync(int actorUserId, RoleSource source, string value, string? label, AppRole role)
    {
        value = value.Trim();
        if (string.IsNullOrWhiteSpace(value)) return OpResult.Fail(source == RoleSource.Group ? "Enter the group's object ID." : "Enter the app role value.");
        if (source == RoleSource.Group && !Guid.TryParse(value, out _)) return OpResult.Fail("A group is identified by its object ID (a GUID), as it appears in the token.");
        if (!BudgetService.AllRoles.Contains(role)) return OpResult.Fail("Pick one role.");

        await using var db = await dbFactory.CreateDbContextAsync();
        if (!budgets.IsAppAdmin(await db.Users.FindAsync(actorUserId))) return OpResult.Fail("Only app admins can change role mappings.");
        if (await db.RoleMappings.AnyAsync(m => m.Source == source && m.Value == value && m.Role == role)) return OpResult.Fail("That mapping already exists.");

        db.RoleMappings.Add(new RoleMapping { Source = source, Value = value, Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(), Role = role });
        await db.SaveChangesAsync();
        var what = $"{(source == RoleSource.Group ? "Entra group" : "Entra app role")} {label ?? value} → {BudgetService.RoleName(role)}";
        await audit.RecordAsync(actorUserId, "mapping.role-add", $"Added role mapping: {what}", "role-mapping", null, value);
        await notifier.NotifyAsync();
        return OpResult.Success($"Added: {what}. It applies to people the next time they sign in.");
    }

    public async Task<OpResult> RemoveAsync(int actorUserId, int mappingId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!budgets.IsAppAdmin(await db.Users.FindAsync(actorUserId))) return OpResult.Fail("Only app admins can change role mappings.");
        var m = await db.RoleMappings.FindAsync(mappingId);
        if (m is null) return OpResult.Fail("Mapping not found.");
        db.RoleMappings.Remove(m);
        await db.SaveChangesAsync();
        await audit.RecordAsync(actorUserId, "mapping.role-remove",
            $"Removed role mapping: {m.Label ?? m.Value} → {BudgetService.RoleName(m.Role)}", "role-mapping", m.Id, m.Value);
        await notifier.NotifyAsync();
        return OpResult.Success("Removed. People keep the role until they next sign in.");
    }

    /// <summary>On a fresh database, map the suggested app role values so assigning them in Entra just works.</summary>
    public static async Task SeedDefaultsAsync(AppDbContext db)
    {
        if (await db.RoleMappings.AnyAsync()) return;
        foreach (var (role, value) in SuggestedAppRoleValues)
            db.RoleMappings.Add(new RoleMapping { Source = RoleSource.AppRole, Value = value, Role = role, Label = $"App role {value}" });
        await db.SaveChangesAsync();
    }
}
