using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ArtifactRecoveryAndBreakGlass : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tenant_wrap_keys_TenantId",
                table: "tenant_wrap_keys");

            migrationBuilder.DropCheckConstraint(
                name: "ck_backup_artifact_encryption",
                table: "backup_artifacts");

            migrationBuilder.AddColumn<Guid>(
                name: "BreakGlassRequestId",
                table: "download_sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Decrypted",
                table: "download_sessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "EncryptionRecipientsJson",
                table: "backup_artifacts",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "artifact_break_glass_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DecidedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecisionReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DownloadSessionId = table.Column<Guid>(type: "uuid", nullable: true),
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

            migrationBuilder.CreateTable(
                name: "tenant_recovery_keys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    KeyId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PublicKeySpki = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_recovery_keys", x => x.Id);
                    table.UniqueConstraint("AK_tenant_recovery_keys_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_tenant_recovery_keys_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_tenant_recovery_keys_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_tenant_recovery_keys_version", "\"Version\" >= 1");
                    table.ForeignKey(
                        name: "FK_tenant_recovery_keys_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_tenant_recovery_keys_users_CreatedBy",
                        column: x => x.CreatedBy,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_tenant_wrap_keys_active",
                table: "tenant_wrap_keys",
                column: "TenantId",
                unique: true,
                filter: "\"State\" = 'Active'");

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

            migrationBuilder.AddCheckConstraint(
                name: "ck_backup_artifact_encryption",
                table: "backup_artifacts",
                sql: "(\"Format\" = 'v1-plain' AND \"EncryptionKeyId\" IS NULL AND \"EncryptionKeyVersion\" IS NULL AND \"EncryptionWrappedDekBase64\" IS NULL AND \"EncryptionRecipientsJson\" IS NULL) OR (\"Format\" = 'v2-bpe' AND \"EncryptionKeyId\" IS NOT NULL AND \"EncryptionKeyVersion\" >= 1 AND \"EncryptionWrappedDekBase64\" IS NOT NULL)");

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

            migrationBuilder.CreateIndex(
                name: "IX_tenant_recovery_keys_CreatedBy",
                table: "tenant_recovery_keys",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "ux_tenant_recovery_keys_tenant",
                table: "tenant_recovery_keys",
                column: "TenantId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_download_sessions_artifact_break_glass_requests_BreakGlassR~",
                table: "download_sessions",
                columns: new[] { "BreakGlassRequestId", "TenantId" },
                principalTable: "artifact_break_glass_requests",
                principalColumns: new[] { "Id", "TenantId" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_download_sessions_artifact_break_glass_requests_BreakGlassR~",
                table: "download_sessions");

            migrationBuilder.DropTable(
                name: "artifact_break_glass_requests");

            migrationBuilder.DropTable(
                name: "tenant_recovery_keys");

            migrationBuilder.DropIndex(
                name: "ux_tenant_wrap_keys_active",
                table: "tenant_wrap_keys");

            migrationBuilder.DropIndex(
                name: "ix_download_sessions_break_glass",
                table: "download_sessions");

            migrationBuilder.DropIndex(
                name: "IX_download_sessions_BreakGlassRequestId_TenantId",
                table: "download_sessions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_download_sessions_channel",
                table: "download_sessions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_backup_artifact_encryption",
                table: "backup_artifacts");

            migrationBuilder.DropColumn(
                name: "BreakGlassRequestId",
                table: "download_sessions");

            migrationBuilder.DropColumn(
                name: "Decrypted",
                table: "download_sessions");

            migrationBuilder.DropColumn(
                name: "EncryptionRecipientsJson",
                table: "backup_artifacts");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_wrap_keys_TenantId",
                table: "tenant_wrap_keys",
                column: "TenantId");

            migrationBuilder.AddCheckConstraint(
                name: "ck_backup_artifact_encryption",
                table: "backup_artifacts",
                sql: "(\"Format\" = 'v1-plain' AND \"EncryptionKeyId\" IS NULL AND \"EncryptionKeyVersion\" IS NULL AND \"EncryptionWrappedDekBase64\" IS NULL) OR (\"Format\" = 'v2-bpe' AND \"EncryptionKeyId\" IS NOT NULL AND \"EncryptionKeyVersion\" >= 1 AND \"EncryptionWrappedDekBase64\" IS NOT NULL)");
        }
    }
}
