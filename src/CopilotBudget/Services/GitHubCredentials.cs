using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace CopilotBudget.Services;

/// <summary>An organization or enterprise where the GitHub App is installed.</summary>
public record GitHubInstallation(long Id, string Account, string AccountType, string? AvatarUrl, DateTime? TokenExpiresUtc);

/// <summary>
/// Hands out the token for a GitHub call. Two ways to connect:
/// <list type="bullet">
/// <item><b>GitHub App</b> (recommended): <c>GitHub:AppId</c> and <c>GitHub:AppPrivateKey</c>. The app signs a short JWT
/// with its private key and exchanges it for an installation token per organization (or enterprise), which expires
/// after an hour. Nothing long-lived belongs to a person, and permissions are set on the app.</item>
/// <item><b>Personal access token</b>: <c>GitHub:Token</c>. Simple, but tied to one person's account.</item>
/// </list>
/// Settings are read live, so a credential rotated in Key Vault takes effect without a restart.
/// </summary>
public class GitHubCredentials(IHttpClientFactory httpFactory, IOptionsMonitor<GitHubOptions> options, ILogger<GitHubCredentials> logger)
{
    public const string HttpClientName = "github-app";

    private readonly ConcurrentDictionary<long, (string Token, DateTime ExpiresUtc)> _tokens = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<GitHubInstallation> _installations = [];
    private DateTime _installationsFetchedUtc = DateTime.MinValue;

    private GitHubOptions Options => options.CurrentValue;

    /// <summary>"app", "token" or "none".</summary>
    public string Mode => Options.HasApp ? "app" : Options.HasToken ? "token" : "none";
    public bool IsConfigured => Mode != "none";

    /// <summary>The token for calls about <paramref name="owner"/> (an org login or enterprise slug).</summary>
    public async Task<string> GetTokenAsync(string? owner, CancellationToken ct)
    {
        if (!Options.HasApp)
            return Options.Token ?? throw new GitHubApiException(System.Net.HttpStatusCode.Unauthorized, "No GitHub credentials are configured.");

        var installation = await FindInstallationAsync(owner, ct)
            ?? throw new GitHubApiException(System.Net.HttpStatusCode.Forbidden,
                $"The GitHub App isn't installed on {owner ?? "any account"}. Install it there (Settings → GitHub Apps), then try again.");
        return await InstallationTokenAsync(installation.Id, ct);
    }

    /// <summary>Where the GitHub App is installed (organizations and enterprises). Empty for a personal token.</summary>
    public async Task<List<GitHubInstallation>> GetInstallationsAsync(bool refresh, CancellationToken ct)
    {
        if (!Options.HasApp) return [];
        if (!refresh && DateTime.UtcNow - _installationsFetchedUtc < TimeSpan.FromMinutes(10)) return WithExpiry(_installations);

        await _lock.WaitAsync(ct);
        try
        {
            var list = new List<GitHubInstallation>();
            for (var page = 1; page <= 10; page++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"app/installations?per_page=100&page={page}");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateJwt());
                using var response = await Http.SendAsync(request, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                    throw new GitHubApiException(response.StatusCode, $"GitHub App installations: {(int)response.StatusCode} {Short(body)}");
                using var doc = JsonDocument.Parse(body);
                var count = 0;
                foreach (var i in doc.RootElement.EnumerateArray())
                {
                    count++;
                    var account = i.GetProperty("account");
                    var login = account.TryGetProperty("login", out var l) ? l.GetString() : account.TryGetProperty("slug", out var sl) ? sl.GetString() : null;
                    if (login is null) continue;
                    list.Add(new GitHubInstallation(i.GetProperty("id").GetInt64(), login,
                        i.TryGetProperty("target_type", out var t) ? t.GetString() ?? "Organization" : "Organization",
                        account.TryGetProperty("avatar_url", out var a) ? a.GetString() : null, null));
                }
                if (count < 100) break;
            }
            _installations = list;
            _installationsFetchedUtc = DateTime.UtcNow;
            return WithExpiry(list);
        }
        finally
        {
            _lock.Release();
        }
    }

    private List<GitHubInstallation> WithExpiry(List<GitHubInstallation> list) =>
        list.Select(i => i with { TokenExpiresUtc = _tokens.TryGetValue(i.Id, out var t) ? t.ExpiresUtc : null }).ToList();

    private async Task<GitHubInstallation?> FindInstallationAsync(string? owner, CancellationToken ct)
    {
        var list = await GetInstallationsAsync(false, ct);
        var match = Find(list, owner);
        if (match is null && list.Count > 0) match = Find(await GetInstallationsAsync(true, ct), owner); // Maybe newly installed.
        return match;

        static GitHubInstallation? Find(List<GitHubInstallation> l, string? owner) => owner is null
            ? l.FirstOrDefault()
            : l.FirstOrDefault(i => string.Equals(i.Account, owner, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string> InstallationTokenAsync(long installationId, CancellationToken ct)
    {
        if (_tokens.TryGetValue(installationId, out var cached) && cached.ExpiresUtc > DateTime.UtcNow.AddMinutes(5))
            return cached.Token;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"app/installations/{installationId}/access_tokens");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateJwt());
        using var response = await Http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new GitHubApiException(response.StatusCode, $"Couldn't get a GitHub App installation token: {(int)response.StatusCode} {Short(body)}");

        using var doc = JsonDocument.Parse(body);
        var token = doc.RootElement.GetProperty("token").GetString()!;
        var expires = doc.RootElement.TryGetProperty("expires_at", out var e) ? e.GetDateTime().ToUniversalTime() : DateTime.UtcNow.AddMinutes(55);
        _tokens[installationId] = (token, expires);
        logger.LogInformation("Got a GitHub App installation token for installation {Installation}, valid until {Expires:u}", installationId, expires);
        return token;
    }

    /// <summary>A GitHub App JWT: RS256, issued a minute in the past for clock skew, valid for 9 minutes.</summary>
    private string CreateJwt()
    {
        var pem = Options.AppPrivateKey!.Replace("\\n", "\n").Trim();
        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        var now = DateTimeOffset.UtcNow;
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iat = now.AddSeconds(-60).ToUnixTimeSeconds(),
            exp = now.AddMinutes(9).ToUnixTimeSeconds(),
            iss = Options.AppId,
        }));
        var signature = rsa.SignData(Encoding.ASCII.GetBytes($"{header}.{payload}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{payload}.{Base64Url(signature)}";
    }

    /// <summary>Forget cached tokens, e.g. after the private key was rotated.</summary>
    public void Reset()
    {
        _tokens.Clear();
        _installationsFetchedUtc = DateTime.MinValue;
    }

    private HttpClient Http => httpFactory.CreateClient(HttpClientName);

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Short(string body) => body.Length > 200 ? body[..200] : body;
}
