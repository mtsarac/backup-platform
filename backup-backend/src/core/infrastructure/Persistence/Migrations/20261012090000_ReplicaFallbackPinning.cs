using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BackupPlatform.Infrastructure.Persistence.Migrations;

[DbContext(typeof(PlatformDbContext))]
[Migration("20261012090000_ReplicaFallbackPinning")]
public sealed class ReplicaFallbackPinning : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION download_lifecycle_guard() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF OLD."ReleasedAt" IS NOT NULL OR NEW."ArtifactId" <> OLD."ArtifactId"
                   OR NEW."TenantId" <> OLD."TenantId" OR NEW."UserId" <> OLD."UserId" OR NEW."ExpiresAt" < OLD."ExpiresAt"
                   OR NEW."SupportSessionId" IS DISTINCT FROM OLD."SupportSessionId"
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

            CREATE OR REPLACE FUNCTION download_replica_guard() RETURNS trigger LANGUAGE plpgsql AS $$
            DECLARE
                replica backup_replicas%ROWTYPE;
                artifact backup_artifacts%ROWTYPE;
            BEGIN
                SELECT * INTO artifact FROM backup_artifacts WHERE "Id" = NEW."ArtifactId" AND "TenantId" = NEW."TenantId" FOR NO KEY UPDATE;
                SELECT * INTO replica FROM backup_replicas WHERE "Id" = NEW."ReplicaId" AND "TenantId" = NEW."TenantId" AND "ArtifactId" = NEW."ArtifactId" FOR UPDATE;
                IF replica."Id" IS NULL OR artifact."Id" IS NULL OR replica."Status" <> 'Available' OR replica."VerificationState" <> 'Verified'
                   OR replica."Checksum" IS DISTINCT FROM artifact."ArtifactChecksum" OR replica."SizeBytes" IS DISTINCT FROM artifact."SizeBytes"
                   OR artifact."ChecksumAlgorithm" <> 'sha256' OR NEW."Checksum" IS DISTINCT FROM artifact."ArtifactChecksum"
                   OR NEW."SizeBytes" IS DISTINCT FROM artifact."SizeBytes"
                   OR NEW."StorageProvider" IS DISTINCT FROM replica."StorageProvider" OR NEW."StorageTarget" IS DISTINCT FROM replica."StorageTarget"
                   OR NEW."Key" IS DISTINCT FROM replica."Key"
                   OR NEW."AgentId" IS DISTINCT FROM (CASE WHEN replica."ExecutionLocation" = 'Agent' THEN replica."OwningAgentId" ELSE NULL END)
                   OR (TG_OP = 'UPDATE' AND replica."ExecutionLocation" <> 'Backend') THEN
                    RAISE EXCEPTION 'An ineligible replica cannot be downloaded.' USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER trg_download_fallback_replica_guard BEFORE UPDATE OF "ReplicaId" ON download_sessions
                FOR EACH ROW WHEN (NEW."ReplicaId" IS DISTINCT FROM OLD."ReplicaId") EXECUTE FUNCTION download_replica_guard();
            CREATE FUNCTION restore_fallback_pin_guard() RETURNS trigger LANGUAGE plpgsql AS $$
            DECLARE
                operation restore_operations%ROWTYPE;
                replica backup_replicas%ROWTYPE;
            BEGIN
                IF OLD."ReleasedAt" IS NOT NULL OR NEW."ReleasedAt" IS NOT NULL
                   OR NEW."OperationId" IS DISTINCT FROM OLD."OperationId" OR NEW."TenantId" IS DISTINCT FROM OLD."TenantId" THEN
                    RAISE EXCEPTION 'A released restore pin cannot be moved.' USING ERRCODE = '23514';
                END IF;
                SELECT * INTO operation FROM restore_operations WHERE "Id" = NEW."OperationId" AND "TenantId" = NEW."TenantId";
                SELECT * INTO replica FROM backup_replicas WHERE "Id" = NEW."ReplicaId" AND "TenantId" = NEW."TenantId"
                    AND "ArtifactId" = operation."ArtifactId" FOR UPDATE;
                IF operation."Id" IS NULL OR operation."Status" NOT IN ('Pending', 'Leased') OR operation."StartedAt" IS NOT NULL
                   OR operation."CancellationRequestedAt" IS NOT NULL OR replica."Id" IS NULL
                   OR replica."Status" NOT IN ('Available', 'DeletePending') OR replica."VerificationState" <> 'Verified' THEN
                    RAISE EXCEPTION 'An ineligible restore source cannot be pinned.' USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER trg_restore_fallback_replica_guard BEFORE UPDATE OF "ReplicaId" ON restore_replica_pins
                FOR EACH ROW WHEN (NEW."ReplicaId" IS DISTINCT FROM OLD."ReplicaId") EXECUTE FUNCTION restore_fallback_pin_guard();
            CREATE FUNCTION restore_fallback_operation_guard() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF OLD."Status" <> 'Pending' OR OLD."StartedAt" IS NOT NULL OR OLD."CancellationRequestedAt" IS NOT NULL
                   OR NEW."Status" NOT IN ('Pending', 'Leased') OR NEW."StartedAt" IS NOT NULL OR NEW."CancellationRequestedAt" IS NOT NULL THEN
                    RAISE EXCEPTION 'A restore source cannot change after handoff.' USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER trg_restore_fallback_operation_guard BEFORE UPDATE OF "ReplicaId" ON restore_operations
                FOR EACH ROW WHEN (NEW."ReplicaId" IS DISTINCT FROM OLD."ReplicaId") EXECUTE FUNCTION restore_fallback_operation_guard();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TRIGGER trg_restore_fallback_replica_guard ON restore_replica_pins;
            DROP TRIGGER trg_download_fallback_replica_guard ON download_sessions;
            DROP FUNCTION restore_fallback_pin_guard();
            DROP TRIGGER trg_restore_fallback_operation_guard ON restore_operations;
            DROP FUNCTION restore_fallback_operation_guard();
            CREATE OR REPLACE FUNCTION download_lifecycle_guard() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF OLD."ReleasedAt" IS NOT NULL OR NEW."ReplicaId" <> OLD."ReplicaId" OR NEW."ArtifactId" <> OLD."ArtifactId"
                   OR NEW."TenantId" <> OLD."TenantId" OR NEW."UserId" <> OLD."UserId" OR NEW."ExpiresAt" < OLD."ExpiresAt"
                   OR ((OLD."ExpiresAt" <= clock_timestamp() OR OLD."LeaseExpiresAt" <= clock_timestamp()) AND NEW."ReleasedAt" IS NULL) THEN
                    RAISE EXCEPTION 'A released or expired download cannot reacquire its hold.' USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END $$;
            CREATE OR REPLACE FUNCTION download_replica_guard() RETURNS trigger LANGUAGE plpgsql AS $$
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
            """);
    }
}