using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UploadBandwidthLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "upload_bandwidth_grants",
                columns: table => new
                {
                    AttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    BytesPerSecond = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_upload_bandwidth_grants", x => x.AttemptId);
                    table.CheckConstraint("ck_upload_bandwidth_grants_rate", "\"BytesPerSecond\" >= 0");
                });

            migrationBuilder.CreateTable(
                name: "upload_bandwidth_limits",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ScopeId = table.Column<Guid>(type: "uuid", nullable: false),
                    BytesPerSecond = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_upload_bandwidth_limits", x => new { x.TenantId, x.Scope, x.ScopeId });
                    table.CheckConstraint("ck_upload_bandwidth_limits_range", "\"BytesPerSecond\" BETWEEN 16000 AND 100000000000");
                });

            migrationBuilder.CreateIndex(
                name: "IX_upload_bandwidth_grants_TenantId_ExpiresAt",
                table: "upload_bandwidth_grants",
                columns: new[] { "TenantId", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "upload_bandwidth_grants");

            migrationBuilder.DropTable(
                name: "upload_bandwidth_limits");
        }
    }
}
