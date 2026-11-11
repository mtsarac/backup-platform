using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SessionLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM user_sessions; DELETE FROM login_throttle_entries;");
            migrationBuilder.DropPrimaryKey(
                name: "PK_login_throttle_entries",
                table: "login_throttle_entries");

            migrationBuilder.DropIndex(
                name: "IX_login_throttle_entries_WindowStart",
                table: "login_throttle_entries");

            migrationBuilder.RenameColumn(
                name: "ExpiresAt",
                table: "user_sessions",
                newName: "LastActivityAt");

            migrationBuilder.RenameColumn(
                name: "WindowStart",
                table: "login_throttle_entries",
                newName: "LockedUntil");

            migrationBuilder.AddColumn<int>(
                name: "AuthenticationState",
                table: "user_sessions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAt",
                table: "user_sessions",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<string>(
                name: "Device",
                table: "user_sessions",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "IpAddress",
                table: "user_sessions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "RememberMe",
                table: "user_sessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "login_throttle_entries",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Scope",
                table: "login_throttle_entries",
                type: "character varying(7)",
                maxLength: 7,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastFailureAt",
                table: "login_throttle_entries",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddPrimaryKey(
                name: "PK_login_throttle_entries",
                table: "login_throttle_entries",
                columns: new[] { "Kind", "Scope", "Identifier" });

            migrationBuilder.CreateIndex(
                name: "IX_login_throttle_entries_LastFailureAt",
                table: "login_throttle_entries",
                column: "LastFailureAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM login_throttle_entries;");
            migrationBuilder.DropPrimaryKey(
                name: "PK_login_throttle_entries",
                table: "login_throttle_entries");

            migrationBuilder.DropIndex(
                name: "IX_login_throttle_entries_LastFailureAt",
                table: "login_throttle_entries");

            migrationBuilder.DropColumn(
                name: "AuthenticationState",
                table: "user_sessions");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "user_sessions");

            migrationBuilder.DropColumn(
                name: "Device",
                table: "user_sessions");

            migrationBuilder.DropColumn(
                name: "IpAddress",
                table: "user_sessions");

            migrationBuilder.DropColumn(
                name: "RememberMe",
                table: "user_sessions");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "login_throttle_entries");

            migrationBuilder.DropColumn(
                name: "Scope",
                table: "login_throttle_entries");

            migrationBuilder.DropColumn(
                name: "LastFailureAt",
                table: "login_throttle_entries");

            migrationBuilder.RenameColumn(
                name: "LastActivityAt",
                table: "user_sessions",
                newName: "ExpiresAt");

            migrationBuilder.RenameColumn(
                name: "LockedUntil",
                table: "login_throttle_entries",
                newName: "WindowStart");

            migrationBuilder.AddPrimaryKey(
                name: "PK_login_throttle_entries",
                table: "login_throttle_entries",
                column: "Identifier");

            migrationBuilder.CreateIndex(
                name: "IX_login_throttle_entries_WindowStart",
                table: "login_throttle_entries",
                column: "WindowStart");
        }
    }
}
