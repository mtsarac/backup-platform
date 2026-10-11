using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AutomaticReplicaRetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                table: "replica_repair_operations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastAttemptAt",
                table: "replica_repair_operations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastFailureCode",
                table: "replica_repair_operations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxAttempts",
                table: "replica_repair_operations",
                type: "integer",
                nullable: false,
                defaultValue: 5);
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextRetryAt",
                table: "replica_repair_operations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_replica_repair_due",
                table: "replica_repair_operations",
                columns: new[] { "NextRetryAt", "Id" },
                filter: "\"Status\" = 'Pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_replica_repair_due",
                table: "replica_repair_operations");

            migrationBuilder.DropColumn(
                name: "AttemptCount",
                table: "replica_repair_operations");

            migrationBuilder.DropColumn(
                name: "LastAttemptAt",
                table: "replica_repair_operations");

            migrationBuilder.DropColumn(
                name: "LastFailureCode",
                table: "replica_repair_operations");

            migrationBuilder.DropColumn(
                name: "MaxAttempts",
                table: "replica_repair_operations");

            migrationBuilder.DropColumn(
                name: "NextRetryAt",
                table: "replica_repair_operations");
        }
    }
}
