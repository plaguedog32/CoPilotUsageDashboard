using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace CopilotBudget.Services;

/// <summary>Where user-level budgets live: GitHub's enterprise budgets API, or a simulated copy for the demo.</summary>
public interface IGitHubBudgetStore
{
    /// <summary>False when there's nowhere to read or write budgets (no token or enterprise configured).</summary>
    bool IsAvailable { get; }

    /// <summary>Shown on the GitHub sync page, e.g. "GitHub enterprise contoso".</summary>
    string Description { get; }

    bool IsSimulated { get; }

    Task<List<GitHubUserBudget>> ListAsync(CancellationToken ct);
    Task<GitHubUserBudget?> GetAsync(string budgetId, CancellationToken ct);
    Task<GitHubUserBudget?> CreateAsync(string login, int dollars, CancellationToken ct);
    Task<GitHubUserBudget?> UpdateAsync(string budgetId, int dollars, CancellationToken ct);
}

/// <summary>User-level budgets in a GitHub Enterprise Cloud account (GitHub:EnterpriseSlug).</summary>
public class EnterpriseBudgetStore(GitHubCopilotClient github, IOptionsMonitor<GitHubOptions> options) : IGitHubBudgetStore
{
    private GitHubOptions Options => options.CurrentValue;
    private string Slug => Options.EnterpriseSlug!;

    /// <summary>Read live: credentials and the enterprise slug can be added in Key Vault while the app runs.</summary>
    public bool IsAvailable => Options.HasCredentials && Options.HasEnterprise;
    public string Description => IsAvailable
        ? $"GitHub enterprise {Options.EnterpriseSlug}"
        : "Not connected. Set GitHub credentials (a GitHub App or token) and the enterprise slug (user budgets are an enterprise feature).";
    public bool IsSimulated => false;

    public Task<List<GitHubUserBudget>> ListAsync(CancellationToken ct) => github.ListUserBudgetsAsync(Slug, ct);
    public Task<GitHubUserBudget?> GetAsync(string budgetId, CancellationToken ct) => github.GetUserBudgetAsync(Slug, budgetId, ct);
    public Task<GitHubUserBudget?> CreateAsync(string login, int dollars, CancellationToken ct) => github.CreateUserBudgetAsync(Slug, login, dollars, ct);
    public Task<GitHubUserBudget?> UpdateAsync(string budgetId, int dollars, CancellationToken ct) => github.UpdateUserBudgetAsync(Slug, budgetId, dollars, ct);
}

/// <summary>
/// In-memory stand-in for GitHub's budgets, so the write-back flow can be tried with the demo data. It starts with one
/// budget the app didn't set (to show reconciliation), and admins can simulate someone editing a budget in GitHub.
/// </summary>
public class SimulatedBudgetStore : IGitHubBudgetStore
{
    private readonly ConcurrentDictionary<string, GitHubUserBudget> _budgets = new();
    private int _nextId = 1000;

    public SimulatedBudgetStore()
    {
        // Someone set Barbara's budget directly in GitHub before the app was connected.
        var id = NextId();
        _budgets[id] = new GitHubUserBudget(id, "barbara", 30, "copilot");
    }

    public bool IsAvailable => true;
    public string Description => "Simulated GitHub (demo data)";
    public bool IsSimulated => true;

    public Task<List<GitHubUserBudget>> ListAsync(CancellationToken ct) => Task.FromResult(_budgets.Values.ToList());

    public Task<GitHubUserBudget?> GetAsync(string budgetId, CancellationToken ct) =>
        Task.FromResult(_budgets.TryGetValue(budgetId, out var b) ? b : null);

    public Task<GitHubUserBudget?> CreateAsync(string login, int dollars, CancellationToken ct)
    {
        var id = NextId();
        var budget = new GitHubUserBudget(id, login, dollars, "copilot");
        _budgets[id] = budget;
        return Task.FromResult<GitHubUserBudget?>(budget);
    }

    public Task<GitHubUserBudget?> UpdateAsync(string budgetId, int dollars, CancellationToken ct)
    {
        if (!_budgets.TryGetValue(budgetId, out var b)) return Task.FromResult<GitHubUserBudget?>(null);
        var updated = b with { Dollars = dollars };
        _budgets[budgetId] = updated;
        return Task.FromResult<GitHubUserBudget?>(updated);
    }

    /// <summary>Pretends someone changed a budget in GitHub's settings, outside the app.</summary>
    public void SimulateOutsideChange(string login, int dollars)
    {
        var existing = _budgets.Values.FirstOrDefault(b => string.Equals(b.Login, login, StringComparison.OrdinalIgnoreCase));
        if (existing is null) CreateAsync(login, dollars, default);
        else _budgets[existing.Id] = existing with { Dollars = dollars };
    }

    private string NextId() => Interlocked.Increment(ref _nextId).ToString();
}
