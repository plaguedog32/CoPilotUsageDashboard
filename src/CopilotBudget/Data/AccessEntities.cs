namespace CopilotBudget.Data;

/// <summary>What someone may do in the app. A person can have several.</summary>
[Flags]
public enum AppRole
{
    None = 0,

    /// <summary>Everything: settings, roles, budgets for every org, every page.</summary>
    Admin = 1,

    /// <summary>Approves transfers between people when approval is on.</summary>
    Approver = 2,

    /// <summary>Approves changes that raise total spend.</summary>
    BudgetAuthority = 4,

    /// <summary>Read-only view of everyone's budgets, history and month-end reports.</summary>
    BudgetVisibility = 8,

    /// <summary>
    /// Runs the connections: GitHub credentials, organizations, write-back, Entra↔GitHub account links, alerts and the
    /// API call log. Can't change anyone's budget or roles.
    /// </summary>
    IntegrationAdmin = 16,

    /// <summary>Read-only access to everything, including the audit log and API call log.</summary>
    Auditor = 32,
}

public enum RoleSource
{
    /// <summary>An Entra app role, matched by its value (e.g. "CopilotBudget.Approver").</summary>
    AppRole,

    /// <summary>An Entra security group, matched by its object ID.</summary>
    Group,
}

/// <summary>Grants a role in this app to everyone with an Entra app role or in an Entra group. Admins manage these.</summary>
public class RoleMapping
{
    public int Id { get; set; }
    public RoleSource Source { get; set; }

    /// <summary>The app role value or the group's object ID, as it appears in the sign-in token.</summary>
    public required string Value { get; set; }

    /// <summary>A friendly name for the group or role, for the admin page.</summary>
    public string? Label { get; set; }
    public AppRole Role { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One admin action: who did what to what, and when. The app only ever adds rows; nothing edits or deletes them.
/// </summary>
public class AuditEntry
{
    public long Id { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Null for the app itself (background jobs, sign-in).</summary>
    public int? ActorUserId { get; set; }

    /// <summary>Copied at the time, so the entry still reads right if the person is renamed or merged.</summary>
    public required string ActorName { get; set; }

    /// <summary>Dotted action name, e.g. "roles.change", "budget.org", "writeback.toggle".</summary>
    public required string Action { get; set; }
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public string? TargetName { get; set; }
    public required string Summary { get; set; }

    /// <summary>Extra detail as JSON (before/after values). Never secrets.</summary>
    public string? Details { get; set; }
}

/// <summary>One call to an outside API (GitHub or Microsoft Graph). Kept for <c>App:ApiLogRetentionDays</c>.</summary>
public class ApiCall
{
    public long Id { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>"GitHub" or "Graph".</summary>
    public required string Service { get; set; }
    public required string Method { get; set; }

    /// <summary>Path and query, without the host.</summary>
    public required string Path { get; set; }

    /// <summary>HTTP status, or 0 when the request never got an answer.</summary>
    public int Status { get; set; }
    public int DurationMs { get; set; }

    /// <summary>GitHub's x-ratelimit-remaining after the call.</summary>
    public int? RateLimitRemaining { get; set; }

    /// <summary>The start of the error body, or the exception message.</summary>
    public string? Error { get; set; }
    public bool Succeeded => Status is >= 200 and < 300;
}

public enum AlertSeverity
{
    Warning,
    Critical,
}

/// <summary>
/// Something that needs a person: failed sign-ins, a sync that's stale or partly failed, a GitHub budget write that
/// failed or needs reconciling. Repeats update the same open alert; it closes itself when the problem clears.
/// </summary>
public class Alert
{
    public int Id { get; set; }

    /// <summary>Identifies the problem, e.g. "sync-stale:contoso". At most one open alert per key.</summary>
    public required string Key { get; set; }

    /// <summary>"sign-in", "sync", "write-back", "directory".</summary>
    public required string Kind { get; set; }
    public AlertSeverity Severity { get; set; }
    public required string Title { get; set; }
    public string? Detail { get; set; }
    public int Count { get; set; } = 1;
    public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedUtc { get; set; }
    public DateTime? AcknowledgedUtc { get; set; }
    public int? AcknowledgedById { get; set; }
    public AppUser? AcknowledgedBy { get; set; }
    public bool IsOpen => ResolvedUtc is null;
}
