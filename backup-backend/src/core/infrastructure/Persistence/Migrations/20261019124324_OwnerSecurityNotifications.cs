using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OwnerSecurityNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SecurityOwnerUserId",
                table: "notification_channels",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_notification_channels_SecurityOwnerUserId",
                table: "notification_channels",
                column: "SecurityOwnerUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_notification_channels_users_SecurityOwnerUserId",
                table: "notification_channels",
                column: "SecurityOwnerUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_notification_channels_users_SecurityOwnerUserId",
                table: "notification_channels");

            migrationBuilder.DropIndex(
                name: "IX_notification_channels_SecurityOwnerUserId",
                table: "notification_channels");

            migrationBuilder.DropColumn(
                name: "SecurityOwnerUserId",
                table: "notification_channels");
        }
    }
}
