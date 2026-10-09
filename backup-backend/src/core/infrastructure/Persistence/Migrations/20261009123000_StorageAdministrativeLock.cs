using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StorageAdministrativeLock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AdministrativelyDisabled",
                table: "storage_connections",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Backward compatibility: If an older platform-managed connection was disabled before this distinction,
            // that disable was administrative. Set AdministrativelyDisabled=true and preserve Enabled=true so owner preference stays normal.
            migrationBuilder.Sql("""
                UPDATE storage_connections
                SET "AdministrativelyDisabled" = true, "Enabled" = true
                WHERE "Ownership" = 'PlatformManaged' AND NOT "Enabled";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore previous single-flag representation: any administrative lock collapses back into Enabled = false.
            migrationBuilder.Sql("""
                UPDATE storage_connections
                SET "Enabled" = false
                WHERE "AdministrativelyDisabled";
                """);

            migrationBuilder.DropColumn(
                name: "AdministrativelyDisabled",
                table: "storage_connections");
        }
    }
}
