using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class JobCredentialClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "job_credential_claims",
                columns: table => new
                {
                    Reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_credential_claims", x => x.Reference);
                    table.CheckConstraint("ck_job_credential_claims_reference", "length(\"Reference\") BETWEEN 1 AND 128");
                });

            migrationBuilder.CreateIndex(
                name: "IX_job_credential_claims_ClaimedAt",
                table: "job_credential_claims",
                column: "ClaimedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "job_credential_claims");
        }
    }
}
