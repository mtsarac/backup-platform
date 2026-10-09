using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations;

[DbContext(typeof(PlatformDbContext))]
[Migration("20261008120000_SecureDownloads")]
public sealed class SecureDownloads : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "download_sessions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                ReplicaId = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                AgentId = table.Column<Guid>(type: "uuid", nullable: true),
                StorageProvider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                StorageTarget = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                Key = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                Checksum = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Filename = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                LeaseOwner = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                TransferredBytes = table.Column<long>(type: "bigint", nullable: false),
                FailureCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_download_sessions", x => x.Id);
                table.UniqueConstraint("AK_download_sessions_Id_TenantId", x => new { x.Id, x.TenantId });
                table.CheckConstraint("ck_download_sessions_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                table.CheckConstraint("ck_download_sessions_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                table.CheckConstraint("ck_download_sessions_state", "\"State\" IN ('Preparing','Ready','Streaming','Completed','Failed','Cancelled')");
                table.CheckConstraint("ck_download_sessions_bytes", "\"SizeBytes\" >= 0 AND \"TransferredBytes\" BETWEEN 0 AND \"SizeBytes\"");
                table.CheckConstraint("ck_download_sessions_expiry", "\"ExpiresAt\" > \"CreatedAt\"");
                table.CheckConstraint("ck_download_sessions_release", "(\"State\" IN ('Completed','Failed','Cancelled')) = (\"ReleasedAt\" IS NOT NULL)");
                table.CheckConstraint("ck_download_sessions_lease", "(\"LeaseOwner\" IS NULL) = (\"LeaseExpiresAt\" IS NULL)");
                table.ForeignKey("FK_download_sessions_tenants_TenantId", x => x.TenantId, "tenants", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_download_sessions_backup_artifacts_ArtifactId_TenantId", x => new { x.ArtifactId, x.TenantId }, "backup_artifacts", new[] { "Id", "TenantId" }, onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_download_sessions_backup_replicas_ReplicaId_TenantId", x => new { x.ReplicaId, x.TenantId }, "backup_replicas", new[] { "Id", "TenantId" }, onDelete: ReferentialAction.Restrict);
            });
        migrationBuilder.CreateIndex("IX_download_sessions_TenantId", "download_sessions", "TenantId");
        migrationBuilder.CreateIndex("IX_download_sessions_ArtifactId_TenantId", "download_sessions", new[] { "ArtifactId", "TenantId" });
        migrationBuilder.CreateIndex("IX_download_sessions_ReplicaId_TenantId", "download_sessions", new[] { "ReplicaId", "TenantId" });
        migrationBuilder.CreateIndex("IX_download_sessions_TenantId_AgentId_State", "download_sessions", new[] { "TenantId", "AgentId", "State" });
        migrationBuilder.CreateIndex("ix_download_sessions_active", "download_sessions", "ReplicaId", filter: "\"ReleasedAt\" IS NULL");
        migrationBuilder.CreateIndex("ix_download_sessions_expiry", "download_sessions", "ExpiresAt", filter: "\"ReleasedAt\" IS NULL");
        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION backup_replica_pin_guard() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF EXISTS (SELECT 1 FROM restore_replica_pins WHERE "ReplicaId" = NEW."Id" AND "ReleasedAt" IS NULL)
                   OR EXISTS (SELECT 1 FROM download_sessions WHERE "ReplicaId" = NEW."Id" AND "TenantId" = NEW."TenantId"
                       AND "ReleasedAt" IS NULL AND "ExpiresAt" > clock_timestamp()
                       AND ("LeaseExpiresAt" IS NULL OR "LeaseExpiresAt" > clock_timestamp())) THEN
                    RAISE EXCEPTION 'A pinned replica cannot begin its physical deletion.' USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END $$;
            CREATE FUNCTION download_replica_guard() RETURNS trigger LANGUAGE plpgsql AS $$
            DECLARE
                replica backup_replicas%ROWTYPE;
                artifact backup_artifacts%ROWTYPE;
            BEGIN
                SELECT * INTO artifact FROM backup_artifacts WHERE "Id" = NEW."ArtifactId" AND "TenantId" = NEW."TenantId" FOR NO KEY UPDATE;
                SELECT * INTO replica FROM backup_replicas WHERE "Id" = NEW."ReplicaId" AND "TenantId" = NEW."TenantId" AND "ArtifactId" = NEW."ArtifactId" FOR UPDATE;
                IF replica."Id" IS NULL OR artifact."Id" IS NULL OR replica."Status" <> 'Available' OR replica."VerificationState" <> 'Verified'
                   OR replica."Checksum" IS DISTINCT FROM artifact."ArtifactChecksum" OR replica."SizeBytes" IS DISTINCT FROM artifact."SizeBytes"
                   OR artifact."ChecksumAlgorithm" <> 'sha256' OR NEW."Checksum" IS DISTINCT FROM artifact."ArtifactChecksum"
                   OR NEW."SizeBytes" IS DISTINCT FROM artifact."SizeBytes" THEN
                    RAISE EXCEPTION 'An ineligible replica cannot be downloaded.' USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER trg_download_replica_guard BEFORE INSERT ON download_sessions FOR EACH ROW EXECUTE FUNCTION download_replica_guard();
            CREATE FUNCTION download_lifecycle_guard() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF OLD."ReleasedAt" IS NOT NULL OR NEW."ReplicaId" <> OLD."ReplicaId" OR NEW."ArtifactId" <> OLD."ArtifactId"
                   OR NEW."TenantId" <> OLD."TenantId" OR NEW."UserId" <> OLD."UserId" OR NEW."ExpiresAt" < OLD."ExpiresAt"
                   OR ((OLD."ExpiresAt" <= clock_timestamp() OR OLD."LeaseExpiresAt" <= clock_timestamp()) AND NEW."ReleasedAt" IS NULL) THEN
                    RAISE EXCEPTION 'A released or expired download cannot reacquire its hold.' USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER trg_download_lifecycle_guard BEFORE UPDATE ON download_sessions FOR EACH ROW EXECUTE FUNCTION download_lifecycle_guard();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TRIGGER trg_download_lifecycle_guard ON download_sessions;
            DROP FUNCTION download_lifecycle_guard();
            DROP TRIGGER trg_download_replica_guard ON download_sessions;
            DROP FUNCTION download_replica_guard();
            CREATE OR REPLACE FUNCTION backup_replica_pin_guard() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF EXISTS (SELECT 1 FROM restore_replica_pins WHERE "ReplicaId" = NEW."Id" AND "ReleasedAt" IS NULL) THEN
                    RAISE EXCEPTION 'A replica pinned by a restore cannot begin its physical deletion.' USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END $$;
            """);
        migrationBuilder.DropTable("download_sessions");
    }
}
