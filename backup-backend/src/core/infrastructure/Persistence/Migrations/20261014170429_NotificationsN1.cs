using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NotificationsN1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notification_channels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Target = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    NormalizedTarget = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Authentication = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    SecretReference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    SecretUpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_channels", x => x.Id);
                    table.UniqueConstraint("AK_notification_channels_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_notification_channels_auth", "\"Authentication\" IN ('None','Bearer','Hmac') AND (\"Kind\" = 'Webhook' OR \"Authentication\" = 'None')");
                    table.CheckConstraint("ck_notification_channels_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_notification_channels_kind", "\"Kind\" IN ('Email','Webhook','Sms')");
                    table.CheckConstraint("ck_notification_channels_name", "length(btrim(\"Name\")) BETWEEN 1 AND 100 AND \"NormalizedName\" = lower(btrim(\"Name\"))");
                    table.CheckConstraint("ck_notification_channels_secret", "(\"SecretReference\" IS NULL AND \"SecretUpdatedAt\" IS NULL) OR (\"Authentication\" <> 'None' AND length(\"SecretReference\") BETWEEN 1 AND 128 AND \"SecretUpdatedAt\" IS NOT NULL)");
                    table.CheckConstraint("ck_notification_channels_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_notification_channels_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "notification_conditions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SubjectKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubjectName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Activations = table.Column<int>(type: "integer", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Notified = table.Column<bool>(type: "boolean", nullable: false),
                    NotifiedSeverity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ActiveSince = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastNotifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SuppressedCount = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_conditions", x => x.Id);
                    table.UniqueConstraint("AK_notification_conditions_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_notification_conditions_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_notification_conditions_key", "length(\"Key\") BETWEEN 1 AND 200");
                    table.CheckConstraint("ck_notification_conditions_severity", "\"Severity\" IN ('Info','Warning','Critical') AND \"NotifiedSeverity\" IN ('Info','Warning','Critical')");
                    table.CheckConstraint("ck_notification_conditions_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_notification_conditions_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "notification_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Phase = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SubjectKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubjectName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FactsJson = table.Column<string>(type: "jsonb", nullable: false),
                    DedupKey = table.Column<string>(type: "character varying(232)", maxLength: 232, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_events", x => x.Id);
                    table.UniqueConstraint("AK_notification_events_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_notification_events_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_notification_events_phase", "\"Phase\" IN ('Raised','Recovered','Test')");
                    table.CheckConstraint("ck_notification_events_severity", "\"Severity\" IN ('Info','Warning','Critical')");
                    table.CheckConstraint("ck_notification_events_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_notification_events_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "notification_policies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    ActiveSince = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EventTypesJson = table.Column<string>(type: "jsonb", nullable: false),
                    MinimumSeverity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ChannelKindsJson = table.Column<string>(type: "jsonb", nullable: false),
                    RecoveryNotifications = table.Column<bool>(type: "boolean", nullable: false),
                    StaleAfterHours = table.Column<int>(type: "integer", nullable: false),
                    AgentOfflineAfterMinutes = table.Column<int>(type: "integer", nullable: false),
                    ReminderIntervalHours = table.Column<int>(type: "integer", nullable: false),
                    MaxNotificationsPerHour = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_policies", x => x.Id);
                    table.UniqueConstraint("AK_notification_policies_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_notification_policies_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_notification_policies_severity", "\"MinimumSeverity\" IN ('Info','Warning','Critical')");
                    table.CheckConstraint("ck_notification_policies_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_notification_policies_thresholds", "\"StaleAfterHours\" BETWEEN 1 AND 720 AND \"AgentOfflineAfterMinutes\" BETWEEN 5 AND 1440 AND \"ReminderIntervalHours\" BETWEEN 0 AND 168 AND \"MaxNotificationsPerHour\" BETWEEN 1 AND 200");
                    table.ForeignKey(
                        name: "FK_notification_policies_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "notification_transport_settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    ConfigurationJson = table.Column<string>(type: "jsonb", nullable: false),
                    SecretReference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    SecretUpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_transport_settings", x => x.Key);
                    table.CheckConstraint("ck_notification_transport_settings_key", "\"Key\" IN ('email','sms')");
                    table.CheckConstraint("ck_notification_transport_settings_secret", "(\"SecretReference\" IS NULL) = (\"SecretUpdatedAt\" IS NULL)");
                });

            migrationBuilder.CreateTable(
                name: "notification_deliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ChannelName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TargetDisplay = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseOwner = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_deliveries", x => x.Id);
                    table.UniqueConstraint("AK_notification_deliveries_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_notification_deliveries_attempts", "\"AttemptCount\" >= 0");
                    table.CheckConstraint("ck_notification_deliveries_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_notification_deliveries_kind", "\"ChannelKind\" IN ('Email','Webhook','Sms')");
                    table.CheckConstraint("ck_notification_deliveries_state", "(\"Status\" = 'Pending' AND \"NextAttemptAt\" IS NOT NULL AND \"LeaseOwner\" IS NULL AND \"CompletedAt\" IS NULL) OR (\"Status\" = 'InProgress' AND \"LeaseOwner\" IS NOT NULL AND \"LeaseExpiresAt\" IS NOT NULL AND \"CompletedAt\" IS NULL) OR (\"Status\" IN ('Sent','Failed','Suppressed','Cancelled') AND \"NextAttemptAt\" IS NULL AND \"LeaseOwner\" IS NULL AND \"CompletedAt\" IS NOT NULL)");
                    table.CheckConstraint("ck_notification_deliveries_status", "\"Status\" IN ('Pending','InProgress','Sent','Failed','Suppressed','Cancelled')");
                    table.CheckConstraint("ck_notification_deliveries_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_notification_deliveries_notification_events_EventId_TenantId",
                        columns: x => new { x.EventId, x.TenantId },
                        principalTable: "notification_events",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_notification_deliveries_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_notification_channels_TenantId",
                table: "notification_channels",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ux_notification_channels_name",
                table: "notification_channels",
                columns: new[] { "TenantId", "NormalizedName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_notification_channels_target",
                table: "notification_channels",
                columns: new[] { "TenantId", "Kind", "NormalizedTarget" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notification_conditions_active",
                table: "notification_conditions",
                columns: new[] { "TenantId", "Type" },
                filter: "\"IsActive\"");

            migrationBuilder.CreateIndex(
                name: "IX_notification_conditions_TenantId",
                table: "notification_conditions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ux_notification_conditions_key",
                table: "notification_conditions",
                columns: new[] { "TenantId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_channel",
                table: "notification_deliveries",
                columns: new[] { "TenantId", "ChannelId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_due",
                table: "notification_deliveries",
                column: "NextAttemptAt",
                filter: "\"Status\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_notification_deliveries_EventId_TenantId",
                table: "notification_deliveries",
                columns: new[] { "EventId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_history",
                table: "notification_deliveries",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_lease",
                table: "notification_deliveries",
                column: "LeaseExpiresAt",
                filter: "\"Status\" = 'InProgress'");

            migrationBuilder.CreateIndex(
                name: "IX_notification_deliveries_TenantId",
                table: "notification_deliveries",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ux_notification_deliveries_event_channel",
                table: "notification_deliveries",
                columns: new[] { "EventId", "ChannelId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notification_events_created",
                table: "notification_events",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "ix_notification_events_recent",
                table: "notification_events",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_notification_events_TenantId",
                table: "notification_events",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ux_notification_events_dedup",
                table: "notification_events",
                columns: new[] { "TenantId", "DedupKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notification_policies_enabled",
                table: "notification_policies",
                column: "Enabled",
                filter: "\"Enabled\"");

            migrationBuilder.CreateIndex(
                name: "ux_notification_policies_tenant",
                table: "notification_policies",
                column: "TenantId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification_channels");

            migrationBuilder.DropTable(
                name: "notification_conditions");

            migrationBuilder.DropTable(
                name: "notification_deliveries");

            migrationBuilder.DropTable(
                name: "notification_policies");

            migrationBuilder.DropTable(
                name: "notification_transport_settings");

            migrationBuilder.DropTable(
                name: "notification_events");
        }
    }
}
