using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SupportAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SupportSessionId",
                table: "restore_operations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SupportSessionId",
                table: "download_sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SupportSessionId",
                table: "audit_events",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "support_access_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlatformAdminId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Permissions = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ClosedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CloseReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestFingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_support_access_sessions", x => x.Id);
                    table.UniqueConstraint("AK_support_access_sessions_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_support_access_sessions_closer", "(\"Status\" IN ('Closed','Revoked')) = (\"ClosedBy\" IS NOT NULL) AND (\"CloseReason\" IS NULL OR \"Status\" = 'Revoked')");
                    table.CheckConstraint("ck_support_access_sessions_end", "(\"Status\" = 'Active') = (\"ClosedAt\" IS NULL)");
                    table.CheckConstraint("ck_support_access_sessions_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_support_access_sessions_key", "length(\"IdempotencyKey\") BETWEEN 1 AND 128 AND length(\"RequestFingerprint\") BETWEEN 1 AND 128");
                    table.CheckConstraint("ck_support_access_sessions_permissions", "\"Permissions\" BETWEEN 1 AND 7 AND (\"Permissions\" & 1) = 1");
                    table.CheckConstraint("ck_support_access_sessions_reason", "\"Reason\" ~ '[^[:space:]]' AND length(\"Reason\") <= 500");
                    table.CheckConstraint("ck_support_access_sessions_status", "\"Status\" IN ('Active','Expired','Closed','Revoked')");
                    table.CheckConstraint("ck_support_access_sessions_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_support_access_sessions_window", "\"ExpiresAt\" > \"StartedAt\" AND \"ExpiresAt\" <= \"StartedAt\" + interval '4 hours'");
                    table.ForeignKey(
                        name: "FK_support_access_sessions_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_support_access_sessions_users_PlatformAdminId",
                        column: x => x.PlatformAdminId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_restore_operations_support",
                table: "restore_operations",
                column: "SupportSessionId",
                filter: "\"SupportSessionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_restore_operations_SupportSessionId_TenantId",
                table: "restore_operations",
                columns: new[] { "SupportSessionId", "TenantId" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_restore_operations_support",
                table: "restore_operations",
                sql: "\"SupportSessionId\" IS NULL OR (\"Mode\" = 'NewDatabase' AND \"RequestedByUserId\" IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_download_sessions_support",
                table: "download_sessions",
                column: "SupportSessionId",
                filter: "\"SupportSessionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_download_sessions_SupportSessionId_TenantId",
                table: "download_sessions",
                columns: new[] { "SupportSessionId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_support_session",
                table: "audit_events",
                columns: new[] { "SupportSessionId", "At" },
                filter: "\"SupportSessionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_support_access_sessions_admin",
                table: "support_access_sessions",
                columns: new[] { "PlatformAdminId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "ix_support_access_sessions_expiry",
                table: "support_access_sessions",
                column: "ExpiresAt",
                filter: "\"Status\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "ix_support_access_sessions_tenant",
                table: "support_access_sessions",
                columns: new[] { "TenantId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_support_access_sessions_TenantId",
                table: "support_access_sessions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ux_support_access_sessions_active",
                table: "support_access_sessions",
                columns: new[] { "TenantId", "PlatformAdminId" },
                unique: true,
                filter: "\"Status\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "ux_support_access_sessions_key",
                table: "support_access_sessions",
                columns: new[] { "TenantId", "PlatformAdminId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_download_sessions_support_access_sessions_SupportSessionId_~",
                table: "download_sessions",
                columns: new[] { "SupportSessionId", "TenantId" },
                principalTable: "support_access_sessions",
                principalColumns: new[] { "Id", "TenantId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_restore_operations_support_access_sessions_SupportSessionId~",
                table: "restore_operations",
                columns: new[] { "SupportSessionId", "TenantId" },
                principalTable: "support_access_sessions",
                principalColumns: new[] { "Id", "TenantId" },
                onDelete: ReferentialAction.Restrict);

            // The grant is fixed in the database, whatever the application does: who, for which tenant, with which permissions, why and for how long can
            // never change, and a session that has ended (expired, closed or revoked) can never change again, so it can neither be reactivated nor extended.
            migrationBuilder.Sql("""
                CREATE FUNCTION support_access_session_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'A support access session is never deleted.' USING ERRCODE = '23514';
                    END IF;
                    IF NEW."Id" IS DISTINCT FROM OLD."Id" OR NEW."TenantId" IS DISTINCT FROM OLD."TenantId"
                        OR NEW."PlatformAdminId" IS DISTINCT FROM OLD."PlatformAdminId" OR NEW."Permissions" IS DISTINCT FROM OLD."Permissions"
                        OR NEW."Reason" IS DISTINCT FROM OLD."Reason" OR NEW."StartedAt" IS DISTINCT FROM OLD."StartedAt"
                        OR NEW."ExpiresAt" IS DISTINCT FROM OLD."ExpiresAt" OR NEW."CreatedAt" IS DISTINCT FROM OLD."CreatedAt"
                        OR NEW."IdempotencyKey" IS DISTINCT FROM OLD."IdempotencyKey" OR NEW."RequestFingerprint" IS DISTINCT FROM OLD."RequestFingerprint" THEN
                        RAISE EXCEPTION 'The tenant, administrator, permissions, reason and window of a support session are immutable.' USING ERRCODE = '23514';
                    END IF;
                    IF OLD."Status" <> 'Active' THEN
                        RAISE EXCEPTION 'An ended support session is final.' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER trg_support_access_session_guard BEFORE UPDATE OR DELETE ON support_access_sessions
                    FOR EACH ROW EXECUTE FUNCTION support_access_session_guard();
                """);
            migrationBuilder.Sql("""
                CREATE FUNCTION support_session_link_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW."SupportSessionId" IS DISTINCT FROM OLD."SupportSessionId" THEN
                        RAISE EXCEPTION 'The support session of an operation cannot change.' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER trg_download_sessions_support_link BEFORE UPDATE ON download_sessions
                    FOR EACH ROW EXECUTE FUNCTION support_session_link_guard();
                CREATE TRIGGER trg_restore_operations_support_link BEFORE UPDATE ON restore_operations
                    FOR EACH ROW EXECUTE FUNCTION support_session_link_guard();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER trg_restore_operations_support_link ON restore_operations; DROP TRIGGER trg_download_sessions_support_link ON download_sessions; DROP FUNCTION support_session_link_guard();");
            migrationBuilder.Sql("DROP TRIGGER trg_support_access_session_guard ON support_access_sessions; DROP FUNCTION support_access_session_guard();");
            migrationBuilder.DropForeignKey(
                name: "FK_download_sessions_support_access_sessions_SupportSessionId_~",
                table: "download_sessions");

            migrationBuilder.DropForeignKey(
                name: "FK_restore_operations_support_access_sessions_SupportSessionId~",
                table: "restore_operations");

            migrationBuilder.DropTable(
                name: "support_access_sessions");

            migrationBuilder.DropIndex(
                name: "ix_restore_operations_support",
                table: "restore_operations");

            migrationBuilder.DropIndex(
                name: "IX_restore_operations_SupportSessionId_TenantId",
                table: "restore_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_restore_operations_support",
                table: "restore_operations");

            migrationBuilder.DropIndex(
                name: "ix_download_sessions_support",
                table: "download_sessions");

            migrationBuilder.DropIndex(
                name: "IX_download_sessions_SupportSessionId_TenantId",
                table: "download_sessions");

            migrationBuilder.DropIndex(
                name: "ix_audit_events_support_session",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "SupportSessionId",
                table: "restore_operations");

            migrationBuilder.DropColumn(
                name: "SupportSessionId",
                table: "download_sessions");

            migrationBuilder.DropColumn(
                name: "SupportSessionId",
                table: "audit_events");
        }
    }
}
