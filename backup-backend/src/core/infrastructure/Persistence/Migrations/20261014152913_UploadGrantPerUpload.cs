using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UploadGrantPerUpload : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Grants are short leases (10 s) that every upload renews: dropping the live ones only makes uploads ask for a slice again.
            migrationBuilder.Sql("DELETE FROM upload_bandwidth_grants;");

            migrationBuilder.DropPrimaryKey(
                name: "PK_upload_bandwidth_grants",
                table: "upload_bandwidth_grants");

            migrationBuilder.AddColumn<Guid>(
                name: "UploadId",
                table: "upload_bandwidth_grants",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddPrimaryKey(
                name: "PK_upload_bandwidth_grants",
                table: "upload_bandwidth_grants",
                column: "UploadId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // An attempt may have several grants now; the old key allows one.
            migrationBuilder.Sql("DELETE FROM upload_bandwidth_grants;");

            migrationBuilder.DropPrimaryKey(
                name: "PK_upload_bandwidth_grants",
                table: "upload_bandwidth_grants");

            migrationBuilder.DropColumn(
                name: "UploadId",
                table: "upload_bandwidth_grants");

            migrationBuilder.AddPrimaryKey(
                name: "PK_upload_bandwidth_grants",
                table: "upload_bandwidth_grants",
                column: "AttemptId");
        }
    }
}
