using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BackupPlatform.Infrastructure.Persistence.Migrations;

/// <summary>
/// A download of a Backend-located replica on an agent-executed provider (sftp, google-drive) is served by an agent the backend selects, so
/// the session names that agent. The guard demanded a NULL agent for every non-agent-located replica and rejected all of them.
/// </summary>
[DbContext(typeof(PlatformDbContext))]
[Migration("20261025090000_ExternalAgentDownloadGuard")]
public sealed class ExternalAgentDownloadGuard : Migration
{
    private const string Body = """
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
               OR NEW."AgentId" IS DISTINCT FROM (CASE WHEN replica."ExecutionLocation" = 'Agent' THEN replica."OwningAgentId"
                                                       WHEN TG_OP = 'INSERT' AND replica."StorageProvider" IN ('sftp', 'google-drive') THEN NEW."AgentId"
                                                       ELSE NULL END)
               OR (replica."ExecutionLocation" <> 'Agent' AND TG_OP = 'INSERT' AND replica."StorageProvider" IN ('sftp', 'google-drive') AND NEW."AgentId" IS NULL)
               OR (TG_OP = 'UPDATE' AND replica."ExecutionLocation" <> 'Backend') THEN
                RAISE EXCEPTION 'An ineligible replica cannot be downloaded.' USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END $$;
        """;

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Body);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
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
        """);
}
