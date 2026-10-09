using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StorageConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Added NULLABLE on purpose: existing replicas are assigned to a connection below, and only then does the column become NOT NULL.
            migrationBuilder.AddColumn<Guid>(
                name: "StorageConnectionId",
                table: "backup_replicas",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "platform_secrets",
                columns: table => new
                {
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Ciphertext = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_secrets", x => x.Name);
                    table.CheckConstraint("ck_platform_secrets_name", "length(\"Name\") BETWEEN 1 AND 128");
                });

            migrationBuilder.CreateTable(
                name: "storage_connections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ProviderType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Ownership = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    QuotaScope = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    ConfigurationJson = table.Column<string>(type: "jsonb", nullable: false),
                    SecretReference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    SecretFieldNamesJson = table.Column<string>(type: "jsonb", nullable: false),
                    SecretUpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastTestStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    LastTestCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastTestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StateChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_storage_connections", x => x.Id);
                    table.UniqueConstraint("AK_storage_connections_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.UniqueConstraint("ak_storage_connections_target", x => new { x.Id, x.TenantId, x.ProviderType });
                    table.CheckConstraint("ck_storage_connections_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_storage_connections_name", "length(btrim(\"Name\")) BETWEEN 1 AND 100 AND \"NormalizedName\" = lower(btrim(\"Name\"))");
                    table.CheckConstraint("ck_storage_connections_ownership", "\"Ownership\" IN ('PlatformManaged','TenantManaged')");
                    table.CheckConstraint("ck_storage_connections_provider", "\"ProviderType\" ~ '^[a-z0-9]([a-z0-9-]*[a-z0-9])?$' AND length(\"ProviderType\") <= 32");
                    table.CheckConstraint("ck_storage_connections_scope", "\"QuotaScope\" ~ '^[a-z0-9]([a-z0-9-]*[a-z0-9])?$' AND length(\"QuotaScope\") <= 64");
                    table.CheckConstraint("ck_storage_connections_secret", "(\"SecretReference\" IS NULL) OR (length(\"SecretReference\") BETWEEN 1 AND 128 AND \"SecretUpdatedAt\" IS NOT NULL)");
                    table.CheckConstraint("ck_storage_connections_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_storage_connections_test", "(\"LastTestStatus\" IS NULL) = (\"LastTestedAt\" IS NULL) AND (\"LastTestStatus\" IS NULL OR \"LastTestStatus\" IN ('Succeeded','Failed','NotTestable'))");
                    table.ForeignKey(
                        name: "FK_storage_connections_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_backup_replicas_connection",
                table: "backup_replicas",
                columns: new[] { "TenantId", "StorageConnectionId" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_replicas_StorageConnectionId_TenantId_StorageProvider",
                table: "backup_replicas",
                columns: new[] { "StorageConnectionId", "TenantId", "StorageProvider" });

            migrationBuilder.CreateIndex(
                name: "ix_storage_connections_ownership",
                table: "storage_connections",
                columns: new[] { "Ownership", "ProviderType" });

            migrationBuilder.CreateIndex(
                name: "IX_storage_connections_TenantId",
                table: "storage_connections",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ux_storage_connections_default",
                table: "storage_connections",
                columns: new[] { "TenantId", "ProviderType" },
                unique: true,
                filter: "\"IsDefault\"");

            migrationBuilder.CreateIndex(
                name: "ux_storage_connections_name",
                table: "storage_connections",
                columns: new[] { "TenantId", "NormalizedName" },
                unique: true);

            // Backfill. Every tenant gets the two system default connections, with exactly the properties the quota layer used to derive for
            // replicas (Central is platform-managed with scope "managed", everything else is tenant-managed with scope "tenant"), so usage under
            // any scope rule is the same before and after the upgrade.
            migrationBuilder.Sql("""
                INSERT INTO storage_connections ("Id", "TenantId", "Name", "NormalizedName", "ProviderType", "Ownership", "QuotaScope", "Enabled", "IsDefault",
                                                 "ConfigurationJson", "SecretFieldNamesJson", "CreatedAt", "UpdatedAt")
                SELECT gen_random_uuid(), t."Id", 'Local', 'local', 'local', 'TenantManaged', 'tenant', true, true, jsonb_build_object(), jsonb_build_array(), now(), now()
                FROM tenants t;

                INSERT INTO storage_connections ("Id", "TenantId", "Name", "NormalizedName", "ProviderType", "Ownership", "QuotaScope", "Enabled", "IsDefault",
                                                 "ConfigurationJson", "SecretFieldNamesJson", "CreatedAt", "UpdatedAt")
                SELECT gen_random_uuid(), t."Id", 'Managed Storage', 'managed storage', 'central', 'PlatformManaged', 'managed', true, true, jsonb_build_object(), jsonb_build_array(), now(), now()
                FROM tenants t;

                -- A provider type the product never wrote (only local and central are accepted at job creation) but that a replica row could still hold:
                -- it gets a disabled, tenant-managed connection of its own so the replica keeps a destination and nothing new can be written to it.
                INSERT INTO storage_connections ("Id", "TenantId", "Name", "NormalizedName", "ProviderType", "Ownership", "QuotaScope", "Enabled", "IsDefault",
                                                 "ConfigurationJson", "SecretFieldNamesJson", "CreatedAt", "UpdatedAt")
                SELECT gen_random_uuid(), r."TenantId", r."StorageProvider", lower(r."StorageProvider"), r."StorageProvider", 'TenantManaged', 'tenant', false, true,
                       jsonb_build_object(), jsonb_build_array(), now(), now()
                FROM (SELECT DISTINCT "TenantId", "StorageProvider" FROM backup_replicas WHERE "StorageProvider" NOT IN ('local', 'central')) r;

                UPDATE backup_replicas r SET "StorageConnectionId" = c."Id"
                FROM storage_connections c
                WHERE c."TenantId" = r."TenantId" AND c."ProviderType" = r."StorageProvider" AND c."IsDefault";
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "StorageConnectionId",
                table: "backup_replicas",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_backup_replicas_storage_connections_StorageConnectionId_Ten~",
                table: "backup_replicas",
                columns: new[] { "StorageConnectionId", "TenantId", "StorageProvider" },
                principalTable: "storage_connections",
                principalColumns: new[] { "Id", "TenantId", "ProviderType" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_backup_replicas_storage_connections_StorageConnectionId_Ten~",
                table: "backup_replicas");

            migrationBuilder.DropTable(
                name: "platform_secrets");

            migrationBuilder.DropTable(
                name: "storage_connections");

            migrationBuilder.DropIndex(
                name: "ix_backup_replicas_connection",
                table: "backup_replicas");

            migrationBuilder.DropIndex(
                name: "IX_backup_replicas_StorageConnectionId_TenantId_StorageProvider",
                table: "backup_replicas");

            migrationBuilder.DropColumn(
                name: "StorageConnectionId",
                table: "backup_replicas");
        }
    }
}
