using Microsoft.EntityFrameworkCore;

namespace CopilotBudget.Data;

/// <summary>
/// The app's database. Each provider has its own subclass (<see cref="SqliteAppDbContext"/>,
/// <see cref="SqlServerAppDbContext"/>) so each gets its own EF Core migrations.
/// </summary>
public abstract class AppDbContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<LedgerEntry> Ledger => Set<LedgerEntry>();
    public DbSet<UsageRecord> Usage => Set<UsageRecord>();
    public DbSet<BudgetRequest> Requests => Set<BudgetRequest>();
    public DbSet<PendingTransfer> PendingTransfers => Set<PendingTransfer>();
    public DbSet<AppSettings> Settings => Set<AppSettings>();
    public DbSet<CreditPool> CreditPools => Set<CreditPool>();
    public DbSet<CreditPoolMember> CreditPoolMembers => Set<CreditPoolMember>();
    public DbSet<PoolEntry> PoolEntries => Set<PoolEntry>();
    public DbSet<BudgetException> BudgetExceptions => Set<BudgetException>();
    public DbSet<BudgetProposal> Proposals => Set<BudgetProposal>();
    public DbSet<GitHubBudgetLink> GitHubBudgets => Set<GitHubBudgetLink>();
    public DbSet<GitHubOperation> GitHubOperations => Set<GitHubOperation>();
    public DbSet<UsageDetail> UsageDetails => Set<UsageDetail>();
    public DbSet<RoleMapping> RoleMappings => Set<RoleMapping>();
    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();
    public DbSet<ApiCall> ApiCalls => Set<ApiCall>();
    public DbSet<Alert> Alerts => Set<Alert>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Credits are whole numbers or small fractions; fixed precision keeps SQL Server and SQLite in step.
        configurationBuilder.Properties<decimal>().HavePrecision(18, 2);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        var sqlite = Database.IsSqlite();

        b.Entity<AppUser>().HasIndex(u => u.Login).IsUnique();
        b.Entity<AppUser>().HasIndex(u => u.Email);
        b.Entity<AppUser>().HasIndex(u => u.EntraObjectId).IsUnique();
        // Matching is by GitHub's numeric ID, which survives a renamed account; logins can change.
        b.Entity<AppUser>().HasIndex(u => u.GitHubId).IsUnique();
        b.Entity<AppUser>().Property(u => u.DisabledReason).HasMaxLength(200);
        b.Entity<AppUser>().Property(u => u.DirectoryClaims).HasMaxLength(4000);
        b.Entity<AppUser>().Property(u => u.Login).HasMaxLength(256);
        b.Entity<AppUser>().Property(u => u.Email).HasMaxLength(256);
        b.Entity<AppUser>().Property(u => u.EntraObjectId).HasMaxLength(64);

        b.Entity<Organization>().HasIndex(o => o.Login).IsUnique();
        b.Entity<Organization>().Property(o => o.Login).HasMaxLength(100);
        b.Entity<LedgerEntry>().Property(l => l.Period).HasMaxLength(7);
        b.Entity<UsageRecord>().Property(u => u.Period).HasMaxLength(7);
        b.Entity<PoolEntry>().Property(e => e.Period).HasMaxLength(7);

        if (sqlite)
        {
            // SQL Server's default collation is already case-insensitive; SQLite's isn't.
            b.Entity<AppUser>().Property(u => u.Login).UseCollation("NOCASE");
            b.Entity<AppUser>().Property(u => u.Email).UseCollation("NOCASE");
            b.Entity<Organization>().Property(o => o.Login).UseCollation("NOCASE");
        }

        b.Entity<Membership>().HasIndex(m => new { m.OrganizationId, m.UserId }).IsUnique();

        b.Entity<LedgerEntry>().HasIndex(l => new { l.UserId, l.Period });
        b.Entity<LedgerEntry>().HasOne(l => l.User).WithMany().HasForeignKey(l => l.UserId);
        b.Entity<LedgerEntry>().HasOne(l => l.Counterparty).WithMany().HasForeignKey(l => l.CounterpartyUserId)
            .OnDelete(DeleteBehavior.SetNull);
        b.Entity<LedgerEntry>().HasOne(l => l.CreatedBy).WithMany().HasForeignKey(l => l.CreatedByUserId)
            .OnDelete(DeleteBehavior.SetNull);
        b.Entity<LedgerEntry>().HasOne(l => l.Organization).WithMany().HasForeignKey(l => l.OrganizationId)
            .OnDelete(DeleteBehavior.SetNull);

        b.Entity<UsageRecord>().HasIndex(u => new { u.MembershipId, u.Period }).IsUnique();

        b.Entity<BudgetRequest>().HasOne(r => r.Requester).WithMany().HasForeignKey(r => r.RequesterId);
        b.Entity<BudgetRequest>().HasOne(r => r.Target).WithMany().HasForeignKey(r => r.TargetId)
            .OnDelete(DeleteBehavior.SetNull);
        b.Entity<BudgetRequest>().HasOne(r => r.ResolvedBy).WithMany().HasForeignKey(r => r.ResolvedById)
            .OnDelete(DeleteBehavior.SetNull);
        b.Entity<BudgetRequest>().HasIndex(r => r.Status);

        b.Entity<PendingTransfer>().HasOne(t => t.From).WithMany().HasForeignKey(t => t.FromUserId);
        b.Entity<PendingTransfer>().HasOne(t => t.To).WithMany().HasForeignKey(t => t.ToUserId);
        b.Entity<PendingTransfer>().HasOne(t => t.ResolvedBy).WithMany().HasForeignKey(t => t.ResolvedById)
            .OnDelete(DeleteBehavior.SetNull);
        b.Entity<PendingTransfer>().HasOne(t => t.BudgetRequest).WithMany().HasForeignKey(t => t.BudgetRequestId)
            .OnDelete(DeleteBehavior.SetNull);
        b.Entity<PendingTransfer>().HasIndex(t => t.Status);

        b.Entity<LedgerEntry>().HasOne(l => l.CreditPool).WithMany().HasForeignKey(l => l.CreditPoolId)
            .OnDelete(DeleteBehavior.SetNull);
        b.Entity<CreditPoolMember>().HasOne(m => m.CreditPool).WithMany(p => p.Members).HasForeignKey(m => m.CreditPoolId);
        b.Entity<CreditPoolMember>().HasIndex(m => new { m.CreditPoolId, m.UserId }).IsUnique();
        b.Entity<PoolEntry>().HasOne(e => e.CreditPool).WithMany().HasForeignKey(e => e.CreditPoolId);
        b.Entity<PoolEntry>().HasOne(e => e.User).WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<PoolEntry>().HasOne(e => e.CreatedBy).WithMany().HasForeignKey(e => e.CreatedByUserId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<PoolEntry>().HasIndex(e => new { e.CreditPoolId, e.Period });

        b.Entity<LedgerEntry>().HasIndex(l => l.TransferId);

        b.Entity<BudgetException>().HasOne(e => e.User).WithMany().HasForeignKey(e => e.UserId);
        b.Entity<BudgetException>().HasOne(e => e.CreatedBy).WithMany().HasForeignKey(e => e.CreatedByUserId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<BudgetException>().HasIndex(e => new { e.UserId, e.EndDate });

        b.Entity<BudgetProposal>().HasOne(p => p.User).WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<BudgetProposal>().HasOne(p => p.Organization).WithMany().HasForeignKey(p => p.OrganizationId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<BudgetProposal>().HasOne(p => p.BudgetException).WithMany().HasForeignKey(p => p.BudgetExceptionId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<BudgetProposal>().HasOne(p => p.ProposedBy).WithMany().HasForeignKey(p => p.ProposedById);
        b.Entity<BudgetProposal>().HasOne(p => p.DecidedBy).WithMany().HasForeignKey(p => p.DecidedById).OnDelete(DeleteBehavior.SetNull);
        b.Entity<BudgetProposal>().HasIndex(p => p.Status);
        b.Entity<BudgetProposal>().Property(p => p.Period).HasMaxLength(7);

        b.Entity<GitHubBudgetLink>().HasOne(g => g.User).WithMany().HasForeignKey(g => g.UserId);
        b.Entity<GitHubBudgetLink>().HasIndex(g => g.UserId).IsUnique();
        b.Entity<GitHubBudgetLink>().Property(g => g.BudgetId).HasMaxLength(100);

        b.Entity<GitHubOperation>().HasOne(o => o.User).WithMany().HasForeignKey(o => o.UserId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<GitHubOperation>().HasIndex(o => o.CreatedUtc);
        b.Entity<GitHubOperation>().Property(o => o.Action).HasMaxLength(20);

        b.Entity<UsageDetail>().HasOne(d => d.Organization).WithMany().HasForeignKey(d => d.OrganizationId);
        b.Entity<UsageDetail>().HasOne(d => d.User).WithMany().HasForeignKey(d => d.UserId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<UsageDetail>().HasIndex(d => new { d.Period, d.OrganizationId, d.UserId, d.Day });
        b.Entity<UsageDetail>().Property(d => d.Period).HasMaxLength(7);
        b.Entity<UsageDetail>().Property(d => d.Sku).HasMaxLength(100);
        b.Entity<UsageDetail>().Property(d => d.Model).HasMaxLength(100);
        b.Entity<UsageDetail>().Property(d => d.Product).HasMaxLength(100);
        b.Entity<UsageDetail>().Property(d => d.Cost).HasPrecision(18, 4);

        b.Entity<AppSettings>().Property(s => s.MonthEndRecipients).HasMaxLength(2000);
        b.Entity<AppSettings>().Property(s => s.SetupCodeHash).HasMaxLength(100);

        b.Entity<RoleMapping>().HasIndex(m => new { m.Source, m.Value, m.Role }).IsUnique();
        b.Entity<RoleMapping>().Property(m => m.Value).HasMaxLength(200);
        b.Entity<RoleMapping>().Property(m => m.Label).HasMaxLength(200);

        b.Entity<AuditEntry>().HasIndex(a => a.CreatedUtc);
        b.Entity<AuditEntry>().HasIndex(a => a.Action);
        b.Entity<AuditEntry>().Property(a => a.ActorName).HasMaxLength(256);
        b.Entity<AuditEntry>().Property(a => a.Action).HasMaxLength(64);
        b.Entity<AuditEntry>().Property(a => a.TargetType).HasMaxLength(64);
        b.Entity<AuditEntry>().Property(a => a.TargetId).HasMaxLength(64);
        b.Entity<AuditEntry>().Property(a => a.TargetName).HasMaxLength(256);
        b.Entity<AuditEntry>().Property(a => a.Summary).HasMaxLength(1000);

        b.Entity<ApiCall>().HasIndex(c => c.CreatedUtc);
        b.Entity<ApiCall>().Property(c => c.Service).HasMaxLength(20);
        b.Entity<ApiCall>().Property(c => c.Method).HasMaxLength(10);
        b.Entity<ApiCall>().Property(c => c.Path).HasMaxLength(1000);
        b.Entity<ApiCall>().Property(c => c.Error).HasMaxLength(1000);

        b.Entity<Alert>().HasIndex(a => new { a.Key, a.ResolvedUtc });
        b.Entity<Alert>().Property(a => a.Key).HasMaxLength(200);
        b.Entity<Alert>().Property(a => a.Kind).HasMaxLength(40);
        b.Entity<Alert>().Property(a => a.Title).HasMaxLength(300);
        b.Entity<Alert>().Property(a => a.Detail).HasMaxLength(2000);
        b.Entity<Alert>().HasOne(a => a.AcknowledgedBy).WithMany().HasForeignKey(a => a.AcknowledgedById).OnDelete(DeleteBehavior.SetNull);

        if (!sqlite)
        {
            // SQL Server rejects tables with several cascading paths to the same parent (many tables point at
            // Users more than once). The app deletes dependents explicitly, so the database doesn't need to.
            foreach (var fk in b.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()))
                fk.DeleteBehavior = DeleteBehavior.NoAction;
        }
    }
}

/// <summary>SQLite, for local development and the demo.</summary>
public class SqliteAppDbContext(DbContextOptions<SqliteAppDbContext> options) : AppDbContext(options);

/// <summary>SQL Server / Azure SQL, for Azure.</summary>
public class SqlServerAppDbContext(DbContextOptions<SqlServerAppDbContext> options) : AppDbContext(options);
