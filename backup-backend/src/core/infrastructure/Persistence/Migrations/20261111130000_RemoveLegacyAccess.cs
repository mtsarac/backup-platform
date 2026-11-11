using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations;

[DbContext(typeof(PlatformDbContext))]
[Migration("20261111130000_RemoveLegacyAccess")]
public sealed class RemoveLegacyAccess : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            LOCK TABLE download_sessions IN ACCESS EXCLUSIVE MODE;
            UPDATE download_sessions
            SET "State" = 'Cancelled', "ReleasedAt" = CURRENT_TIMESTAMP,
                "LeaseOwner" = NULL, "LeaseExpiresAt" = NULL
            WHERE "SupportSessionId" IS NOT NULL AND "ReleasedAt" IS NULL;
            """);

        migrationBuilder.Sql("""
            DROP TRIGGER trg_download_sessions_support_link ON download_sessions;
            DROP TRIGGER trg_restore_operations_support_link ON restore_operations;
            DROP FUNCTION public.support_session_link_guard();
            """);
        DownloadLifecycleGuard(migrationBuilder, legacy: false);

        migrationBuilder.DropForeignKey("FK_download_sessions_support_access_sessions_SupportSessionId_~", "download_sessions");
        migrationBuilder.DropForeignKey("FK_restore_operations_support_access_sessions_SupportSessionId~", "restore_operations");
        migrationBuilder.DropTable("support_access_sessions");
        migrationBuilder.DropCheckConstraint("ck_download_sessions_channel", "download_sessions");
        migrationBuilder.Sql("DROP FUNCTION public.support_access_session_guard();");
        migrationBuilder.DropCheckConstraint("ck_restore_operations_support", "restore_operations");
        migrationBuilder.DropIndex("ix_download_sessions_support", "download_sessions");
        migrationBuilder.DropIndex("IX_download_sessions_SupportSessionId_TenantId", "download_sessions");
        migrationBuilder.DropIndex("ix_restore_operations_support", "restore_operations");
        migrationBuilder.DropIndex("IX_restore_operations_SupportSessionId_TenantId", "restore_operations");
        migrationBuilder.DropColumn("SupportSessionId", "download_sessions");
        migrationBuilder.DropColumn("SupportSessionId", "restore_operations");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE support_access_sessions (
                "Id" uuid NOT NULL PRIMARY KEY,
                "TenantId" uuid NOT NULL REFERENCES tenants ("Id") ON DELETE RESTRICT,
                "PlatformAdminId" uuid NOT NULL REFERENCES users ("Id") ON DELETE RESTRICT,
                "Reason" varchar(500) NOT NULL,
                "Permissions" integer NOT NULL,
                "StartedAt" timestamptz NOT NULL,
                "ExpiresAt" timestamptz NOT NULL,
                "Status" varchar(16) NOT NULL,
                "ClosedAt" timestamptz,
                "ClosedBy" uuid,
                "CloseReason" varchar(500),
                "CreatedAt" timestamptz NOT NULL,
                "IdempotencyKey" varchar(128) NOT NULL,
                "RequestFingerprint" varchar(128) NOT NULL,
                CONSTRAINT "AK_support_access_sessions_Id_TenantId" UNIQUE ("Id", "TenantId"),
                CONSTRAINT ck_support_access_sessions_closer CHECK (("Status" IN ('Closed','Revoked')) = ("ClosedBy" IS NOT NULL) AND ("CloseReason" IS NULL OR "Status" = 'Revoked')),
                CONSTRAINT ck_support_access_sessions_end CHECK (("Status" = 'Active') = ("ClosedAt" IS NULL)),
                CONSTRAINT ck_support_access_sessions_id CHECK ("Id" <> '00000000-0000-0000-0000-000000000000'::uuid),
                CONSTRAINT ck_support_access_sessions_tenant CHECK ("TenantId" <> '00000000-0000-0000-0000-000000000000'::uuid),
                CONSTRAINT ck_support_access_sessions_key CHECK (length("IdempotencyKey") BETWEEN 1 AND 128 AND length("RequestFingerprint") BETWEEN 1 AND 128),
                CONSTRAINT ck_support_access_sessions_permissions CHECK ("Permissions" BETWEEN 1 AND 7 AND ("Permissions" & 1) = 1),
                CONSTRAINT ck_support_access_sessions_reason CHECK ("Reason" ~ '[^[:space:]]' AND length("Reason") <= 500),
                CONSTRAINT ck_support_access_sessions_status CHECK ("Status" IN ('Active','Expired','Closed','Revoked')),
                CONSTRAINT ck_support_access_sessions_window CHECK ("ExpiresAt" > "StartedAt" AND "ExpiresAt" <= "StartedAt" + interval '4 hours')
            );
            CREATE INDEX ix_support_access_sessions_admin ON support_access_sessions ("PlatformAdminId", "StartedAt");
            CREATE INDEX ix_support_access_sessions_expiry ON support_access_sessions ("ExpiresAt") WHERE "Status" = 'Active';
            CREATE INDEX ix_support_access_sessions_tenant ON support_access_sessions ("TenantId", "StartedAt");
            CREATE INDEX "IX_support_access_sessions_TenantId" ON support_access_sessions ("TenantId");
            CREATE UNIQUE INDEX ux_support_access_sessions_active ON support_access_sessions ("TenantId", "PlatformAdminId") WHERE "Status" = 'Active';
            CREATE UNIQUE INDEX ux_support_access_sessions_key ON support_access_sessions ("TenantId", "PlatformAdminId", "IdempotencyKey");
            ALTER TABLE download_sessions ADD COLUMN "SupportSessionId" uuid;
            ALTER TABLE restore_operations ADD COLUMN "SupportSessionId" uuid;
            ALTER TABLE download_sessions ADD CONSTRAINT "FK_download_sessions_support_access_sessions_SupportSessionId_~" FOREIGN KEY ("SupportSessionId", "TenantId") REFERENCES support_access_sessions ("Id", "TenantId") ON DELETE RESTRICT;
            ALTER TABLE restore_operations ADD CONSTRAINT "FK_restore_operations_support_access_sessions_SupportSessionId~" FOREIGN KEY ("SupportSessionId", "TenantId") REFERENCES support_access_sessions ("Id", "TenantId") ON DELETE RESTRICT;
            ALTER TABLE download_sessions ADD CONSTRAINT ck_download_sessions_channel CHECK (NOT "Decrypted" OR "SupportSessionId" IS NULL);
            ALTER TABLE restore_operations ADD CONSTRAINT ck_restore_operations_support CHECK ("SupportSessionId" IS NULL OR ("Mode" = 'NewDatabase' AND "RequestedByUserId" IS NOT NULL));
            CREATE INDEX ix_download_sessions_support ON download_sessions ("SupportSessionId") WHERE "SupportSessionId" IS NOT NULL;
            CREATE INDEX "IX_download_sessions_SupportSessionId_TenantId" ON download_sessions ("SupportSessionId", "TenantId");
            CREATE INDEX ix_restore_operations_support ON restore_operations ("SupportSessionId") WHERE "SupportSessionId" IS NOT NULL;
            CREATE INDEX "IX_restore_operations_SupportSessionId_TenantId" ON restore_operations ("SupportSessionId", "TenantId");
            """);
        DownloadLifecycleGuard(migrationBuilder, legacy: true);
        migrationBuilder.Sql("""
            CREATE FUNCTION public.support_session_link_guard() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW."SupportSessionId" IS DISTINCT FROM OLD."SupportSessionId" THEN
                    RAISE EXCEPTION 'The support session of an operation cannot change.' USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER trg_download_sessions_support_link BEFORE UPDATE ON download_sessions FOR EACH ROW EXECUTE FUNCTION public.support_session_link_guard();
            CREATE TRIGGER trg_restore_operations_support_link BEFORE UPDATE ON restore_operations FOR EACH ROW EXECUTE FUNCTION public.support_session_link_guard();
            CREATE FUNCTION public.support_access_session_guard() RETURNS trigger LANGUAGE plpgsql AS $$
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
            CREATE TRIGGER trg_support_access_session_guard BEFORE DELETE OR UPDATE ON support_access_sessions FOR EACH ROW EXECUTE FUNCTION public.support_access_session_guard();
            """);
    }

    private static void DownloadLifecycleGuard(MigrationBuilder migrationBuilder, bool legacy)
    {
        var legacyCheck = legacy ? "OR NEW.\"SupportSessionId\" IS DISTINCT FROM OLD.\"SupportSessionId\"" : "";
        migrationBuilder.Sql($$"""
            CREATE OR REPLACE FUNCTION public.download_lifecycle_guard() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF OLD."ReleasedAt" IS NOT NULL OR NEW."ArtifactId" <> OLD."ArtifactId"
                   OR NEW."TenantId" <> OLD."TenantId" OR NEW."UserId" <> OLD."UserId" OR NEW."ExpiresAt" < OLD."ExpiresAt"
                   {{legacyCheck}}
                   OR NEW."Checksum" IS DISTINCT FROM OLD."Checksum" OR NEW."SizeBytes" IS DISTINCT FROM OLD."SizeBytes"
                   OR ((OLD."ExpiresAt" <= clock_timestamp() OR OLD."LeaseExpiresAt" <= clock_timestamp()) AND NEW."ReleasedAt" IS NULL) THEN
                    RAISE EXCEPTION 'A released or expired download cannot reacquire its hold.' USING ERRCODE = '23514';
                END IF;
                IF NEW."ReplicaId" IS DISTINCT FROM OLD."ReplicaId" AND
                   (OLD."State" NOT IN ('Ready', 'Streaming') OR NEW."State" IS DISTINCT FROM OLD."State"
                    OR OLD."TransferredBytes" <> 0 OR NEW."TransferredBytes" <> 0 OR NEW."ReleasedAt" IS NOT NULL
                    OR NEW."ExpiresAt" IS DISTINCT FROM OLD."ExpiresAt" OR NEW."Filename" IS DISTINCT FROM OLD."Filename"
                    OR NEW."CreatedAt" IS DISTINCT FROM OLD."CreatedAt"
                    OR NEW."LeaseOwner" IS DISTINCT FROM OLD."LeaseOwner" OR NEW."LeaseExpiresAt" IS DISTINCT FROM OLD."LeaseExpiresAt") THEN
                    RAISE EXCEPTION 'A download source can change only before transfer under its live lease.' USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END $$;
            """);
    }
}
