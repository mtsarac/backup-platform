using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DownloadSessionShareId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ShareId",
                table: "download_sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_download_sessions_ShareId",
                table: "download_sessions",
                column: "ShareId");

            migrationBuilder.AddForeignKey(
                name: "FK_download_sessions_backup_shares_ShareId",
                table: "download_sessions",
                column: "ShareId",
                principalTable: "backup_shares",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_download_sessions_backup_shares_ShareId",
                table: "download_sessions");

            migrationBuilder.DropIndex(
                name: "IX_download_sessions_ShareId",
                table: "download_sessions");

            migrationBuilder.DropColumn(
                name: "ShareId",
                table: "download_sessions");
        }
    }
}
