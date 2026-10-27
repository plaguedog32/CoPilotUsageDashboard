namespace CopilotBudget.Data;

/// <summary>
/// A person. Usually a GitHub user (from a Copilot seat or org membership) linked to the Microsoft Entra account
/// they sign in with. Someone who signs in before their GitHub account is known gets a placeholder
/// (<see cref="HasGitHubAccount"/> false, <see cref="Login"/> set to their email) that's merged into their GitHub
/// user once the two are matched.
/// </summary>
public class AppUser
{
    public int Id { get; set; }

    /// <summary>GitHub login, or the work email for a placeholder that isn't linked to GitHub yet.</summary>
    public required string Login { get; set; }

    public bool HasGitHubAccount { get; set; } = true;

    /// <summary>Work email, lower case. Used to match a Microsoft sign-in to a GitHub user.</summary>
    public string? Email { get; set; }

    /// <summary>Where <see cref="Email"/> came from: "github-saml", "admin", or "entra".</summary>
    public string? EmailSource { get; set; }

    /// <summary>Microsoft Entra object ID (oid) of the account this person signs in with.</summary>
    public string? EntraObjectId { get; set; }
    public long? GitHubId { get; set; }
    public string? Name { get; set; }
    public string? AvatarUrl { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginUtc { get; set; }

    /// <summary>Roles an admin gave this person in the app.</summary>
    public AppRole AssignedRoles { get; set; }

    /// <summary>Roles from their Entra app roles or groups (through the role mappings), refreshed at every sign-in.</summary>
    public AppRole DirectoryRoles { get; set; }

    /// <summary>The Entra app roles and group IDs in their last sign-in token, comma-separated, to help set up mappings.</summary>
    public string? DirectoryClaims { get; set; }

    /// <summary>Every role they have, however they got it. People listed in App:Admins are admins on top of this.</summary>
    public AppRole Roles => AssignedRoles | DirectoryRoles;

    public bool IsAdmin => Roles.HasFlag(AppRole.Admin);
    public bool IsApprover => Roles.HasFlag(AppRole.Approver);
    public bool IsBudgetAuthority => Roles.HasFlag(AppRole.BudgetAuthority);
    public bool IsBudgetVisibility => Roles.HasFlag(AppRole.BudgetVisibility);
    public bool IsIntegrationAdmin => Roles.HasFlag(AppRole.IntegrationAdmin);
    public bool IsAuditor => Roles.HasFlag(AppRole.Auditor);

    /// <summary>
    /// Left the company: their Entra account is disabled or deleted, or an admin marked them. They get no new credits,
    /// are hidden from everyday lists, and their pending items were cancelled.
    /// </summary>
    public bool IsDisabled { get; set; }
    public DateTime? DisabledUtc { get; set; }
    public string? DisabledReason { get; set; }

    /// <summary>When an admin finished reassigning what they had (credits, reserves, roles).</summary>
    public DateTime? DepartureHandledUtc { get; set; }

    /// <summary>Last time the Entra account was checked for being enabled.</summary>
    public DateTime? DirectoryCheckedUtc { get; set; }

    /// <summary>
    /// Monthly budget set for this person by an admin. Null means the default: the largest monthly budget among
    /// the organizations where they have a seat.
    /// </summary>
    public decimal? MonthlyBudgetOverride { get; set; }
    public string? OverrideNote { get; set; }

    /// <summary>GitHub cost center the person belongs to (enterprise billing), for usage breakdowns.</summary>
    public string? CostCenter { get; set; }

    /// <summary>Show amounts in dollars rather than credits.</summary>
    public bool PrefersDollars { get; set; }

    public List<Membership> Memberships { get; set; } = [];

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Login : Name!;
}

/// <summary>
/// A GitHub organization whose Copilot seats are tracked by this app. Organizations decide who has a seat,
/// who can manage settings, and the monthly budget their seat holders get. Budgets themselves belong to people.
/// </summary>
public class Organization
{
    public int Id { get; set; }
    public required string Login { get; set; }
    public string? DisplayName { get; set; }

    /// <summary>Credits every seat holder in this org receives at the start of each month.</summary>
    public decimal DefaultMonthlyAllocation { get; set; } = 300;

    /// <summary>Demo orgs are seeded locally and never synced with GitHub.</summary>
    public bool IsDemo { get; set; }

    public string? PlanType { get; set; }
    public int TotalSeats { get; set; }
    public int ActiveSeatsThisCycle { get; set; }
    public int InactiveSeatsThisCycle { get; set; }
    public DateTime? LastSyncedUtc { get; set; }
    public string? LastSyncMessage { get; set; }
    public bool LastSyncFailed { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public List<Membership> Memberships { get; set; } = [];

    public string Name => string.IsNullOrWhiteSpace(DisplayName) ? Login : DisplayName!;
}

/// <summary>A user's place in an organization, including their Copilot seat details.</summary>
public class Membership
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public int OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;

    public bool IsOrgAdmin { get; set; }
    public bool HasSeat { get; set; }
    public string? PlanType { get; set; }
    public DateTime? SeatCreatedUtc { get; set; }
    public DateTime? LastActivityUtc { get; set; }
    public string? LastActivityEditor { get; set; }
    public DateTime? PendingCancellationDate { get; set; }
}

public enum LedgerEntryType
{
    /// <summary>A person's monthly budget, granted at the start of the month by one of their organizations.</summary>
    MonthlyAllocation,
    TransferIn,
    TransferOut,
    AdminAdjustment,
    /// <summary>Change to someone's monthly budget this month because an organization's budget changed.</summary>
    BudgetChange,
    /// <summary>Credits moved from a credit pool (prepaid, non-expiring) into this month's budget.</summary>
    PoolDraw,
}

/// <summary>
/// Every change to someone's budget is a ledger row. Budgets belong to people, not organizations:
/// a person's budget for a month is the sum of their rows for that month.
/// </summary>
public class LedgerEntry
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public required string Period { get; set; }
    public decimal Amount { get; set; }
    public LedgerEntryType Type { get; set; }

    /// <summary>The organization that granted this budget (monthly allocations and budget changes only).</summary>
    public int? OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public int? CounterpartyUserId { get; set; }
    public AppUser? Counterparty { get; set; }

    /// <summary>The credit pool these credits came from (pool draws only).</summary>
    public int? CreditPoolId { get; set; }
    public CreditPool? CreditPool { get; set; }
    public int? BudgetRequestId { get; set; }

    /// <summary>Shared by the two rows of a transfer (the sender's and the recipient's), so either side finds the other.</summary>
    public Guid? TransferId { get; set; }
    public string? Note { get; set; }
    public int? CreatedByUserId { get; set; }
    public AppUser? CreatedBy { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Copilot credit usage for a seat in one organization in a month, as reported by GitHub.</summary>
public class UsageRecord
{
    public int Id { get; set; }
    public int MembershipId { get; set; }
    public Membership Membership { get; set; } = null!;
    public required string Period { get; set; }
    public decimal Used { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public enum RequestStatus
{
    Pending,
    Approved,
    Declined,
    Cancelled,
    /// <summary>Someone filled it, and the transfer is waiting for an approver.</summary>
    AwaitingApproval,
    /// <summary>Nobody filled it before the month ended.</summary>
    Expired,
}

/// <summary>
/// Prepaid credits that don't expire. A personal reserve has one member; a shared pool feeds several people.
/// Members move credits into their monthly budget when they need them (or automatically, to cover overage),
/// so credits stay in the pool until they're actually used.
/// </summary>
public class CreditPool
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public bool IsShared { get; set; }

    /// <summary>Automatically cover members who go over their monthly budget, up to what's left.</summary>
    public bool AutoCover { get; set; } = true;

    /// <summary>When set, the pool is topped back up to this amount on the 1st of each month.</summary>
    public decimal? MonthlyRefill { get; set; }

    /// <summary>Purchase order, contract or other reference for the credits.</summary>
    public string? Note { get; set; }

    public bool IsArchived { get; set; }
    public int? CreatedByUserId { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public List<CreditPoolMember> Members { get; set; } = [];
}

public class CreditPoolMember
{
    public int Id { get; set; }
    public int CreditPoolId { get; set; }
    public CreditPool CreditPool { get; set; } = null!;
    public int UserId { get; set; }
    public AppUser User { get; set; } = null!;
}

public enum PoolEntryType
{
    /// <summary>Credits added by an admin (a purchase or top-up).</summary>
    Added,
    /// <summary>The automatic monthly top-up.</summary>
    Refill,
    /// <summary>A member moved credits into their monthly budget.</summary>
    Draw,
    /// <summary>Moved automatically to cover a member who went over budget.</summary>
    AutoCover,
    /// <summary>Credits taken back out by an admin.</summary>
    Removed,
}

/// <summary>Every change to a pool's balance. The balance is the sum of its entries.</summary>
public class PoolEntry
{
    public int Id { get; set; }
    public int CreditPoolId { get; set; }
    public CreditPool CreditPool { get; set; } = null!;
    public decimal Amount { get; set; }
    public PoolEntryType Type { get; set; }
    public required string Period { get; set; }

    /// <summary>Who received the credits (draws and auto-cover).</summary>
    public int? UserId { get; set; }
    public AppUser? User { get; set; }
    public string? Note { get; set; }
    public int? CreatedByUserId { get; set; }
    public AppUser? CreatedBy { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>App-wide settings that admins change in the app. There is a single row.</summary>
public class AppSettings
{
    public int Id { get; set; }

    /// <summary>When on, transfers between people wait for an approver before they happen.</summary>
    public bool RequireTransferApproval { get; set; }

    /// <summary>With approval on, only transfers of at least this many credits need an approver. Null means all do.</summary>
    public decimal? ApprovalThreshold { get; set; }

    /// <summary>Credits a sender must keep for themselves after sending (0 lets people send everything).</summary>
    public decimal MinimumKeep { get; set; }

    /// <summary>Changes that raise total spend wait for a budget authority. Admins and org owners can still propose them.</summary>
    public bool RequireBudgetAuthority { get; set; } = true;

    /// <summary>Write budgets to GitHub. This is the admin off switch: when false nothing is written, but reads continue.</summary>
    public bool GitHubWriteBackEnabled { get; set; }

    /// <summary>Send the month-end close email to every app admin (on top of <see cref="MonthEndRecipients"/>).</summary>
    public bool MonthEndToAdmins { get; set; } = true;

    /// <summary>Extra people who get the month-end close email: work emails, comma-separated (e.g. a finance mailbox).</summary>
    public string? MonthEndRecipients { get; set; }

    /// <summary>
    /// First-run setup: hash of the one-time code that lets the first person claim admin (shown in the app's log at
    /// startup while nobody is an admin). Never stored in plain text.
    /// </summary>
    public string? SetupCodeHash { get; set; }
    public DateTime? SetupCodeExpiresUtc { get; set; }

    /// <summary>Wrong setup codes entered since the code was made. After too many, the code stops working until a restart.</summary>
    public int SetupCodeFailures { get; set; }

    /// <summary>When an admin finished the setup checklist.</summary>
    public DateTime? SetupCompletedUtc { get; set; }

    /// <summary>Last month whose month-end close report was sent to admins.</summary>
    public string? LastClosedPeriod { get; set; }

    public DateTime? UpdatedUtc { get; set; }
    public int? UpdatedByUserId { get; set; }
}

public enum TransferStatus
{
    Pending,
    Approved,
    Rejected,
    Cancelled,
    /// <summary>Not approved before the month ended, so the held credits went back to the sender.</summary>
    Expired,
}

/// <summary>
/// A transfer waiting for an approver (approval mode only). Nothing moves until it's approved; meanwhile the
/// amount is held from the giver's available budget so it can't be given twice.
/// </summary>
public class PendingTransfer
{
    public int Id { get; set; }
    public int FromUserId { get; set; }
    public AppUser From { get; set; } = null!;
    public int ToUserId { get; set; }
    public AppUser To { get; set; } = null!;
    public decimal Amount { get; set; }
    public string? Note { get; set; }

    /// <summary>Set when this transfer fills a budget request.</summary>
    public int? BudgetRequestId { get; set; }
    public BudgetRequest? BudgetRequest { get; set; }

    public int CreatedByUserId { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public TransferStatus Status { get; set; } = TransferStatus.Pending;
    public int? ResolvedById { get; set; }
    public AppUser? ResolvedBy { get; set; }
    public DateTime? ResolvedUtc { get; set; }
    public string? ResponseNote { get; set; }
}

/// <summary>
/// A request for budget from one person to another. When <see cref="TargetId"/> is null the request
/// is open and anyone with a seat can fulfill it.
/// </summary>
public class BudgetRequest
{
    public int Id { get; set; }
    public int RequesterId { get; set; }
    public AppUser Requester { get; set; } = null!;
    public int? TargetId { get; set; }
    public AppUser? Target { get; set; }
    public decimal Amount { get; set; }
    public required string Reason { get; set; }
    public RequestStatus Status { get; set; } = RequestStatus.Pending;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedUtc { get; set; }
    public int? ResolvedById { get; set; }
    public AppUser? ResolvedBy { get; set; }
    public string? ResponseNote { get; set; }
}
