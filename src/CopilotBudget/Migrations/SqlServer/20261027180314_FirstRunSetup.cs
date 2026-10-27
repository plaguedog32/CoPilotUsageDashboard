using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CopilotBudget.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class FirstRunSetup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "SetupCodeExpiresUtc",
                table: "Settings",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SetupCodeFailures",
                table: "Settings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SetupCodeHash",
                table: "Settings",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SetupCompletedUtc",
                table: "Settings",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SetupCodeExpiresUtc",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "SetupCodeFailures",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "SetupCodeHash",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "SetupCompletedUtc",
                table: "Settings");
        }
    }
}
