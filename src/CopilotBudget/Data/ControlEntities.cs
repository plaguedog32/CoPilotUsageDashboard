namespace CopilotBudget.Data;

/// <summary>
/// A temporary change to someone's monthly budget between two dates (inclusive), e.g. extra credits for a launch.
/// While active it's added on top of their normal monthly budget; once it ends their budget goes back to normal.
/// </summary>
public class BudgetException
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public AppUser User { get; set; } = null!;

    /// <summary>Credits added to the monthly budget while the exception is active. Can be negative.</summary>
    public decimal Amount { get; set; }

    /// <summary>First day it applies (UTC date).</summary>
    public DateTime StartDate { get; set; }

    /// <summary>Last day it applies (UTC date, inclusive).</summary>
    public DateTime EndDate { get; set; }
    public required string Reason { get; set; }

    /// <summary>False until a budget authority approves it (when it raises spend and approval is required).</summary>
    public bool IsApproved { get; set; }
    public bool IsCancelled { get; set; }
    public int? CreatedByUserId { get; set; }
    public AppUser? CreatedBy { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public bool IsActiveOn(DateTime day) =>
        IsApproved && !IsCancelled && day.Date >= StartDate.Date && day.Date <= EndDate.Date;
}

public enum BudgetChangeKind
{
    /// <summary>An organization's monthly budget per seat.</summary>
    OrganizationBudget,
    /// <summary>One person's monthly budget, replacing their org default. A null value clears it.</summary>
    PersonOverride,
    /// <summary>A temporary exception.</summary>
    Exception,
    /// <summary>A one-off adjustment to someone's budget this month.</summary>
    Adjustment,
}

public enum ProposalStatus
{
    Pending,
    Applied,
    Rejected,
    Cancelled,
    /// <summary>Not decided before the month ended.</summary>
    Expired,
}

/// <summary>
/// A change that raises total spend, waiting for a budget authority. Nothing changes until it's approved.
/// Changes that don't raise spend apply straight away and never become proposals.
/// </summary>
public class BudgetProposal
{
    public int Id { get; set; }
    public BudgetChangeKind Kind { get; set; }
    public int? OrganizationId { get; set; }
    public Organization? Organization { get; set; }
    public int? UserId { get; set; }
    public AppUser? User { get; set; }
    public int? BudgetExceptionId { get; set; }
    public BudgetException? BudgetException { get; set; }

    /// <summary>New monthly budget (org budget or override) or the adjustment amount. Null clears an override.</summary>
    public decimal? Value { get; set; }

    /// <summary>Change to this month's total budget across everyone, worked out when it was proposed.</summary>
    public decimal SpendChange { get; set; }
    public required string Summary { get; set; }
    public string? Note { get; set; }
    public required string Period { get; set; }
    public ProposalStatus Status { get; set; } = ProposalStatus.Pending;
    public int ProposedById { get; set; }
    public AppUser ProposedBy { get; set; } = null!;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public int? DecidedById { get; set; }
    public AppUser? DecidedBy { get; set; }
    public DateTime? DecidedUtc { get; set; }
    public string? DecisionNote { get; set; }
}

public enum GitHubBudgetState
{
    /// <summary>Not checked yet.</summary>
    Unknown,
    /// <summary>GitHub's budget matches what the app expects.</summary>
    InSync,
    /// <summary>GitHub needs updating, but writing is switched off.</summary>
    WritePaused,
    /// <summary>GitHub's budget was changed outside the app, or a write didn't stick. An admin has to decide.</summary>
    ReconciliationRequired,
    /// <summary>The last read or write failed. It's retried on the next pass.</summary>
    Error,
}

/// <summary>
/// The app's view of one person's user-level budget in GitHub. The app reads the budget before every write, only
/// writes if GitHub still holds the value the app last wrote, then reads it again to check the write stuck.
/// </summary>
public class GitHubBudgetLink
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public AppUser User { get; set; } = null!;

    /// <summary>GitHub's budget ID, once one exists.</summary>
    public string? BudgetId { get; set; }

    /// <summary>Dollar cap the app wants in GitHub. Whole dollars, which is all GitHub accepts.</summary>
    public int? DesiredDollars { get; set; }

    /// <summary>Dollar cap GitHub reported on the last read. Null means there was no budget.</summary>
    public int? GitHubDollars { get; set; }

    /// <summary>The last value the app wrote and confirmed. Anything else in GitHub was changed outside the app.</summary>
    public int? LastWrittenDollars { get; set; }
    public GitHubBudgetState State { get; set; }
    public string? Message { get; set; }
    public DateTime? LastCheckedUtc { get; set; }
    public DateTime? LastWrittenUtc { get; set; }
}

/// <summary>Every call the app makes to read, change or check a GitHub budget, for auditing and troubleshooting.</summary>
public class GitHubOperation
{
    public int Id { get; set; }
    public int? UserId { get; set; }
    public AppUser? User { get; set; }

    /// <summary>Groups the read, write and check of one update.</summary>
    public Guid RunId { get; set; }

    /// <summary>"read", "create", "update", "verify" or "resolve".</summary>
    public required string Action { get; set; }
    public bool Succeeded { get; set; }
    public int? Before { get; set; }
    public int? After { get; set; }
    public string? Message { get; set; }
    public int? ActorUserId { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Usage broken down by model and SKU, as GitHub's usage report returns it. Rows with a user cover that person's
/// whole month in one org; rows without a user cover one day of an organization (<see cref="Day"/> set).
/// </summary>
public class UsageDetail
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;
    public int? UserId { get; set; }
    public AppUser? User { get; set; }
    public required string Period { get; set; }

    /// <summary>Day of the month for daily org rows; null for a person's whole month.</summary>
    public int? Day { get; set; }
    public string? Product { get; set; }
    public required string Sku { get; set; }
    public required string Model { get; set; }

    /// <summary>Credits.</summary>
    public decimal Quantity { get; set; }

    /// <summary>Gross cost in dollars.</summary>
    public decimal Cost { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
