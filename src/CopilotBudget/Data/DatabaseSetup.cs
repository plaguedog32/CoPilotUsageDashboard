using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CopilotBudget.Data;

/// <summary>
/// Hands out the provider-specific context (SQLite or SQL Server) as an <see cref="AppDbContext"/>, so the rest of
/// the app doesn't care which database it's on.
/// </summary>
public class AppDbContextFactory(IConfiguration configuration) : IDbContextFactory<AppDbContext>
{
    private readonly DbContextOptions<SqliteAppDbContext>? _sqlite = DatabaseSetup.IsSqlServer(configuration) ? null
        : new DbContextOptionsBuilder<SqliteAppDbContext>().UseSqlite(DatabaseSetup.ConnectionString(configuration)).Options;

    private readonly DbContextOptions<SqlServerAppDbContext>? _sqlServer = !DatabaseSetup.IsSqlServer(configuration) ? null
        : new DbContextOptionsBuilder<SqlServerAppDbContext>()
            .UseSqlServer(DatabaseSetup.ConnectionString(configuration), sql => sql.EnableRetryOnFailure()).Options;

    public AppDbContext CreateDbContext() =>
        _sqlServer is not null ? new SqlServerAppDbContext(_sqlServer) : new SqliteAppDbContext(_sqlite!);
}

public static class DatabaseSetup
{
    public static bool IsSqlServer(IConfiguration configuration) =>
        string.Equals(configuration["Database:Provider"], "SqlServer", StringComparison.OrdinalIgnoreCase);

    public static string ConnectionString(IConfiguration configuration) =>
        configuration.GetConnectionString("Default")
        ?? (IsSqlServer(configuration)
            ? throw new InvalidOperationException("ConnectionStrings:Default is required for SqlServer.")
            : "Data Source=copilot-budget.db");

    /// <summary>
    /// Applies EF Core migrations. A local SQLite database made before migrations existed (by EnsureCreated) can't be
    /// upgraded in place, so it's moved aside and a fresh one is created. A SQL Server database in that state stops
    /// the app instead of guessing.
    /// </summary>
    public static async Task MigrateAsync(IDbContextFactory<AppDbContext> factory, ILogger logger)
    {
        await using (var db = factory.CreateDbContext())
        {
            var created = await db.Database.CanConnectAsync();
            var applied = created ? (await db.Database.GetAppliedMigrationsAsync()).Any() : false;
            var hasTables = created && await HasAppTablesAsync(db);
            if (hasTables && !applied)
            {
                if (!db.Database.IsSqlite())
                    throw new InvalidOperationException(
                        "The database has the app's tables but no migration history (it was created before migrations). " +
                        "Back it up, then either recreate it or baseline it by inserting the initial migration into __EFMigrationsHistory. See the README.");

                var path = new SqliteConnectionStringBuilder(db.Database.GetConnectionString()).DataSource;
                await db.DisposeAsync();
                SqliteConnection.ClearAllPools();
                var backup = $"{path}.backup-{DateTime.UtcNow:yyyyMMddHHmmss}";
                File.Move(path, backup);
                foreach (var extra in new[] { "-shm", "-wal" })
                    if (File.Exists(path + extra)) File.Move(path + extra, backup + extra);
                logger.LogWarning("The local database predates migrations; moved it to {Backup} and created a new one.", backup);
            }
        }

        await using var context = factory.CreateDbContext();
        var pending = (await context.Database.GetPendingMigrationsAsync()).ToList();
        if (pending.Count > 0)
        {
            logger.LogInformation("Applying {Count} database migrations: {Migrations}", pending.Count, string.Join(", ", pending));
            await context.Database.MigrateAsync();
        }
    }

    private static async Task<bool> HasAppTablesAsync(AppDbContext db)
    {
        var sql = db.Database.IsSqlite()
            ? "SELECT COUNT(*) AS \"Value\" FROM sqlite_master WHERE type = 'table' AND name = 'Users'"
            : "SELECT COUNT(*) AS [Value] FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Users'";
        return await db.Database.SqlQueryRaw<int>(sql).SingleAsync() > 0;
    }
}

/// <summary>For <c>dotnet ef migrations add X --context SqliteAppDbContext --output-dir Migrations/Sqlite</c>.</summary>
public class SqliteDesignTimeFactory : IDesignTimeDbContextFactory<SqliteAppDbContext>
{
    public SqliteAppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SqliteAppDbContext>().UseSqlite("Data Source=design-time.db").Options);
}

/// <summary>For <c>dotnet ef migrations add X --context SqlServerAppDbContext --output-dir Migrations/SqlServer</c>.</summary>
public class SqlServerDesignTimeFactory : IDesignTimeDbContextFactory<SqlServerAppDbContext>
{
    public SqlServerAppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SqlServerAppDbContext>()
            .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=CopilotBudgetDesign;Trusted_Connection=True").Options);
}
