using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations;

/// <summary>An unsubscribed address keeps that refusal: a confirmation link mailed earlier can no longer verify it again.</summary>
public partial class NotificationChannelUnsubscribedAt : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>("UnsubscribedAt", "notification_channels", type: "timestamp with time zone", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn("UnsubscribedAt", "notification_channels");
}
