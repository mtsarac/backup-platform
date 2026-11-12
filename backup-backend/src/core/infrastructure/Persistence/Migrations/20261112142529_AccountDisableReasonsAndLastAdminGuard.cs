using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AccountDisableReasonsAndLastAdminGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "IsDisabled",
                table: "users",
                newName: "IsSecurityDisabled");

            migrationBuilder.AddColumn<bool>(
                name: "IsManuallyDisabled",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsNoMembershipDisabled",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ManualDisableReason",
                table: "users",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsManuallyDisabled",
                table: "users");

            migrationBuilder.DropColumn(
                name: "IsNoMembershipDisabled",
                table: "users");

            migrationBuilder.DropColumn(
                name: "ManualDisableReason",
                table: "users");

            migrationBuilder.RenameColumn(
                name: "IsSecurityDisabled",
                table: "users",
                newName: "IsDisabled");
        }
    }
}
