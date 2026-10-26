using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PendingModelSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "Options_Files_Paths",
                table: "restore_operations",
                type: "text[]",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Options_Files_RelativeDirectory",
                table: "restore_operations",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Options_Files_Paths",
                table: "restore_operations");

            migrationBuilder.DropColumn(
                name: "Options_Files_RelativeDirectory",
                table: "restore_operations");
        }
    }
}
