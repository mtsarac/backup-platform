using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ArtifactEncryption : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EncryptionKeyId",
                table: "backup_artifacts",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EncryptionKeyVersion",
                table: "backup_artifacts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EncryptionWrappedDekBase64",
                table: "backup_artifacts",
                type: "character varying(4096)",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Format",
                table: "backup_artifacts",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "v1-plain");

            migrationBuilder.CreateTable(
                name: "tenant_wrap_keys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    KeyId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PublicKeySpki = table.Column<byte[]>(type: "bytea", nullable: false),
                    SecretReference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_wrap_keys", x => x.Id);
                    table.UniqueConstraint("AK_tenant_wrap_keys_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_tenant_wrap_keys_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_tenant_wrap_keys_state", "\"State\" IN ('Active','DecryptOnly','Retired')");
                    table.CheckConstraint("ck_tenant_wrap_keys_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_tenant_wrap_keys_version", "\"Version\" >= 1");
                    table.ForeignKey(
                        name: "FK_tenant_wrap_keys_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_backup_artifact_encryption",
                table: "backup_artifacts",
                sql: "(\"Format\" = 'v1-plain' AND \"EncryptionKeyId\" IS NULL AND \"EncryptionKeyVersion\" IS NULL AND \"EncryptionWrappedDekBase64\" IS NULL) OR (\"Format\" = 'v2-bpe' AND \"EncryptionKeyId\" IS NOT NULL AND \"EncryptionKeyVersion\" >= 1 AND \"EncryptionWrappedDekBase64\" IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_wrap_keys_KeyId",
                table: "tenant_wrap_keys",
                column: "KeyId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenant_wrap_keys_TenantId",
                table: "tenant_wrap_keys",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_wrap_keys_TenantId_Version",
                table: "tenant_wrap_keys",
                columns: new[] { "TenantId", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_wrap_keys");

            migrationBuilder.DropCheckConstraint(
                name: "ck_backup_artifact_encryption",
                table: "backup_artifacts");

            migrationBuilder.DropColumn(
                name: "EncryptionKeyId",
                table: "backup_artifacts");

            migrationBuilder.DropColumn(
                name: "EncryptionKeyVersion",
                table: "backup_artifacts");

            migrationBuilder.DropColumn(
                name: "EncryptionWrappedDekBase64",
                table: "backup_artifacts");

            migrationBuilder.DropColumn(
                name: "Format",
                table: "backup_artifacts");
        }
    }
}
