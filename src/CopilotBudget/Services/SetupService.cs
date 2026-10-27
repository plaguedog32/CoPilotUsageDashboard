using System.Security.Cryptography;
using System.Text;
using CopilotBudget.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CopilotBudget.Services;

/// <summary>One line of the setup checklist.</summary>
/// <param name="Optional">Nice to have; the app works without it.</param>
public record SetupStep(string Key, string Title, bool Done, string Detail, bool Optional = false, string? Link = null);

/// <summary>
/// First run. While nobody is an admin, the app makes a one-time setup code at startup and writes it to its log (App
/// Service log stream / Application Insights). The first person who signs in with Microsoft and enters that code
/// becomes admin, then finishes setup on the Setup page: GitHub credentials go straight into Key Vault, never into the
/// database. People can also be made admin without the code: through the CopilotBudget.Admin app role in Entra, or the
/// App:Admins setting.
/// </summary>
public class SetupService(
    IDbContextFactory<AppDbContext> dbFactory,
    IConfiguration configuration,
    IOptions<AppOptions> appOptions,
    GitHubCredentials credentials,
    IGitHubBudgetStore budgetStore,
    IDirectoryClient directory,
    IEmailSender email,
    BudgetService budgets,
    AuditLog audit,
    AlertService alerts,
    ChangeNotifier notifier,
    ILogger<SetupService> logger)
{
    public const int MaxCodeAttempts = 10;
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromHours(24);
    private static readonly SemaphoreSlim Lock = new(1, 1);

    /// <summary>Nobody can administer the app yet: no admins in the database, from Entra, or in App:Admins.</summary>
    public async Task<bool> NeedsAdminAsync()
    {
        if (appOptions.Value.Admins.Any(a => !string.IsNullOrWhiteSpace(a))) return false;
        await using var db = await dbFactory.CreateDbContextAsync();
        return !(await db.Users.AsNoTracking().Where(u => !u.IsDisabled).ToListAsync()).Any(u => u.IsAdmin);
    }

    /// <summary>At startup: if nobody is an admin, make a fresh one-time code and write it to the log.</summary>
    public static async Task IssueCodeIfNeededAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var setup = scope.ServiceProvider.GetRequiredService<SetupService>();
        await setup.IssueCodeIfNeededAsync();
    }

    private async Task IssueCodeIfNeededAsync()
    {
        if (!await NeedsAdminAsync()) return;
        var code = NewCode();
        await using var db = await dbFactory.CreateDbContextAsync();
        var settings = await db.Settings.FirstOrDefaultAsync();
        if (settings is null) db.Settings.Add(settings = new AppSettings());
        settings.SetupCodeHash = Hash(code);
        settings.SetupCodeExpiresUtc = DateTime.UtcNow.Add(CodeLifetime);
        settings.SetupCodeFailures = 0;
        await db.SaveChangesAsync();
        // Deliberately logged: whoever can read the app's logs (Azure access) is trusted to set it up.
        logger.LogWarning(
            "FIRST-RUN SETUP: nobody is an admin yet. Sign in with Microsoft, open /setup and enter this one-time code: {SetupCode} " +
            "(valid for {Hours} hours or until used; restarting the app makes a new one).", code, (int)CodeLifetime.TotalHours);
    }

    /// <summary>The signed-in person enters the code from the log and becomes the first admin.</summary>
    public async Task<OpResult> ClaimAdminAsync(int userId, string code)
    {
        await Lock.WaitAsync();
        try
        {
            if (!await NeedsAdminAsync()) return OpResult.Fail("Someone is already an admin. Ask them to give you access.");
            await using var db = await dbFactory.CreateDbContextAsync();
            var settings = await db.Settings.FirstOrDefaultAsync();
            var user = await db.Users.FindAsync(userId);
            if (user is null || user.IsDisabled) return OpResult.Fail("Your account can't be made an admin.");
            if (settings?.SetupCodeHash is null || settings.SetupCodeExpiresUtc < DateTime.UtcNow)
                return OpResult.Fail("There's no valid setup code. Restart the web app to get a new one in its log.");
            if (settings.SetupCodeFailures >= MaxCodeAttempts)
                return OpResult.Fail("Too many wrong codes. Restart the web app to get a new one.");

            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(code)), Encoding.ASCII.GetBytes(settings.SetupCodeHash)))
            {
                settings.SetupCodeFailures++;
                await db.SaveChangesAsync();
                logger.LogWarning("Wrong setup code entered by {Login} ({Failures} of {Max})", user.Login, settings.SetupCodeFailures, MaxCodeAttempts);
                if (settings.SetupCodeFailures >= 3)
                    await alerts.RaiseAsync("setup-code-failures", "sign-in", AlertSeverity.Warning, "Wrong setup codes are being entered",
                        $"{settings.SetupCodeFailures} wrong first-run setup codes, most recently by {user.Login}.");
                return OpResult.Fail($"That code isn't right. {MaxCodeAttempts - settings.SetupCodeFailures} tries left.");
            }

            user.AssignedRoles |= AppRole.Admin;
            settings.SetupCodeHash = null;
            settings.SetupCodeExpiresUtc = null;
            settings.SetupCodeFailures = 0;
            await db.SaveChangesAsync();
            await audit.RecordAsync(userId, "setup.claim-admin", $"{user.Login} claimed admin with the first-run setup code", "user", user.Id, user.Login);
            await alerts.ResolveAsync("setup-code-failures");
            logger.LogWarning("{Login} is now the first admin (claimed with the setup code)", user.Login);
            await notifier.NotifyAsync();
            return OpResult.Success("You're an admin. Finish setting up the app below.");
        }
        finally
        {
            Lock.Release();
        }
    }

    public async Task<bool> IsCompleteAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return (await BudgetService.GetSettingsAsync(db)).SetupCompletedUtc is not null;
    }

    public async Task<OpResult> CompleteAsync(int actorUserId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!budgets.IsAppAdmin(await db.Users.FindAsync(actorUserId))) return OpResult.Fail("Only admins can finish setup.");
        var settings = await db.Settings.FirstOrDefaultAsync();
        if (settings is null) db.Settings.Add(settings = new AppSettings());
        settings.SetupCompletedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync(actorUserId, "setup.complete", "Marked first-run setup as finished");
        await notifier.NotifyAsync();
        return OpResult.Success("Setup is finished. You can come back to this page any time.");
    }

    /// <summary>The setup checklist: what's configured, what's missing, and where to fix it.</summary>
    public async Task<List<SetupStep>> GetStatusAsync()
    {
        var enterpriseConnected = budgetStore.IsAvailable && !budgetStore.IsSimulated;
        var access = enterpriseConnected ? await budgetStore.GetWriteAccessAsync(CancellationToken.None) : null;

        await using var db = await dbFactory.CreateDbContextAsync();
        var users = await db.Users.AsNoTracking().Where(u => !u.IsDisabled).ToListAsync();
        var admins = users.Count(u => budgets.IsAppAdmin(u));
        var orgs = await db.Organizations.CountAsync(o => !o.IsDemo);
        var syncedOrgs = await db.Organizations.CountAsync(o => !o.IsDemo && o.LastSyncedUtc != null && !o.LastSyncFailed);
        var mappings = await db.RoleMappings.CountAsync();
        var sqlServer = DatabaseSetup.IsSqlServer(configuration);
        var keyVault = configuration["KeyVault:Uri"] is { Length: > 0 };
        var entra = !string.IsNullOrWhiteSpace(configuration["AzureAd:ClientId"]) && !string.IsNullOrWhiteSpace(configuration["AzureAd:TenantId"]);
        var insights = !string.IsNullOrWhiteSpace(configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]);

        return
        [
            new("entra", "Microsoft Entra sign-in", entra,
                entra ? "People sign in with their work account." : "Not configured: only demo sign-in works. Set AzureAd__TenantId and AzureAd__ClientId."),
            new("admins", "Admins", admins > 0,
                $"{admins} admin{(admins == 1 ? "" : "s")}. Give more people roles on People & access, or through Entra app roles ({mappings} role mapping{(mappings == 1 ? "" : "s")}).",
                Link: "admin/people"),
            new("database", "Database", sqlServer,
                sqlServer ? "Azure SQL / SQL Server, managed by migrations." : "SQLite: fine for trying it out, not for App Service. Set Database__Provider=SqlServer and ConnectionStrings__Default."),
            new("keyvault", "Key Vault for secrets", keyVault,
                keyVault ? $"Secrets are kept in {configuration["KeyVault:Uri"]} and read with the app's managed identity."
                         : "Not configured, so credentials can't be saved from this page. Set KeyVault__Uri and give the app's identity Key Vault Secrets Officer."),
            new("github", "GitHub connection", credentials.IsConfigured,
                credentials.Mode switch
                {
                    "app" => "Connected as a GitHub App.",
                    "token" => "Connected with a personal access token. A GitHub App is recommended.",
                    _ => "Not connected. Add a GitHub App (recommended) or a token below.",
                }),
            new("orgs", "GitHub organizations", orgs > 0,
                orgs == 0 ? "None added yet. Add the organizations whose Copilot seats you manage." : $"{orgs} added, {syncedOrgs} synced.", Link: "admin"),
            new("enterprise", "GitHub enterprise (user budgets)", budgetStore.IsAvailable && !budgetStore.IsSimulated,
                budgetStore.IsAvailable && !budgetStore.IsSimulated
                    ? access is { CanWrite: true }
                        ? $"Budgets can be written to {configuration["GitHub:EnterpriseSlug"]}. Writing stays off until you switch it on."
                        : $"Budgets are read from {configuration["GitHub:EnterpriseSlug"]}, read-only. {access?.Reason}"
                    : "Optional. Needed to enforce budgets in GitHub (user-level budgets) and for cost centers.",
                Optional: true, Link: "admin/github"),
            new("graph", "Disabled-account check (Microsoft Graph)", directory.IsAvailable,
                directory.IsAvailable ? "Checks every few hours. Needs User.Read.All (application) with admin consent; failures show under Alerts."
                                      : "Optional. Needs Entra sign-in configured.",
                Optional: true, Link: "admin/people"),
            new("insights", "Application Insights", insights,
                insights ? "Telemetry, logs and alert metrics are being sent." : "Optional. Link an Application Insights resource to the web app.",
                Optional: true),
            new("email", "Email", email.IsConfigured,
                email.IsConfigured ? "Email is being sent." : "Optional. Sending email isn't implemented yet; notifications are only logged.",
                Optional: true),
        ];
    }

    /// <summary>12 characters from an unambiguous alphabet, e.g. K7QM-3XPD-W9TB (about 60 bits).</summary>
    private static string NewCode()
    {
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        var chars = new char[12];
        for (var i = 0; i < chars.Length; i++) chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        var s = new string(chars);
        return $"{s[..4]}-{s[4..8]}-{s[8..]}";
    }

    private static string Hash(string code)
    {
        var normalized = new string(code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}
