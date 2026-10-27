using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using CopilotBudget.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CopilotBudget.Services;

/// <summary>One check of a connection, for the Integration page.</summary>
public record ConnectionCheck(string Name, bool Ok, string Detail);

/// <summary>
/// The GitHub and Microsoft connections: what's configured (never the secret values), a connection test, and rotating
/// secrets in Azure Key Vault. Secrets are never stored in the app's database.
/// </summary>
public class CredentialService(
    IConfiguration configuration,
    IOptionsMonitor<GitHubOptions> gitHub,
    GitHubCredentials credentials,
    GitHubCopilotClient client,
    IGitHubBudgetStore budgetStore,
    IDirectoryClient directory,
    IDbContextFactory<AppDbContext> dbFactory,
    BudgetService budgets,
    AuditLog audit,
    ILogger<CredentialService> logger)
{
    /// <summary>Key Vault secret names an integration admin may set. Configuration keys use "--" for ":".</summary>
    public static readonly IReadOnlyDictionary<string, string> RotatableSecrets = new Dictionary<string, string>
    {
        ["GitHub--AppId"] = "GitHub App ID",
        ["GitHub--AppPrivateKey"] = "GitHub App private key (PEM)",
        ["GitHub--Token"] = "GitHub personal access token",
        ["GitHub--EnterpriseSlug"] = "GitHub enterprise slug",
    };

    /// <summary>Values that aren't secret, so the page may show them back.</summary>
    public static bool IsSecret(string name) => name is "GitHub--AppPrivateKey" or "GitHub--Token";

    /// <summary>Checks the app can reach Key Vault with its identity (reading secret names, not values).</summary>
    public async Task<ConnectionCheck> CheckKeyVaultAsync(CancellationToken ct = default)
    {
        if (KeyVaultUri is null) return new ConnectionCheck("Key Vault", false, "KeyVault:Uri isn't set.");
        try
        {
            var secrets = new SecretClient(new Uri(KeyVaultUri), new DefaultAzureCredential());
            await foreach (var _ in secrets.GetPropertiesOfSecretsAsync(ct)) break;
            return new ConnectionCheck("Key Vault", true, "Reachable with the app's identity.");
        }
        catch (Exception ex) when (ex is Azure.RequestFailedException or AuthenticationFailedException)
        {
            return new ConnectionCheck("Key Vault", false, ex.Message);
        }
    }

    public string? KeyVaultUri => configuration["KeyVault:Uri"] is { Length: > 0 } uri ? uri : null;
    public string Mode => credentials.Mode;
    public GitHubOptions GitHub => gitHub.CurrentValue;
    public IGitHubBudgetStore BudgetStore => budgetStore;
    public IDirectoryClient Directory => directory;
    public bool EntraConfigured => !string.IsNullOrWhiteSpace(configuration["AzureAd:ClientId"]);

    public Task<List<GitHubInstallation>> GetInstallationsAsync(CancellationToken ct = default) => credentials.GetInstallationsAsync(true, ct);

    /// <summary>Tries each connection: GitHub for every tracked org, the enterprise budgets, and Microsoft Graph.</summary>
    public async Task<List<ConnectionCheck>> TestAsync(int actorUserId, CancellationToken ct = default)
    {
        var checks = new List<ConnectionCheck>();
        if (!credentials.IsConfigured)
        {
            checks.Add(new ConnectionCheck("GitHub", false, "No GitHub App or token is configured."));
        }
        else
        {
            if (credentials.Mode == "app")
            {
                try
                {
                    var installs = await credentials.GetInstallationsAsync(true, ct);
                    checks.Add(new ConnectionCheck("GitHub App", true, $"Installed on {installs.Count}: {string.Join(", ", installs.Select(i => i.Account))}"));
                }
                catch (Exception ex) when (ex is GitHubApiException or HttpRequestException or System.Security.Cryptography.CryptographicException or ArgumentException)
                {
                    checks.Add(new ConnectionCheck("GitHub App", false, ex.Message));
                }
            }

            List<string> orgs;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
                orgs = await db.Organizations.Where(o => !o.IsDemo).Select(o => o.Login).ToListAsync(ct);
            foreach (var org in orgs)
            {
                try
                {
                    var billing = await client.GetBillingAsync(org, ct);
                    checks.Add(new ConnectionCheck($"Org {org}", true, $"Copilot {billing.PlanType}, {billing.SeatBreakdown?.Total ?? 0} seats readable."));
                }
                catch (Exception ex) when (ex is GitHubApiException or HttpRequestException)
                {
                    checks.Add(new ConnectionCheck($"Org {org}", false, ex.Message));
                }
            }
            if (budgetStore.IsAvailable && !budgetStore.IsSimulated)
            {
                try
                {
                    var list = await budgetStore.ListAsync(ct);
                    checks.Add(new ConnectionCheck($"Enterprise {GitHub.EnterpriseSlug} budgets", true, $"{list.Count} user budgets readable."));
                }
                catch (Exception ex) when (ex is GitHubApiException or HttpRequestException)
                {
                    checks.Add(new ConnectionCheck($"Enterprise {GitHub.EnterpriseSlug} budgets", false, ex.Message));
                }
            }
        }

        if (directory.IsAvailable)
        {
            try
            {
                await directory.GetAccountStatesAsync([Guid.Empty.ToString()], ct);
                checks.Add(new ConnectionCheck("Microsoft Graph", true, "Can read Entra accounts."));
            }
            catch (Exception ex) when (ex is HttpRequestException or AuthenticationFailedException or Azure.RequestFailedException)
            {
                checks.Add(new ConnectionCheck("Microsoft Graph", false, ex.Message));
            }
        }

        await audit.RecordAsync(actorUserId, "credentials.test",
            $"Tested connections: {checks.Count(c => c.Ok)} of {checks.Count} OK", null, null, null, checks.Select(c => new { c.Name, c.Ok }));
        return checks;
    }

    /// <summary>Writes a new secret value to Key Vault and reloads configuration. The value is never logged or stored here.</summary>
    public async Task<OpResult> SetSecretAsync(int actorUserId, string name, string value, CancellationToken ct = default)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            if (!budgets.CanManageIntegration(await db.Users.FindAsync([actorUserId], ct)))
                return OpResult.Fail("Only admins and integration admins can change credentials.");
        if (KeyVaultUri is null) return OpResult.Fail("Key Vault isn't configured (KeyVault:Uri), so credentials are managed in app settings.");
        if (!RotatableSecrets.ContainsKey(name)) return OpResult.Fail("That isn't a credential this page manages.");
        value = value.Trim();
        if (value.Length == 0) return OpResult.Fail("Enter the new value.");
        if (name == "GitHub--AppPrivateKey" && !value.Contains("PRIVATE KEY")) return OpResult.Fail("Paste the whole PEM private key, including the BEGIN/END lines.");
        if (name == "GitHub--AppId" && !value.All(char.IsLetterOrDigit)) return OpResult.Fail("The App ID is a number (or the app's client ID).");
        if (name == "GitHub--EnterpriseSlug" && !System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z0-9-]+$"))
            return OpResult.Fail("The enterprise slug is the short name in its URL, e.g. github.com/enterprises/contoso → contoso.");

        try
        {
            var secrets = new SecretClient(new Uri(KeyVaultUri), new DefaultAzureCredential());
            await secrets.SetSecretAsync(name, value, ct);
        }
        catch (Exception ex) when (ex is Azure.RequestFailedException or AuthenticationFailedException)
        {
            logger.LogWarning(ex, "Couldn't write {Secret} to Key Vault", name);
            return OpResult.Fail("Key Vault refused the change: " + ex.Message + " The app's identity needs the Key Vault Secrets Officer role to rotate secrets.");
        }

        if (configuration is IConfigurationRoot root) root.Reload();
        credentials.Reset();
        await audit.RecordAsync(actorUserId, "credentials.rotate", $"Updated {RotatableSecrets[name]} in Key Vault", "secret", null, name);
        return OpResult.Success($"Saved {RotatableSecrets[name]} to Key Vault and reloaded settings.");
    }
}
