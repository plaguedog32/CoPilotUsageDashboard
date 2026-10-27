using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace CopilotBudget.Services;

public class GitHubApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// Thin wrapper over the GitHub REST endpoints this app needs:
/// <list type="bullet">
/// <item><c>GET /orgs/{org}/copilot/billing</c> - seat summary and plan</item>
/// <item><c>GET /orgs/{org}/copilot/billing/seats</c> - who has a seat and when they last used it</item>
/// <item><c>GET /orgs/{org}/members?role=admin</c> - org owners, who become budget admins</item>
/// <item><c>GET /user/orgs</c> - organizations the token's owner belongs to, offered on the Admin page</item>
/// <item><c>GET /organizations/{org}/settings/billing/ai_credit/usage</c> - per-user Copilot AI credit usage</item>
/// </list>
/// </summary>
public class GitHubCopilotClient(HttpClient http, IOptionsMonitor<GitHubOptions> options, GitHubCredentials credentials)
{
    private static readonly JsonSerializerOptions SnakeCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions CamelCase = new(JsonSerializerDefaults.Web);

    public Task<CopilotBilling> GetBillingAsync(string org, CancellationToken ct) =>
        GetAsync<CopilotBilling>(org, $"orgs/{Uri.EscapeDataString(org)}/copilot/billing", SnakeCase, ct);

    public async Task<List<CopilotSeat>> GetSeatsAsync(string org, CancellationToken ct)
    {
        var seats = new List<CopilotSeat>();
        for (var page = 1; ; page++)
        {
            var result = await GetAsync<CopilotSeatPage>(org,
                $"orgs/{Uri.EscapeDataString(org)}/copilot/billing/seats?per_page=100&page={page}", SnakeCase, ct);
            seats.AddRange(result.Seats);
            if (result.Seats.Count < 100 || seats.Count >= result.TotalSeats) break;
        }
        return seats;
    }

    /// <summary>
    /// Work emails for GitHub users, from the SAML/SCIM identities GitHub keeps when an organization (or, with
    /// <paramref name="enterpriseSlug"/>, an enterprise) uses SSO, e.g. with Microsoft Entra. Login → email.
    /// Returns an empty map when SSO isn't set up. Needs an org owner token (admin:org or read:org with SSO access).
    /// </summary>
    public async Task<Dictionary<string, string>> GetSsoEmailsAsync(string org, string? enterpriseSlug, CancellationToken ct)
    {
        const string fields = "pageInfo { hasNextPage endCursor } nodes { samlIdentity { nameId emails { value } } scimIdentity { emails { value } } user { login } }";
        var query = string.IsNullOrWhiteSpace(enterpriseSlug)
            ? $"query($owner:String!,$after:String){{ organization(login:$owner){{ samlIdentityProvider{{ externalIdentities(first:100,after:$after){{ {fields} }} }} }} }}"
            : $"query($owner:String!,$after:String){{ enterprise(slug:$owner){{ ownerInfo{{ samlIdentityProvider{{ externalIdentities(first:100,after:$after){{ {fields} }} }} }} }} }}";
        var owner = string.IsNullOrWhiteSpace(enterpriseSlug) ? org : enterpriseSlug!;

        var emails = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? after = null;
        for (var page = 0; page < 100; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "graphql")
            {
                Content = JsonContent.Create(new { query, variables = new { owner, after } }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await credentials.GetTokenAsync(owner, ct));
            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new GitHubApiException(response.StatusCode, DescribeError(response.StatusCode, "graphql", body));

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement.GetProperty("data");
            var holder = string.IsNullOrWhiteSpace(enterpriseSlug)
                ? root.GetProperty("organization")
                : root.GetProperty("enterprise").GetProperty("ownerInfo");
            if (holder.ValueKind == JsonValueKind.Null
                || !holder.TryGetProperty("samlIdentityProvider", out var idp) || idp.ValueKind == JsonValueKind.Null)
                return emails; // No SSO configured.

            var identities = idp.GetProperty("externalIdentities");
            foreach (var node in identities.GetProperty("nodes").EnumerateArray())
            {
                if (!node.TryGetProperty("user", out var user) || user.ValueKind == JsonValueKind.Null) continue;
                var login = user.GetProperty("login").GetString();
                var email = PickEmail(node);
                if (login is not null && email is not null) emails[login] = email;
            }

            var pageInfo = identities.GetProperty("pageInfo");
            if (!pageInfo.GetProperty("hasNextPage").GetBoolean()) break;
            after = pageInfo.GetProperty("endCursor").GetString();
        }
        return emails;
    }

    /// <summary>The SAML NameID when it's an email (it usually is with Entra), otherwise the first listed email.</summary>
    private static string? PickEmail(JsonElement node)
    {
        if (node.TryGetProperty("samlIdentity", out var saml) && saml.ValueKind == JsonValueKind.Object)
        {
            var nameId = saml.TryGetProperty("nameId", out var n) ? n.GetString() : null;
            if (nameId is not null && nameId.Contains('@')) return nameId;
            if (FirstEmail(saml) is { } samlEmail) return samlEmail;
        }
        return node.TryGetProperty("scimIdentity", out var scim) && scim.ValueKind == JsonValueKind.Object ? FirstEmail(scim) : null;

        static string? FirstEmail(JsonElement identity) =>
            identity.TryGetProperty("emails", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(e => e.TryGetProperty("value", out var v) ? v.GetString() : null).FirstOrDefault(v => v?.Contains('@') == true)
                : null;
    }

    /// <summary>Every organization the token's owner belongs to (needs the read:org scope).</summary>
    public async Task<List<GitHubOrganization>> GetMyOrganizationsAsync(CancellationToken ct)
    {
        if (credentials.Mode == "app")
        {
            // A GitHub App sees the organizations it's installed on.
            return (await credentials.GetInstallationsAsync(true, ct))
                .Where(i => i.AccountType.Equals("Organization", StringComparison.OrdinalIgnoreCase))
                .Select(i => new GitHubOrganization(i.Account, i.Id, i.AvatarUrl, "GitHub App installed")).ToList();
        }
        var orgs = new List<GitHubOrganization>();
        for (var page = 1; ; page++)
        {
            var result = await GetAsync<List<GitHubOrganization>>("", $"user/orgs?per_page=100&page={page}", SnakeCase, ct);
            orgs.AddRange(result);
            if (result.Count < 100) break;
        }
        return orgs;
    }

    public async Task<List<GitHubAccount>> GetAdminsAsync(string org, CancellationToken ct)
    {
        var admins = new List<GitHubAccount>();
        for (var page = 1; ; page++)
        {
            var result = await GetAsync<List<GitHubAccount>>(org,
                $"orgs/{Uri.EscapeDataString(org)}/members?role=admin&per_page=100&page={page}", SnakeCase, ct);
            admins.AddRange(result);
            if (result.Count < 100) break;
        }
        return admins;
    }

    /// <summary>AI credits a user used in the org for the given month, one item per model and SKU.</summary>
    public async Task<List<CreditUsageItem>> GetCreditUsageAsync(string org, string user, int year, int month, CancellationToken ct)
    {
        var result = await GetAsync<CreditUsage>(org,
            $"organizations/{Uri.EscapeDataString(org)}/settings/billing/ai_credit/usage" +
            $"?user={Uri.EscapeDataString(user)}&year={year}&month={month}", CamelCase, ct);
        return result.UsageItems;
    }

    /// <summary>AI credits the whole org used on one day, one item per model and SKU.</summary>
    public async Task<List<CreditUsageItem>> GetOrgDailyUsageAsync(string org, int year, int month, int day, CancellationToken ct)
    {
        var result = await GetAsync<CreditUsage>(org,
            $"organizations/{Uri.EscapeDataString(org)}/settings/billing/ai_credit/usage" +
            $"?year={year}&month={month}&day={day}", CamelCase, ct);
        return result.UsageItems;
    }

    // ---------- Enterprise billing: user-level budgets and cost centers ----------

    private static string Enterprise(string slug) => $"enterprises/{Uri.EscapeDataString(slug)}/settings/billing";

    /// <summary>Every user-scoped budget in the enterprise.</summary>
    public async Task<List<GitHubUserBudget>> ListUserBudgetsAsync(string enterprise, CancellationToken ct)
    {
        var budgets = new List<GitHubUserBudget>();
        for (var page = 1; page <= 100; page++)
        {
            using var doc = await SendAsync(enterprise, HttpMethod.Get, $"{Enterprise(enterprise)}/budgets?per_page=100&page={page}", null, ct);
            var root = doc.RootElement;
            var list = root.ValueKind == JsonValueKind.Array ? root
                : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("budgets", out var b) ? b : default;
            if (list.ValueKind != JsonValueKind.Array) break;
            var count = 0;
            foreach (var item in list.EnumerateArray())
            {
                count++;
                if (ParseBudget(item) is { } budget) budgets.Add(budget);
            }
            var more = root.ValueKind == JsonValueKind.Object
                       && root.TryGetProperty("has_next_page", out var next) && next.ValueKind == JsonValueKind.True;
            if (!more && count < 100) break;
        }
        return budgets;
    }

    public async Task<GitHubUserBudget?> GetUserBudgetAsync(string enterprise, string budgetId, CancellationToken ct)
    {
        try
        {
            using var doc = await SendAsync(enterprise, HttpMethod.Get, $"{Enterprise(enterprise)}/budgets/{Uri.EscapeDataString(budgetId)}", null, ct);
            return ParseBudget(Unwrap(doc.RootElement));
        }
        catch (GitHubApiException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>Creates a hard-stop budget for one user. GitHub takes whole dollars.</summary>
    public async Task<GitHubUserBudget?> CreateUserBudgetAsync(string enterprise, string login, int dollars, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["budget_scope"] = "user",
            ["user"] = login,
            ["budget_entity_name"] = login,
            ["budget_amount"] = dollars,
            ["prevent_further_usage"] = true,
            ["budget_type"] = options.CurrentValue.BudgetType,
            ["budget_product_sku"] = options.CurrentValue.BudgetProductSku,
        };
        using var doc = await SendAsync(enterprise, HttpMethod.Post, $"{Enterprise(enterprise)}/budgets", body, ct);
        return ParseBudget(Unwrap(doc.RootElement));
    }

    public async Task<GitHubUserBudget?> UpdateUserBudgetAsync(string enterprise, string budgetId, int dollars, CancellationToken ct)
    {
        var body = new Dictionary<string, object?> { ["budget_amount"] = dollars, ["prevent_further_usage"] = true };
        using var doc = await SendAsync(enterprise, HttpMethod.Patch, $"{Enterprise(enterprise)}/budgets/{Uri.EscapeDataString(budgetId)}", body, ct);
        return ParseBudget(Unwrap(doc.RootElement));
    }

    /// <summary>Cost center name for each user login that belongs to an active cost center.</summary>
    public async Task<Dictionary<string, string>> GetCostCentersAsync(string enterprise, CancellationToken ct)
    {
        using var doc = await SendAsync(enterprise, HttpMethod.Get, $"{Enterprise(enterprise)}/cost-centers", null, ct);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var root = doc.RootElement;
        var list = root.ValueKind == JsonValueKind.Array ? root
            : root.ValueKind != JsonValueKind.Object ? default
            : root.TryGetProperty("costCenters", out var c) ? c
            : root.TryGetProperty("cost_centers", out var c2) ? c2 : default;
        if (list.ValueKind != JsonValueKind.Array) return map;
        foreach (var center in list.EnumerateArray())
        {
            var name = Str(center, "name");
            if (name is null) continue;
            if (Str(center, "state") is { } state && !state.Equals("active", StringComparison.OrdinalIgnoreCase)) continue;
            if (!center.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Array) continue;
            foreach (var r in resources.EnumerateArray())
            {
                if (string.Equals(Str(r, "type"), "user", StringComparison.OrdinalIgnoreCase) && Str(r, "name") is { } login)
                    map[login] = name;
            }
        }
        return map;
    }

    private static JsonElement Unwrap(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("budget", out var inner) ? inner : root;

    private static GitHubUserBudget? ParseBudget(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!string.Equals(Str(e, "budget_scope"), "user", StringComparison.OrdinalIgnoreCase)) return null;
        var id = Str(e, "id");
        var login = Str(e, "user") ?? Str(e, "budget_entity_name");
        if (id is null || login is null) return null;
        var amount = e.TryGetProperty("budget_amount", out var a) && a.ValueKind == JsonValueKind.Number
            ? (int)Math.Floor(a.GetDecimal()) : 0;
        return new GitHubUserBudget(id, login, amount, Str(e, "budget_product_sku"));
    }

    private static string? Str(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.Object when v.TryGetProperty("login", out var l) => l.GetString(),
            _ => null,
        };
    }

    private async Task<JsonDocument> SendAsync(string owner, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await credentials.GetTokenAsync(owner, ct));
        if (body is not null) request.Content = JsonContent.Create(body);

        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new GitHubApiException(response.StatusCode, DescribeError(response.StatusCode, path, text));
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
    }

    private async Task<T> GetAsync<T>(string owner, string path, JsonSerializerOptions json, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await credentials.GetTokenAsync(owner, ct));

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new GitHubApiException(response.StatusCode, DescribeError(response.StatusCode, path, body));
        }

        return await response.Content.ReadFromJsonAsync<T>(json, ct)
               ?? throw new GitHubApiException(response.StatusCode, $"Empty response from {path}");
    }

    private static string DescribeError(HttpStatusCode status, string path, string body)
    {
        var detail = body.Length > 300 ? body[..300] : body;
        var hint = status switch
        {
            HttpStatusCode.Unauthorized => "The GitHub token is missing or invalid.",
            HttpStatusCode.Forbidden when path.StartsWith("enterprises/") =>
                "The token lacks permission. Enterprise billing needs an enterprise owner or billing manager token (manage_billing:enterprise).",
            HttpStatusCode.Forbidden => "The token lacks permission. It needs manage_billing:copilot and read:org, and you must be an org owner or billing manager.",
            HttpStatusCode.NotFound => "Not found. Check the organization name and that Copilot Business/Enterprise is enabled.",
            _ => null,
        };
        return $"GitHub returned {(int)status} for {path.Split('?')[0]}. {hint} {detail}".Trim();
    }
}

public record CopilotBilling(
    SeatBreakdown? SeatBreakdown,
    string? PlanType,
    string? SeatManagementSetting);

public record SeatBreakdown(
    int Total,
    int AddedThisCycle,
    int PendingInvitation,
    int PendingCancellation,
    int ActiveThisCycle,
    int InactiveThisCycle);

public record CopilotSeatPage(int TotalSeats, List<CopilotSeat> Seats);

public record CopilotSeat(
    DateTime? CreatedAt,
    DateTime? UpdatedAt,
    DateTime? PendingCancellationDate,
    DateTime? LastActivityAt,
    string? LastActivityEditor,
    string? PlanType,
    GitHubAccount? Assignee);

public record GitHubAccount(string Login, long Id, string? AvatarUrl, string? Type);

public record GitHubOrganization(string Login, long Id, string? AvatarUrl, string? Description);

public record CreditUsage(List<CreditUsageItem> UsageItems);

public record CreditUsageItem(
    string? Product,
    string? Sku,
    string? Model,
    [property: JsonPropertyName("grossQuantity")] decimal GrossQuantity,
    [property: JsonPropertyName("netQuantity")] decimal NetQuantity,
    [property: JsonPropertyName("grossAmount")] decimal GrossAmount = 0);

/// <summary>A user-level budget in GitHub. <paramref name="Dollars"/> is GitHub's budget_amount, in whole dollars.</summary>
public record GitHubUserBudget(string Id, string Login, int Dollars, string? Sku);
