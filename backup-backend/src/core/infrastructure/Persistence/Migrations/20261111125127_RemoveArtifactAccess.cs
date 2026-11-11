using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveArtifactAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_download_sessions_artifact_break_glass_requests_BreakGlassR~",
                table: "download_sessions");

            migrationBuilder.DropForeignKey(
                name: "FK_notification_channels_users_SecurityOwnerUserId",
                table: "notification_channels");

            migrationBuilder.DropTable(
                name: "artifact_break_glass_requests");

            migrationBuilder.DropIndex(
                name: "IX_notification_channels_SecurityOwnerUserId",
                table: "notification_channels");

            migrationBuilder.DropIndex(
                name: "ix_download_sessions_break_glass",
                table: "download_sessions");

            migrationBuilder.DropIndex(
                name: "IX_download_sessions_BreakGlassRequestId_TenantId",
                table: "download_sessions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_download_sessions_channel",
                table: "download_sessions");

            migrationBuilder.DropColumn(
                name: "SecurityOwnerUserId",
                table: "notification_channels");

            migrationBuilder.DropColumn(
                name: "BreakGlassRequestId",
                table: "download_sessions");

            migrationBuilder.AddCheckConstraint(
                name: "ck_download_sessions_channel",
                table: "download_sessions",
                sql: "NOT \"Decrypted\" OR \"SupportSessionId\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_download_sessions_channel",
                table: "download_sessions");

            migrationBuilder.AddColumn<Guid>(
                name: "SecurityOwnerUserId",
                table: "notification_channels",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BreakGlassRequestId",
                table: "download_sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "artifact_break_glass_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecidedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    DecisionReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DownloadSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_artifact_break_glass_requests", x => x.Id);
                    table.UniqueConstraint("AK_artifact_break_glass_requests_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_artifact_break_glass_consumed", "(\"Status\" = 'Consumed') = (\"ConsumedAt\" IS NOT NULL AND \"DownloadSessionId\" IS NOT NULL)");
                    table.CheckConstraint("ck_artifact_break_glass_decision", "(\"Status\" IN ('Approved','Denied','Consumed')) = (\"DecidedBy\" IS NOT NULL AND \"DecidedAt\" IS NOT NULL) OR \"Status\" = 'Expired'");
                    table.CheckConstraint("ck_artifact_break_glass_dual_control", "\"DecidedBy\" IS NULL OR \"DecidedBy\" <> \"RequestedBy\"");
                    table.CheckConstraint("ck_artifact_break_glass_reason", "\"Reason\" ~ '[^[:space:]]' AND length(\"Reason\") <= 500");
                    table.CheckConstraint("ck_artifact_break_glass_requests_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_artifact_break_glass_requests_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_artifact_break_glass_status", "\"Status\" IN ('Pending','Approved','Denied','Consumed','Expired')");
                    table.CheckConstraint("ck_artifact_break_glass_window", "\"ExpiresAt\" > \"CreatedAt\" AND \"ExpiresAt\" <= \"CreatedAt\" + interval '4 hours'");
                    table.ForeignKey(
                        name: "FK_artifact_break_glass_requests_backup_artifacts_ArtifactId_T~",
                        columns: x => new { x.ArtifactId, x.TenantId },
                        principalTable: "backup_artifacts",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_artifact_break_glass_requests_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_artifact_break_glass_requests_users_DecidedBy",
                        column: x => x.DecidedBy,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_artifact_break_glass_requests_users_RequestedBy",
                        column: x => x.RequestedBy,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_notification_channels_SecurityOwnerUserId",
                table: "notification_channels",
                column: "SecurityOwnerUserId");

            migrationBuilder.CreateIndex(
                name: "ix_download_sessions_break_glass",
                table: "download_sessions",
                column: "BreakGlassRequestId",
                filter: "\"BreakGlassRequestId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_download_sessions_BreakGlassRequestId_TenantId",
                table: "download_sessions",
                columns: new[] { "BreakGlassRequestId", "TenantId" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_download_sessions_channel",
                table: "download_sessions",
                sql: "(\"BreakGlassRequestId\" IS NULL OR \"Decrypted\") AND (NOT \"Decrypted\" OR \"SupportSessionId\" IS NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_artifact_break_glass_expiry",
                table: "artifact_break_glass_requests",
                column: "ExpiresAt",
                filter: "\"Status\" IN ('Pending','Approved')");

            migrationBuilder.CreateIndex(
                name: "IX_artifact_break_glass_requests_ArtifactId_TenantId",
                table: "artifact_break_glass_requests",
                columns: new[] { "ArtifactId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_artifact_break_glass_requests_DecidedBy",
                table: "artifact_break_glass_requests",
                column: "DecidedBy");

            migrationBuilder.CreateIndex(
                name: "IX_artifact_break_glass_requests_RequestedBy",
                table: "artifact_break_glass_requests",
                column: "RequestedBy");

            migrationBuilder.CreateIndex(
                name: "IX_artifact_break_glass_requests_TenantId",
                table: "artifact_break_glass_requests",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ix_artifact_break_glass_tenant",
                table: "artifact_break_glass_requests",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_download_sessions_artifact_break_glass_requests_BreakGlassR~",
                table: "download_sessions",
                columns: new[] { "BreakGlassRequestId", "TenantId" },
                principalTable: "artifact_break_glass_requests",
                principalColumns: new[] { "Id", "TenantId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_notification_channels_users_SecurityOwnerUserId",
                table: "notification_channels",
                column: "SecurityOwnerUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
