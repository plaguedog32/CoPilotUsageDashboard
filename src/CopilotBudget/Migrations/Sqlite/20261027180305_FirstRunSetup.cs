using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CopilotBudget.Migrations.Sqlite
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
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SetupCodeFailures",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SetupCodeHash",
                table: "Settings",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SetupCompletedUtc",
                table: "Settings",
                type: "TEXT",
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
