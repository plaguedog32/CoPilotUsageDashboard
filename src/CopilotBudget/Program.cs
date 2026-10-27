using System.Security.Claims;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using CopilotBudget.Components;
using CopilotBudget.Data;
using CopilotBudget.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using OpenTelemetry.Metrics;

var builder = WebApplication.CreateBuilder(args);

// Secrets (GitHub App key or token, Entra client secret, SQL connection string) can live in Azure Key Vault, read with
// the web app's managed identity. Secret names use "--" for ":", e.g. GitHub--AppPrivateKey.
if (builder.Configuration["KeyVault:Uri"] is { Length: > 0 } vaultUri)
    builder.Configuration.AddAzureKeyVault(new Uri(vaultUri), new DefaultAzureCredential());

builder.Services.Configure<AppOptions>(builder.Configuration.GetSection(AppOptions.Section));

// Demo sign-in and demo data are for local development only. Outside Development they're forced off, so a stray
// setting can't open "sign in as anyone" on a real deployment. (App:AllowDemoInProduction overrides, for a demo site.)
var demoRequested = builder.Configuration.GetValue<bool>("App:EnableDemoLogin") || builder.Configuration.GetValue<bool>("App:SeedDemoData");
var demoAllowed = builder.Environment.IsDevelopment() || builder.Configuration.GetValue<bool>("App:AllowDemoInProduction");
builder.Services.PostConfigure<AppOptions>(o =>
{
    if (demoAllowed) return;
    o.EnableDemoLogin = false;
    o.SeedDemoData = false;
});
builder.Services.Configure<GitHubOptions>(builder.Configuration.GetSection(GitHubOptions.Section));
var gitHubOptions = builder.Configuration.GetSection(GitHubOptions.Section).Get<GitHubOptions>() ?? new();
var entraSection = builder.Configuration.GetSection("AzureAd");
var entraConfigured = !string.IsNullOrWhiteSpace(entraSection["ClientId"]) && !string.IsNullOrWhiteSpace(entraSection["TenantId"]);

// Telemetry: requests, dependencies (GitHub, Graph, SQL), logs and the app's own metrics go to Application Insights
// when a connection string is set (APPLICATIONINSIGHTS_CONNECTION_STRING, which App Service sets for you).
var telemetry = builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter(Telemetry.Name));
if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
    telemetry.UseAzureMonitor();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// SQLite for local development; Azure SQL (SqlServer) in Azure. Azure SQL can use the managed identity with
// "Authentication=Active Directory Default" in the connection string. Each provider has its own migrations.
builder.Services.AddSingleton<IDbContextFactory<AppDbContext>, AppDbContextFactory>();

// Outside API calls are logged (ApiCall table) and counted.
builder.Services.AddSingleton<ApiCallRecorder>();
builder.Services.AddHostedService<ApiCallWriter>();
void GitHubDefaults(HttpClient client)
{
    client.BaseAddress = new Uri(gitHubOptions.ApiBaseUrl);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("CopilotBudgetDashboard/1.0");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
}
builder.Services.AddHttpClient<GitHubCopilotClient>(GitHubDefaults)
    .AddHttpMessageHandler(sp => new ApiCallLoggingHandler(sp.GetRequiredService<ApiCallRecorder>(), "GitHub"));
builder.Services.AddHttpClient(GitHubCredentials.HttpClientName, GitHubDefaults)
    .AddHttpMessageHandler(sp => new ApiCallLoggingHandler(sp.GetRequiredService<ApiCallRecorder>(), "GitHub"));
builder.Services.AddHttpClient(GraphDirectoryClient.HttpClientName, c => c.BaseAddress = new Uri("https://graph.microsoft.com/"))
    .AddHttpMessageHandler(sp => new ApiCallLoggingHandler(sp.GetRequiredService<ApiCallRecorder>(), "Graph"));
builder.Services.AddSingleton<GitHubCredentials>();

builder.Services.AddSingleton<ChangeNotifier>();
builder.Services.AddSingleton<LiveStatus>();
builder.Services.AddScoped<AuditLog>();
builder.Services.AddScoped<AlertService>();
builder.Services.AddScoped<BudgetService>();
builder.Services.AddScoped<BudgetChangeService>();
builder.Services.AddScoped<TrendService>();
builder.Services.AddScoped<PoolService>();
builder.Services.AddScoped<IdentityLinkService>();
builder.Services.AddScoped<RoleMappingService>();
builder.Services.AddScoped<DepartureService>();
builder.Services.AddScoped<CredentialService>();
builder.Services.AddScoped<OrgSyncService>();
builder.Services.AddScoped<UsageBreakdownService>();
builder.Services.AddScoped<MonthEndService>();
builder.Services.AddScoped<ExpiryWatch>();
builder.Services.AddScoped<SetupService>();
builder.Services.AddScoped<GitHubWriteBackService>();
builder.Services.AddScoped<Notifications>();
builder.Services.AddScoped<UserSession>();

// Email is a hook only: swap NotImplementedEmailSender for a real sender when one is chosen.
builder.Services.AddSingleton<IEmailSender, NotImplementedEmailSender>();

// Disabled or deleted Entra accounts are found through Microsoft Graph.
if (entraConfigured) builder.Services.AddSingleton<IDirectoryClient, GraphDirectoryClient>();
else builder.Services.AddSingleton<IDirectoryClient, NoDirectoryClient>();

// User-level budgets live on the GitHub enterprise (connected once credentials and the enterprise slug are set, even
// later from the Setup page). The local demo gets a simulated GitHub instead, to show the flow.
var seedDemo = demoAllowed && builder.Configuration.GetValue<bool>("App:SeedDemoData");
if (seedDemo && !(gitHubOptions.HasCredentials && gitHubOptions.HasEnterprise))
    builder.Services.AddSingleton<IGitHubBudgetStore, SimulatedBudgetStore>();
else
    builder.Services.AddSingleton<IGitHubBudgetStore, EnterpriseBudgetStore>();

builder.Services.AddHostedService<OrgSyncBackgroundService>();
builder.Services.AddHostedService<GitHubWriteBackBackgroundService>();
builder.Services.AddHostedService<HousekeepingBackgroundService>();
builder.Services.AddHostedService<DirectoryCheckBackgroundService>();
builder.Services.AddHealthChecks();

// Sign-in: Microsoft Entra ID (OpenID Connect) into a cookie. Demo sign-in (development only) uses the same cookie.
var auth = builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme);
if (entraConfigured)
{
    auth.AddMicrosoftIdentityWebApp(entraSection, OpenIdConnectDefaults.AuthenticationScheme, CookieAuthenticationDefaults.AuthenticationScheme);
    builder.Services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o =>
    {
        var previous = o.Events.OnTokenValidated;
        o.Events.OnTokenValidated = async ctx =>
        {
            await previous(ctx);
            var principal = ctx.Principal!;
            var objectId = principal.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier")
                           ?? principal.FindFirstValue("oid");
            if (objectId is null)
            {
                await SignInFailedAsync(ctx.HttpContext, "The Microsoft account has no object ID.");
                ctx.Fail("The Microsoft account has no object ID.");
                return;
            }
            var email = principal.FindFirstValue(ClaimTypes.Email) ?? principal.FindFirstValue("email")
                        ?? principal.FindFirstValue("preferred_username") ?? principal.FindFirstValue(ClaimTypes.Upn);
            var name = principal.FindFirstValue("name");

            // App roles and groups from the token become roles here through the admin's role mappings.
            var appRoles = principal.FindAll("roles").Concat(principal.FindAll(ClaimTypes.Role)).Select(c => c.Value).Distinct().ToList();
            var groups = principal.FindAll("groups").Select(c => c.Value).Distinct().ToList();
            var log = ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("SignIn");
            if (principal.HasClaim(c => c.Type is "hasgroups" or "_claim_names"))
                log.LogWarning("{Email} is in too many groups for the token to list them; use app roles (or assign groups to the app) for role mappings", email);

            // Match the Microsoft account to a GitHub user by email (or remember it until one turns up).
            var linker = ctx.HttpContext.RequestServices.GetRequiredService<IdentityLinkService>();
            var user = await linker.SignInAsync(objectId, email, name, appRoles, groups);
            if (user.IsDisabled)
            {
                await SignInFailedAsync(ctx.HttpContext, $"{email ?? objectId} tried to sign in, but is marked as left ({user.DisabledReason}).");
                ctx.Fail("This account has been marked as left.");
                return;
            }
            ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(UserSession.UserIdClaim, user.Id.ToString()));
        };
        o.Events.OnRemoteFailure = async ctx =>
        {
            await SignInFailedAsync(ctx.HttpContext, ctx.Failure?.Message ?? "Unknown sign-in failure");
            ctx.Response.Redirect("/account/login?error=signin");
            ctx.HandleResponse();
        };
    });
}
else
{
    auth.AddCookie();
}
builder.Services.Configure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, o =>
{
    o.LoginPath = "/account/login";
    o.LogoutPath = "/account/logout";
    o.AccessDeniedPath = "/account/login";
    o.ExpireTimeSpan = TimeSpan.FromHours(12);
    o.SlidingExpiration = true;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

// Azure App Service terminates TLS in front of the app; trust its forwarded headers so redirects use https.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

var app = builder.Build();

await InitializeDatabaseAsync(app);
if (demoRequested && !demoAllowed)
    app.Logger.LogWarning("App:EnableDemoLogin / App:SeedDemoData are set but ignored outside Development.");
// While nobody is an admin, write a one-time setup code to the log for the first person to claim admin at /setup.
await SetupService.IssueCodeIfNeededAsync(app.Services);

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapHealthChecks("/healthz");
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapGet("/account/microsoft-login", (string? returnUrl) =>
    entraConfigured
        ? Results.Challenge(new AuthenticationProperties { RedirectUri = UrlHelpers.SafeReturnUrl(returnUrl) },
            [OpenIdConnectDefaults.AuthenticationScheme])
        : Results.Redirect("/account/login"));

app.MapPost("/account/logout", async (HttpContext ctx) =>
{
    // Signs out of this app only; the Microsoft session stays, so signing back in is one click.
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/account/login");
});

app.Run();

// A failed or refused sign-in: logged, counted, and kept as one open alert that counts repeats.
static async Task SignInFailedAsync(HttpContext http, string reason)
{
    Telemetry.SignInFailures.Add(1);
    http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("SignIn").LogWarning("Sign-in failed: {Reason}", reason);
    await http.RequestServices.GetRequiredService<AlertService>()
        .RaiseAsync("signin-failures", "sign-in", CopilotBudget.Data.AlertSeverity.Warning, "Sign-ins are failing", reason);
}

static async Task InitializeDatabaseAsync(WebApplication app)
{
    var factory = app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
    var appOptions = app.Services.GetRequiredService<IOptions<AppOptions>>().Value;
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Database");

    // Migrations bring the schema up to date (creating the database if needed).
    await DatabaseSetup.MigrateAsync(factory, logger);

    await using var db = await factory.CreateDbContextAsync();
    await RoleMappingService.SeedDefaultsAsync(db);
    if (appOptions.SeedDemoData) await DemoSeeder.SeedAsync(db);

    foreach (var login in appOptions.Organizations.Where(l => !string.IsNullOrWhiteSpace(l)))
    {
        if (!await db.Organizations.AnyAsync(o => o.Login == login))
            db.Organizations.Add(new Organization { Login = login.Trim() });
    }
    await db.SaveChangesAsync();
}
