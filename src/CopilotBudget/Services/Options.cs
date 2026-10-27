namespace CopilotBudget.Services;

public class AppOptions
{
    public const string Section = "App";

    /// <summary>What a unit of budget is called in the UI.</summary>
    public string UnitLabel { get; set; } = "credits";

    /// <summary>How many days before the reset admins are warned about credits that will expire unused.</summary>
    public int ExpiryWarningDays { get; set; } = 7;

    /// <summary>Days of outside API calls (GitHub, Graph) to keep in the log.</summary>
    public int ApiLogRetentionDays { get; set; } = 30;

    /// <summary>GitHub logins or work emails that are always admins.</summary>
    public List<string> Admins { get; set; } = [];

    /// <summary>Shows a "sign in as" picker instead of requiring GitHub OAuth. Never enable in production.</summary>
    public bool EnableDemoLogin { get; set; }

    /// <summary>Seeds a demo organization with fake members when the database is empty.</summary>
    public bool SeedDemoData { get; set; }

    /// <summary>What one credit costs. GitHub prices AI credits at $0.01 each.</summary>
    public decimal DollarsPerCredit { get; set; } = 0.01m;

    /// <summary>Organizations to add on startup if they are not tracked yet.</summary>
    public List<string> Organizations { get; set; } = [];

    /// <summary>Entries in App:Admins can be GitHub logins or work emails (for admins without a GitHub account).</summary>
    public bool IsAdmin(string? loginOrEmail) =>
        !string.IsNullOrWhiteSpace(loginOrEmail) && Admins.Any(a => string.Equals(a.Trim(), loginOrEmail.Trim(), StringComparison.OrdinalIgnoreCase));
}

public class GitHubOptions
{
    public const string Section = "GitHub";

    public string ApiBaseUrl { get; set; } = "https://api.github.com/";

    /// <summary>
    /// Token used to read Copilot data. Needs the <c>manage_billing:copilot</c> and <c>read:org</c>
    /// scopes (classic PAT), or the equivalent fine-grained / GitHub App permissions.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>
    /// GitHub enterprise slug. When set, work emails for matching Microsoft sign-ins to GitHub users are read from
    /// the enterprise's SSO identities instead of each organization's.
    /// </summary>
    public string? EnterpriseSlug { get; set; }

    /// <summary>Read work emails from GitHub SSO (SAML/SCIM) identities during sync, to link Microsoft sign-ins.</summary>
    public bool SyncSsoEmails { get; set; } = true;

    /// <summary>How often seats and usage are pulled from GitHub. Each sync makes about two calls per seat.</summary>
    public int SyncIntervalMinutes { get; set; } = 10;

    /// <summary>How often user budgets are checked against GitHub (and written, if write-back is on).</summary>
    public int WriteBackIntervalSeconds { get; set; } = 120;

    /// <summary>
    /// Product the user-level budgets cover (GitHub's budget_product_sku). Check the value against your
    /// enterprise's existing budgets before turning write-back on.
    /// </summary>
    public string BudgetProductSku { get; set; } = "copilot";

    /// <summary>GitHub's budget_type for the budgets the app creates.</summary>
    public string BudgetType { get; set; } = "ProductPricing";

    /// <summary>Pull each organization's usage per day (by model and SKU) for this month and last.</summary>
    public bool SyncDailyUsage { get; set; } = true;

    /// <summary>Read GitHub cost centers (enterprise only) so usage can be broken down by cost center.</summary>
    public bool SyncCostCenters { get; set; } = true;

    /// <summary>Pull per-user Copilot AI credit usage from the billing API.</summary>
    public bool SyncCreditUsage { get; set; } = true;

    /// <summary>
    /// How many months of usage to keep filled in, including the current one. Missing months are
    /// backfilled from GitHub, which keeps 24 months. Stored months are never deleted.
    /// </summary>
    public int UsageHistoryMonths { get; set; } = 12;

    /// <summary>Limit on backfill calls per org per sync, so a first sync of a big org stays under the rate limit.</summary>
    public int MaxBackfillCallsPerSync { get; set; } = 500;

    public bool HasToken => !string.IsNullOrWhiteSpace(Token);

    /// <summary>GitHub App ID (or client ID). With <see cref="AppPrivateKey"/>, the app connects as a GitHub App instead of with a token.</summary>
    public string? AppId { get; set; }

    /// <summary>The GitHub App's private key, PEM. Keep it in Key Vault (secret name GitHub--AppPrivateKey).</summary>
    public string? AppPrivateKey { get; set; }

    public bool HasApp => !string.IsNullOrWhiteSpace(AppId) && !string.IsNullOrWhiteSpace(AppPrivateKey);

    /// <summary>A token or a GitHub App is set up.</summary>
    public bool HasCredentials => HasToken || HasApp;

    /// <summary>Budgets, cost centers and enterprise SSO all live on the enterprise.</summary>
    public bool HasEnterprise => !string.IsNullOrWhiteSpace(EnterpriseSlug);
}
