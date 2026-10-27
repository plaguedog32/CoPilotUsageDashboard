using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CopilotBudget.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApiCalls",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Service = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Method = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    DurationMs = table.Column<int>(type: "INTEGER", nullable: false),
                    RateLimitRemaining = table.Column<int>(type: "INTEGER", nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiCalls", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ActorUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    ActorName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TargetType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    TargetId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    TargetName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Details = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CreditPools",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    IsShared = table.Column<bool>(type: "INTEGER", nullable: false),
                    AutoCover = table.Column<bool>(type: "INTEGER", nullable: false),
                    MonthlyRefill = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreditPools", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Organizations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Login = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, collation: "NOCASE"),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    DefaultMonthlyAllocation = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    IsDemo = table.Column<bool>(type: "INTEGER", nullable: false),
                    PlanType = table.Column<string>(type: "TEXT", nullable: true),
                    TotalSeats = table.Column<int>(type: "INTEGER", nullable: false),
                    ActiveSeatsThisCycle = table.Column<int>(type: "INTEGER", nullable: false),
                    InactiveSeatsThisCycle = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSyncedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSyncMessage = table.Column<string>(type: "TEXT", nullable: true),
                    LastSyncFailed = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Organizations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RoleMappings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Role = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleMappings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RequireTransferApproval = table.Column<bool>(type: "INTEGER", nullable: false),
                    ApprovalThreshold = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    MinimumKeep = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    RequireBudgetAuthority = table.Column<bool>(type: "INTEGER", nullable: false),
                    GitHubWriteBackEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastClosedPeriod = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Login = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, collation: "NOCASE"),
                    HasGitHubAccount = table.Column<bool>(type: "INTEGER", nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true, collation: "NOCASE"),
                    EmailSource = table.Column<string>(type: "TEXT", nullable: true),
                    EntraObjectId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    GitHubId = table.Column<long>(type: "INTEGER", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    AvatarUrl = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastLoginUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AssignedRoles = table.Column<int>(type: "INTEGER", nullable: false),
                    DirectoryRoles = table.Column<int>(type: "INTEGER", nullable: false),
                    DirectoryClaims = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    IsDisabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    DisabledUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DisabledReason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    DepartureHandledUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DirectoryCheckedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    MonthlyBudgetOverride = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    OverrideNote = table.Column<string>(type: "TEXT", nullable: true),
                    CostCenter = table.Column<string>(type: "TEXT", nullable: true),
                    PrefersDollars = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Alerts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Key = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Severity = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Count = table.Column<int>(type: "INTEGER", nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ResolvedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AcknowledgedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AcknowledgedById = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Alerts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Alerts_Users_AcknowledgedById",
                        column: x => x.AcknowledgedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "BudgetExceptions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    StartDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    IsApproved = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsCancelled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BudgetExceptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BudgetExceptions_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_BudgetExceptions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CreditPoolMembers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CreditPoolId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreditPoolMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CreditPoolMembers_CreditPools_CreditPoolId",
                        column: x => x.CreditPoolId,
                        principalTable: "CreditPools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CreditPoolMembers_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GitHubBudgets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    BudgetId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    DesiredDollars = table.Column<int>(type: "INTEGER", nullable: true),
                    GitHubDollars = table.Column<int>(type: "INTEGER", nullable: true),
                    LastWrittenDollars = table.Column<int>(type: "INTEGER", nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: true),
                    LastCheckedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastWrittenUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitHubBudgets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GitHubBudgets_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GitHubOperations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Succeeded = table.Column<bool>(type: "INTEGER", nullable: false),
                    Before = table.Column<int>(type: "INTEGER", nullable: true),
                    After = table.Column<int>(type: "INTEGER", nullable: true),
                    Message = table.Column<string>(type: "TEXT", nullable: true),
                    ActorUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitHubOperations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GitHubOperations_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Ledger",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Period = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    OrganizationId = table.Column<int>(type: "INTEGER", nullable: true),
                    CounterpartyUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreditPoolId = table.Column<int>(type: "INTEGER", nullable: true),
                    BudgetRequestId = table.Column<int>(type: "INTEGER", nullable: true),
                    TransferId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Ledger", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Ledger_CreditPools_CreditPoolId",
                        column: x => x.CreditPoolId,
                        principalTable: "CreditPools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Ledger_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Ledger_Users_CounterpartyUserId",
                        column: x => x.CounterpartyUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Ledger_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Ledger_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Memberships",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    OrganizationId = table.Column<int>(type: "INTEGER", nullable: false),
                    IsOrgAdmin = table.Column<bool>(type: "INTEGER", nullable: false),
                    HasSeat = table.Column<bool>(type: "INTEGER", nullable: false),
                    PlanType = table.Column<string>(type: "TEXT", nullable: true),
                    SeatCreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastActivityUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastActivityEditor = table.Column<string>(type: "TEXT", nullable: true),
                    PendingCancellationDate = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Memberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Memberships_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Memberships_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PoolEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CreditPoolId = table.Column<int>(type: "INTEGER", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Period = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PoolEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PoolEntries_CreditPools_CreditPoolId",
                        column: x => x.CreditPoolId,
                        principalTable: "CreditPools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PoolEntries_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PoolEntries_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Requests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RequesterId = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetId = table.Column<int>(type: "INTEGER", nullable: true),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ResolvedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ResolvedById = table.Column<int>(type: "INTEGER", nullable: true),
                    ResponseNote = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Requests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Requests_Users_RequesterId",
                        column: x => x.RequesterId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Requests_Users_ResolvedById",
                        column: x => x.ResolvedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Requests_Users_TargetId",
                        column: x => x.TargetId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "UsageDetails",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OrganizationId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    Period = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false),
                    Day = table.Column<int>(type: "INTEGER", nullable: true),
                    Product = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Sku = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Quantity = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Cost = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageDetails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UsageDetails_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UsageDetails_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Proposals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    OrganizationId = table.Column<int>(type: "INTEGER", nullable: true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    BudgetExceptionId = table.Column<int>(type: "INTEGER", nullable: true),
                    Value = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    SpendChange = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    Period = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ProposedById = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DecidedById = table.Column<int>(type: "INTEGER", nullable: true),
                    DecidedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DecisionNote = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Proposals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Proposals_BudgetExceptions_BudgetExceptionId",
                        column: x => x.BudgetExceptionId,
                        principalTable: "BudgetExceptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Proposals_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Proposals_Users_DecidedById",
                        column: x => x.DecidedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Proposals_Users_ProposedById",
                        column: x => x.ProposedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Proposals_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Usage",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MembershipId = table.Column<int>(type: "INTEGER", nullable: false),
                    Period = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false),
                    Used = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Usage", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Usage_Memberships_MembershipId",
                        column: x => x.MembershipId,
                        principalTable: "Memberships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PendingTransfers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FromUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    ToUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    BudgetRequestId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ResolvedById = table.Column<int>(type: "INTEGER", nullable: true),
                    ResolvedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ResponseNote = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingTransfers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PendingTransfers_Requests_BudgetRequestId",
                        column: x => x.BudgetRequestId,
                        principalTable: "Requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PendingTransfers_Users_FromUserId",
                        column: x => x.FromUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PendingTransfers_Users_ResolvedById",
                        column: x => x.ResolvedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PendingTransfers_Users_ToUserId",
                        column: x => x.ToUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_AcknowledgedById",
                table: "Alerts",
                column: "AcknowledgedById");

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_Key_ResolvedUtc",
                table: "Alerts",
                columns: new[] { "Key", "ResolvedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ApiCalls_CreatedUtc",
                table: "ApiCalls",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_Action",
                table: "AuditLog",
                column: "Action");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_CreatedUtc",
                table: "AuditLog",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_BudgetExceptions_CreatedByUserId",
                table: "BudgetExceptions",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_BudgetExceptions_UserId_EndDate",
                table: "BudgetExceptions",
                columns: new[] { "UserId", "EndDate" });

            migrationBuilder.CreateIndex(
                name: "IX_CreditPoolMembers_CreditPoolId_UserId",
                table: "CreditPoolMembers",
                columns: new[] { "CreditPoolId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CreditPoolMembers_UserId",
                table: "CreditPoolMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_GitHubBudgets_UserId",
                table: "GitHubBudgets",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GitHubOperations_CreatedUtc",
                table: "GitHubOperations",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_GitHubOperations_UserId",
                table: "GitHubOperations",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Ledger_CounterpartyUserId",
                table: "Ledger",
                column: "CounterpartyUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Ledger_CreatedByUserId",
                table: "Ledger",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Ledger_CreditPoolId",
                table: "Ledger",
                column: "CreditPoolId");

            migrationBuilder.CreateIndex(
                name: "IX_Ledger_OrganizationId",
                table: "Ledger",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_Ledger_TransferId",
                table: "Ledger",
                column: "TransferId");

            migrationBuilder.CreateIndex(
                name: "IX_Ledger_UserId_Period",
                table: "Ledger",
                columns: new[] { "UserId", "Period" });

            migrationBuilder.CreateIndex(
                name: "IX_Memberships_OrganizationId_UserId",
                table: "Memberships",
                columns: new[] { "OrganizationId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Memberships_UserId",
                table: "Memberships",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Organizations_Login",
                table: "Organizations",
                column: "Login",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PendingTransfers_BudgetRequestId",
                table: "PendingTransfers",
                column: "BudgetRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingTransfers_FromUserId",
                table: "PendingTransfers",
                column: "FromUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingTransfers_ResolvedById",
                table: "PendingTransfers",
                column: "ResolvedById");

            migrationBuilder.CreateIndex(
                name: "IX_PendingTransfers_Status",
                table: "PendingTransfers",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_PendingTransfers_ToUserId",
                table: "PendingTransfers",
                column: "ToUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PoolEntries_CreatedByUserId",
                table: "PoolEntries",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PoolEntries_CreditPoolId_Period",
                table: "PoolEntries",
                columns: new[] { "CreditPoolId", "Period" });

            migrationBuilder.CreateIndex(
                name: "IX_PoolEntries_UserId",
                table: "PoolEntries",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Proposals_BudgetExceptionId",
                table: "Proposals",
                column: "BudgetExceptionId");

            migrationBuilder.CreateIndex(
                name: "IX_Proposals_DecidedById",
                table: "Proposals",
                column: "DecidedById");

            migrationBuilder.CreateIndex(
                name: "IX_Proposals_OrganizationId",
                table: "Proposals",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_Proposals_ProposedById",
                table: "Proposals",
                column: "ProposedById");

            migrationBuilder.CreateIndex(
                name: "IX_Proposals_Status",
                table: "Proposals",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Proposals_UserId",
                table: "Proposals",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Requests_RequesterId",
                table: "Requests",
                column: "RequesterId");

            migrationBuilder.CreateIndex(
                name: "IX_Requests_ResolvedById",
                table: "Requests",
                column: "ResolvedById");

            migrationBuilder.CreateIndex(
                name: "IX_Requests_Status",
                table: "Requests",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Requests_TargetId",
                table: "Requests",
                column: "TargetId");

            migrationBuilder.CreateIndex(
                name: "IX_RoleMappings_Source_Value_Role",
                table: "RoleMappings",
                columns: new[] { "Source", "Value", "Role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Usage_MembershipId_Period",
                table: "Usage",
                columns: new[] { "MembershipId", "Period" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UsageDetails_OrganizationId",
                table: "UsageDetails",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_UsageDetails_Period_OrganizationId_UserId_Day",
                table: "UsageDetails",
                columns: new[] { "Period", "OrganizationId", "UserId", "Day" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageDetails_UserId",
                table: "UsageDetails",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_Users_EntraObjectId",
                table: "Users",
                column: "EntraObjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_GitHubId",
                table: "Users",
                column: "GitHubId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Login",
                table: "Users",
                column: "Login",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Alerts");

            migrationBuilder.DropTable(
                name: "ApiCalls");

            migrationBuilder.DropTable(
                name: "AuditLog");

            migrationBuilder.DropTable(
                name: "CreditPoolMembers");

            migrationBuilder.DropTable(
                name: "GitHubBudgets");

            migrationBuilder.DropTable(
                name: "GitHubOperations");

            migrationBuilder.DropTable(
                name: "Ledger");

            migrationBuilder.DropTable(
                name: "PendingTransfers");

            migrationBuilder.DropTable(
                name: "PoolEntries");

            migrationBuilder.DropTable(
                name: "Proposals");

            migrationBuilder.DropTable(
                name: "RoleMappings");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "Usage");

            migrationBuilder.DropTable(
                name: "UsageDetails");

            migrationBuilder.DropTable(
                name: "Requests");

            migrationBuilder.DropTable(
                name: "CreditPools");

            migrationBuilder.DropTable(
                name: "BudgetExceptions");

            migrationBuilder.DropTable(
                name: "Memberships");

            migrationBuilder.DropTable(
                name: "Organizations");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
