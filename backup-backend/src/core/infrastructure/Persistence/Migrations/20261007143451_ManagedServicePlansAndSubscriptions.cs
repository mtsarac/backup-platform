using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ManagedServicePlansAndSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "plans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    StorageLimitBytes = table.Column<long>(type: "bigint", nullable: true),
                    QuotaPolicyReference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PolicyConfigurationVersion = table.Column<int>(type: "integer", nullable: false),
                    IsSystem = table.Column<bool>(type: "boolean", nullable: false),
                    SystemKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plans", x => x.Id);
                    table.CheckConstraint("ck_plans_id_nonempty", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_plans_name_nonblank", "\"Name\" ~ '[^[:space:]]' AND \"NormalizedName\" ~ '[^[:space:]]'");
                    table.CheckConstraint("ck_plans_policy_configuration_version", "\"PolicyConfigurationVersion\" >= 1");
                    table.CheckConstraint("ck_plans_storage_limit", "\"StorageLimitBytes\" IS NULL OR (\"StorageLimitBytes\" > 0 AND \"StorageLimitBytes\" <= 1000000000000000)");
                    table.CheckConstraint("ck_plans_system_key", "\"SystemKey\" IS NULL OR \"IsSystem\"");
                });

            migrationBuilder.CreateTable(
                name: "tenant_subscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StartsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StorageLimitOverrideKind = table.Column<int>(type: "integer", nullable: false),
                    StorageLimitOverrideBytes = table.Column<long>(type: "bigint", nullable: true),
                    AssignedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_subscriptions", x => x.Id);
                    table.CheckConstraint("ck_tenant_subscriptions_closed_has_end", "\"Status\" NOT IN (3, 4) OR \"EndsAt\" IS NOT NULL");
                    table.CheckConstraint("ck_tenant_subscriptions_id_nonempty", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"PlanId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_tenant_subscriptions_override", "(\"StorageLimitOverrideKind\" = 0 AND \"StorageLimitOverrideBytes\" IS NULL) OR (\"StorageLimitOverrideKind\" = 1 AND \"StorageLimitOverrideBytes\" IS NOT NULL AND \"StorageLimitOverrideBytes\" > 0 AND \"StorageLimitOverrideBytes\" <= 1000000000000000) OR (\"StorageLimitOverrideKind\" = 2 AND \"StorageLimitOverrideBytes\" IS NULL)");
                    table.CheckConstraint("ck_tenant_subscriptions_period", "\"EndsAt\" IS NULL OR \"EndsAt\" >= \"StartsAt\"");
                    table.CheckConstraint("ck_tenant_subscriptions_status", "\"Status\" IN (1, 2, 3, 4)");
                    table.ForeignKey(
                        name: "FK_tenant_subscriptions_plans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_tenant_subscriptions_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_tenant_subscriptions_users_AssignedByUserId",
                        column: x => x.AssignedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_plans_normalized_name",
                table: "plans",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_plans_system_key",
                table: "plans",
                column: "SystemKey",
                unique: true,
                filter: "\"SystemKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_subscriptions_AssignedByUserId",
                table: "tenant_subscriptions",
                column: "AssignedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_subscriptions_PlanId",
                table: "tenant_subscriptions",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_subscriptions_TenantId_CreatedAt",
                table: "tenant_subscriptions",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "ux_tenant_subscriptions_one_open_per_tenant",
                table: "tenant_subscriptions",
                column: "TenantId",
                unique: true,
                filter: "\"Status\" IN (1, 2)");

            // Data, not code: the commercial plans and the legacy plan are rows. Fixed identifiers and ON CONFLICT DO NOTHING keep this
            // deterministic and safe to run again (the idempotent release script skips applied migrations; the guards cover partial states).
            migrationBuilder.Sql("""
                INSERT INTO plans ("Id", "Name", "NormalizedName", "Enabled", "StorageLimitBytes", "QuotaPolicyReference",
                                   "PolicyConfigurationVersion", "IsSystem", "SystemKey", "CreatedAt", "UpdatedAt")
                VALUES
                    ('61ff8c7d-5cf1-5b91-8be8-a9e25d5f53ea', '1 GB',  '1 gb',  TRUE,  1000000000,  NULL, 1, FALSE, NULL, now(), now()),
                    ('a6a9a7d1-293a-5ec9-a30f-603428c86559', '5 GB',  '5 gb',  TRUE,  5000000000,  NULL, 1, FALSE, NULL, now(), now()),
                    ('16aeeb9a-192d-518f-861c-fd3e417ca8ec', '10 GB', '10 gb', TRUE, 10000000000,  NULL, 1, FALSE, NULL, now(), now()),
                    ('bd58c99e-3efa-5c02-920c-b54e1230f3e6', '20 GB', '20 gb', TRUE, 20000000000,  NULL, 1, FALSE, NULL, now(), now()),
                    ('f31c79cf-c393-53c2-969d-90bcf0f63ef7', 'Legacy Unlimited', 'legacy unlimited', TRUE, NULL, NULL, 1, TRUE, 'legacy_unlimited', now(), now())
                ON CONFLICT DO NOTHING;
                """);

            // Every tenant that exists at deploy time keeps working exactly as before: it gets an Active, unlimited legacy subscription.
            // The subscription id is derived from the tenant id, so the backfill is deterministic; tenants created later start without one.
            migrationBuilder.Sql("""
                INSERT INTO tenant_subscriptions ("Id", "TenantId", "PlanId", "Status", "StartsAt", "EndsAt", "StorageLimitOverrideKind",
                                                  "StorageLimitOverrideBytes", "AssignedByUserId", "CreatedAt", "UpdatedAt")
                SELECT md5('legacy-unlimited-subscription:' || t."Id"::text)::uuid, t."Id", p."Id", 1, now(), NULL, 0, NULL, NULL, now(), now()
                FROM tenants t
                CROSS JOIN plans p
                WHERE p."SystemKey" = 'legacy_unlimited'
                  AND NOT EXISTS (SELECT 1 FROM tenant_subscriptions s WHERE s."TenantId" = t."Id")
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_subscriptions");

            migrationBuilder.DropTable(
                name: "plans");
        }
    }
}
