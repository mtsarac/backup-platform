using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UserNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_notification_deliveries_notification_events_EventId_TenantId",
                table: "notification_deliveries");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_notification_events_Id_TenantId",
                table: "notification_events");

            migrationBuilder.DropIndex(
                name: "IX_notification_events_TenantId",
                table: "notification_events");

            migrationBuilder.DropCheckConstraint(
                name: "ck_notification_events_id",
                table: "notification_events");

            migrationBuilder.DropCheckConstraint(
                name: "ck_notification_events_tenant",
                table: "notification_events");

            migrationBuilder.DropIndex(
                name: "IX_notification_deliveries_EventId_TenantId",
                table: "notification_deliveries");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "notification_events",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.CreateTable(
                name: "notification_history_settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    RetentionDays = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_history_settings", x => x.Id);
                    table.CheckConstraint("ck_notification_history_settings_retention", "\"RetentionDays\" BETWEEN 1 AND 36500");
                    table.CheckConstraint("ck_notification_history_settings_singleton", "\"Id\" = 1");
                });

            migrationBuilder.CreateTable(
                name: "user_notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReadAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_notifications_notification_events_EventId",
                        column: x => x.EventId,
                        principalTable: "notification_events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_user_notifications_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_notification_events_Id_TenantId",
                table: "notification_events",
                columns: new[] { "Id", "TenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_notification_events_global_dedup",
                table: "notification_events",
                column: "DedupKey",
                unique: true,
                filter: "\"TenantId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_user_notifications_EventId_UserId",
                table: "user_notifications",
                columns: new[] { "EventId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_notifications_UserId",
                table: "user_notifications",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_notification_deliveries_notification_events_EventId",
                table: "notification_deliveries",
                column: "EventId",
                principalTable: "notification_events",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.Sql("""
                ALTER TABLE notification_events ADD CONSTRAINT ck_notification_events_id CHECK ("Id" <> '00000000-0000-0000-0000-000000000000'::uuid);
                ALTER TABLE notification_events ADD CONSTRAINT ck_notification_events_tenant CHECK ("TenantId" IS NULL OR "TenantId" <> '00000000-0000-0000-0000-000000000000'::uuid);
                ALTER TABLE notification_deliveries ADD CONSTRAINT fk_notification_delivery_event_tenant
                    FOREIGN KEY ("EventId", "TenantId") REFERENCES notification_events ("Id", "TenantId") ON DELETE CASCADE;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_notification_deliveries_notification_events_EventId",
                table: "notification_deliveries");

            migrationBuilder.Sql("""
                ALTER TABLE notification_deliveries DROP CONSTRAINT fk_notification_delivery_event_tenant;
                ALTER TABLE notification_events DROP CONSTRAINT ck_notification_events_id;
                ALTER TABLE notification_events DROP CONSTRAINT ck_notification_events_tenant;
                """);

            migrationBuilder.DropTable(
                name: "notification_history_settings");

            migrationBuilder.DropTable(
                name: "user_notifications");

            migrationBuilder.DropIndex(
                name: "IX_notification_events_Id_TenantId",
                table: "notification_events");

            migrationBuilder.DropIndex(
                name: "ux_notification_events_global_dedup",
                table: "notification_events");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "notification_events",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_notification_events_Id_TenantId",
                table: "notification_events",
                columns: new[] { "Id", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_notification_events_TenantId",
                table: "notification_events",
                column: "TenantId");

            migrationBuilder.AddCheckConstraint(
                name: "ck_notification_events_id",
                table: "notification_events",
                sql: "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");

            migrationBuilder.AddCheckConstraint(
                name: "ck_notification_events_tenant",
                table: "notification_events",
                sql: "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");

            migrationBuilder.CreateIndex(
                name: "IX_notification_deliveries_EventId_TenantId",
                table: "notification_deliveries",
                columns: new[] { "EventId", "TenantId" });

            migrationBuilder.AddForeignKey(
                name: "FK_notification_deliveries_notification_events_EventId_TenantId",
                table: "notification_deliveries",
                columns: new[] { "EventId", "TenantId" },
                principalTable: "notification_events",
                principalColumns: new[] { "Id", "TenantId" },
                onDelete: ReferentialAction.Cascade);
        }
    }
}
