using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CopilotBudget.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class MonthEndRecipients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MonthEndRecipients",
                table: "Settings",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MonthEndToAdmins",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MonthEndRecipients",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "MonthEndToAdmins",
                table: "Settings");
        }
    }
}
