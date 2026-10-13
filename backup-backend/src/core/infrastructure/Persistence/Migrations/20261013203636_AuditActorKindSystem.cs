using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditActorKindSystem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_events_actor_kind",
                table: "audit_events");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_events_actor_kind",
                table: "audit_events",
                sql: "\"ActorKind\" IN (1, 2, 3, 4, 5)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Retain the widened check on rollback so append-only events never require rewriting or deletion.
            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_events_actor_kind",
                table: "audit_events");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_events_actor_kind",
                table: "audit_events",
                sql: "\"ActorKind\" IN (1, 2, 3, 4, 5)");
        }
    }
}
