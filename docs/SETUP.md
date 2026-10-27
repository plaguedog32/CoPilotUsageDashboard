# First-time setup on Azure

This guide takes you from nothing to a running Copilot Budget web app that you're the admin of. It takes about an
hour, most of it waiting for Azure. You need:

- **Azure:** Contributor (or Owner) on a resource group, and permission to assign roles in it
  (*Owner* or *User Access Administrator*) for the managed-identity steps.
- **Microsoft Entra ID:** permission to create an app registration. Granting Microsoft Graph admin consent needs a
  *Privileged Role Administrator*, *Cloud Application Administrator* or *Global Administrator*.
- **GitHub:** owner of each organization whose Copilot seats you'll manage, and an enterprise owner if you want budgets
  enforced in GitHub.

Demo sign-in and demo data never run in a deployed app. Outside the `Development` environment, `App__EnableDemoLogin`
and `App__SeedDemoData` are forced off even if someone sets them, and a warning is logged. App Service runs as
`Production` unless you change `ASPNETCORE_ENVIRONMENT`, so don't change it.

## What goes where

| What | Where it lives | Why |
|---|---|---|
| Entra client secret | Key Vault (`AzureAd--ClientSecret`) | Needed before anyone can sign in, so it's set during deployment |
| GitHub App ID and private key, or token | Key Vault, saved by the first admin from the app's **Setup** page | Saved through the app, never typed into Azure settings |
| GitHub enterprise slug | Key Vault, saved from the **Setup** page | |
| Tenant ID, client ID, Key Vault URI, database connection | App settings | Not secrets |
| Everything else (orgs, roles, budgets, rules) | The app's database | Changed in the app |

Secrets are never written to the app's database, never logged, and never shown back on screen. The app reads Key Vault
with its **managed identity**, so there's no Key Vault password anywhere.

## 1. Create the Azure resources

Create these in one resource group and region:

1. **App Service plan:** Linux, Basic B1 or higher. B1 is fine for a few hundred people.
2. **Web App:**
   - Runtime: **.NET 10**.
   - Configuration → General settings:
     - **Web sockets: On.** The app's pages live-update over a persistent connection.
     - **ARR affinity: On.**
     - **Always on: On.** Background syncing and the month-end close run inside the app.
     - **HTTPS only: On.**
   - Scale out: **1 instance.** Transfers are made safe within one instance; don't autoscale.
   - Monitoring → Health check path: `/healthz`.
   - Identity → **System assigned: On.** Note the object ID; it's the app's managed identity.
3. **Azure SQL Database:**
   - Use the serverless General Purpose tier, or Basic.
   - Under *Microsoft Entra authentication*, set yourself (or a group) as the Entra admin.
   - Turn on **Allow Azure services and resources to access this server** (or use a private endpoint).
4. **Key Vault:**
   - Permission model: **Azure role-based access control**.
   - Access control (IAM) → Add role assignment:
     - Give **Key Vault Secrets Officer** to the web app's managed identity. That lets the app save the GitHub
       credentials you enter on the Setup page.
       - If you'd rather the app could only read, give it *Key Vault Secrets User* and add the GitHub secrets
         yourself in the portal.
     - Give yourself **Key Vault Secrets Officer** too, so you can add the Entra secret in step 2.
5. **Application Insights** (recommended):
   - Create one (workspace-based).
   - Web App → Application Insights → Enable → select it. This sets `APPLICATIONINSIGHTS_CONNECTION_STRING`.

### Let the app use the database

Connect to the database as the Entra admin (Query editor in the portal, or Azure Data Studio) and run, using the web
app's name:

```sql
CREATE USER [<web-app-name>] FROM EXTERNAL PROVIDER;
ALTER ROLE db_owner ADD MEMBER [<web-app-name>];
```

`db_owner` lets the app create and upgrade its own tables on startup. If your policy doesn't allow that, use
`db_datareader`, `db_datawriter` and `db_ddladmin`.

## 2. Register the app in Microsoft Entra ID

Entra admin center → App registrations → **New registration**:

1. Name: `Copilot Budget`. Supported account types: **this organizational directory only**.
2. Redirect URI: platform **Web**, `https://<web-app-name>.azurewebsites.net/signin-oidc` (or your custom domain).
3. Note the **Application (client) ID** and **Directory (tenant) ID** from the Overview page.
4. Certificates & secrets → **New client secret**.
   - Copy the value straight into Key Vault: Key Vault → Secrets → Generate/Import, name **`AzureAd--ClientSecret`**.
   - Don't paste it anywhere else.
   - It expires; put a reminder in your calendar to rotate it (create a new one, update the Key Vault secret, restart
     the web app).
5. Token configuration:
   - Add optional claim **email** (ID token). Optional: the app falls back to the sign-in name.
   - To give roles by Entra group, also **Add groups claim** → Security groups → Group ID. Skip this if you'll use
     app roles (below), which is simpler.
6. App roles → **Create app role** for each role you want to hand out in Entra. Allowed member types: Users/Groups.
   The values must match exactly:

   | Value | Role |
   |---|---|
   | `CopilotBudget.Admin` | Admin: everything |
   | `CopilotBudget.Approver` | Approves transfers and requests |
   | `CopilotBudget.BudgetAuthority` | Approves budget increases |
   | `CopilotBudget.BudgetVisibility` | Sees everyone's usage, read-only |
   | `CopilotBudget.IntegrationAdmin` | Manages GitHub credentials and org mappings, not budgets |
   | `CopilotBudget.Auditor` | Reads the audit log |

   Roles can also be given inside the app (People & access), so this step is optional, but it's the tidy way to manage
   access at scale.
7. API permissions → Add → Microsoft Graph → **Application permissions** → `User.Read.All` → **Grant admin consent**.
   - This lets the app spot people whose accounts were disabled, and tidy up their credits.
   - Optional: without it, that check is simply off.
8. Optional: in Enterprise applications → Copilot Budget → Properties, set **Assignment required** = Yes to limit who
   can sign in.
   - With that on, assign users or groups under *Users and groups*, where you also assign the app roles.

## 3. Configure the web app

Web App → Settings → **Environment variables** → App settings. Add:

| Name | Value |
|---|---|
| `AzureAd__TenantId` | Directory (tenant) ID |
| `AzureAd__ClientId` | Application (client) ID |
| `KeyVault__Uri` | `https://<vault-name>.vault.azure.net/` |
| `Database__Provider` | `SqlServer` |

Then under *Connection strings*, add `Default`, type **SQLAzure**:

```
Server=tcp:<server>.database.windows.net,1433;Database=<db>;Authentication=Active Directory Default;Encrypt=True;
```

Leave these alone:

- **Don't** set `App__EnableDemoLogin`, `App__SeedDemoData` or `ASPNETCORE_ENVIRONMENT`.
- **Don't** put GitHub credentials here; the Setup page handles them (step 6).
- You **don't need** `App__Admins__0`; the first admin claims the app instead (step 5). It still works if you prefer
  it.

## 4. Deploy

**From GitHub Actions** (`.github/workflows/deploy-azure-webapp.yml`, run by hand from the Actions tab):

1. Create an Entra app registration for deployments, or reuse one. Under *Certificates & secrets → Federated
   credentials*, add a **GitHub Actions** credential:
   - your org and repo;
   - entity type *Environment*;
   - environment name `production`.
2. Give that registration the **Website Contributor** role on the web app.
3. In the repo, under Settings → Environments, create `production`. Under Settings → Variables, add:
   - `AZURE_CLIENT_ID` (the deployment registration);
   - `AZURE_TENANT_ID`;
   - `AZURE_SUBSCRIPTION_ID`;
   - `AZURE_WEBAPP_NAME`.
4. Actions → *Deploy to Azure Web App* → **Run workflow**.

**Or by hand:**

```bash
dotnet publish src/CopilotBudget/CopilotBudget.csproj -c Release -o publish
cd publish && zip -r ../app.zip . && cd ..
az webapp deploy -g <resource-group> -n <web-app-name> --src-path app.zip --type zip
```

On first start the app creates its tables, adds the default role mappings for the app roles above, and, because
nobody is an admin yet, writes a **one-time setup code** to its log.

## 5. Claim admin (first sign-in)

1. Web App → Monitoring → **Log stream**. (On a Windows plan, first turn on App Service logs → *Application logging
   (Filesystem)*.) Look for:

   ```
   FIRST-RUN SETUP: nobody is an admin yet. Sign in with Microsoft, open /setup and enter this one-time code: ABCD-EFGH-JKLM (valid for 24 hours ...)
   ```

   - If you missed it, restart the web app to get a new code.
   - With Application Insights you can also query it:
     `traces | where message startswith "FIRST-RUN SETUP" | top 1 by timestamp`.
2. Browse to the app and **Sign in with Microsoft**. A banner says nobody is an admin yet. Open **Setup**.
3. Enter the code → **Claim admin**. You're now the admin.

How the code is protected:

- It only ever appears in the app's log (and Application Insights, if linked), which only people with access to the
  web app in Azure can read.
- The app stores just a hash of it.
- It works once and expires after 24 hours.
- It's locked after 10 wrong tries, and admins are alerted after 3.
- Once there's an admin, no code is issued again.
- The claim is recorded in the audit log.

Prefer not to use a code? Either:

- assign yourself the `CopilotBudget.Admin` app role (Enterprise applications → Copilot Budget → Users and groups); or
- set `App__Admins__0` to your work email.

Then sign in.

## 6. Finish setup in the app

The **Setup** page (sidebar, for admins) has a checklist of what's configured and what's missing. It also lets you
connect GitHub without the credentials leaving the app.

### Connect GitHub

**GitHub App (recommended):** it belongs to the organization, not a person; its permissions are limited; and its
tokens last an hour.

1. In GitHub, go to your organization → Settings → Developer settings → GitHub Apps → **New GitHub App**.
   - Name: e.g. `Copilot Budget (Contoso)`. Homepage URL: your app's URL.
   - Webhook: **uncheck Active**. No callback URL is needed.
   - Organization permissions:
     - *GitHub Copilot Business*: **Read**;
     - *Members*: **Read**;
     - *Administration*: **Read**.
   - For budgets in GitHub and cost centers, the app also needs enterprise billing access. Install it on the
     enterprise with *Enterprise administration* write, if your plan offers that. If GitHub doesn't accept App tokens
     for enterprise billing in your plan, use a token instead (below).
   - *Where can this GitHub App be installed?* **Only on this account** (or Any account, for several orgs or an
     enterprise).
2. Create it, note the **App ID**, then **Generate a private key**. A `.pem` file downloads.
3. **Install App** on each organization (and the enterprise).
4. In Copilot Budget → Setup → **Connect GitHub**:
   - choose *GitHub App*;
   - enter the App ID;
   - choose the `.pem` file (or paste it);
   - optionally enter the enterprise slug;
   - click **Save to Key Vault and test**.
5. Delete the downloaded `.pem` file. The only copy is now in Key Vault.

**Personal access token:** simpler, but it stops working when its owner leaves. Use a classic token with:

- `manage_billing:copilot` and `read:org`;
- `manage_billing:enterprise`, if you want user budgets.

Choose *Personal access token* and paste it on the Setup page.

The app writes to GitHub only when write-back is on, and only if GitHub confirms the connection may write
enterprise budgets. A token without `manage_billing:enterprise` (or any fine-grained token) keeps the app read-only:
the *GitHub sync* page says so and locks the write-back switch off.

What happens when you click **Save to Key Vault and test**:

1. Each value goes straight to Key Vault as `GitHub--AppId`, `GitHub--AppPrivateKey`, `GitHub--Token` or
   `GitHub--EnterpriseSlug`, using the app's managed identity.
2. The page clears the fields.
3. The app reloads its settings and tests the connection; no restart is needed.
4. The change is recorded in the audit log, without the value.

Admins and integration admins can rotate these later on the **Integration** page.

### Then

1. **Admin** page: add the GitHub organizations whose Copilot seats you manage. The first sync starts right away and
   takes a few minutes.
2. **People & access**:
   - Give other people roles, or check that the role mappings match your Entra app roles or groups.
   - Link anyone whose sign-in didn't match a GitHub user automatically.
3. **Budgets**: set the monthly company-wide budget per person, and the transfer rules (approval threshold, minimum to
   keep).
4. **GitHub sync** (optional): once the enterprise slug is set and you've checked the numbers, switch on **writing
   budgets to GitHub**. It's off until you turn it on.
5. **Month-end** report: choose who gets the month-end email.
6. Back on **Setup**, click **Finish setup** once the required steps are ticked. That hides the setup banner.

## 7. Check it works

- [ ] Signing in with a work account works, and someone without a role sees only their own credits.
- [ ] Setup shows Entra, Admins, Database, Key Vault, GitHub connection and GitHub organizations all ticked.
- [ ] Integration → **Test connections** is all OK.
- [ ] Admin → the organizations show a recent sync and the right number of seats.
- [ ] Alerts page is empty (or only shows what you expect).
- [ ] Application Insights → Live metrics shows requests.
- [ ] The sign-in page has no "sign in as" list.

## Troubleshooting

| Symptom | Fix |
|---|---|
| Sign-in fails with "redirect URI mismatch" | The redirect URI in the app registration must match the site's address exactly, ending in `/signin-oidc` |
| The app won't start and the log mentions Key Vault | The managed identity is missing its Key Vault role, or `KeyVault__Uri` is wrong. Role assignments can take a few minutes to apply |
| "Key Vault refused the change" on the Setup page | The identity has *Secrets User* but needs *Secrets Officer* to save secrets |
| The app won't start and the log mentions `Login failed for user '<token-identified principal>'` | Run the `CREATE USER ... FROM EXTERNAL PROVIDER` step, with the web app's exact name |
| Pages stop updating or keep reconnecting | Turn Web sockets and ARR affinity on |
| No setup code in the log | Someone is already an admin (`App__Admins`, the Admin app role, or a claimed code). Sign in as them |
| Code says it's expired or locked | Restart the web app for a new one |
| The disabled-account check shows "not available" | Grant Graph `User.Read.All` (application) with admin consent |
