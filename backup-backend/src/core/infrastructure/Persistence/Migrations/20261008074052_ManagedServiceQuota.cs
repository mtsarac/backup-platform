using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ManagedServiceQuota : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_backup_occurrence_outcome",
                table: "backup_schedule_occurrences");

            migrationBuilder.CreateTable(
                name: "quota_policies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    AccountingMode = table.Column<int>(type: "integer", nullable: false),
                    IncludedStorageScopes = table.Column<string>(type: "jsonb", nullable: false),
                    EnforcementMode = table.Column<int>(type: "integer", nullable: false),
                    WarningThresholdPercent = table.Column<int>(type: "integer", nullable: false),
                    CriticalThresholdPercent = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quota_policies", x => x.Id);
                    table.UniqueConstraint("ux_quota_policies_key", x => x.Key);
                    table.CheckConstraint("ck_quota_policies_accounting_mode", "\"AccountingMode\" IN (1, 2)");
                    table.CheckConstraint("ck_quota_policies_default_enabled", "NOT \"IsDefault\" OR \"Enabled\"");
                    table.CheckConstraint("ck_quota_policies_enforcement_mode", "\"EnforcementMode\" IN (1, 2, 3)");
                    table.CheckConstraint("ck_quota_policies_id_nonempty", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_quota_policies_key", "\"Key\" ~ '^[a-z0-9][a-z0-9._-]{0,63}$'");
                    table.CheckConstraint("ck_quota_policies_name_nonblank", "\"Name\" ~ '[^[:space:]]' AND \"NormalizedName\" ~ '[^[:space:]]'");
                    table.CheckConstraint("ck_quota_policies_scopes", "CASE WHEN jsonb_typeof(\"IncludedStorageScopes\") = 'array' THEN jsonb_array_length(\"IncludedStorageScopes\") BETWEEN 1 AND 8 ELSE FALSE END");
                    table.CheckConstraint("ck_quota_policies_thresholds", "\"WarningThresholdPercent\" BETWEEN 1 AND 99 AND \"CriticalThresholdPercent\" BETWEEN 2 AND 100 AND \"CriticalThresholdPercent\" > \"WarningThresholdPercent\"");
                    table.CheckConstraint("ck_quota_policies_version", "\"Version\" >= 1");
                });

            migrationBuilder.CreateTable(
                name: "quota_reservations",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Bytes = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReleaseReason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quota_reservations", x => x.RunId);
                    table.CheckConstraint("ck_quota_reservations_bytes", "\"Bytes\" > 0");
                    table.CheckConstraint("ck_quota_reservations_ids", "\"RunId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_quota_reservations_period", "\"ExpiresAt\" > \"CreatedAt\"");
                    table.CheckConstraint("ck_quota_reservations_reason", "\"ReleaseReason\" IS NULL OR \"ReleaseReason\" IN ('run_finished', 'expired', 'run_not_created', 'orphaned')");
                    table.CheckConstraint("ck_quota_reservations_release", "(\"ReleasedAt\" IS NULL) = (\"ReleaseReason\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_quota_reservations_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tenant_storage_usage",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    UsedBytes = table.Column<long>(type: "bigint", nullable: false),
                    ReservedBytes = table.Column<long>(type: "bigint", nullable: false),
                    CalculatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AccountingMode = table.Column<int>(type: "integer", nullable: false),
                    PolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyVersion = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_storage_usage", x => x.TenantId);
                    table.CheckConstraint("ck_tenant_storage_usage_accounting_mode", "\"AccountingMode\" IN (1, 2)");
                    table.CheckConstraint("ck_tenant_storage_usage_policy_version", "\"PolicyVersion\" >= 1");
                    table.CheckConstraint("ck_tenant_storage_usage_reserved", "\"ReservedBytes\" >= 0");
                    table.CheckConstraint("ck_tenant_storage_usage_revision", "\"Revision\" >= 0");
                    table.CheckConstraint("ck_tenant_storage_usage_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_tenant_storage_usage_used", "\"UsedBytes\" >= 0");
                    table.ForeignKey(
                        name: "FK_tenant_storage_usage_quota_policies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "quota_policies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_tenant_storage_usage_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_plans_QuotaPolicyReference",
                table: "plans",
                column: "QuotaPolicyReference");

            migrationBuilder.AddCheckConstraint(
                name: "ck_backup_occurrence_outcome",
                table: "backup_schedule_occurrences",
                sql: "\"Outcome\" IN ('RunCreated','SkippedOverlap','MissedGraceExpired','MissedSuperseded','JobDisabled','AdmissionDenied')");

            migrationBuilder.CreateIndex(
                name: "ux_quota_policies_normalized_name",
                table: "quota_policies",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_quota_policies_one_default",
                table: "quota_policies",
                column: "IsDefault",
                unique: true,
                filter: "\"IsDefault\"");

            migrationBuilder.CreateIndex(
                name: "ix_quota_reservations_active",
                table: "quota_reservations",
                column: "TenantId",
                filter: "\"ReleasedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_quota_reservations_expiry",
                table: "quota_reservations",
                column: "ExpiresAt",
                filter: "\"ReleasedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_tenant_storage_usage_calculated",
                table: "tenant_storage_usage",
                column: "CalculatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_storage_usage_PolicyId",
                table: "tenant_storage_usage",
                column: "PolicyId");

            // Data, not code: the first commercial policy is a row. The fixed identifier matches QuotaPolicy.DefaultId and ON CONFLICT DO NOTHING
            // keeps the statement safe to run again. There is deliberately NO usage backfill here: computing every tenant's usage would hold
            // long locks on a large database. Projections are computed per tenant, in short transactions, by the maintenance loop at startup, and
            // on first use (admission and reads recompute a missing projection before trusting it), so no decision ever relies on a missing one.
            migrationBuilder.Sql("""
                INSERT INTO quota_policies ("Id", "Key", "Name", "NormalizedName", "Version", "AccountingMode", "IncludedStorageScopes", "EnforcementMode",
                                            "WarningThresholdPercent", "CriticalThresholdPercent", "Enabled", "IsDefault", "CreatedAt", "UpdatedAt")
                VALUES ('14e8fce9-11ed-47a9-aa04-b6ead7a8cd3d', 'default', 'Default (logical, all storage)', 'default (logical, all storage)', 1, 1,
                        '[{"kind":"all"}]'::jsonb, 1, 80, 90, TRUE, TRUE, now(), now())
                ON CONFLICT DO NOTHING;
                """);

            // M1 stored the plan's policy reference as an opaque string that nothing resolved. It now references quota_policies.Key, so a reference to a
            // policy that does not exist (none can have been meaningful) is cleared, which means "use the default policy", before the key is enforced.
            migrationBuilder.Sql("""
                UPDATE plans SET "QuotaPolicyReference" = NULL
                WHERE "QuotaPolicyReference" IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM quota_policies p WHERE p."Key" = plans."QuotaPolicyReference");
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_plans_quota_policies_QuotaPolicyReference",
                table: "plans",
                column: "QuotaPolicyReference",
                principalTable: "quota_policies",
                principalColumn: "Key",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_plans_quota_policies_QuotaPolicyReference",
                table: "plans");

            migrationBuilder.DropTable(
                name: "quota_reservations");

            migrationBuilder.DropTable(
                name: "tenant_storage_usage");

            migrationBuilder.DropTable(
                name: "quota_policies");

            migrationBuilder.DropIndex(
                name: "IX_plans_QuotaPolicyReference",
                table: "plans");

            migrationBuilder.DropCheckConstraint(
                name: "ck_backup_occurrence_outcome",
                table: "backup_schedule_occurrences");

            // The older constraint does not know the denied outcome; those decisions cannot be kept.
            migrationBuilder.Sql("DELETE FROM backup_schedule_occurrences WHERE \"Outcome\" = 'AdmissionDenied';");

            migrationBuilder.AddCheckConstraint(
                name: "ck_backup_occurrence_outcome",
                table: "backup_schedule_occurrences",
                sql: "\"Outcome\" IN ('RunCreated','SkippedOverlap','MissedGraceExpired','MissedSuperseded','JobDisabled')");
        }
    }
}
