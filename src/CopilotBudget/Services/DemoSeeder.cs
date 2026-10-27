using CopilotBudget.Data;
using Microsoft.EntityFrameworkCore;

namespace CopilotBudget.Services;

/// <summary>
/// Fills an empty database with two fake organizations so the app can be tried without GitHub.
/// Grace is in both, to show that people are shared across organizations.
/// </summary>
public static class DemoSeeder
{
    /// <summary>
    /// <paramref name="Used"/> is this month so far; <paramref name="Then"/> and <paramref name="Recent"/> are
    /// typical full-month usage a year ago and last month, so the history shows a trend between them.
    /// </summary>
    private record Person(string Login, string Name, decimal Used, decimal Then, decimal Recent, params (string Org, bool Owner)[] Orgs);

    public const int HistoryMonths = 12;

    /// <summary>The figures below were written for ~300-credit budgets; budgets are now 1,900 and 3,900.</summary>
    private const decimal Scale = 6.2m;

    /// <summary>The named characters, plus <see cref="ExtraPeople"/> generated people so lists look like a real company.</summary>
    // A property, not a field: static fields initialize in order, and Named is declared below.
    private static Person[] People => _people ??= [.. Named, .. Generated(ExtraPeople)];
    private static Person[]? _people;

    public const int ExtraPeople = 50;

    /// <summary>
    /// An admin with no GitHub account or Copilot seat, like someone in IT who only runs the app.
    /// Listed on the demo sign-in page alongside the named characters.
    /// </summary>
    public const string SeatlessAdminLogin = "sam.rivera@contoso.com";

    /// <summary>How many named characters there are (created first, so they have the lowest IDs).</summary>
    public static int NamedPeople => Named.Length;

    private static readonly Person[] Named =
    [
        new("mona", "Mona Lisa", 142, 180, 165, ("contoso-demo", true)),
        new("hubot", "Hubot", 288, 150, 300, ("contoso-demo", false)),
        new("octocat", "The Octocat", 97, 120, 110, ("contoso-demo", false)),
        new("ada", "Ada Lovelace", 345, 200, 370, ("contoso-demo", false)),
        new("linus", "Linus T.", 12, 45, 20, ("contoso-demo", false)),
        new("grace", "Grace Hopper", 210, 260, 240, ("contoso-demo", true), ("fabrikam-demo", false)),
        new("alan", "Alan Turing", 0, 230, 10, ("contoso-demo", false)),
        new("margaret", "Margaret Hamilton", 276, 240, 290, ("contoso-demo", false)),
        new("katherine", "Katherine Johnson", 430, 110, 470, ("fabrikam-demo", true)),
        new("dennis", "Dennis Ritchie", 64, 90, 75, ("fabrikam-demo", false)),
        new("barbara", "Barbara Liskov", 512, 240, 540, ("fabrikam-demo", false)),
    ];

    private static readonly string[] FirstNames =
    [
        "Aisha", "Ben", "Carmen", "Dmitri", "Elena", "Farid", "Grete", "Hiro", "Ines", "Jamal", "Kofi", "Lena", "Mateo",
        "Nadia", "Oskar", "Priya", "Quinn", "Rosa", "Sanjay", "Tomas", "Uma", "Viktor", "Wen", "Ximena", "Yusuf", "Zara",
        "Arjun", "Bianca", "Chen", "Dalia", "Emeka", "Freya", "Gabriel", "Hana", "Ivan",
    ];

    private static readonly string[] LastNames =
    [
        "Okafor", "Lindqvist", "Moreau", "Tanaka", "Haddad", "Novak", "Reyes", "Schmidt", "Kowalski", "Nguyen", "Patel",
        "O'Brien", "Rossi", "Ivanova", "Mensah", "Fischer", "Castillo", "Sato", "Kim", "Dubois", "Silva", "Andersen", "Ahmed",
    ];

    private static readonly string[] Departments = ["Platform", "Payments", "Developer experience", "Data science", "Mobile", "Security"];

    /// <summary>
    /// Made-up people with a realistic spread: most use a modest share of their budget, a few are heavy users who run out
    /// before the reset, some barely use Copilot. Fixed seed, so the demo is the same every time.
    /// </summary>
    private static IEnumerable<Person> Generated(int count)
    {
        var random = new Random(2024);
        var used = new HashSet<string>();
        for (var i = 0; i < count; i++)
        {
            string first, last, login;
            do
            {
                first = FirstNames[random.Next(FirstNames.Length)];
                last = LastNames[random.Next(LastNames.Length)];
                login = $"{first}-{last}".ToLowerInvariant().Replace("'", "");
            } while (!used.Add(login));

            // Usage per ~300 budget (scaled up later): light, typical or heavy.
            var kind = random.NextDouble();
            var recent = kind switch
            {
                < 0.15 => random.Next(0, 40),     // barely uses it
                < 0.85 => random.Next(80, 260),   // typical
                _ => random.Next(290, 380),       // heavy: runs out before the reset
            };
            var then = Math.Max(0, recent + random.Next(-120, 60));
            var now = Math.Max(0, Math.Round(recent * (decimal)(0.85 + random.NextDouble() * 0.3)));

            // About two thirds in Contoso, the rest in Fabrikam, a few in both.
            var org = random.NextDouble();
            (string, bool)[] orgs = org switch
            {
                < 0.62 => [("contoso-demo", false)],
                < 0.94 => [("fabrikam-demo", false)],
                _ => [("contoso-demo", false), ("fabrikam-demo", false)],
            };
            yield return new Person(login, $"{first} {last}", now, then, recent, orgs);
        }
    }

    public static async Task SeedAsync(AppDbContext db)
    {
        if (await db.Organizations.AnyAsync()) return;

        var now = DateTime.UtcNow;
        var period = Period.Current;
        var history = Period.LastMonths(HistoryMonths);
        var random = new Random(42); // Fixed seed: the demo looks the same every time.

        var orgs = new[]
        {
            new Organization { Login = "contoso-demo", DisplayName = "Contoso (demo)", PlanType = "business", DefaultMonthlyAllocation = 1900 },
            new Organization { Login = "fabrikam-demo", DisplayName = "Fabrikam (demo)", PlanType = "enterprise", DefaultMonthlyAllocation = 3900 },
        };
        foreach (var org in orgs)
        {
            var seats = People.Where(p => p.Orgs.Any(o => o.Org == org.Login)).ToList();
            org.IsDemo = true;
            org.TotalSeats = seats.Count;
            org.ActiveSeatsThisCycle = seats.Count(p => p.Used > 0);
            org.InactiveSeatsThisCycle = seats.Count(p => p.Used == 0);
            org.LastSyncedUtc = now;
            org.LastSyncMessage = "Demo data. Not connected to GitHub.";
        }
        db.Organizations.AddRange(orgs);

        var editors = new[] { "vscode", "JetBrains", "copilot-chat", "vscode", "copilot-cli" };
        var users = new Dictionary<string, AppUser>();
        var memberships = new List<Membership>();
        foreach (var (p, i) in People.Select((p, i) => (p, i)))
        {
            // No avatar URL: these logins belong to real GitHub users, so demo people get initials instead.
            // Work emails as GitHub SSO would report them, so Microsoft sign-ins match these people.
            var user = users[p.Login] = new AppUser
            {
                Login = p.Login, Name = p.Name, Email = $"{p.Login}@contoso.com", EmailSource = "github-saml",
                GitHubId = 9_000_000 + i, // GitHub's numeric user ID, which survives renames. Fake for the demo.
            };
            foreach (var (orgLogin, owner) in p.Orgs)
            {
                var org = orgs.First(o => o.Login == orgLogin);
                memberships.Add(new Membership
                {
                    Organization = org,
                    User = user,
                    IsOrgAdmin = owner,
                    HasSeat = true,
                    PlanType = org.PlanType,
                    SeatCreatedUtc = now.AddMonths(-6).AddDays(i * 3),
                    LastActivityUtc = p.Used == 0 ? now.AddDays(-40) : now.AddHours(-(i * 7 + 1)),
                    LastActivityEditor = editors[i % editors.Length],
                });
            }
        }
        // One person per role, so each can be tried by signing in as them.
        users["grace"].AssignedRoles = AppRole.Approver;
        // Margaret approves budget increases, so changes made as mona (an admin, not an authority) wait for her.
        users["margaret"].AssignedRoles = AppRole.BudgetAuthority;
        users["octocat"].AssignedRoles = AppRole.IntegrationAdmin;
        users["dennis"].AssignedRoles = AppRole.Auditor;
        users["linus"].AssignedRoles = AppRole.BudgetVisibility;
        // Sam runs the app but has no Copilot seat or GitHub account: an admin-only view.
        db.Users.Add(new AppUser
        {
            Login = SeatlessAdminLogin, Name = "Sam Rivera", Email = SeatlessAdminLogin, EmailSource = "entra",
            HasGitHubAccount = false, AssignedRoles = AppRole.Admin,
        });
        // Alan left: his Entra account was disabled. His unused credits are waiting to be reassigned.
        users["alan"].IsDisabled = true;
        users["alan"].DisabledUtc = now.AddDays(-1);
        users["alan"].DisabledReason = "Entra account disabled";
        // Linus has a smaller budget set for him: he mostly uses completions.
        users["linus"].MonthlyBudgetOverride = 1000;
        users["linus"].OverrideNote = "Mostly uses completions";
        foreach (var (login, center) in CostCenters) users[login].CostCenter = center;
        foreach (var (p, i) in People.Skip(Named.Length).Select((p, i) => (p, i)))
            users[p.Login].CostCenter = i % 9 == 8 ? null : Departments[i % Departments.Length]; // A few with none.
        db.Memberships.AddRange(memberships);
        await db.SaveChangesAsync();

        foreach (var p in People)
        {
            var user = users[p.Login];
            var mine = memberships.Where(m => m.UserId == user.Id).ToList();
            // Monthly budget is the largest of the person's orgs (see BudgetService.MonthlyBudgetFor).
            var best = mine.MaxBy(m => m.Organization.DefaultMonthlyAllocation)!;
            var monthly = user.MonthlyBudgetOverride ?? best.Organization.DefaultMonthlyAllocation;
            for (var i = 0; i < history.Count; i++)
            {
                var month = history[i];
                db.Ledger.Add(new LedgerEntry
                {
                    UserId = user.Id, Period = month, Amount = monthly,
                    Type = LedgerEntryType.MonthlyAllocation, OrganizationId = best.OrganizationId,
                    Note = $"Monthly budget for {Period.Display(month)}",
                    CreatedUtc = FirstOfMonth(now).AddMonths(i - history.Count + 1),
                });

                // Past months drift from Then to Recent with some noise; this month is what's used so far.
                decimal used;
                // Usage figures are per ~300 credits; scale them to today's budgets, and this month to how far through it we are.
                if (month == period) used = Math.Round(p.Used * Scale * (decimal)(Pace.Elapsed() / 0.87));
                else
                {
                    var t = history.Count <= 2 ? 1m : (decimal)i / (history.Count - 2);
                    var typical = (p.Then + (p.Recent - p.Then) * t) * Scale;
                    used = Math.Max(0, Math.Round(typical * (decimal)(0.88 + random.NextDouble() * 0.24)));
                }

                // Usage is reported per org seat. People in two orgs split it between them.
                var share = mine.Count == 1 ? used : Math.Round(used * 0.6m);
                db.Usage.Add(new UsageRecord { MembershipId = mine[0].Id, Period = month, Used = share });
                if (mine.Count > 1)
                    db.Usage.Add(new UsageRecord { MembershipId = mine[1].Id, Period = month, Used = used - share });
            }
        }

        int U(string login) => users[login].Id;

        // A finished request: Ada asked for budget, and Alan (who barely uses Copilot) filled it.
        var fulfilled = new BudgetRequest
        {
            RequesterId = U("ada"), Amount = 100,
            Reason = "Large refactor of the billing service this sprint.", Status = RequestStatus.Approved,
            CreatedUtc = now.AddDays(-3), ResolvedUtc = now.AddDays(-2), ResolvedById = U("alan"),
            ResponseNote = "Go for it, I'm on leave most of the month.",
        };
        db.Requests.Add(fulfilled);
        await db.SaveChangesAsync();
        AddTransfer(db, U("alan"), U("ada"), 100, $"Request #{fulfilled.Id}: {fulfilled.Reason}", fulfilled.Id, now.AddDays(-2));

        // Credits sent with no request, including across organizations.
        AddTransfer(db, U("linus"), U("hubot"), 50, "Have some of mine, I mostly use completions.", null, now.AddDays(-1));
        AddTransfer(db, U("dennis"), U("barbara"), 120, "Finish that migration!", null, now.AddHours(-20));

        db.Requests.AddRange(
            new BudgetRequest
            {
                RequesterId = U("margaret"), Amount = 350,
                Reason = "Writing integration tests with agent mode before the release.", CreatedUtc = now.AddHours(-5),
            },
            new BudgetRequest
            {
                RequesterId = U("hubot"), Amount = 250,
                Reason = "Running low after a week of code review with Copilot.", CreatedUtc = now.AddHours(-2),
            },
            new BudgetRequest
            {
                RequesterId = U("mona"), Amount = 150,
                Reason = "Trying out a new model for docs generation.", CreatedUtc = now.AddMinutes(-30),
            },
            new BudgetRequest
            {
                RequesterId = U("katherine"), Amount = 450,
                Reason = "Trajectory model rewrite needs a few long agent sessions.", CreatedUtc = now.AddHours(-1),
            },
            new BudgetRequest
            {
                RequesterId = U("octocat"), Amount = 80,
                Reason = "Prototype spike.", Status = RequestStatus.Declined, CreatedUtc = now.AddDays(-4),
                ResolvedUtc = now.AddDays(-4).AddHours(3), ResolvedById = U("grace"),
                ResponseNote = "We can't cover spikes this month, sorry.",
            });

        var reserve = new CreditPool
        {
            Name = "Barbara's prepaid pack", IsShared = false, AutoCover = true, Note = "Bought separately for the migration project",
            Members = [new CreditPoolMember { UserId = U("barbara") }], CreatedUtc = now.AddDays(-20),
        };
        var dataScience = new CreditPool
        {
            Name = "Data science team", IsShared = true, AutoCover = true, MonthlyRefill = 2000,
            Members = [new CreditPoolMember { UserId = U("katherine") }, new CreditPoolMember { UserId = U("dennis") }, new CreditPoolMember { UserId = U("barbara") }],
            CreatedUtc = now.AddDays(-40),
        };
        var launch = new CreditPool
        {
            Name = "Launch week", IsShared = true, AutoCover = false, Note = "One-off prepaid credits for the v3 launch",
            Members = [new CreditPoolMember { UserId = U("ada") }, new CreditPoolMember { UserId = U("hubot") }, new CreditPoolMember { UserId = U("margaret") }],
            CreatedUtc = now.AddDays(-6),
        };
        db.CreditPools.AddRange(reserve, dataScience, launch);
        await db.SaveChangesAsync();
        db.PoolEntries.AddRange(
            new PoolEntry { CreditPoolId = reserve.Id, Amount = 5000, Type = PoolEntryType.Added, Period = period, Note = "PO 4471", CreatedByUserId = U("mona"), CreatedUtc = now.AddDays(-20) },
            new PoolEntry { CreditPoolId = dataScience.Id, Amount = 2000, Type = PoolEntryType.Refill, Period = period, Note = $"Monthly top-up for {Period.Display(period)}", CreatedUtc = FirstOfMonth(now) },
            new PoolEntry { CreditPoolId = launch.Id, Amount = 3000, Type = PoolEntryType.Added, Period = period, Note = "PO 4502", CreatedByUserId = U("mona"), CreatedUtc = now.AddDays(-6) });
        // Katherine already used some of the team pool this month.
        db.PoolEntries.Add(new PoolEntry { CreditPoolId = dataScience.Id, Amount = -300, Type = PoolEntryType.Draw, Period = period, UserId = U("katherine"), CreatedByUserId = U("katherine"), CreatedUtc = now.AddDays(-2) });
        db.Ledger.Add(new LedgerEntry
        {
            UserId = U("katherine"), Period = period, Amount = 300, Type = LedgerEntryType.PoolDraw, CreditPoolId = dataScience.Id,
            Note = "Taken from Data science team", CreatedByUserId = U("katherine"), CreatedUtc = now.AddDays(-2),
        });

        db.Ledger.Add(new LedgerEntry
        {
            UserId = U("ada"), Period = period, Amount = 50, Type = LedgerEntryType.AdminAdjustment,
            Note = "Extra budget for the on-call rotation.", CreatedByUserId = U("mona"), CreatedUtc = now.AddDays(-1).AddHours(-4),
        });

        // A temporary exception (Ada's launch week) and a raise waiting for a budget authority.
        db.BudgetExceptions.Add(new BudgetException
        {
            UserId = U("ada"), Amount = 1000, StartDate = now.Date.AddDays(-3), EndDate = now.Date.AddDays(10),
            Reason = "Launch week: running agent mode on the release branch", IsApproved = true, CreatedByUserId = U("mona"),
            CreatedUtc = now.AddDays(-3),
        });
        db.Proposals.Add(new BudgetProposal
        {
            Kind = BudgetChangeKind.PersonOverride, UserId = U("katherine"), Value = 6000, SpendChange = 2100,
            Summary = "Set katherine's monthly budget to 6,000 credits", Note = "Trajectory model rewrite runs through next quarter",
            Period = period, ProposedById = U("grace"), CreatedUtc = now.AddHours(-6),
        });
        // Last month's leftovers that expired at the reset.
        db.Requests.Add(new BudgetRequest
        {
            RequesterId = U("dennis"), Amount = 200, Reason = "Year-end report generation.", Status = RequestStatus.Expired,
            CreatedUtc = FirstOfMonth(now).AddDays(-3), ResolvedUtc = FirstOfMonth(now), ResponseNote = "Not filled before the month ended.",
        });

        // The demo is ready to use: no setup checklist nagging.
        if (!await db.Settings.AnyAsync()) db.Settings.Add(new AppSettings { SetupCompletedUtc = now });

        await db.SaveChangesAsync();
        await SeedUsageDetailsAsync(db, random);
    }

    /// <summary>Cost centers for the demo people.</summary>
    private static readonly (string Login, string Center)[] CostCenters =
    [
        ("mona", "Platform"), ("hubot", "Platform"), ("octocat", "Developer experience"), ("ada", "Payments"),
        ("linus", "Platform"), ("grace", "Developer experience"), ("margaret", "Payments"),
        ("katherine", "Data science"), ("dennis", "Data science"), ("barbara", "Data science"),
    ];

    private static readonly string[] Models = ["Claude Sonnet 4.6", "GPT-5.2", "Claude Opus 4.6", "Gemini 3 Pro", "GPT-5 mini"];
    private static readonly string[] Skus = ["copilot_agent_ai_credits", "copilot_chat_ai_credits", "copilot_code_review_ai_credits"];

    /// <summary>
    /// Splits each person's usage by model and SKU (like GitHub's usage report), and spreads this month and last month
    /// over days for each organization, quieter at weekends.
    /// </summary>
    private static async Task SeedUsageDetailsAsync(AppDbContext db, Random random)
    {
        var usage = await db.Usage.Include(u => u.Membership).AsNoTracking().ToListAsync();
        var today = DateTime.UtcNow.Date;
        var recent = new HashSet<string> { Period.Current, Period.Previous(Period.Current) };
        var daily = new Dictionary<(int Org, string Period, int Day, string Model, string Sku), decimal>();

        foreach (var u in usage.Where(u => u.Used > 0))
        {
            // Everyone has a favourite model; weights are fixed per person so months look alike.
            var personal = new Random(u.Membership.UserId * 7919);
            var modelWeights = Models.Select((_, i) => Math.Pow(personal.NextDouble() + 0.15, 2) * (i == 0 ? 2.2 : 1)).ToArray();
            var skuWeights = new[] { 0.55 + personal.NextDouble() * 0.2, 0.25, 0.08 + personal.NextDouble() * 0.1 };
            var cells = new List<(string Model, string Sku, decimal Quantity)>();
            for (var m = 0; m < Models.Length; m++)
                for (var k = 0; k < Skus.Length; k++)
                    cells.Add((Models[m], Skus[k], (decimal)(modelWeights[m] / modelWeights.Sum() * skuWeights[k] / skuWeights.Sum())));
            // Round to whole credits and put any rounding difference on the biggest cell, so rows add up to the total.
            var split = cells.Select(c => (c.Model, c.Sku, Quantity: Math.Round(c.Quantity * u.Used))).ToList();
            var diff = u.Used - split.Sum(c => c.Quantity);
            var biggest = split.IndexOf(split.MaxBy(c => c.Quantity));
            split[biggest] = split[biggest] with { Quantity = split[biggest].Quantity + diff };

            foreach (var c in split.Where(c => c.Quantity > 0))
            {
                db.UsageDetails.Add(new UsageDetail
                {
                    OrganizationId = u.Membership.OrganizationId, UserId = u.Membership.UserId, Period = u.Period,
                    Product = "copilot", Sku = c.Sku, Model = c.Model, Quantity = c.Quantity, Cost = c.Quantity * 0.01m,
                });
            }

            if (!recent.Contains(u.Period)) continue;
            var start = Period.StartUtc(u.Period);
            var days = Enumerable.Range(0, DateTime.DaysInMonth(start.Year, start.Month)).Select(d => start.AddDays(d))
                .Where(d => d <= today).ToList();
            var dayWeights = days.Select(d => (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 0.15 : 1.0) * (0.6 + random.NextDouble())).ToArray();
            var total = dayWeights.Sum();
            for (var i = 0; i < days.Count; i++)
            {
                foreach (var c in split.Where(c => c.Quantity > 0))
                {
                    var key = (u.Membership.OrganizationId, u.Period, days[i].Day, c.Model, c.Sku);
                    daily[key] = daily.GetValueOrDefault(key) + c.Quantity * (decimal)(dayWeights[i] / total);
                }
            }
        }

        foreach (var ((org, period, day, model, sku), quantity) in daily)
        {
            var q = Math.Round(quantity, 1);
            if (q <= 0) continue;
            db.UsageDetails.Add(new UsageDetail
            {
                OrganizationId = org, Period = period, Day = day, Product = "copilot", Sku = sku, Model = model,
                Quantity = q, Cost = q * 0.01m,
            });
        }
        await db.SaveChangesAsync();
    }

    private static void AddTransfer(AppDbContext db, int from, int to, decimal amount, string note, int? requestId, DateTime when)
    {
        var period = Period.Current;
        var transferId = Guid.NewGuid();
        db.Ledger.Add(new LedgerEntry
        {
            UserId = from, CounterpartyUserId = to, Period = period, Amount = -amount,
            Type = LedgerEntryType.TransferOut, Note = note, BudgetRequestId = requestId, CreatedByUserId = from, CreatedUtc = when, TransferId = transferId,
        });
        db.Ledger.Add(new LedgerEntry
        {
            UserId = to, CounterpartyUserId = from, Period = period, Amount = amount,
            Type = LedgerEntryType.TransferIn, Note = note, BudgetRequestId = requestId, CreatedByUserId = from, CreatedUtc = when, TransferId = transferId,
        });
    }

    private static DateTime FirstOfMonth(DateTime d) => new(d.Year, d.Month, 1, 0, 0, 0, DateTimeKind.Utc);
}
