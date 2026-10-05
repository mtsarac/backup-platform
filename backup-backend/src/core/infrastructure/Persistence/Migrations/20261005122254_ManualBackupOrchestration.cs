using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ManualBackupOrchestration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "backup_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BackupType = table.Column<string>(type: "text", nullable: false),
                    Configuration = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    OverlapPolicy = table.Column<string>(type: "text", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_jobs", x => x.Id);
                    table.UniqueConstraint("AK_backup_jobs_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_backup_job_policy", "\"BackupType\" = 'Full' AND \"OverlapPolicy\" = 'Queue'");
                    table.CheckConstraint("ck_backup_jobs_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_backup_jobs_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_backup_jobs_agents_AgentId_TenantId",
                        columns: x => new { x.AgentId, x.TenantId },
                        principalTable: "agents",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_backup_jobs_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "backup_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Configuration = table.Column<string>(type: "jsonb", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CurrentAttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    BackupType = table.Column<string>(type: "text", nullable: false),
                    TriggerType = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancellationRequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TerminalReason = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_runs", x => x.Id);
                    table.UniqueConstraint("AK_backup_runs_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_backup_runs_attempt_number", "\"CurrentAttemptNumber\" >= 1");
                    table.CheckConstraint("ck_backup_runs_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_backup_runs_status", "\"Status\" BETWEEN 1 AND 7");
                    table.CheckConstraint("ck_backup_runs_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_backup_runs_terminal", "(\"Status\" IN (4,5,6,7)) = (\"CompletedAt\" IS NOT NULL)");
                    table.CheckConstraint("ck_backup_runs_type", "\"BackupType\" = 'Full' AND \"TriggerType\" = 'Manual'");
                    table.ForeignKey(
                        name: "FK_backup_runs_agents_AgentId_TenantId",
                        columns: x => new { x.AgentId, x.TenantId },
                        principalTable: "agents",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_backup_runs_backup_jobs_JobId_TenantId",
                        columns: x => new { x.JobId, x.TenantId },
                        principalTable: "backup_jobs",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_backup_runs_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "backup_run_attempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LeasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SoftwareVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ProtocolVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Platform = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ProgressStage = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ProgressPercent = table.Column<double>(type: "double precision", nullable: true),
                    ProgressAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TerminalReason = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_run_attempts", x => x.Id);
                    table.UniqueConstraint("AK_backup_run_attempts_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_backup_attempt_lease", "\"Status\" NOT IN (2,3) OR (\"LeaseToken\" IS NOT NULL AND \"LeaseToken\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"LeaseExpiresAt\" IS NOT NULL)");
                    table.CheckConstraint("ck_backup_attempt_number", "\"Number\" >= 1");
                    table.CheckConstraint("ck_backup_attempt_progress", "\"ProgressPercent\" IS NULL OR (\"ProgressPercent\" >= 0 AND \"ProgressPercent\" <= 100)");
                    table.CheckConstraint("ck_backup_attempt_started", "\"Status\" <> 3 OR \"StartedAt\" IS NOT NULL");
                    table.CheckConstraint("ck_backup_attempt_terminal", "(\"Status\" IN (4,5,6,7)) = (\"CompletedAt\" IS NOT NULL)");
                    table.CheckConstraint("ck_backup_run_attempts_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_backup_run_attempts_status", "\"Status\" BETWEEN 1 AND 7");
                    table.CheckConstraint("ck_backup_run_attempts_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_backup_run_attempts_agents_AgentId_TenantId",
                        columns: x => new { x.AgentId, x.TenantId },
                        principalTable: "agents",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_backup_run_attempts_backup_runs_RunId_TenantId",
                        columns: x => new { x.RunId, x.TenantId },
                        principalTable: "backup_runs",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_backup_run_attempts_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "backup_artifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    BackupType = table.Column<string>(type: "text", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    SourceChecksum = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ArtifactChecksum = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ChecksumAlgorithm = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SourceMetadataJson = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: false),
                    AppliedOptionsJson = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: false),
                    Sealed = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_artifacts", x => x.Id);
                    table.UniqueConstraint("AK_backup_artifacts_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.UniqueConstraint("AK_backup_artifacts_Id_TenantId_SizeBytes_ArtifactChecksum_Che~", x => new { x.Id, x.TenantId, x.SizeBytes, x.ArtifactChecksum, x.ChecksumAlgorithm });
                    table.CheckConstraint("ck_backup_artifact_checksums", "\"ChecksumAlgorithm\" = 'sha256' AND \"SourceChecksum\" ~ '^[0-9a-f]{64}$' AND \"ArtifactChecksum\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_backup_artifact_size", "\"SizeBytes\" > 0");
                    table.CheckConstraint("ck_backup_artifact_type", "\"BackupType\" = 'Full'");
                    table.CheckConstraint("ck_backup_artifacts_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_backup_artifacts_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_backup_artifacts_backup_run_attempts_AttemptId_TenantId",
                        columns: x => new { x.AttemptId, x.TenantId },
                        principalTable: "backup_run_attempts",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_backup_artifacts_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "backup_lifecycle_facts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_lifecycle_facts", x => x.Id);
                    table.UniqueConstraint("AK_backup_lifecycle_facts_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_backup_lifecycle_facts_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_backup_lifecycle_facts_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_backup_lifecycle_facts_backup_run_attempts_AttemptId_Tenant~",
                        columns: x => new { x.AttemptId, x.TenantId },
                        principalTable: "backup_run_attempts",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_backup_lifecycle_facts_backup_runs_RunId_TenantId",
                        columns: x => new { x.RunId, x.TenantId },
                        principalTable: "backup_runs",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_backup_lifecycle_facts_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "backup_replicas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    StorageProvider = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    StorageTarget = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Key = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Checksum = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ChecksumAlgorithm = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    VerificationState = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    VerificationMethod = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_replicas", x => x.Id);
                    table.UniqueConstraint("AK_backup_replicas_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_backup_replica_checksum", "\"ChecksumAlgorithm\" = 'sha256' AND \"Checksum\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_backup_replica_size", "\"SizeBytes\" > 0");
                    table.CheckConstraint("ck_backup_replica_status", "\"Status\" = 'Available'");
                    table.CheckConstraint("ck_backup_replica_verification", "\"VerificationState\" IN ('Verified','Unverified','Failed') AND ((\"VerificationState\" = 'Verified') = (\"VerifiedAt\" IS NOT NULL)) AND (\"VerifiedAt\" IS NULL OR \"VerifiedAt\" >= \"CreatedAt\")");
                    table.CheckConstraint("ck_backup_replicas_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_backup_replicas_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_backup_replicas_backup_artifacts_ArtifactId_TenantId_SizeBy~",
                        columns: x => new { x.ArtifactId, x.TenantId, x.SizeBytes, x.Checksum, x.ChecksumAlgorithm },
                        principalTable: "backup_artifacts",
                        principalColumns: new[] { "Id", "TenantId", "SizeBytes", "ArtifactChecksum", "ChecksumAlgorithm" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_backup_replicas_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_backup_artifacts_AttemptId_TenantId",
                table: "backup_artifacts",
                columns: new[] { "AttemptId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_artifacts_TenantId",
                table: "backup_artifacts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_backup_artifacts_TenantId_AttemptId",
                table: "backup_artifacts",
                columns: new[] { "TenantId", "AttemptId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_backup_jobs_AgentId_TenantId",
                table: "backup_jobs",
                columns: new[] { "AgentId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_jobs_TenantId",
                table: "backup_jobs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_backup_jobs_TenantId_AgentId",
                table: "backup_jobs",
                columns: new[] { "TenantId", "AgentId" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_lifecycle_facts_AttemptId_TenantId",
                table: "backup_lifecycle_facts",
                columns: new[] { "AttemptId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_lifecycle_facts_RunId_TenantId",
                table: "backup_lifecycle_facts",
                columns: new[] { "RunId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_lifecycle_facts_TenantId",
                table: "backup_lifecycle_facts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_backup_lifecycle_facts_TenantId_RunId_At",
                table: "backup_lifecycle_facts",
                columns: new[] { "TenantId", "RunId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_replicas_ArtifactId_TenantId_SizeBytes_Checksum_Chec~",
                table: "backup_replicas",
                columns: new[] { "ArtifactId", "TenantId", "SizeBytes", "Checksum", "ChecksumAlgorithm" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_replicas_TenantId",
                table: "backup_replicas",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_backup_replicas_TenantId_ArtifactId_StorageProvider_Storage~",
                table: "backup_replicas",
                columns: new[] { "TenantId", "ArtifactId", "StorageProvider", "StorageTarget", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_backup_run_attempts_AgentId_TenantId",
                table: "backup_run_attempts",
                columns: new[] { "AgentId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_run_attempts_LeaseToken",
                table: "backup_run_attempts",
                column: "LeaseToken",
                unique: true,
                filter: "\"LeaseToken\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_backup_run_attempts_RunId_TenantId",
                table: "backup_run_attempts",
                columns: new[] { "RunId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_run_attempts_Status_LeaseExpiresAt",
                table: "backup_run_attempts",
                columns: new[] { "Status", "LeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_run_attempts_TenantId",
                table: "backup_run_attempts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_backup_run_attempts_TenantId_AgentId",
                table: "backup_run_attempts",
                columns: new[] { "TenantId", "AgentId" },
                unique: true,
                filter: "\"Status\" IN (2,3)");

            migrationBuilder.CreateIndex(
                name: "IX_backup_run_attempts_TenantId_RunId_Number",
                table: "backup_run_attempts",
                columns: new[] { "TenantId", "RunId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_backup_runs_AgentId_TenantId",
                table: "backup_runs",
                columns: new[] { "AgentId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_runs_JobId_TenantId",
                table: "backup_runs",
                columns: new[] { "JobId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_runs_TenantId",
                table: "backup_runs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_backup_runs_TenantId_AgentId_Status_CreatedAt",
                table: "backup_runs",
                columns: new[] { "TenantId", "AgentId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_runs_TenantId_CreatedAt",
                table: "backup_runs",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_runs_TenantId_JobId_CreatedAt",
                table: "backup_runs",
                columns: new[] { "TenantId", "JobId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_runs_TenantId_JobId_IdempotencyKey",
                table: "backup_runs",
                columns: new[] { "TenantId", "JobId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_backup_runs_TenantId_Status_CreatedAt",
                table: "backup_runs",
                columns: new[] { "TenantId", "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "backup_lifecycle_facts");

            migrationBuilder.DropTable(
                name: "backup_replicas");

            migrationBuilder.DropTable(
                name: "backup_artifacts");

            migrationBuilder.DropTable(
                name: "backup_run_attempts");

            migrationBuilder.DropTable(
                name: "backup_runs");

            migrationBuilder.DropTable(
                name: "backup_jobs");
        }
    }
}
