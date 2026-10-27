using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CopilotBudget.Migrations.SqlServer
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
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MonthEndToAdmins",
                table: "Settings",
                type: "bit",
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
