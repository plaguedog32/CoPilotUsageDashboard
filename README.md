<img src="assets/icon.svg" width="64" height="64" alt="">

# Copilot Budget Dashboard

A dark-themed ASP.NET Core Blazor (interactive server-side rendering) dashboard for GitHub Copilot.
It tracks Copilot seats and AI credit usage for the GitHub organizations you choose, gives every
seat holder a monthly budget of credits, lets people **request** and **send** credits to each other, and
keeps each person's **user-level budget in GitHub** (their hard stop) in step with the app.

Organizations decide who has a Copilot seat, who can manage settings, and how many credits their seat
holders get each month. Everyone across all tracked organizations is one shared group of people: anyone
can ask everyone for credits, and anyone can fill a request or send credits to anyone else. People only ever see one
company-wide budget: GitHub organization names and per-org breakdowns are shown only to org owners, admins,
integration admins and auditors.

## What it does

| Page | What you can do |
|---|---|
| **Dashboard** | Your available budget and usage bar; open credit requests from other people, least left first, with a *Fill* button; recent activity you were part of. People who oversee budgets also see company totals, a month-end forecast (projected use, credits that will expire unused, pace difference, demand beyond budgets) and a warning when unallocated credits are about to expire |
| **Requests** | Three tabs. *My requests*: your requests, then a form to ask everyone for credits. *Requests from others*: fill them (a dialog shows both balances before and after), sorted by who has the least left; admins can decline. *Send credits*: send part of your remaining credits to anyone, sorted from least left to most (`/send` links open this tab) |
| **Credit pools** | Prepaid credits that don't expire: personal reserves for one person and shared pools for a group. Members take credits when they need them; auto-cover handles going over budget |
| **Members** | Everyone across all orgs by full name: last activity, budget, usage, projected run-out date and status (see below); sortable by any column; Send opens a quick dialog; admins can move credits and change budgets |
| **Usage breakdown** | People who oversee budgets: credits by day (stacked by model), by model, SKU, product and cost center, for any month and org. Everyone else: each model's relative share of usage per day, without credit amounts or cost centers |
| **Trends** | People who oversee budgets only: credits used per month by org (line chart) and by person (heatmap table), each person's usage against their budget, over 3 to 24 months |
| **Setup** | Admins: a checklist of what's configured, claiming admin on a new install, and saving GitHub credentials to Key Vault (see [docs/SETUP.md](docs/SETUP.md)) |
| **History** | A ledger of every budget change (yours, or everyone's for admins). Both rows of a transfer share a transfer ID; click it to see both sides |
| **Approvals** | When approval mode is on: approvers sign off or reject transfers; everyone sees and can cancel their own pending transfers |
| **Admin** | Transfer rules (approval threshold, minimum to keep), the budget-authority switch, and one card per org: set its monthly budget per member (with a spend preview), sync with GitHub, remove it. Add any org GitHub can see from a list |
| **People & access** | Roles per person (assigned here, or from Entra), Entra app role/group → role mappings, Microsoft↔GitHub links, work emails, and people who left (with one-click reassignment) |
| **Integration** | GitHub App or token status, where the app is installed, a connection test, credential rotation in Key Vault, and the log of every GitHub and Microsoft Graph call |
| **Alerts** | Failed sign-ins, stale or partly failed syncs, and failed or unreconciled GitHub budget writes |
| **Audit log** | Every admin action, searchable and exportable to CSV |
| **Budgets** | Per-person monthly budgets (overrides of the org default), temporary exceptions with start and end dates, and budget increases waiting for a budget authority |
| **GitHub sync** | Write-back of user-level budgets to GitHub: the off switch, each person's state (in sync, waiting, reconciliation required, failed), reconciliation actions, and a log of every read, write and check |
| **Month-end close** | Per person: budget, received, sent, adjustments, pool credits, used, expired unused and over budget, in credits and dollars. Export to CSV. Emailed automatically when each month ends to all admins and/or the people admins choose on the page |

Every page updates live. Blazor Server keeps a SignalR connection open to each browser, and every change (a
transfer, a budget change, a sync from GitHub) pushes fresh numbers to everyone who has the site open. The
**Live** dot in the top bar pulses when that happens. The **Credits / $** switch in the top bar shows every amount
in credits or dollars (`App:DollarsPerCredit`, default $0.01); it's saved on each person's account, so it follows
them across devices. Wherever names appear, the signed-in person is shown as a highlighted **You**.

### Statuses and usage bars

Usage bars show the share of the budget **left** (solid) against what's used (hatched), with a pale marker for the
share of the month left until the reset and a countdown to it. Hover a bar for the pace in words. Each person gets
one status:

| Status | Meaning |
|---|---|
| **Over budget** | Used more than their budget |
| **Running low** | Used 85% or more |
| **Ahead of pace** | Using credits faster than an even spend; on track to run out before the reset |
| **N% unused** | On track to use less than 80% of their budget, so about N% would expire unused. Not shown in the first days of the month. These are the credits worth sending to someone running low |
| **On pace** | On track to use 80–100% of their budget |

### How budgets work

- Budgets belong to people, not organizations. At the start of each month, every Copilot seat holder
  gets their organization's **monthly budget** (set per org on the Admin page).
- Someone with seats in more than one org gets the **largest** of those orgs' budgets, not the total,
  because GitHub bills one Copilot seat per person.
- The org budget is the default. Admins (and owners of the person's org) can **override** it for one person,
  and add **temporary exceptions**: extra (or fewer) credits a month between two dates. Exceptions are added
  on the start date and taken off again after the end date.
- Changes apply to the current month straight away. Transfers and adjustments already made are kept.
- **Usage** is pulled from GitHub and added up across a person's orgs. **Available = budget − usage.**
- Sending credits, or filling someone's request, moves part of *your* available budget to them for the
  rest of the month. You can't send more than you have, minus the **minimum to keep** if an admin set one.
- Requests, held transfers and budget proposals still pending when the month ends **expire**.
- Every change is a row in a ledger, so the History page is a full audit trail.

### Budget authority and spend previews

- Any admin or org owner can propose a change to budgets: an org's monthly budget, a person's monthly budget,
  an exception, or a one-off adjustment. Each shows a **preview** first: this month's total before and after,
  the change to every later month, the cost of an exception over its whole length, the dollar amount, and who
  is affected.
- Changes that **raise total spend** wait for a **budget authority** (a role set on the Admin page) unless the
  person making them is one. Nothing changes until it's approved. Lowering spend applies straight away.
  Admins can switch this rule off.

### Usage history

- Usage is stored per person, per org seat, per month, and is never deleted, so the Trends page can show
  how each org and each person's usage changes over time.
- Each sync refreshes this month and last month, and backfills any older month in the history window
  (`GitHub:UsageHistoryMonths`, default 12, up to the 24 months GitHub keeps) that hasn't been fetched yet.
  Backfill is capped per sync (`GitHub:MaxBackfillCallsPerSync`) and carries on at the next sync.
- Budgets for past months only exist for months the app was running. Older months show usage without a budget.

### Writing budgets to GitHub

GitHub has no API to move AI credits between people, but GitHub Enterprise Cloud has **user-level budgets**: a
dollar cap per person that stops their usage when reached. The app keeps each person's user-level budget equal to
their budget in the app (credits × `App:DollarsPerCredit`, rounded **down** to whole dollars, which is all GitHub
accepts), so sending credits or raising a budget changes what GitHub lets them use.

- **Read before write.** Every pass lists the enterprise's user budgets first, and each budget is read again right
  before it's written.
- **Only overwrite our own value.** If GitHub holds anything other than the value the app last wrote (someone
  changed it in GitHub, deleted it, or a budget existed before the app was connected), that person goes to
  **reconciliation required** and nothing is written for them until an admin decides: *Use app value*
  (overwrite GitHub) or *Match GitHub* (change the app's budget to match, through the normal approval rules).
- **Check afterwards.** After every write the budget is read back. If it doesn't match, it's flagged for
  reconciliation.
- **Off switch.** Write-back is **off by default**. While off, the app still reads GitHub and shows who would
  change. Admins switch it on and off on the GitHub sync page.
- Checks run every `GitHub:WriteBackIntervalSeconds` (default 120) and a few seconds after any change in the app.
  Every read, write, check and decision is logged with a run ID.
- Admins are told (through the email hook) when people need reconciling.
- Without an enterprise configured, the demo uses a **simulated GitHub** so you can try the whole flow, including
  a pre-existing budget (Barbara) and a *Simulate edit* button that pretends someone changed a budget in GitHub.

> Check `GitHub:BudgetProductSku` and `GitHub:BudgetType` against a budget you create by hand in your enterprise
> before switching write-back on: GitHub's docs don't pin down the SKU name for Copilot AI credits.

## Quick start (demo mode, no GitHub needed)

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
cd src/CopilotBudget
dotnet run --launch-profile http
```

Open http://localhost:5121 and pick a demo user. `mona` is an admin. You can also simulate a Microsoft
sign-in with a work email (demo people have `<login>@contoso.com`) to try the email matching and linking. The Development settings
(`appsettings.Development.json`) seed two fake organizations, **Contoso (demo)** and
**Fabrikam (demo)**, with 61 people (11 named characters, one per role, plus 50 generated people with a realistic
spread of usage) and 12 months of usage history, and turn on "sign in as" so you can try every feature. `grace` is in both.

## Connecting to real GitHub organizations

### 1. Connect to GitHub: a GitHub App (recommended) or a token

**GitHub App.** Nothing belongs to one person, permissions are limited to what the app needs, and tokens last an hour.

1. Settings → Developer settings → GitHub Apps → New GitHub App (in the organization or enterprise). No webhook or
   callback URL is needed.
2. Organization permissions: *GitHub Copilot Business* read, *Members* read, *Administration* read (usage). For user
   budgets and cost centers the app must also be installed on the **enterprise** with enterprise billing permission
   (*Enterprise administration* write). Check GitHub's current docs for which enterprise billing endpoints accept
   GitHub App tokens in your plan; if yours don't, use a token for the enterprise calls.
3. Generate a private key and note the App ID. Install the app on each organization (and the enterprise).
4. Set `GitHub:AppId` and `GitHub:AppPrivateKey` (in Key Vault: `GitHub--AppId`, `GitHub--AppPrivateKey`).

The app signs a short-lived JWT with the private key, exchanges it for an installation token per organization or
enterprise, caches it until shortly before it expires, and lists the orgs it's installed on for the Admin page.
Integration admins can rotate the key on the Integration page when Key Vault is configured.

**Personal access token.** Simpler, but tied to one person (it breaks when they leave). Classic PAT with
`manage_billing:copilot` and `read:org` (plus `manage_billing:enterprise` for user budgets); the owner must be an
owner or billing manager of each organization. Set `GitHub:Token`. If both are set, the GitHub App is used.

### 2. Microsoft Entra ID for sign-in

People sign in with their Microsoft work account. Register an app in Entra ID (App registrations → New):

- Redirect URI (Web): `https://<your-app>.azurewebsites.net/signin-oidc` (add `https://localhost:7xxx/signin-oidc`
  for local testing)
- Create a client secret (or use a certificate), and note the tenant ID and client (application) ID.
- Optional: under *Token configuration*, add the `email` optional claim. The app falls back to
  `preferred_username` (the UPN), which is usually the work email.

### 3. How Microsoft accounts are matched to GitHub users

Matching is by work email:

1. During each sync, the app reads work emails from the SAML/SCIM identities GitHub keeps for the org (or for
   the enterprise, if `GitHub:EnterpriseSlug` is set) when SSO with Entra is turned on. This needs an org owner
   token.
2. When someone signs in, their Entra email is matched (case-insensitively) to a GitHub user with that email,
   and the link is remembered by their Entra object ID.
3. If nothing matches yet, they still get in to view the dashboards, but can't give or request credits. They
   appear under **Sign-ins to link** on the Admin page, where an admin can pick their GitHub user, or type the
   GitHub user's work email in the *People & roles* list. They're linked automatically as soon as a matching
   email turns up.

### 4. Configure

Keep secrets out of source control. For local development, use user secrets:

```bash
cd src/CopilotBudget
dotnet user-secrets init
dotnet user-secrets set "GitHub:Token" "ghp_..."
dotnet user-secrets set "AzureAd:TenantId" "<tenant-guid>"
dotnet user-secrets set "AzureAd:ClientId" "<client-guid>"
dotnet user-secrets set "AzureAd:ClientSecret" "..."
dotnet user-secrets set "App:Admins:0" "you@company.com"
```

| Setting | Meaning |
|---|---|
| `AzureAd:TenantId` / `ClientId` / `ClientSecret` | Entra app registration for "Sign in with Microsoft" |
| `GitHub:AppId` / `GitHub:AppPrivateKey` | Connect as a GitHub App (recommended) |
| `GitHub:Token` | Or connect with a personal access token |
| `GitHub:EnterpriseSlug` | Your GitHub enterprise. Needed for writing user budgets and reading cost centers; also reads SSO emails from the enterprise |
| `GitHub:SyncSsoEmails` | Read work emails from GitHub SSO to link sign-ins (default true) |
| `GitHub:SyncIntervalMinutes` | How often to pull seats and usage from GitHub (default 10, minimum 2). Each sync makes about two usage calls per seat, so allow for GitHub's 5,000 requests an hour |
| `GitHub:WriteBackIntervalSeconds` | How often user budgets are checked against GitHub (default 120) |
| `GitHub:BudgetProductSku` / `BudgetType` | What the user budgets the app creates cover (defaults `copilot`, `ProductPricing`) |
| `GitHub:SyncDailyUsage` | Pull each org's usage per day for this month and last (default true) |
| `GitHub:SyncCostCenters` | Read enterprise cost centers for usage by cost center (default true) |
| `GitHub:SyncCreditUsage` | Pull per-user AI credit usage (needs the enhanced billing platform) |
| `GitHub:UsageHistoryMonths` | Months of usage history to keep filled in, including this month (default 12, max 24) |
| `GitHub:MaxBackfillCallsPerSync` | Most history backfill calls per org per sync, to stay under the rate limit (default 500) |
| `App:Admins` | GitHub logins or work emails that are always admins (use an email to bootstrap the first admin) |
| `App:Organizations` | Orgs to start tracking on startup (optional: the Admin page lists every org the token can see) |
| `App:UnitLabel` | What budget units are called in the UI (default "credits") |
| `App:DollarsPerCredit` | Price of one credit, for the $ view, reports and GitHub budgets (default 0.01) |
| `App:ApiLogRetentionDays` | Days of GitHub/Graph call log to keep (default 30) |
| `Directory:CheckAccounts` / `CheckIntervalHours` | Check Entra for disabled accounts (default on when Entra is configured, every 6 hours) |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Send telemetry to Application Insights (App Service sets this when you link one) |
| `Database:Provider` | `Sqlite` (default, local) or `SqlServer` (Azure SQL) |
| `ConnectionStrings:Default` | Database connection string |
| `KeyVault:Uri` | Load secrets from Azure Key Vault with the app's managed identity (optional) |

## Deploying to Azure (App Service web app)

**Step-by-step guide: [docs/SETUP.md](docs/SETUP.md).** It covers the Azure resources, permissions, the Entra app
registration, deployment, claiming admin with the one-time setup code, and saving GitHub credentials to Key Vault from
the app's Setup page. In short:

1. **Database:** create an Azure SQL database. Set `Database__Provider=SqlServer` and
   `ConnectionStrings__Default` (as a connection string of type SQLAzure). With the web app's managed identity
   granted access to the database, use
   `Server=tcp:<server>.database.windows.net;Database=<db>;Authentication=Active Directory Default;Encrypt=True`.
   Tables are created on first start. (SQLite works on App Service only if the file is under `/home/data`, and
   isn't recommended.)
2. **Web app:** .NET 10, **Web sockets on** (Blazor Server needs them), **ARR affinity on**, **one instance**
   (transfers are serialized in-process; see Notes and limits). Health check path: `/healthz`.
3. **Secrets:** put `AzureAd--ClientSecret` in Key Vault, give the web app's managed identity *Key Vault Secrets
   Officer*, and set `KeyVault__Uri`. The GitHub credentials are entered by the first admin on the Setup page, which
   saves them to Key Vault (`GitHub--AppId`, `GitHub--AppPrivateKey` or `GitHub--Token`, `GitHub--EnterpriseSlug`).
4. **Graph:** grant the app registration (or the managed identity) Microsoft Graph *User.Read.All* (application, admin
   consent) for the disabled-account check.
5. **Application Insights:** create one and link it to the web app (this sets `APPLICATIONINSIGHTS_CONNECTION_STRING`).
   See *Monitoring* below for alert rules.
6. **App settings:** `AzureAd__TenantId`, `AzureAd__ClientId`. Demo sign-in and demo data are forced off outside
   the Development environment, whatever the settings say.
7. **First admin:** after deploying, copy the one-time `FIRST-RUN SETUP` code from the web app's Log stream, sign in,
   and enter it on the Setup page. (Or assign yourself the `CopilotBudget.Admin` app role, or set `App__Admins__0`.)
8. **Deploy:** run the *Deploy to Azure Web App* GitHub Actions workflow (`.github/workflows/deploy-azure-webapp.yml`)
   after setting its repository variables, or use `dotnet publish` and zip deploy.

### Roles

Anyone in your Entra tenant can sign in and view the dashboards. Sending and requesting credits needs a Copilot
seat (and a linked GitHub user). On top of that:

| Role | Can |
|---|---|
| **Org owner** (synced from GitHub) | Set their org's monthly budget, sync it, change budgets for people in their org, decline requests |
| **Admin** | Everything: every org and person, roles and role mappings, settings, month-end report. People in `App:Admins` are always admins |
| **Approver** | Approve or reject transfers when approval is on (never their own) |
| **Budget authority** | Approve changes that raise total spend (never their own proposals) |
| **Budget visibility** | Read-only: everyone's budgets, history, the Budgets page and month-end reports |
| **Integration admin** | GitHub credentials (rotation in Key Vault), adding/removing/syncing orgs, write-back switch and reconciliation, Microsoft↔GitHub links, work emails, alerts, API log. **Not** budgets or roles |
| **Auditor** | Read-only access to everything, including the audit log, API call log, alerts and GitHub sync |

Roles can be assigned on the People & access page, or come from **Entra**:

- In the app registration, add **App roles** (the app suggests `CopilotBudget.Admin`, `CopilotBudget.Approver`,
  `CopilotBudget.BudgetAuthority`, `CopilotBudget.BudgetVisibility`, `CopilotBudget.IntegrationAdmin`,
  `CopilotBudget.Auditor`, and maps them by default), then assign people or groups to them under
  *Enterprise applications → Users and groups*.
- Or map **security groups**: add the *groups* claim to the token (Token configuration) and map each group's object ID
  to a role. App roles are better for large tenants: tokens stop listing groups past 200 memberships.
- Admins manage the mappings on People & access (the page suggests values it has seen in recent sign-in tokens).
  Roles from Entra are refreshed at every sign-in, so removing someone from a group takes effect when they next sign in,
  and show as *Entra* in the roles list. Only admins can change mappings, since a mapping can grant Admin.

### People who leave

- Every few hours (`Directory:CheckIntervalHours`, default 6) the app asks **Microsoft Graph** whether each linked
  Entra account is still enabled. This needs the **User.Read.All** application permission (admin consent) for the
  app registration, or for the web app's managed identity when there's no client secret. Admins can also mark someone
  as left by hand, and an account enabled again in Entra is restored automatically.
- Someone who leaves: gets no new credits (no monthly budget, no transfers to them, no pool auto-cover); is hidden from
  everyday lists (people who manage or oversee others still see them, marked *left*); can't sign in; has their open
  requests, transfers waiting for approval, budget proposals and exceptions cancelled (a request they were filling
  reopens for others); and their GitHub budget falls to what they used.
- Admins get an email (through the hook) and see them at the top of **People & access**, with what they still hold:
  credits left this month, personal reserves, shared pools, roles, and any Copilot seat still assigned in GitHub.
  **Reassign everything** moves the credits and reserves to one person (or returns the credits and archives the
  reserves), removes them from shared pools, and clears their roles, in one audited step.

### Matching GitHub users

People are matched by **GitHub's numeric user ID**, not their login, so a renamed GitHub account keeps its history:
the login is updated and the rename is recorded in the audit log. (Logins are only used for people seen before their
ID was known.) GitHub budgets are matched by budget ID first for the same reason.

### Credit pools (prepaid, non-expiring credits)

GitHub has no per-person prepaid credits (included AI credits are pooled across the billing account and reset
monthly), so the app tracks them itself:

- A **personal reserve** belongs to one person; a **shared pool** feeds a group. App admins create them, add or
  remove credits (with a note such as a PO number), change members, and archive them.
- Credits stay in the pool until they're used. Members can **take credits** into this month's budget, and with
  **auto-cover** on, the app moves exactly what's needed whenever a member goes over their monthly budget
  (personal reserves first, then shared pools). Anything taken expires at the reset like the rest of the budget,
  so auto-cover means nothing is taken early.
- Shared pools can have a **monthly top-up**: the pool is topped back up to that amount on the 1st.
- Taking credits from a pool isn't a transfer between people, so it doesn't need approval.
- To enforce limits in GitHub too, mirror them with GitHub's budgets (per user or per cost center) in your
  enterprise billing settings.

### Transfer rules

- **Approval:** with approval on, transfers (or only those of at least the **threshold** you set) don't move
  credits straight away. The amount is held from the sender's available budget and waits on the Approvals page.
  Approving moves the credits; rejecting, the sender cancelling, or the month ending releases the hold, and a
  filled request goes back to open.
- **Minimum to keep:** senders must keep at least this many credits for themselves.
- **Admin moves:** app admins can move unused credits from one person to another (Members → *Move credits*). It
  happens straight away, skipping approval and the minimum to keep, needs a reason that both people are told, and is
  recorded in the audit log. Total spend doesn't change, so no budget authority is needed.
- Both sides of a transfer are two ledger rows sharing one **transfer ID**.

### Notifications

Sending email is a hook that isn't implemented yet: `IEmailSender` in `Services/Email.cs`, registered as
`NotImplementedEmailSender`, which only logs. The app already calls it for credits received, transfers waiting for
approval and their outcome, filled requests, budget increases waiting for a budget authority and their outcome,
GitHub budgets needing reconciliation, and the month-end close report (with the CSV attached). Plug in a real
sender (Azure Communication Services, SendGrid, Microsoft Graph...) in `Program.cs`.

## Logging, alerts and audit

- **API call log:** every call to GitHub and Microsoft Graph is recorded (method, path, status, time taken, GitHub's
  remaining rate limit, and the start of any error body; never headers or tokens), kept for `App:ApiLogRetentionDays`,
  and shown on the Integration page.
- **Alerts** (Alerts page, emailed to admins and integration admins through the hook, logged at warning/error level):
  - sign-in failures, including someone marked as left trying to sign in;
  - an organization that hasn't synced successfully for three sync intervals (at least 30 minutes), a failed sync,
    or a sync that only partly worked (e.g. usage or SSO emails unreadable);
  - GitHub budget writes that failed, or budgets that need reconciling, and failures to read GitHub budgets or Entra
    accounts.
  - credits about to expire: in the last `App:ExpiryWarningDays` days of the month (default 7), how many credits are on
    track to go unused, who could use them, and who has the most to spare; and any time, credits still held by people
    who left. These go to admins and budget authorities, and also show as a banner on their dashboard.

  Repeats update the same alert, and alerts close themselves when the problem clears.
- **Audit log:** every admin action (settings, roles and mappings, budget changes and their approval, transfer
  approvals, declined requests, pools, organizations, account links, emails and cost centers, people leaving and
  reassignments, credentials, write-back, alert handling, report exports and emails) with who, what and before/after
  details. The app only ever adds entries. For tamper-evidence beyond the app, use SQL Server ledger tables or stream
  the logs (each entry is also logged) to Log Analytics.

### Monitoring with Application Insights

With `APPLICATIONINSIGHTS_CONNECTION_STRING` set, requests, dependencies (GitHub, Graph, SQL), logs and these metrics
are sent through OpenTelemetry: `copilotbudget.api.calls`, `copilotbudget.api.failures`,
`copilotbudget.signin.failures`, `copilotbudget.alerts.raised` (with `kind` and `severity`), and
`copilotbudget.writeback.failures`. Example Azure Monitor alert rules (log search):

```kusto
// Any new critical alert in the app
traces | where message startswith "Alert " and severityLevel >= 3
// Sign-in failures in the last 15 minutes
customMetrics | where name == "copilotbudget.signin.failures" | summarize sum(value) by bin(timestamp, 15m)
// Failed GitHub calls
dependencies | where target has "api.github.com" and success == false
```

## Database migrations

The schema is managed with EF Core migrations, one set per provider: `Migrations/Sqlite` and
`Migrations/SqlServer`. The app applies pending migrations at startup. After changing the model:

```bash
cd src/CopilotBudget
dotnet tool restore
dotnet ef migrations add <Name> --context SqliteAppDbContext --output-dir Migrations/Sqlite
dotnet ef migrations add <Name> --context SqlServerAppDbContext --output-dir Migrations/SqlServer
```

To apply them before deploying instead (e.g. from a pipeline), use
`dotnet ef database update --context SqlServerAppDbContext --connection "<connection string>"`, or generate a script
with `dotnet ef migrations script --idempotent --context SqlServerAppDbContext`. A local SQLite database created before
migrations existed is moved aside and recreated; a SQL Server database in that state stops the app, so back it up and
recreate it (or baseline it by inserting `InitialCreate` into `__EFMigrationsHistory`).

## GitHub APIs used

- `GET /orgs/{org}/copilot/billing`: plan and seat summary
- `GET /orgs/{org}/copilot/billing/seats`: seat holders and last activity
- `GET /user/orgs` (token) or `GET /app/installations` (GitHub App): organizations to offer on the Admin page
- `POST /app/installations/{id}/access_tokens`: GitHub App installation tokens
- `GET /orgs/{org}/members?role=admin`: org owners
- `GET /organizations/{org}/settings/billing/ai_credit/usage?user=&year=&month=`: per-user AI credit
  usage, by model and SKU (`grossQuantity`, `grossAmount`). If an org isn't on the enhanced billing platform,
  this call fails; the sync still succeeds without usage and shows a warning on the Admin page.
- `GET /organizations/{org}/settings/billing/ai_credit/usage?year=&month=&day=`: an org's usage for one day,
  by model and SKU
- `GET/POST/PATCH /enterprises/{enterprise}/settings/billing/budgets`: user-level budgets (write-back)
- `GET /enterprises/{enterprise}/settings/billing/cost-centers`: cost center membership

## Project layout

```
src/CopilotBudget/
  Program.cs                 auth, DI, database startup
  Data/                      EF Core entities, DbContext (SQLite or SQL Server) and migrations
  Services/
    BudgetService.cs         allocation, transfer and request rules (all permission checks live here)
    BudgetChangeService.cs   org budgets, overrides, exceptions, adjustments, spend previews, budget authority
    GitHubWriteBackService.cs  read-before-write, verify, reconciliation, off switch, operation log
    GitHubBudgetStore.cs     GitHub enterprise budgets, or a simulated copy for the demo
    OrgSyncService.cs        GitHub → database sync + background sync service
    UsageBreakdownService.cs usage by day, model, SKU and cost center
    MonthEndService.cs       month-end close report, CSV, automatic email to admins at month end
    Forecast.cs              month-end forecast and run-out dates
    Email.cs                 email hook (not implemented) and who gets told what
    GitHubCopilotClient.cs   GitHub REST client
    TrendService.cs          monthly usage history for the Trends page
    UserSession.cs           signed-in user, their orgs and permissions, per browser tab
    SetupService.cs          first-run setup code, claiming admin, setup checklist
    CredentialService.cs     connection tests and saving GitHub credentials to Key Vault
    Pace.cs                  pace, run-out and "unused at the reset" maths behind statuses and bars
    DemoSeeder.cs            demo organizations
  Components/
    Pages/                   dashboard pages (all require sign-in)
    Account/Login.razor      sign-in page (static server rendering so it can set the cookie)
    Shared/                  charts (inline SVG), meter, badges, avatars, icons, page base class
  wwwroot/app.css            dark theme
```

## Notes and limits

- The schema is managed by EF Core migrations (see *Database migrations*).
- Transfers are serialized with an in-process lock, so run a **single instance**. To scale out, move
  to SQL Server/PostgreSQL and use database transactions with row locking.
- Budgets reset monthly (UTC). Unused credits don't carry over; the month-end close report shows what expired.
- GitHub budgets are whole dollars, so a budget of 1,950 credits becomes a $19 hard stop.
- Removing an organization keeps people's budget history. Their budget for the current month isn't
  taken back.
