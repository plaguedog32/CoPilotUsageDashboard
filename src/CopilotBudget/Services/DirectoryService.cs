using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;

namespace CopilotBudget.Services;

/// <summary>Whether Microsoft Entra accounts are still enabled.</summary>
public interface IDirectoryClient
{
    bool IsAvailable { get; }
    string Description { get; }

    /// <summary>For each object ID: true if enabled, false if disabled, null if the account no longer exists.</summary>
    Task<Dictionary<string, bool?>> GetAccountStatesAsync(IReadOnlyList<string> objectIds, CancellationToken ct);
}

/// <summary>
/// Reads <c>accountEnabled</c> from Microsoft Graph, 20 accounts per batch request. Needs the application permission
/// User.Read.All (admin consent) for the app registration, or for the web app's managed identity. Uses the Entra
/// client secret when one is configured, otherwise the managed identity.
/// </summary>
public class GraphDirectoryClient(IHttpClientFactory httpFactory, IConfiguration configuration) : IDirectoryClient
{
    public const string HttpClientName = "graph";
    private TokenCredential? _credential;

    public bool IsAvailable =>
        !string.IsNullOrWhiteSpace(configuration["AzureAd:TenantId"]) && configuration.GetValue("Directory:CheckAccounts", true);

    public string Description => IsAvailable
        ? "Microsoft Graph (User.Read.All)"
        : "Not connected. Needs Microsoft Entra sign-in configured, and Directory:CheckAccounts on.";

    private TokenCredential Credential => _credential ??=
        !string.IsNullOrWhiteSpace(configuration["AzureAd:ClientSecret"])
            ? new ClientSecretCredential(configuration["AzureAd:TenantId"], configuration["AzureAd:ClientId"], configuration["AzureAd:ClientSecret"])
            : new DefaultAzureCredential();

    public async Task<Dictionary<string, bool?>> GetAccountStatesAsync(IReadOnlyList<string> objectIds, CancellationToken ct)
    {
        var result = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
        if (objectIds.Count == 0) return result;
        var token = await Credential.GetTokenAsync(new TokenRequestContext(["https://graph.microsoft.com/.default"]), ct);
        var http = httpFactory.CreateClient(HttpClientName);

        foreach (var chunk in objectIds.Chunk(20))
        {
            var body = new
            {
                requests = chunk.Select((id, i) => new { id = i.ToString(), method = "GET", url = $"/users/{Uri.EscapeDataString(id)}?$select=id,accountEnabled" }),
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, "v1.0/$batch") { Content = JsonContent.Create(body) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            using var response = await http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Microsoft Graph returned {(int)response.StatusCode}: {(text.Length > 300 ? text[..300] : text)}");

            using var doc = JsonDocument.Parse(text);
            foreach (var r in doc.RootElement.GetProperty("responses").EnumerateArray())
            {
                var id = chunk[int.Parse(r.GetProperty("id").GetString()!)];
                var status = r.GetProperty("status").GetInt32();
                if (status == 404) result[id] = null;
                else if (status == 200 && r.GetProperty("body").TryGetProperty("accountEnabled", out var enabled)) result[id] = enabled.GetBoolean();
                else if (status is 401 or 403)
                    throw new HttpRequestException("Microsoft Graph refused to read accounts. Grant the app (or managed identity) User.Read.All with admin consent.");
                // Other statuses (throttling) are skipped this round.
            }
        }
        return result;
    }
}

/// <summary>No directory to ask (demo, or Entra not configured). Admins can still mark people as left by hand.</summary>
public class NoDirectoryClient : IDirectoryClient
{
    public bool IsAvailable => false;
    public string Description => "Not connected (Microsoft Entra isn't configured). Mark people as left by hand.";
    public Task<Dictionary<string, bool?>> GetAccountStatesAsync(IReadOnlyList<string> objectIds, CancellationToken ct) =>
        Task.FromResult(new Dictionary<string, bool?>());
}
