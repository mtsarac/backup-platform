using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BackupShares : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "backup_shares",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByRole = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_shares", x => x.Id);
                    table.UniqueConstraint("AK_backup_shares_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_backup_shares_artifact_id_nonempty", "\"ArtifactId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_backup_shares_created_by_role", "\"CreatedByRole\" IN (1, 2)");
                    table.CheckConstraint("ck_backup_shares_created_by_user_id_nonempty", "\"CreatedByUserId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_backup_shares_expiry", "\"ExpiresAtUtc\" > \"CreatedAtUtc\"");
                    table.CheckConstraint("ck_backup_shares_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_backup_shares_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_backup_shares_backup_artifacts_ArtifactId_TenantId",
                        columns: x => new { x.ArtifactId, x.TenantId },
                        principalTable: "backup_artifacts",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_backup_shares_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_backup_shares_users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_backup_shares_users_RevokedByUserId",
                        column: x => x.RevokedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_backup_shares_ArtifactId_TenantId",
                table: "backup_shares",
                columns: new[] { "ArtifactId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_shares_CreatedByUserId",
                table: "backup_shares",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_backup_shares_ExpiresAtUtc",
                table: "backup_shares",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_backup_shares_RevokedByUserId",
                table: "backup_shares",
                column: "RevokedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_backup_shares_TenantId",
                table: "backup_shares",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_backup_shares_TenantId_ArtifactId",
                table: "backup_shares",
                columns: new[] { "TenantId", "ArtifactId" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_shares_TenantId_CreatedAtUtc",
                table: "backup_shares",
                columns: new[] { "TenantId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "backup_shares");
        }
    }
}
