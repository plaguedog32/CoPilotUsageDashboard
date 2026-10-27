using System.Security.Claims;
using CopilotBudget.Data;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CopilotBudget.Services;

/// <summary>
/// Per-circuit state: who is signed in, which organizations they belong to, and what they can manage.
/// Everyone across all tracked organizations shares one pool of people, so there's no "current org".
/// </summary>
public class UserSession(
    AuthenticationStateProvider authState,
    IDbContextFactory<AppDbContext> dbFactory,
    BudgetService budgets,
    PoolService pools,
    SetupService setup,
    IOptions<AppOptions> appOptions)
{
    /// <summary>Cookie claim holding the AppUser ID (set at Microsoft or demo sign-in).</summary>
    public const string UserIdClaim = "cb_user_id";

    private Task? _loading;

    public AppUser? User { get; private set; }

    /// <summary>True when this is the signed-in person.</summary>
    public bool IsMe(AppUser? user) => user is not null && User is not null && user.Id == User.Id;

    /// <summary>"You" for the signed-in person, otherwise their GitHub login.</summary>
    public string NameOf(AppUser? user, string fallback = "Unknown") => IsMe(user) ? "You" : user?.Login ?? fallback;

    /// <summary>Every role the person has (assigned, from Entra, or App:Admins).</summary>
    public AppRole Roles { get; private set; }

    public bool IsAppAdmin => Roles.HasFlag(AppRole.Admin);

    public bool IsApprover => Roles.HasFlag(AppRole.Approver);

    /// <summary>Approvers and app admins can approve transfers when approval mode is on.</summary>
    public bool CanApproveTransfers => IsApprover || IsAppAdmin;

    /// <summary>Read access to everyone's budgets, history and reports: admins, budget visibility, auditors.</summary>
    public bool CanSeeEveryone => (Roles & (AppRole.Admin | AppRole.BudgetVisibility | AppRole.Auditor)) != 0;

    /// <summary>Connections, organizations, account links, write-back, alerts and the API log.</summary>
    public bool CanManageIntegration => (Roles & (AppRole.Admin | AppRole.IntegrationAdmin)) != 0;

    /// <summary>The audit log and API call log.</summary>
    public bool CanViewAudit => (Roles & (AppRole.Admin | AppRole.Auditor)) != 0;

    /// <summary>Sees alerts: anyone who runs or audits the integrations.</summary>
    public bool CanSeeAlerts => CanManageIntegration || CanViewAudit;

    /// <summary>People who left are hidden from everyday lists, except for those who manage or oversee people.</summary>
    public bool ShowsDeparted => IsManager || CanSeeEveryone || CanManageIntegration;

    /// <summary>
    /// GitHub organizations are plumbing: they decide who has a seat and the default budget. Only the people who run
    /// them see org names and per-org breakdowns; everyone else sees one company-wide budget.
    /// </summary>
    public bool ShowsOrganizations => IsManager || CanManageIntegration || CanViewAudit;

    /// <summary>Drops people who have left, unless this person should see them.</summary>
    public IEnumerable<MemberBudget> Visible(IEnumerable<MemberBudget> budgets) =>
        ShowsDeparted ? budgets : budgets.Where(b => !b.User.IsDisabled);

    /// <summary>Nobody is an admin yet: the first person to sign in with the setup code claims it.</summary>
    public bool NeedsFirstAdmin { get; private set; }

    /// <summary>An admin hasn't finished the setup checklist yet.</summary>
    public bool SetupPending { get; private set; }

    /// <summary>Signed in, but marked as left. They see nothing.</summary>
    public bool IsDisabledAccount { get; private set; }

    /// <summary>Approval mode: transfers (or ones over the threshold) wait for an approver.</summary>
    public bool ApprovalRequired => Settings.RequireTransferApproval;

    /// <summary>App-wide settings, reloaded with the session.</summary>
    public AppSettings Settings { get; private set; } = new();

    /// <summary>Approves changes that raise total spend.</summary>
    public bool IsBudgetAuthority => Roles.HasFlag(AppRole.BudgetAuthority);

    /// <summary>Every tracked organization, for display.</summary>
    public IReadOnlyList<Organization> Organizations { get; private set; } = [];

    /// <summary>The signed-in user's memberships, one per organization they belong to.</summary>
    public IReadOnlyList<Membership> Memberships { get; private set; } = [];

    /// <summary>Has a Copilot seat in at least one org, so has a budget to give or request from.</summary>
    public bool HasSeat => Memberships.Any(m => m.HasSeat);

    /// <summary>Any signed-in person can view the dashboards; changes depend on their seat and roles.</summary>
    public bool InUserbase => User is not null;

    /// <summary>
    /// Here only to run or oversee things: an admin, auditor, etc. with no Copilot seat. They have no budget of their own,
    /// and don't need a GitHub user at all.
    /// </summary>
    public bool IsAdminOnly => !HasSeat && HasManagePages;

    /// <summary>
    /// Company totals (credits allocated, used, expiring, trends, cost centers) are for people who run or oversee budgets:
    /// admins, org owners, budget authorities, budget visibility and auditors. Everyone else sees their own budget.
    /// </summary>
    public bool SeesCompanyTotals => IsManager || IsBudgetAuthority || CanSeeEveryone;

    /// <summary>A friendly first name: their display name, or the part of their email before the @.</summary>
    public string FirstName => User is null ? "" : User.Name is { Length: > 0 } n ? n.Split(' ')[0]
        : User.Login.Contains('@') ? Capitalize(User.Login.Split('@')[0].Split('.', '_', '-')[0]) : User.Login;

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Signed in with a Microsoft account that isn't matched to a GitHub user yet.</summary>
    public bool IsUnlinked => User is { HasGitHubAccount: false };

    /// <summary>Orgs where the user is an owner (synced from GitHub).</summary>
    public IReadOnlySet<int> OwnedOrganizationIds { get; private set; } = new HashSet<int>();

    public bool CanManage(int organizationId) => IsAppAdmin || OwnedOrganizationIds.Contains(organizationId);

    /// <summary>App admins and org owners: can manage budgets and decline any request.</summary>
    public bool IsManager => IsAppAdmin || OwnedOrganizationIds.Count > 0;

    /// <summary>Anyone who sees a Manage section: managers, budget authorities, integration admins, auditors, visibility.</summary>
    public bool HasManagePages => IsManager || IsBudgetAuthority || CanManageIntegration || CanSeeEveryone;

    /// <summary>App admins can adjust anyone; org owners can adjust people in their orgs.</summary>
    public bool CanAdjust(MemberBudget person) =>
        IsAppAdmin || person.Memberships.Any(m => OwnedOrganizationIds.Contains(m.OrganizationId));

    /// <summary>What a unit of budget is called in sentences. Always "credits", even when amounts show in dollars.</summary>
    public string UnitLabel => appOptions.Value.UnitLabel;

    // ---------- Credits or dollars ----------

    /// <summary>Show amounts in dollars (1 credit = DollarsPerCredit) instead of credits.</summary>
    public bool ShowDollars { get; private set; }

    public decimal DollarsPerCredit => appOptions.Value.DollarsPerCredit;

    /// <summary>A credit amount in the chosen unit, without a unit name: "1,900" or "$19".</summary>
    public string Amt(decimal credits) => ShowDollars ? Format.Dollars(credits * DollarsPerCredit) : Format.Amount(credits);

    /// <summary>With a sign: "+50" or "+$0.50".</summary>
    public string Signed(decimal credits) => ShowDollars ? Format.SignedDollars(credits * DollarsPerCredit) : Format.Signed(credits);

    /// <summary>Unit name to put after <see cref="Amt"/>: "credits", or nothing for dollars (the $ says it).</summary>
    public string Unit => ShowDollars ? "" : UnitLabel;

    /// <summary>"1,900 credits" or "$19".</summary>
    public string WithUnit(decimal credits) => ShowDollars ? Amt(credits) : $"{Format.Amount(credits)} {UnitLabel}";

    /// <summary>Short chart tick labels: 1.2K or $12.</summary>
    public string Compact(decimal credits) => ShowDollars
        ? (credits * DollarsPerCredit) switch { >= 1000 and var d => $"${d / 1000:0.#}K", var d => Format.Dollars(Math.Round(d)) }
        : Components.Shared.ChartScale.Compact(credits);

    /// <summary>For credit inputs while showing dollars: "≈ $0.25".</summary>
    public string DollarHint(decimal credits) => ShowDollars ? "≈ " + Format.Dollars(credits * DollarsPerCredit) : "";

    public async Task SetShowDollarsAsync(bool dollars)
    {
        ShowDollars = dollars;
        if (User is not null) await budgets.SetPrefersDollarsAsync(User.Id, dollars);
        Changed?.Invoke();
    }

    public event Action? Changed;

    public Task EnsureLoadedAsync() => _loading ??= LoadAsync();

    /// <summary>Reloads the user's organizations, e.g. after a sync or adding an org.</summary>
    public async Task RefreshAsync()
    {
        _loading = LoadAsync();
        await _loading;
        Changed?.Invoke();
    }

    private async Task LoadAsync()
    {
        var state = await authState.GetAuthenticationStateAsync();
        if (state.User.Identity?.IsAuthenticated != true) return;

        // The Entra object ID is authoritative: it survives a placeholder being merged into its GitHub user.
        await using var db = await dbFactory.CreateDbContextAsync();
        var objectId = state.User.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier")
                       ?? state.User.FindFirstValue("oid");
        // A Microsoft sign-in is trusted only through its object ID; the stored user ID is just for demo sign-in.
        if (objectId is not null)
            User = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.EntraObjectId == objectId);
        else if (int.TryParse(state.User.FindFirstValue(UserIdClaim), out var userId))
            User = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (User is null) return;
        if (User.IsDisabled)
        {
            // Marked as left: treat as not signed in to the userbase.
            IsDisabledAccount = true;
            User = null;
            Roles = AppRole.None;
            return;
        }

        Roles = budgets.RolesOf(User);
        NeedsFirstAdmin = await setup.NeedsAdminAsync();
        ShowDollars = User.PrefersDollars;
        Settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync() ?? new AppSettings();
        SetupPending = IsAppAdmin && Settings.SetupCompletedUtc is null;
        Memberships = await db.Memberships.Include(m => m.Organization).AsNoTracking()
            .Where(m => m.UserId == User.Id)
            .ToListAsync();
        OwnedOrganizationIds = Memberships.Where(m => m.IsOrgAdmin).Select(m => m.OrganizationId).ToHashSet();
        Organizations = (await db.Organizations.AsNoTracking().ToListAsync())
            .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();

        // A new month starts with fresh budgets; create them lazily on first visit.
        await budgets.EnsureMonthlyAllocationsAsync(Period.Current);
        await pools.EnsureMonthlyRefillsAsync(Period.Current);
        await pools.CoverOveragesAsync();
    }
}
