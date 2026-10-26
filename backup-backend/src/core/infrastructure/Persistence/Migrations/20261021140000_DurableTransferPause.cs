using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BackupPlatform.Infrastructure.Persistence.Migrations;

[DbContext(typeof(PlatformDbContext))]
[Migration("20261021140000_DurableTransferPause")]
public sealed class DurableTransferPause : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE backup_runs DROP CONSTRAINT ck_backup_runs_status;
        ALTER TABLE backup_runs ADD CONSTRAINT ck_backup_runs_status CHECK ("Status" BETWEEN 1 AND 9);
        ALTER TABLE backup_run_attempts DROP CONSTRAINT ck_backup_run_attempts_status;
        ALTER TABLE backup_run_attempts ADD CONSTRAINT ck_backup_run_attempts_status CHECK (("Status" BETWEEN 1 AND 7) OR "Status" = 9);
        ALTER TABLE backup_run_attempts ADD COLUMN "PauseRequested" boolean NOT NULL DEFAULT false;
        ALTER TABLE backup_run_attempts ADD COLUMN "PauseReason" integer NULL;
        DROP INDEX "IX_backup_runs_TenantId_JobId";
        CREATE UNIQUE INDEX "IX_backup_runs_TenantId_JobId" ON backup_runs ("TenantId", "JobId") WHERE "OverlapPolicy" = 'Skip' AND "Status" IN (1,2,3,8,9);
        CREATE TABLE transfer_checkpoints (
            "TenantId" uuid NOT NULL, "AttemptId" uuid NOT NULL, "StorageProvider" varchar(32) NOT NULL,
            "DestinationId" uuid NOT NULL, "VerifiedByteOffset" bigint NOT NULL,
            "ProviderState" varchar(2097152) NULL, "RecordedAt" timestamptz NOT NULL,
            CONSTRAINT pk_transfer_checkpoints PRIMARY KEY ("TenantId", "AttemptId", "StorageProvider", "DestinationId"),
            CONSTRAINT ck_transfer_checkpoint_offset CHECK ("VerifiedByteOffset" >= 0),
            CONSTRAINT fk_transfer_checkpoint_attempt FOREIGN KEY ("AttemptId", "TenantId") REFERENCES backup_run_attempts ("Id", "TenantId") ON DELETE CASCADE);
        CREATE INDEX "IX_transfer_checkpoints_AttemptId_TenantId" ON transfer_checkpoints ("AttemptId", "TenantId");
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE backup_runs DROP CONSTRAINT ck_backup_runs_status;
        ALTER TABLE backup_runs ADD CONSTRAINT ck_backup_runs_status CHECK ("Status" BETWEEN 1 AND 8);
        ALTER TABLE backup_run_attempts DROP CONSTRAINT ck_backup_run_attempts_status;
        ALTER TABLE backup_run_attempts ADD CONSTRAINT ck_backup_run_attempts_status CHECK ("Status" BETWEEN 1 AND 7);
        DROP INDEX "IX_backup_runs_TenantId_JobId";
        CREATE UNIQUE INDEX "IX_backup_runs_TenantId_JobId" ON backup_runs ("TenantId", "JobId") WHERE "OverlapPolicy" = 'Skip' AND "Status" IN (1,2,3,8);
        DROP TABLE transfer_checkpoints;
        ALTER TABLE backup_run_attempts DROP COLUMN "PauseRequested", DROP COLUMN "PauseReason";
        """);
}
