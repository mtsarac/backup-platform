using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NotificationRecipientConfirmation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RecipientVerifiedAt",
                table: "notification_channels",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_notification_channels_recipient_verified",
                table: "notification_channels",
                sql: "\"RecipientVerifiedAt\" IS NULL OR \"Kind\" = 'Email'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_notification_channels_recipient_verified",
                table: "notification_channels");

            migrationBuilder.DropColumn(
                name: "RecipientVerifiedAt",
                table: "notification_channels");
        }
    }
}
