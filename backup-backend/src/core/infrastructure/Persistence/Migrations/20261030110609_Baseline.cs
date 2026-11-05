using System;
using System.IO;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupPlatform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Baseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorKind = table.Column<int>(type: "integer", nullable: false),
                    Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TargetId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DetailsJson = table.Column<string>(type: "jsonb", nullable: false),
                    SupportSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    SealKeyId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    SealSignature = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.Id);
                    table.CheckConstraint("ck_audit_events_actor_kind", "\"ActorKind\" IN (1, 2, 3, 4, 5)");
                    table.CheckConstraint("ck_audit_events_text", "length(\"Action\") > 0 AND length(\"TargetType\") > 0 AND length(\"TargetId\") > 0");
                });

            migrationBuilder.CreateTable(
                name: "backup_transfer_leases",
                columns: table => new
                {
                    LockId = table.Column<long>(type: "bigint", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_transfer_leases", x => x.LockId);
                });

            migrationBuilder.CreateTable(
                name: "bandwidth_profile_assignments",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ScopeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bandwidth_profile_assignments", x => new { x.TenantId, x.Scope, x.ScopeId });
                });

            migrationBuilder.CreateTable(
                name: "bandwidth_profiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TimeZoneId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bandwidth_profiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "google_drive_file_metadata",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    FileId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ParentFolderId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    FileName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Checksum = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Md5Checksum = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DriftKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    DriftDetectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_google_drive_file_metadata", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "google_drive_oauth_states",
                columns: table => new
                {
                    State = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CodeVerifier = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RedirectUri = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Used = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_google_drive_oauth_states", x => x.State);
                });

            migrationBuilder.CreateTable(
                name: "google_drive_staging_reservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: false),
                    VolumeId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    NodeId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SharedVolume = table.Column<bool>(type: "boolean", nullable: false),
                    DirectoryPath = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    FileName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExpectedBytes = table.Column<long>(type: "bigint", nullable: false),
                    WrittenBytes = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_google_drive_staging_reservations", x => x.Id);
                    table.CheckConstraint("ck_google_drive_staging_bytes", "\"ExpectedBytes\" > 0 AND \"WrittenBytes\" >= 0 AND \"WrittenBytes\" <= \"ExpectedBytes\"");
                });

            migrationBuilder.CreateTable(
                name: "login_throttle_entries",
                columns: table => new
                {
                    Identifier = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Failures = table.Column<int>(type: "integer", nullable: false),
                    WindowStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_login_throttle_entries", x => x.Identifier);
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
                name: "platform_agent_settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    ServerUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    EnrollmentTokenLifetimeMinutes = table.Column<int>(type: "integer", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_agent_settings", x => x.Id);
                    table.CheckConstraint("ck_platform_agent_settings_singleton", "\"Id\" = 1");
                });

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
                name: "tenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenants", x => x.Id);
                    table.CheckConstraint("ck_tenants_id_nonempty", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_tenants_name_nonblank", "\"Name\" ~ '[^[:space:]]'");
                });

            migrationBuilder.CreateTable(
                name: "upload_bandwidth_grants",
                columns: table => new
                {
                    UploadId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                    BytesPerSecond = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_upload_bandwidth_grants", x => x.UploadId);
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

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                    table.CheckConstraint("ck_users_display_name_nonblank", "\"DisplayName\" ~ '[^[:space:]]'");
                    table.CheckConstraint("ck_users_id_nonempty", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                });

            migrationBuilder.CreateTable(
                name: "transfer_schedule_windows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    DayOfWeek = table.Column<int>(type: "integer", nullable: false),
                    StartTime = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    EndTime = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    BytesPerSecond = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_transfer_schedule_windows", x => x.Id);
                    table.CheckConstraint("ck_transfer_schedule_windows_rate", "\"BytesPerSecond\" = 0 OR \"BytesPerSecond\" BETWEEN 16000 AND 100000000000");
                    table.ForeignKey(
                        name: "FK_transfer_schedule_windows_bandwidth_profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "bandwidth_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

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
                    table.ForeignKey(
                        name: "FK_plans_quota_policies_QuotaPolicyReference",
                        column: x => x.QuotaPolicyReference,
                        principalTable: "quota_policies",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "agents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    HostName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    InstallationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Software = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CredentialGeneration = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agents", x => x.Id);
                    table.UniqueConstraint("ak_agents_id_tenant_id", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_agents_credential_generation", "(\"Status\" = 1 AND \"CredentialGeneration\" = 0)\nOR (\"Status\" IN (2, 3) AND \"CredentialGeneration\" >= 1)\nOR (\"Status\" = 4 AND \"CredentialGeneration\" >= 0)");
                    table.CheckConstraint("ck_agents_host_name", "\"HostName\" IS NULL OR \"HostName\" ~ '[^[:space:]]'");
                    table.CheckConstraint("ck_agents_id_nonempty", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_agents_installation_id", "\"InstallationId\" IS NULL OR \"InstallationId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_agents_status", "\"Status\" BETWEEN 1 AND 4");
                    table.CheckConstraint("ck_agents_tenant_id_nonempty", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_agents_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "backup_storage_cleanups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                    StorageProvider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StorageName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TargetIdentity = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Key = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    IsPartial = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_storage_cleanups", x => x.Id);
                    table.UniqueConstraint("AK_backup_storage_cleanups_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_backup_storage_cleanups_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_backup_storage_cleanups_lease", "(\"Status\" = 2 AND \"CompletedAt\" IS NOT NULL) OR (\"Status\" = 1 AND (\"LeaseToken\" IS NULL) = (\"LeaseExpiresAt\" IS NULL))");
                    table.CheckConstraint("ck_backup_storage_cleanups_status", "\"Status\" IN (1, 2)");
                    table.CheckConstraint("ck_backup_storage_cleanups_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_backup_storage_cleanups_tenants_TenantId",
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
                    AdministrativelyDisabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
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
                    RecipientVerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UnsubscribedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SecurityOwnerUserId = table.Column<Guid>(type: "uuid", nullable: true),
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
                    table.CheckConstraint("ck_notification_channels_recipient_verified", "\"RecipientVerifiedAt\" IS NULL OR \"Kind\" = 'Email'");
                    table.CheckConstraint("ck_notification_channels_secret", "(\"SecretReference\" IS NULL AND \"SecretUpdatedAt\" IS NULL) OR (\"Authentication\" <> 'None' AND length(\"SecretReference\") BETWEEN 1 AND 128 AND \"SecretUpdatedAt\" IS NOT NULL)");
                    table.CheckConstraint("ck_notification_channels_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_notification_channels_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_notification_channels_users_SecurityOwnerUserId",
                        column: x => x.SecurityOwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "password_credentials",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LoginIdentifier = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    NormalizedLoginIdentifier = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_password_credentials", x => x.UserId);
                    table.CheckConstraint("ck_password_credentials_hash", "\"PasswordHash\" ~ '[^[:space:]]'");
                    table.CheckConstraint("ck_password_credentials_login", "\"LoginIdentifier\" ~ '^[A-Za-z0-9._-]{3,128}$'");
                    table.CheckConstraint("ck_password_credentials_normalized_login", "\"NormalizedLoginIdentifier\" = upper(\"LoginIdentifier\" COLLATE \"C\")");
                    table.CheckConstraint("ck_password_credentials_user_id", "\"UserId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_password_credentials_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "platform_admins",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_admins", x => x.UserId);
                    table.CheckConstraint("ck_platform_admins_user_id_nonempty", "\"UserId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_platform_admins_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "support_access_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlatformAdminId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Permissions = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ClosedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CloseReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestFingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_support_access_sessions", x => x.Id);
                    table.UniqueConstraint("AK_support_access_sessions_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_support_access_sessions_closer", "(\"Status\" IN ('Closed','Revoked')) = (\"ClosedBy\" IS NOT NULL) AND (\"CloseReason\" IS NULL OR \"Status\" = 'Revoked')");
                    table.CheckConstraint("ck_support_access_sessions_end", "(\"Status\" = 'Active') = (\"ClosedAt\" IS NULL)");
                    table.CheckConstraint("ck_support_access_sessions_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_support_access_sessions_key", "length(\"IdempotencyKey\") BETWEEN 1 AND 128 AND length(\"RequestFingerprint\") BETWEEN 1 AND 128");
                    table.CheckConstraint("ck_support_access_sessions_permissions", "\"Permissions\" BETWEEN 1 AND 7 AND (\"Permissions\" & 1) = 1");
                    table.CheckConstraint("ck_support_access_sessions_reason", "\"Reason\" ~ '[^[:space:]]' AND length(\"Reason\") <= 500");
                    table.CheckConstraint("ck_support_access_sessions_status", "\"Status\" IN ('Active','Expired','Closed','Revoked')");
                    table.CheckConstraint("ck_support_access_sessions_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_support_access_sessions_window", "\"ExpiresAt\" > \"StartedAt\" AND \"ExpiresAt\" <= \"StartedAt\" + interval '4 hours'");
                    table.ForeignKey(
                        name: "FK_support_access_sessions_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_support_access_sessions_users_PlatformAdminId",
                        column: x => x.PlatformAdminId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tenant_agent_enrollment_policies",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ChangedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_agent_enrollment_policies", x => x.TenantId);
                    table.CheckConstraint("ck_tenant_agent_enrollment_policies_tenant_id", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_tenant_agent_enrollment_policies_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_tenant_agent_enrollment_policies_users_ChangedBy",
                        column: x => x.ChangedBy,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tenant_memberships",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_memberships", x => new { x.TenantId, x.UserId });
                    table.CheckConstraint("ck_tenant_memberships_role", "\"Role\" BETWEEN 1 AND 4");
                    table.CheckConstraint("ck_tenant_memberships_tenant_id_nonempty", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_tenant_memberships_user_id_nonempty", "\"UserId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_tenant_memberships_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_tenant_memberships_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tenant_recovery_keys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    KeyId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PublicKeySpki = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_recovery_keys", x => x.Id);
                    table.UniqueConstraint("AK_tenant_recovery_keys_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_tenant_recovery_keys_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_tenant_recovery_keys_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_tenant_recovery_keys_version", "\"Version\" >= 1");
                    table.ForeignKey(
                        name: "FK_tenant_recovery_keys_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_tenant_recovery_keys_users_CreatedBy",
                        column: x => x.CreatedBy,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_sessions", x => x.Id);
                    table.CheckConstraint("ck_user_sessions_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_user_sessions_user_id", "\"UserId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_user_sessions_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
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

            migrationBuilder.CreateTable(
                name: "agent_capabilities",
                columns: table => new
                {
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Identifier = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Support = table.Column<int>(type: "integer", nullable: false),
                    MetadataJson = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    Revision = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReportedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_capabilities", x => new { x.AgentId, x.TenantId, x.Identifier });
                    table.CheckConstraint("ck_agent_capabilities_support", "\"Support\" IN (0, 1, 2)");
                    table.CheckConstraint("ck_agent_capabilities_tenant_id", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_agent_capabilities_text", "length(\"Identifier\") > 0 AND length(\"Revision\") > 0");
                    table.ForeignKey(
                        name: "FK_agent_capabilities_agents_AgentId_TenantId",
                        columns: x => new { x.AgentId, x.TenantId },
                        principalTable: "agents",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "agent_connectivity",
                columns: table => new
                {
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastHeartbeatAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastConnectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReportedAgentVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ReportedProtocolMajor = table.Column<int>(type: "integer", nullable: true),
                    ReportedProtocolMinor = table.Column<int>(type: "integer", nullable: true),
                    ReportedPlatform = table.Column<int>(type: "integer", nullable: true),
                    ReportedOsDescription = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ReportedRuntimeVersion = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CapabilityRevision = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_connectivity", x => x.AgentId);
                    table.CheckConstraint("ck_agent_connectivity_protocol", "(\"ReportedProtocolMajor\" IS NULL) = (\"ReportedProtocolMinor\" IS NULL) AND (\"ReportedProtocolMajor\" IS NULL OR (\"ReportedProtocolMajor\" >= 1 AND \"ReportedProtocolMinor\" >= 0))");
                    table.CheckConstraint("ck_agent_connectivity_tenant_id", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_agent_connectivity_agents_AgentId_TenantId",
                        columns: x => new { x.AgentId, x.TenantId },
                        principalTable: "agents",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "agent_credentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Generation = table.Column<int>(type: "integer", nullable: false),
                    SecretHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ActivatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevocationReason = table.Column<int>(type: "integer", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_credentials", x => x.Id);
                    table.CheckConstraint("ck_agent_credentials_generation", "\"Generation\" >= 1");
                    table.CheckConstraint("ck_agent_credentials_hash", "octet_length(\"SecretHash\") = 32");
                    table.CheckConstraint("ck_agent_credentials_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_agent_credentials_state", "(\"Status\" = 1 AND \"ExpiresAt\" IS NOT NULL AND \"ActivatedAt\" IS NULL AND \"RevokedAt\" IS NULL AND \"RevocationReason\" IS NULL)\nOR (\"Status\" = 2 AND \"ExpiresAt\" IS NULL AND \"ActivatedAt\" IS NOT NULL AND \"RevokedAt\" IS NULL AND \"RevocationReason\" IS NULL)\nOR (\"Status\" = 3 AND \"RevokedAt\" IS NOT NULL AND \"RevocationReason\" BETWEEN 1 AND 3)");
                    table.ForeignKey(
                        name: "FK_agent_credentials_agents_AgentId_TenantId",
                        columns: x => new { x.AgentId, x.TenantId },
                        principalTable: "agents",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "agent_enrollment_tokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    SecretHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConsumedByAgentId = table.Column<Guid>(type: "uuid", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_enrollment_tokens", x => x.Id);
                    table.CheckConstraint("ck_agent_enrollment_tokens_consumption", "(\"ConsumedAt\" IS NULL) = (\"ConsumedByAgentId\" IS NULL)");
                    table.CheckConstraint("ck_agent_enrollment_tokens_expiry", "\"ExpiresAt\" > \"CreatedAt\"");
                    table.CheckConstraint("ck_agent_enrollment_tokens_hash", "octet_length(\"SecretHash\") = 32");
                    table.CheckConstraint("ck_agent_enrollment_tokens_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_agent_enrollment_tokens_revocation", "(\"RevokedAt\" IS NULL) = (\"RevokedBy\" IS NULL)");
                    table.CheckConstraint("ck_agent_enrollment_tokens_tenant_id", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_agent_enrollment_tokens_terminal", "\"ConsumedAt\" IS NULL OR \"RevokedAt\" IS NULL");
                    table.ForeignKey(
                        name: "FK_agent_enrollment_tokens_agents_ConsumedByAgentId_TenantId",
                        columns: x => new { x.ConsumedByAgentId, x.TenantId },
                        principalTable: "agents",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_agent_enrollment_tokens_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_agent_enrollment_tokens_users_CreatedBy",
                        column: x => x.CreatedBy,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_agent_enrollment_tokens_users_RevokedBy",
                        column: x => x.RevokedBy,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "backup_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BackupType = table.Column<string>(type: "text", nullable: false),
                    Configuration = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    OverlapPolicy = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    RetentionDeleteGrace = table.Column<TimeSpan>(type: "interval", nullable: false),
                    RetentionEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    RetentionKeepFor = table.Column<TimeSpan>(type: "interval", nullable: false),
                    RetryBackoff = table.Column<int>(type: "integer", nullable: false),
                    RetryInitialDelay = table.Column<TimeSpan>(type: "interval", nullable: false),
                    RetryJitter = table.Column<double>(type: "double precision", nullable: false),
                    RetryMaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    RetryMaxDelay = table.Column<TimeSpan>(type: "interval", nullable: false),
                    ScheduleCron = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ScheduleEffectiveFrom = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ScheduleEnabled = table.Column<bool>(type: "boolean", nullable: true),
                    ScheduleMissedRunGrace = table.Column<TimeSpan>(type: "interval", nullable: true),
                    ScheduleTimeZone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_jobs", x => x.Id);
                    table.UniqueConstraint("AK_backup_jobs_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_backup_job_policy", "\"BackupType\" = 'Full' AND \"OverlapPolicy\" IN ('Skip','Queue')");
                    table.CheckConstraint("ck_backup_jobs_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_backup_jobs_retention_policy", "\"RetentionKeepFor\" BETWEEN interval '1 second' AND interval '3650 days' AND \"RetentionDeleteGrace\" BETWEEN interval '1 second' AND interval '365 days'");
                    table.CheckConstraint("ck_backup_jobs_retry_policy", "\"RetryMaxAttempts\" BETWEEN 1 AND 10 AND \"RetryInitialDelay\" >= interval '1 second' AND \"RetryMaxDelay\" >= \"RetryInitialDelay\" AND \"RetryMaxDelay\" <= interval '24 hours' AND \"RetryBackoff\" IN (1,2) AND \"RetryJitter\" BETWEEN 0 AND 1");
                    table.CheckConstraint("ck_backup_jobs_schedule", "(\"ScheduleEnabled\" IS NULL AND \"ScheduleCron\" IS NULL AND \"ScheduleTimeZone\" IS NULL AND \"ScheduleMissedRunGrace\" IS NULL AND \"ScheduleEffectiveFrom\" IS NULL) OR (\"ScheduleEnabled\" IS NOT NULL AND \"ScheduleCron\" IS NOT NULL AND \"ScheduleTimeZone\" IS NOT NULL AND \"ScheduleMissedRunGrace\" IS NOT NULL AND \"ScheduleEffectiveFrom\" IS NOT NULL AND \"ScheduleMissedRunGrace\" BETWEEN interval '1 minute' AND interval '7 days')");
                    table.CheckConstraint("ck_backup_jobs_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_backup_jobs_agents_AgentId_TenantId",
                        columns: x => new { x.AgentId, x.TenantId },
                        principalTable: "agents",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_backup_jobs_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "discovery_agent_states",
                columns: table => new
                {
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    ScanIntervalMinutes = table.Column<int>(type: "integer", nullable: false),
                    IncludeDocker = table.Column<bool>(type: "boolean", nullable: false),
                    ScanRequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ScanRequestReason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    RequestedInstanceKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ScanId = table.Column<Guid>(type: "uuid", nullable: true),
                    ScanReason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    ScanStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextScanDueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastScanAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSuccessfulScanAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastOutcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    LastFailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SourcesJson = table.Column<string>(type: "jsonb", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discovery_agent_states", x => x.AgentId);
                    table.CheckConstraint("ck_discovery_agent_states_ids", "\"AgentId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_discovery_agent_states_interval", "\"ScanIntervalMinutes\" BETWEEN 15 AND 10080");
                    table.ForeignKey(
                        name: "FK_discovery_agent_states_agents_AgentId_TenantId",
                        columns: x => new { x.AgentId, x.TenantId },
                        principalTable: "agents",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "discovery_instances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstanceKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Engine = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    InstanceName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: true),
                    Version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Edition = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    HostPlatform = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ContainerName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ContainerImage = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Presence = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    MissCount = table.Column<int>(type: "integer", nullable: false),
                    Reachability = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Access = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AccessReasonCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FullBackup = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FullBackupReasonCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CredentialReference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    IntegratedSecurity = table.Column<bool>(type: "boolean", nullable: false),
                    Encrypt = table.Column<bool>(type: "boolean", nullable: false),
                    TrustServerCertificate = table.Column<bool>(type: "boolean", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastChangeKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    LastChangeAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DatabasesCheckedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discovery_instances", x => x.Id);
                    table.UniqueConstraint("AK_discovery_instances_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_discovery_instances_binding", "NOT (\"IntegratedSecurity\" AND \"CredentialReference\" IS NOT NULL)");
                    table.CheckConstraint("ck_discovery_instances_engine", "\"Engine\" IN ('mssql','postgres')");
                    table.CheckConstraint("ck_discovery_instances_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_discovery_instances_port", "\"Port\" IS NULL OR \"Port\" BETWEEN 1 AND 65535");
                    table.CheckConstraint("ck_discovery_instances_presence", "\"Presence\" IN ('Present','Missing')");
                    table.CheckConstraint("ck_discovery_instances_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_discovery_instances_agents_AgentId_TenantId",
                        columns: x => new { x.AgentId, x.TenantId },
                        principalTable: "agents",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_discovery_instances_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
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

            migrationBuilder.CreateTable(
                name: "storage_capacity_snapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    TotalBytes = table.Column<long>(type: "bigint", nullable: true),
                    UsedBytes = table.Column<long>(type: "bigint", nullable: true),
                    FreeBytes = table.Column<long>(type: "bigint", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ReportedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ErrorMessage = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_storage_capacity_snapshots", x => x.Id);
                    table.UniqueConstraint("AK_storage_capacity_snapshots_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_storage_capacity_snapshots_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_storage_capacity_snapshots_status", "\"Status\" IN ('Available','Warning','Critical','CapacityUnavailable')");
                    table.CheckConstraint("ck_storage_capacity_snapshots_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_storage_capacity_snapshots_storage_connections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalTable: "storage_connections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_storage_capacity_snapshots_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "storage_capacity_tasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RoundId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConnectionUpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletionJson = table.Column<string>(type: "jsonb", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_storage_capacity_tasks", x => x.Id);
                    table.UniqueConstraint("AK_storage_capacity_tasks_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_storage_capacity_tasks_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_storage_capacity_tasks_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_storage_capacity_tasks_storage_connections_ConnectionId_Ten~",
                        columns: x => new { x.ConnectionId, x.TenantId },
                        principalTable: "storage_connections",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_storage_capacity_tasks_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "backup_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Configuration = table.Column<string>(type: "jsonb", nullable: false),
                    OverlapPolicy = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CurrentAttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    RetryState = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    BackupType = table.Column<string>(type: "text", nullable: false),
                    TriggerType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ScheduledOccurrence = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ClaimDeadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancellationRequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TerminalReason = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    SizeAnomalyStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    SizeAnomalyDeviationRatio = table.Column<double>(type: "double precision", nullable: true),
                    SizeAnomalyBaselineBytes = table.Column<long>(type: "bigint", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    RetentionDeleteGrace = table.Column<TimeSpan>(type: "interval", nullable: false),
                    RetentionEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    RetentionKeepFor = table.Column<TimeSpan>(type: "interval", nullable: false),
                    RetryBackoff = table.Column<int>(type: "integer", nullable: false),
                    RetryInitialDelay = table.Column<TimeSpan>(type: "interval", nullable: false),
                    RetryJitter = table.Column<double>(type: "double precision", nullable: false),
                    RetryMaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    RetryMaxDelay = table.Column<TimeSpan>(type: "interval", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_runs", x => x.Id);
                    table.UniqueConstraint("AK_backup_runs_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_backup_runs_attempt_budget", "\"CurrentAttemptNumber\" <= \"RetryMaxAttempts\"");
                    table.CheckConstraint("ck_backup_runs_attempt_number", "\"CurrentAttemptNumber\" >= 1");
                    table.CheckConstraint("ck_backup_runs_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_backup_runs_overlap", "\"OverlapPolicy\" IN ('Skip','Queue')");
                    table.CheckConstraint("ck_backup_runs_retention_policy", "\"RetentionKeepFor\" BETWEEN interval '1 second' AND interval '3650 days' AND \"RetentionDeleteGrace\" BETWEEN interval '1 second' AND interval '365 days'");
                    table.CheckConstraint("ck_backup_runs_retry_policy", "\"RetryMaxAttempts\" BETWEEN 1 AND 10 AND \"RetryInitialDelay\" >= interval '1 second' AND \"RetryMaxDelay\" >= \"RetryInitialDelay\" AND \"RetryMaxDelay\" <= interval '24 hours' AND \"RetryBackoff\" IN (1,2) AND \"RetryJitter\" BETWEEN 0 AND 1");
                    table.CheckConstraint("ck_backup_runs_retry_schedule", "(\"Status\" = 8) = (\"NextAttemptAt\" IS NOT NULL)");
                    table.CheckConstraint("ck_backup_runs_retry_state", "\"RetryState\" BETWEEN 0 AND 3 AND ((\"Status\" = 8) = (\"RetryState\" = 1))");
                    table.CheckConstraint("ck_backup_runs_status", "\"Status\" BETWEEN 1 AND 9");
                    table.CheckConstraint("ck_backup_runs_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_backup_runs_terminal", "(\"Status\" IN (4,5,6,7)) = (\"CompletedAt\" IS NOT NULL)");
                    table.CheckConstraint("ck_backup_runs_trigger", "(\"TriggerType\" = 'Manual' AND \"ScheduledOccurrence\" IS NULL AND \"ClaimDeadline\" IS NULL) OR (\"TriggerType\" = 'Scheduled' AND \"ScheduledOccurrence\" IS NOT NULL AND \"ClaimDeadline\" IS NOT NULL AND \"ClaimDeadline\" >= \"ScheduledOccurrence\")");
                    table.CheckConstraint("ck_backup_runs_type", "\"BackupType\" = 'Full'");
                    table.ForeignKey(
                        name: "FK_backup_runs_agents_AgentId_TenantId",
                        columns: x => new { x.AgentId, x.TenantId },
                        principalTable: "agents",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_backup_runs_backup_jobs_JobId_TenantId",
                        columns: x => new { x.JobId, x.TenantId },
                        principalTable: "backup_jobs",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_backup_runs_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "discovery_databases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    IsSystem = table.Column<bool>(type: "boolean", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FullBackup = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FullBackupReasonCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Presence = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discovery_databases", x => x.Id);
                    table.UniqueConstraint("AK_discovery_databases_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_discovery_databases_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_discovery_databases_presence", "\"Presence\" IN ('Present','Missing')");
                    table.CheckConstraint("ck_discovery_databases_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_discovery_databases_discovery_instances_InstanceId_TenantId",
                        columns: x => new { x.InstanceId, x.TenantId },
                        principalTable: "discovery_instances",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_discovery_databases_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "backup_run_attempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EligibleAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LeasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SoftwareVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ProtocolVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Platform = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ProgressStage = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ProgressPercent = table.Column<double>(type: "double precision", nullable: true),
                    ProgressAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TerminalReason = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    PauseRequested = table.Column<bool>(type: "boolean", nullable: false),
                    PauseReason = table.Column<int>(type: "integer", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_run_attempts", x => x.Id);
                    table.UniqueConstraint("AK_backup_run_attempts_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_backup_attempt_lease", "\"Status\" NOT IN (2,3) OR (\"LeaseToken\" IS NOT NULL AND \"LeaseToken\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"LeaseExpiresAt\" IS NOT NULL)");
                    table.CheckConstraint("ck_backup_attempt_number", "\"Number\" >= 1");
                    table.CheckConstraint("ck_backup_attempt_progress", "\"ProgressPercent\" IS NULL OR (\"ProgressPercent\" >= 0 AND \"ProgressPercent\" <= 100)");
                    table.CheckConstraint("ck_backup_attempt_started", "\"Status\" <> 3 OR \"StartedAt\" IS NOT NULL");
                    table.CheckConstraint("ck_backup_attempt_terminal", "(\"Status\" IN (4,5,6,7)) = (\"CompletedAt\" IS NOT NULL)");
                    table.CheckConstraint("ck_backup_run_attempts_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_backup_run_attempts_status", "(\"Status\" BETWEEN 1 AND 7) OR \"Status\" = 9");
                    table.CheckConstraint("ck_backup_run_attempts_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_backup_run_attempts_agents_AgentId_TenantId",
                        columns: x => new { x.AgentId, x.TenantId },
                        principalTable: "agents",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_backup_run_attempts_backup_runs_RunId_TenantId",
                        columns: x => new { x.RunId, x.TenantId },
                        principalTable: "backup_runs",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_backup_run_attempts_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "backup_schedule_occurrences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScheduledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_schedule_occurrences", x => x.Id);
                    table.UniqueConstraint("AK_backup_schedule_occurrences_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_backup_occurrence_outcome", "\"Outcome\" IN ('RunCreated','SkippedOverlap','MissedGraceExpired','MissedSuperseded','JobDisabled','AdmissionDenied')");
                    table.CheckConstraint("ck_backup_occurrence_run", "(\"Outcome\" IN ('RunCreated','SkippedOverlap')) = (\"RunId\" IS NOT NULL)");
                    table.CheckConstraint("ck_backup_schedule_occurrences_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_backup_schedule_occurrences_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_backup_schedule_occurrences_backup_jobs_JobId_TenantId",
                        columns: x => new { x.JobId, x.TenantId },
                        principalTable: "backup_jobs",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_backup_schedule_occurrences_backup_runs_RunId_TenantId",
                        columns: x => new { x.RunId, x.TenantId },
                        principalTable: "backup_runs",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_backup_schedule_occurrences_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "backup_artifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    BackupType = table.Column<string>(type: "text", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    SourceChecksum = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ArtifactChecksum = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ChecksumAlgorithm = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SourceMetadataJson = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: false),
                    AppliedOptionsJson = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: false),
                    Format = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "v1-plain"),
                    EncryptionKeyId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    EncryptionKeyVersion = table.Column<int>(type: "integer", nullable: true),
                    EncryptionWrappedDekBase64 = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    EncryptionRecipientsJson = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RetentionExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RetentionGracePeriod = table.Column<TimeSpan>(type: "interval", nullable: false, defaultValue: new TimeSpan(1, 0, 0, 0, 0)),
                    RetentionPinnedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RetentionAppliedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Sealed = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_artifacts", x => x.Id);
                    table.UniqueConstraint("AK_backup_artifacts_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.UniqueConstraint("AK_backup_artifacts_Id_TenantId_SizeBytes_ArtifactChecksum_Che~", x => new { x.Id, x.TenantId, x.SizeBytes, x.ArtifactChecksum, x.ChecksumAlgorithm });
                    table.CheckConstraint("ck_backup_artifact_checksums", "\"ChecksumAlgorithm\" = 'sha256' AND \"SourceChecksum\" ~ '^[0-9a-f]{64}$' AND \"ArtifactChecksum\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_backup_artifact_encryption", "(\"Format\" = 'v1-plain' AND \"EncryptionKeyId\" IS NULL AND \"EncryptionKeyVersion\" IS NULL AND \"EncryptionWrappedDekBase64\" IS NULL AND \"EncryptionRecipientsJson\" IS NULL) OR (\"Format\" = 'v2-bpe' AND \"EncryptionKeyId\" IS NOT NULL AND \"EncryptionKeyVersion\" >= 1 AND \"EncryptionWrappedDekBase64\" IS NOT NULL)");
                    table.CheckConstraint("ck_backup_artifact_retention", "\"RetentionGracePeriod\" BETWEEN interval '1 second' AND interval '365 days' AND (\"RetentionExpiresAt\" IS NULL OR (\"AcceptedAt\" IS NOT NULL AND \"RetentionExpiresAt\" > \"AcceptedAt\"))");
                    table.CheckConstraint("ck_backup_artifact_size", "\"SizeBytes\" > 0");
                    table.CheckConstraint("ck_backup_artifact_type", "\"BackupType\" = 'Full'");
                    table.CheckConstraint("ck_backup_artifacts_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_backup_artifacts_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_backup_artifacts_backup_run_attempts_AttemptId_TenantId",
                        columns: x => new { x.AttemptId, x.TenantId },
                        principalTable: "backup_run_attempts",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_backup_artifacts_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "backup_destination_outcomes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                    StorageProvider = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    StorageConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false),
                    ExecutionLocation = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Upload = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Verification = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FailureCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Key = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    ObjectMayRemain = table.Column<bool>(type: "boolean", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReplicaId = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_destination_outcomes", x => x.Id);
                    table.UniqueConstraint("AK_backup_destination_outcomes_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_backup_destination_outcome_state", "\"State\" IN ('Verified','Failed','Cancelled') AND \"Upload\" IN ('Succeeded','Failed','NotAttempted') AND \"Verification\" IN ('Verified','Failed','NotRun')");
                    table.CheckConstraint("ck_backup_destination_outcome_times", "\"CompletedAt\" >= \"StartedAt\"");
                    table.CheckConstraint("ck_backup_destination_outcome_verified", "(\"State\" = 'Verified') = (\"ReplicaId\" IS NOT NULL) AND (\"State\" <> 'Verified' OR (\"FailureCode\" IS NULL AND \"Upload\" = 'Succeeded' AND \"Verification\" = 'Verified' AND \"Key\" IS NOT NULL)) AND (\"State\" = 'Verified' OR (\"FailureCode\" IS NOT NULL AND \"Verification\" <> 'Verified')) AND (NOT \"ObjectMayRemain\" OR (\"State\" <> 'Verified' AND \"Key\" IS NOT NULL AND \"Upload\" = 'Succeeded'))");
                    table.CheckConstraint("ck_backup_destination_outcomes_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_backup_destination_outcomes_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_backup_destination_outcomes_backup_run_attempts_AttemptId_T~",
                        columns: x => new { x.AttemptId, x.TenantId },
                        principalTable: "backup_run_attempts",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_backup_destination_outcomes_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "backup_lifecycle_facts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_lifecycle_facts", x => x.Id);
                    table.UniqueConstraint("AK_backup_lifecycle_facts_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_backup_lifecycle_facts_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_backup_lifecycle_facts_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_backup_lifecycle_facts_backup_run_attempts_AttemptId_Tenant~",
                        columns: x => new { x.AttemptId, x.TenantId },
                        principalTable: "backup_run_attempts",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_backup_lifecycle_facts_backup_runs_RunId_TenantId",
                        columns: x => new { x.RunId, x.TenantId },
                        principalTable: "backup_runs",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_backup_lifecycle_facts_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "transfer_checkpoints",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                    StorageProvider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DestinationId = table.Column<Guid>(type: "uuid", nullable: false),
                    VerifiedByteOffset = table.Column<long>(type: "bigint", nullable: false),
                    ProviderState = table.Column<string>(type: "character varying(2097152)", maxLength: 2097152, nullable: true),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_transfer_checkpoints", x => new { x.TenantId, x.AttemptId, x.StorageProvider, x.DestinationId });
                    table.CheckConstraint("ck_transfer_checkpoint_offset", "\"VerifiedByteOffset\" >= 0");
                    table.ForeignKey(
                        name: "FK_transfer_checkpoints_backup_run_attempts_AttemptId_TenantId",
                        columns: x => new { x.AttemptId, x.TenantId },
                        principalTable: "backup_run_attempts",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "artifact_break_glass_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DecidedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecisionReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DownloadSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_artifact_break_glass_requests", x => x.Id);
                    table.UniqueConstraint("AK_artifact_break_glass_requests_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_artifact_break_glass_consumed", "(\"Status\" = 'Consumed') = (\"ConsumedAt\" IS NOT NULL AND \"DownloadSessionId\" IS NOT NULL)");
                    table.CheckConstraint("ck_artifact_break_glass_decision", "(\"Status\" IN ('Approved','Denied','Consumed')) = (\"DecidedBy\" IS NOT NULL AND \"DecidedAt\" IS NOT NULL) OR \"Status\" = 'Expired'");
                    table.CheckConstraint("ck_artifact_break_glass_dual_control", "\"DecidedBy\" IS NULL OR \"DecidedBy\" <> \"RequestedBy\"");
                    table.CheckConstraint("ck_artifact_break_glass_reason", "\"Reason\" ~ '[^[:space:]]' AND length(\"Reason\") <= 500");
                    table.CheckConstraint("ck_artifact_break_glass_requests_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_artifact_break_glass_requests_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_artifact_break_glass_status", "\"Status\" IN ('Pending','Approved','Denied','Consumed','Expired')");
                    table.CheckConstraint("ck_artifact_break_glass_window", "\"ExpiresAt\" > \"CreatedAt\" AND \"ExpiresAt\" <= \"CreatedAt\" + interval '4 hours'");
                    table.ForeignKey(
                        name: "FK_artifact_break_glass_requests_backup_artifacts_ArtifactId_T~",
                        columns: x => new { x.ArtifactId, x.TenantId },
                        principalTable: "backup_artifacts",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_artifact_break_glass_requests_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_artifact_break_glass_requests_users_DecidedBy",
                        column: x => x.DecidedBy,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_artifact_break_glass_requests_users_RequestedBy",
                        column: x => x.RequestedBy,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "backup_replicas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    StorageProvider = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StorageTarget = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ExecutionLocation = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OwningAgentId = table.Column<Guid>(type: "uuid", nullable: true),
                    StorageConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Checksum = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ChecksumAlgorithm = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    VerificationState = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    VerificationMethod = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeleteRequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeleteReason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    DeleteRequestedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeleteAfter = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeleteAttempts = table.Column<int>(type: "integer", nullable: false),
                    DeleteLeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    DeleteLeaseOwner = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    DeleteLeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastDeleteAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeleteNextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeleteFailureCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeleteOperationId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeleteAgentId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeleteCompletionToken = table.Column<Guid>(type: "uuid", nullable: true),
                    DeleteCompletionAbsenceVerified = table.Column<bool>(type: "boolean", nullable: true),
                    DeleteCompletionFailureCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_replicas", x => x.Id);
                    table.UniqueConstraint("AK_backup_replicas_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_backup_replica_checksum", "\"ChecksumAlgorithm\" = 'sha256' AND \"Checksum\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_backup_replica_completion", "(\"DeleteCompletionToken\" IS NULL AND \"DeleteCompletionAbsenceVerified\" IS NULL AND \"DeleteCompletionFailureCode\" IS NULL) OR ((\"ExecutionLocation\" = 'Agent' OR (\"ExecutionLocation\" = 'Backend' AND \"StorageProvider\" IN ('sftp','google-drive') AND \"DeleteAgentId\" IS NOT NULL)) AND \"DeleteOperationId\" IS NOT NULL AND \"DeleteCompletionToken\" IS NOT NULL AND \"DeleteCompletionAbsenceVerified\" IS NOT NULL AND ((\"Status\" = 'Deleted' AND \"DeleteCompletionAbsenceVerified\" AND \"DeleteCompletionFailureCode\" IS NULL) OR (\"Status\" = 'DeleteFailed' AND NOT \"DeleteCompletionAbsenceVerified\" AND \"DeleteCompletionFailureCode\" = \"DeleteFailureCode\")))");
                    table.CheckConstraint("ck_backup_replica_executor", "(\"DeleteAgentId\" IS NULL OR \"DeleteAgentId\" <> '00000000-0000-0000-0000-000000000000'::uuid) AND ((\"ExecutionLocation\" = 'Backend' AND \"OwningAgentId\" IS NULL AND (\"StorageProvider\" IN ('sftp','google-drive') OR (\"DeleteOperationId\" IS NULL AND \"DeleteCompletionToken\" IS NULL AND \"DeleteAgentId\" IS NULL))) OR (\"ExecutionLocation\" = 'Agent' AND \"StorageProvider\" = 'local' AND \"OwningAgentId\" IS NOT NULL AND \"OwningAgentId\" <> '00000000-0000-0000-0000-000000000000'::uuid))");
                    table.CheckConstraint("ck_backup_replica_lifecycle", "(\"DeleteReason\" IS NULL OR \"DeleteReason\" IN ('RetentionExpired','Manual')) AND (\"DeleteRequestedByUserId\" IS NULL OR \"DeleteReason\" = 'Manual') AND ((\"Status\" IN ('Available','Quarantined') AND \"DeleteRequestedAt\" IS NULL AND \"DeleteReason\" IS NULL AND \"DeleteAfter\" IS NULL AND \"DeleteLeaseToken\" IS NULL AND \"DeleteLeaseOwner\" IS NULL AND \"DeleteLeaseExpiresAt\" IS NULL AND \"DeleteNextAttemptAt\" IS NULL AND \"DeleteFailureCode\" IS NULL AND \"DeletedAt\" IS NULL AND \"DeleteAttempts\" = 0) OR (\"Status\" = 'DeletePending' AND \"DeleteRequestedAt\" IS NOT NULL AND \"DeleteReason\" IS NOT NULL AND \"DeleteAfter\" IS NOT NULL AND \"DeleteAfter\" >= \"DeleteRequestedAt\" AND \"DeleteLeaseToken\" IS NULL AND \"DeleteLeaseOwner\" IS NULL AND \"DeleteLeaseExpiresAt\" IS NULL AND \"DeleteNextAttemptAt\" IS NULL AND \"DeleteFailureCode\" IS NULL AND \"DeletedAt\" IS NULL AND \"DeleteAttempts\" = 0) OR (\"Status\" = 'Deleting' AND \"DeleteRequestedAt\" IS NOT NULL AND \"DeleteReason\" IS NOT NULL AND \"DeleteAfter\" IS NOT NULL AND \"DeletedAt\" IS NULL AND ((\"DeleteLeaseToken\" IS NOT NULL AND \"DeleteLeaseOwner\" IS NOT NULL AND \"DeleteLeaseExpiresAt\" IS NOT NULL AND \"DeleteNextAttemptAt\" IS NULL AND \"DeleteAttempts\" >= 1 AND (\"ExecutionLocation\" = 'Backend' OR (\"DeleteOperationId\" IS NOT NULL AND \"DeleteFailureCode\" IS NULL))) OR ((\"ExecutionLocation\" = 'Agent' OR (\"ExecutionLocation\" = 'Backend' AND \"StorageProvider\" IN ('sftp','google-drive'))) AND \"DeleteOperationId\" IS NOT NULL AND \"DeleteLeaseToken\" IS NULL AND \"DeleteLeaseOwner\" IS NULL AND \"DeleteLeaseExpiresAt\" IS NULL AND \"DeleteNextAttemptAt\" IS NOT NULL AND \"DeleteFailureCode\" = 'agent_unavailable' AND \"DeleteAttempts\" >= 0))) OR (\"Status\" = 'DeleteFailed' AND \"DeleteRequestedAt\" IS NOT NULL AND \"DeleteReason\" IS NOT NULL AND \"DeleteAfter\" IS NOT NULL AND \"DeleteLeaseToken\" IS NULL AND \"DeleteLeaseOwner\" IS NULL AND \"DeleteLeaseExpiresAt\" IS NULL AND (\"ExecutionLocation\" = 'Agent' OR (\"ExecutionLocation\" = 'Backend' AND \"StorageProvider\" IN ('sftp','google-drive')) OR \"DeleteNextAttemptAt\" IS NOT NULL) AND \"DeleteFailureCode\" IS NOT NULL AND \"DeletedAt\" IS NULL AND \"DeleteAttempts\" >= 1) OR (\"Status\" = 'Deleted' AND \"DeleteRequestedAt\" IS NOT NULL AND \"DeleteReason\" IS NOT NULL AND \"DeletedAt\" IS NOT NULL AND \"DeleteLeaseToken\" IS NULL AND \"DeleteLeaseOwner\" IS NULL AND \"DeleteLeaseExpiresAt\" IS NULL AND \"DeleteNextAttemptAt\" IS NULL AND \"DeleteFailureCode\" IS NULL AND \"DeleteAttempts\" >= 1))");
                    table.CheckConstraint("ck_backup_replica_size", "\"SizeBytes\" > 0");
                    table.CheckConstraint("ck_backup_replica_status", "\"Status\" IN ('Available','DeletePending','Deleting','DeleteFailed','Deleted','Quarantined')");
                    table.CheckConstraint("ck_backup_replica_verification", "\"VerificationState\" IN ('Verified','Unverified','Failed') AND ((\"VerificationState\" = 'Verified') = (\"VerifiedAt\" IS NOT NULL)) AND (\"VerifiedAt\" IS NULL OR \"VerifiedAt\" >= \"CreatedAt\")");
                    table.CheckConstraint("ck_backup_replicas_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_backup_replicas_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_backup_replicas_backup_artifacts_ArtifactId_TenantId_SizeBy~",
                        columns: x => new { x.ArtifactId, x.TenantId, x.SizeBytes, x.Checksum, x.ChecksumAlgorithm },
                        principalTable: "backup_artifacts",
                        principalColumns: new[] { "Id", "TenantId", "SizeBytes", "ArtifactChecksum", "ChecksumAlgorithm" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_backup_replicas_storage_connections_StorageConnectionId_Ten~",
                        columns: x => new { x.StorageConnectionId, x.TenantId, x.StorageProvider },
                        principalTable: "storage_connections",
                        principalColumns: new[] { "Id", "TenantId", "ProviderType" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_backup_replicas_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "download_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReplicaId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupportSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Decrypted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    BreakGlassRequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: true),
                    StorageProvider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StorageTarget = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Key = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Checksum = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Filename = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LeaseOwner = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TransferredBytes = table.Column<long>(type: "bigint", nullable: false),
                    FailureCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_download_sessions", x => x.Id);
                    table.UniqueConstraint("AK_download_sessions_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_download_sessions_bytes", "\"SizeBytes\" >= 0 AND \"TransferredBytes\" BETWEEN 0 AND \"SizeBytes\"");
                    table.CheckConstraint("ck_download_sessions_channel", "(\"BreakGlassRequestId\" IS NULL OR \"Decrypted\") AND (NOT \"Decrypted\" OR \"SupportSessionId\" IS NULL)");
                    table.CheckConstraint("ck_download_sessions_expiry", "\"ExpiresAt\" > \"CreatedAt\"");
                    table.CheckConstraint("ck_download_sessions_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_download_sessions_lease", "(\"LeaseOwner\" IS NULL) = (\"LeaseExpiresAt\" IS NULL)");
                    table.CheckConstraint("ck_download_sessions_release", "(\"State\" IN ('Completed','Failed','Cancelled')) = (\"ReleasedAt\" IS NOT NULL)");
                    table.CheckConstraint("ck_download_sessions_state", "\"State\" IN ('Preparing','Ready','Streaming','Completed','Failed','Cancelled')");
                    table.CheckConstraint("ck_download_sessions_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_download_sessions_artifact_break_glass_requests_BreakGlassR~",
                        columns: x => new { x.BreakGlassRequestId, x.TenantId },
                        principalTable: "artifact_break_glass_requests",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_download_sessions_backup_artifacts_ArtifactId_TenantId",
                        columns: x => new { x.ArtifactId, x.TenantId },
                        principalTable: "backup_artifacts",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_download_sessions_backup_replicas_ReplicaId_TenantId",
                        columns: x => new { x.ReplicaId, x.TenantId },
                        principalTable: "backup_replicas",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_download_sessions_support_access_sessions_SupportSessionId_~",
                        columns: x => new { x.SupportSessionId, x.TenantId },
                        principalTable: "support_access_sessions",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_download_sessions_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "replica_repair_operations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    StorageConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    StorageProvider = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SourceReplicaId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetReplicaId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LeaseOwner = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    TransferredBytes = table.Column<long>(type: "bigint", nullable: false),
                    TotalBytes = table.Column<long>(type: "bigint", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    NextRetryAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastFailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancellationRequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReservedQuotaBytes = table.Column<long>(type: "bigint", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_replica_repair_operations", x => x.Id);
                    table.UniqueConstraint("AK_replica_repair_operations_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_replica_repair_operations_bytes", "\"TransferredBytes\" >= 0 AND \"TotalBytes\" >= 0 AND \"TransferredBytes\" <= \"TotalBytes\"");
                    table.CheckConstraint("ck_replica_repair_operations_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_replica_repair_operations_reserved", "\"ReservedQuotaBytes\" >= 0");
                    table.CheckConstraint("ck_replica_repair_operations_status", "\"Status\" IN ('Pending','InProgress','Succeeded','Failed','Cancelled')");
                    table.CheckConstraint("ck_replica_repair_operations_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_replica_repair_operations_terminal", "(\"Status\" IN ('Succeeded','Failed','Cancelled')) = (\"CompletedAt\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_replica_repair_operations_backup_artifacts_ArtifactId_Tenan~",
                        columns: x => new { x.ArtifactId, x.TenantId },
                        principalTable: "backup_artifacts",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_replica_repair_operations_backup_replicas_SourceReplicaId_T~",
                        columns: x => new { x.SourceReplicaId, x.TenantId },
                        principalTable: "backup_replicas",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_replica_repair_operations_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "restore_operations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Purpose = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "Execution"),
                    PrecheckId = table.Column<Guid>(type: "uuid", nullable: true),
                    SupportSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReplicaId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceAccess = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    DatabaseName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Mode = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    OverwritePolicy = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestFingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CurrentAttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancellationRequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    TargetState = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    ResultJson = table.Column<string>(type: "jsonb", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    CleanupAttempts = table.Column<int>(type: "integer", nullable: false),
                    CleanupDatabaseName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CleanupKind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    CleanupLastFailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CleanupLeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CleanupLeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    CleanupNextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CleanupRequestedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CleanupState = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CleanupUpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    OptionTerminateActiveConnections = table.Column<bool>(type: "boolean", nullable: false),
                    OptionVerifyBackup = table.Column<bool>(type: "boolean", nullable: false),
                    OptionVerifyChecksum = table.Column<bool>(type: "boolean", nullable: false),
                    Options_Files_Paths = table.Column<string[]>(type: "text[]", nullable: true),
                    Options_Files_RelativeDirectory = table.Column<string>(type: "text", nullable: true),
                    TargetCredentialReference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TargetEncrypt = table.Column<bool>(type: "boolean", nullable: false),
                    TargetHost = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    TargetIntegratedSecurity = table.Column<bool>(type: "boolean", nullable: false),
                    TargetPort = table.Column<int>(type: "integer", nullable: true),
                    TargetTrustServerCertificate = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_restore_operations", x => x.Id);
                    table.UniqueConstraint("AK_restore_operations_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_restore_operations_attempt", "\"CurrentAttemptNumber\" >= 1");
                    table.CheckConstraint("ck_restore_operations_cleanup", "\"CleanupKind\" IN ('None','VerificationDatabase','RollbackCopy') AND \"CleanupState\" IN ('Idle','Retained','Pending','Completed') AND \"CleanupAttempts\" >= 0 AND (\"CleanupKind\" = 'None') = (\"CleanupDatabaseName\" IS NULL) AND (\"CleanupKind\" <> 'None' OR \"CleanupState\" = 'Idle') AND (\"Purpose\" = 'Verification') = (\"CleanupKind\" = 'VerificationDatabase') AND (\"CleanupKind\" <> 'VerificationDatabase' OR \"CleanupDatabaseName\" = \"DatabaseName\") AND (\"CleanupState\" IN ('Pending','Completed') OR \"CleanupLeaseToken\" IS NULL) AND (\"CleanupState\" NOT IN ('Pending','Completed','Retained') OR \"CompletedAt\" IS NOT NULL)");
                    table.CheckConstraint("ck_restore_operations_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_restore_operations_mode", "(\"Mode\" = 'NewDatabase' AND \"OverwritePolicy\" = 'Forbidden') OR (\"Mode\" = 'ReplaceExistingDatabase' AND \"OverwritePolicy\" = 'ReplaceExisting')");
                    table.CheckConstraint("ck_restore_operations_outcome", "(\"Status\" <> 'Succeeded' OR (((\"Purpose\" = 'Execution' AND \"TargetState\" = 'Online') OR (\"Purpose\" <> 'Execution' AND \"TargetState\" = 'NotModified')) AND \"FailureCode\" IS NULL)) AND (\"Status\" NOT IN ('Failed','Cancelled','AgentLost') OR (\"TargetState\" <> 'Online' AND \"FailureCode\" IS NOT NULL))");
                    table.CheckConstraint("ck_restore_operations_purpose", "\"Purpose\" IN ('Execution','Precheck','Discovery','Verification')");
                    table.CheckConstraint("ck_restore_operations_result", "\"ResultJson\" IS NULL OR \"CompletedAt\" IS NOT NULL");
                    table.CheckConstraint("ck_restore_operations_source", "\"SourceAccess\" IN ('AgentLocal','CentralStorage','BackendRelayed','AgentDirect')");
                    table.CheckConstraint("ck_restore_operations_status", "\"Status\" IN ('Pending','Leased','Running','Succeeded','Failed','Cancelled','AgentLost')");
                    table.CheckConstraint("ck_restore_operations_support", "\"SupportSessionId\" IS NULL OR (\"Mode\" = 'NewDatabase' AND \"RequestedByUserId\" IS NOT NULL)");
                    table.CheckConstraint("ck_restore_operations_target", "(\"TargetPort\" IS NULL OR \"TargetPort\" BETWEEN 1 AND 65535) AND ((\"TargetIntegratedSecurity\" AND \"TargetCredentialReference\" IS NULL) OR (NOT \"TargetIntegratedSecurity\" AND \"TargetCredentialReference\" IS NOT NULL))");
                    table.CheckConstraint("ck_restore_operations_target_state", "\"TargetState\" IS NULL OR \"TargetState\" IN ('NotModified','Online','PartiallyRestored','Unknown')");
                    table.CheckConstraint("ck_restore_operations_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_restore_operations_terminal", "(\"Status\" IN ('Succeeded','Failed','Cancelled','AgentLost')) = (\"CompletedAt\" IS NOT NULL) AND (\"CompletedAt\" IS NOT NULL) = (\"TargetState\" IS NOT NULL)");
                    table.CheckConstraint("ck_restore_operations_text", "((\"Purpose\" = 'Discovery' AND \"DatabaseName\" = '') OR (\"Purpose\" <> 'Discovery' AND length(\"DatabaseName\") BETWEEN 1 AND 128)) AND length(\"IdempotencyKey\") BETWEEN 1 AND 128 AND length(\"RequestFingerprint\") BETWEEN 1 AND 128");
                    table.ForeignKey(
                        name: "FK_restore_operations_agents_AgentId_TenantId",
                        columns: x => new { x.AgentId, x.TenantId },
                        principalTable: "agents",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_restore_operations_backup_artifacts_ArtifactId_TenantId",
                        columns: x => new { x.ArtifactId, x.TenantId },
                        principalTable: "backup_artifacts",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_restore_operations_backup_replicas_ReplicaId_TenantId",
                        columns: x => new { x.ReplicaId, x.TenantId },
                        principalTable: "backup_replicas",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_restore_operations_support_access_sessions_SupportSessionId~",
                        columns: x => new { x.SupportSessionId, x.TenantId },
                        principalTable: "support_access_sessions",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_restore_operations_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "restore_attempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LeasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SoftwareVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ProtocolVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Platform = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ProgressStage = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ProgressPercent = table.Column<double>(type: "double precision", nullable: true),
                    ProgressAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    TargetState = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_restore_attempts", x => x.Id);
                    table.UniqueConstraint("AK_restore_attempts_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_restore_attempts_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_restore_attempts_lease", "\"Status\" NOT IN ('Leased','Running') OR (\"LeaseToken\" IS NOT NULL AND \"LeaseToken\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"LeaseExpiresAt\" IS NOT NULL)");
                    table.CheckConstraint("ck_restore_attempts_number", "\"Number\" >= 1");
                    table.CheckConstraint("ck_restore_attempts_progress", "\"ProgressPercent\" IS NULL OR (\"ProgressPercent\" >= 0 AND \"ProgressPercent\" <= 100)");
                    table.CheckConstraint("ck_restore_attempts_started", "\"Status\" <> 'Running' OR \"StartedAt\" IS NOT NULL");
                    table.CheckConstraint("ck_restore_attempts_status", "\"Status\" IN ('Pending','Leased','Running','Succeeded','Failed','Cancelled','AgentLost')");
                    table.CheckConstraint("ck_restore_attempts_target_state", "\"TargetState\" IS NULL OR \"TargetState\" IN ('NotModified','Online','PartiallyRestored','Unknown')");
                    table.CheckConstraint("ck_restore_attempts_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_restore_attempts_terminal", "(\"Status\" IN ('Succeeded','Failed','Cancelled','AgentLost')) = (\"CompletedAt\" IS NOT NULL) AND (\"CompletedAt\" IS NOT NULL) = (\"TargetState\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_restore_attempts_agents_AgentId_TenantId",
                        columns: x => new { x.AgentId, x.TenantId },
                        principalTable: "agents",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_restore_attempts_restore_operations_OperationId_TenantId",
                        columns: x => new { x.OperationId, x.TenantId },
                        principalTable: "restore_operations",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_restore_attempts_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "restore_replica_pins",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReplicaId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_restore_replica_pins", x => x.Id);
                    table.UniqueConstraint("AK_restore_replica_pins_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.CheckConstraint("ck_restore_replica_pins_id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_restore_replica_pins_tenant", "\"TenantId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_restore_replica_pins_backup_replicas_ReplicaId_TenantId",
                        columns: x => new { x.ReplicaId, x.TenantId },
                        principalTable: "backup_replicas",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_restore_replica_pins_restore_operations_OperationId_TenantId",
                        columns: x => new { x.OperationId, x.TenantId },
                        principalTable: "restore_operations",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_restore_replica_pins_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_capabilities_TenantId_AgentId",
                table: "agent_capabilities",
                columns: new[] { "TenantId", "AgentId" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_connectivity_AgentId_TenantId",
                table: "agent_connectivity",
                columns: new[] { "AgentId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_connectivity_TenantId_LastSeenAt",
                table: "agent_connectivity",
                columns: new[] { "TenantId", "LastSeenAt" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_credentials_AgentId_TenantId",
                table: "agent_credentials",
                columns: new[] { "AgentId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "ux_agent_credentials_one_active",
                table: "agent_credentials",
                column: "AgentId",
                unique: true,
                filter: "\"Status\" = 2");

            migrationBuilder.CreateIndex(
                name: "ux_agent_credentials_one_pending",
                table: "agent_credentials",
                column: "AgentId",
                unique: true,
                filter: "\"Status\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_agent_enrollment_tokens_ConsumedByAgentId_TenantId",
                table: "agent_enrollment_tokens",
                columns: new[] { "ConsumedByAgentId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_enrollment_tokens_CreatedBy",
                table: "agent_enrollment_tokens",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_agent_enrollment_tokens_RevokedBy",
                table: "agent_enrollment_tokens",
                column: "RevokedBy");

            migrationBuilder.CreateIndex(
                name: "IX_agent_enrollment_tokens_TenantId",
                table: "agent_enrollment_tokens",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_agents_TenantId",
                table: "agents",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ux_agents_tenant_installation_live",
                table: "agents",
                columns: new[] { "TenantId", "InstallationId" },
                unique: true,
                filter: "\"InstallationId\" IS NOT NULL AND \"Status\" <> 4");

            migrationBuilder.CreateIndex(
                name: "ix_artifact_break_glass_expiry",
                table: "artifact_break_glass_requests",
                column: "ExpiresAt",
                filter: "\"Status\" IN ('Pending','Approved')");

            migrationBuilder.CreateIndex(
                name: "IX_artifact_break_glass_requests_ArtifactId_TenantId",
                table: "artifact_break_glass_requests",
                columns: new[] { "ArtifactId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_artifact_break_glass_requests_DecidedBy",
                table: "artifact_break_glass_requests",
                column: "DecidedBy");

            migrationBuilder.CreateIndex(
                name: "IX_artifact_break_glass_requests_RequestedBy",
                table: "artifact_break_glass_requests",
                column: "RequestedBy");

            migrationBuilder.CreateIndex(
                name: "IX_artifact_break_glass_requests_TenantId",
                table: "artifact_break_glass_requests",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ix_artifact_break_glass_tenant",
                table: "artifact_break_glass_requests",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_At_Id",
                table: "audit_events",
                columns: new[] { "At", "Id" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_support_session",
                table: "audit_events",
                columns: new[] { "SupportSessionId", "At" },
                filter: "\"SupportSessionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_TenantId_At",
                table: "audit_events",
                columns: new[] { "TenantId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_artifacts_AttemptId_TenantId",
                table: "backup_artifacts",
                columns: new[] { "AttemptId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "ix_backup_artifacts_retention_due",
                table: "backup_artifacts",
                column: "RetentionExpiresAt",
                filter: "\"RetentionExpiresAt\" IS NOT NULL AND \"RetentionPinnedAt\" IS NULL AND \"RetentionAppliedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_backup_artifacts_TenantId",
                table: "backup_artifacts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_backup_artifacts_TenantId_AttemptId",
                table: "backup_artifacts",
                columns: new[] { "TenantId", "AttemptId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_backup_destination_outcomes_AttemptId_TenantId",
                table: "backup_destination_outcomes",
                columns: new[] { "AttemptId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_destination_outcomes_TenantId",
                table: "backup_destination_outcomes",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_backup_destination_outcomes_TenantId_AttemptId_StorageProvi~",
                table: "backup_destination_outcomes",
                columns: new[] { "TenantId", "AttemptId", "StorageProvider", "StorageConnectionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_backup_destination_outcomes_TenantId_RunId",
                table: "backup_destination_outcomes",
                columns: new[] { "TenantId", "RunId" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_jobs_AgentId_TenantId",
                table: "backup_jobs",
                columns: new[] { "AgentId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "ix_backup_jobs_scheduled",
                table: "backup_jobs",
                columns: new[] { "TenantId", "Id" },
                filter: "\"Enabled\" AND \"ScheduleEnabled\"");

            migrationBuilder.CreateIndex(
                name: "IX_backup_jobs_TenantId",
                table: "backup_jobs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_backup_jobs_TenantId_AgentId",
                table: "backup_jobs",
                columns: new[] { "TenantId", "AgentId" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_lifecycle_facts_AttemptId_TenantId",
                table: "backup_lifecycle_facts",
                columns: new[] { "AttemptId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_lifecycle_facts_RunId_TenantId",
                table: "backup_lifecycle_facts",
                columns: new[] { "RunId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_lifecycle_facts_TenantId",
                table: "backup_lifecycle_facts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_backup_lifecycle_facts_TenantId_RunId_At",
                table: "backup_lifecycle_facts",
                columns: new[] { "TenantId", "RunId", "At" });

            migrationBuilder.CreateIndex(
                name: "ix_backup_replicas_agent_deletions",
                table: "backup_replicas",
                columns: new[] { "TenantId", "OwningAgentId", "DeleteAfter" },
                filter: "\"ExecutionLocation\" = 'Agent' AND \"Status\" = 'Deleting'");

            migrationBuilder.CreateIndex(
                name: "IX_backup_replicas_ArtifactId_TenantId_SizeBytes_Checksum_Chec~",
                table: "backup_replicas",
                columns: new[] { "ArtifactId", "TenantId", "SizeBytes", "Checksum", "ChecksumAlgorithm" });

            migrationBuilder.CreateIndex(
                name: "ix_backup_replicas_connection",
                table: "backup_replicas",
                columns: new[] { "TenantId", "StorageConnectionId" });

            migrationBuilder.CreateIndex(
                name: "ix_backup_replicas_delete_failed",
                table: "backup_replicas",
                column: "DeleteNextAttemptAt",
                filter: "\"Status\" = 'DeleteFailed'");

            migrationBuilder.CreateIndex(
                name: "ix_backup_replicas_delete_leased",
                table: "backup_replicas",
                column: "DeleteLeaseExpiresAt",
                filter: "\"Status\" = 'Deleting'");

            migrationBuilder.CreateIndex(
                name: "ix_backup_replicas_delete_pending",
                table: "backup_replicas",
                column: "DeleteAfter",
                filter: "\"Status\" = 'DeletePending'");

            migrationBuilder.CreateIndex(
                name: "ix_backup_replicas_external_agent_deletions",
                table: "backup_replicas",
                columns: new[] { "TenantId", "DeleteAfter" },
                filter: "\"ExecutionLocation\" = 'Backend' AND \"StorageProvider\" IN ('sftp','google-drive') AND \"Status\" = 'Deleting'");

            migrationBuilder.CreateIndex(
                name: "ix_backup_replicas_location",
                table: "backup_replicas",
                columns: new[] { "StorageProvider", "Key" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_replicas_StorageConnectionId_TenantId_StorageProvider",
                table: "backup_replicas",
                columns: new[] { "StorageConnectionId", "TenantId", "StorageProvider" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_replicas_TenantId",
                table: "backup_replicas",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_backup_replicas_TenantId_ArtifactId_StorageProvider_Storage~",
                table: "backup_replicas",
                columns: new[] { "TenantId", "ArtifactId", "StorageProvider", "StorageTarget", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_backup_replicas_live_verified_destination",
                table: "backup_replicas",
                columns: new[] { "TenantId", "ArtifactId", "StorageConnectionId" },
                unique: true,
                filter: "\"VerificationState\" = 'Verified' AND \"Status\" <> 'Deleted'");

            migrationBuilder.CreateIndex(
                name: "IX_backup_run_attempts_AgentId_TenantId",
                table: "backup_run_attempts",
                columns: new[] { "AgentId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_run_attempts_LeaseToken",
                table: "backup_run_attempts",
                column: "LeaseToken",
                unique: true,
                filter: "\"LeaseToken\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_backup_run_attempts_RunId_TenantId",
                table: "backup_run_attempts",
                columns: new[] { "RunId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_run_attempts_Status_LeaseExpiresAt",
                table: "backup_run_attempts",
                columns: new[] { "Status", "LeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_run_attempts_TenantId",
                table: "backup_run_attempts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_backup_run_attempts_TenantId_AgentId_EligibleAt",
                table: "backup_run_attempts",
                columns: new[] { "TenantId", "AgentId", "EligibleAt" },
                filter: "\"Status\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_backup_run_attempts_TenantId_AgentId_Status",
                table: "backup_run_attempts",
                columns: new[] { "TenantId", "AgentId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_run_attempts_TenantId_RunId_Number",
                table: "backup_run_attempts",
                columns: new[] { "TenantId", "RunId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_backup_runs_AgentId_TenantId",
                table: "backup_runs",
                columns: new[] { "AgentId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_runs_ClaimDeadline_Id",
                table: "backup_runs",
                columns: new[] { "ClaimDeadline", "Id" },
                filter: "\"Status\" = 1 AND \"ClaimDeadline\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_backup_runs_JobId_TenantId",
                table: "backup_runs",
                columns: new[] { "JobId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_runs_NextAttemptAt_Id",
                table: "backup_runs",
                columns: new[] { "NextAttemptAt", "Id" },
                filter: "\"Status\" = 8");

            migrationBuilder.CreateIndex(
                name: "IX_backup_runs_TenantId",
                table: "backup_runs",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_backup_runs_TenantId_AgentId_Status_CreatedAt",
                table: "backup_runs",
                columns: new[] { "TenantId", "AgentId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_runs_TenantId_CreatedAt",
                table: "backup_runs",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_runs_TenantId_JobId",
                table: "backup_runs",
                columns: new[] { "TenantId", "JobId" },
                unique: true,
                filter: "\"OverlapPolicy\" = 'Skip' AND \"Status\" IN (1,2,3,8,9)");

            migrationBuilder.CreateIndex(
                name: "IX_backup_runs_TenantId_JobId_CreatedAt",
                table: "backup_runs",
                columns: new[] { "TenantId", "JobId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_runs_TenantId_JobId_IdempotencyKey",
                table: "backup_runs",
                columns: new[] { "TenantId", "JobId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_backup_runs_TenantId_JobId_ScheduledOccurrence",
                table: "backup_runs",
                columns: new[] { "TenantId", "JobId", "ScheduledOccurrence" },
                unique: true,
                filter: "\"ScheduledOccurrence\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_backup_runs_TenantId_SizeAnomalyStatus_CompletedAt",
                table: "backup_runs",
                columns: new[] { "TenantId", "SizeAnomalyStatus", "CompletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_runs_TenantId_Status_CreatedAt",
                table: "backup_runs",
                columns: new[] { "TenantId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "ix_backup_occurrences_decided",
                table: "backup_schedule_occurrences",
                column: "DecidedAt");

            migrationBuilder.CreateIndex(
                name: "IX_backup_schedule_occurrences_JobId_TenantId",
                table: "backup_schedule_occurrences",
                columns: new[] { "JobId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_schedule_occurrences_RunId_TenantId",
                table: "backup_schedule_occurrences",
                columns: new[] { "RunId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_schedule_occurrences_TenantId",
                table: "backup_schedule_occurrences",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_backup_schedule_occurrences_TenantId_JobId_ScheduledAt",
                table: "backup_schedule_occurrences",
                columns: new[] { "TenantId", "JobId", "ScheduledAt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_backup_schedule_occurrences_TenantId_RunId",
                table: "backup_schedule_occurrences",
                columns: new[] { "TenantId", "RunId" },
                unique: true,
                filter: "\"Outcome\" = 'RunCreated'");

            migrationBuilder.CreateIndex(
                name: "IX_backup_storage_cleanups_StorageProvider_StorageName_Status_~",
                table: "backup_storage_cleanups",
                columns: new[] { "StorageProvider", "StorageName", "Status", "LeaseExpiresAt" },
                filter: "\"Status\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_backup_storage_cleanups_StorageProvider_StorageName_TargetI~",
                table: "backup_storage_cleanups",
                columns: new[] { "StorageProvider", "StorageName", "TargetIdentity" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_backup_storage_cleanups_TenantId",
                table: "backup_storage_cleanups",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_backup_transfer_leases_ExpiresAt",
                table: "backup_transfer_leases",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_bandwidth_profile_assignments_ProfileId",
                table: "bandwidth_profile_assignments",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_bandwidth_profiles_TenantId_Name",
                table: "bandwidth_profiles",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_discovery_agent_states_AgentId_TenantId",
                table: "discovery_agent_states",
                columns: new[] { "AgentId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_discovery_agent_states_TenantId",
                table: "discovery_agent_states",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_discovery_databases_InstanceId_TenantId",
                table: "discovery_databases",
                columns: new[] { "InstanceId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_discovery_databases_TenantId",
                table: "discovery_databases",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ux_discovery_databases_instance_name",
                table: "discovery_databases",
                columns: new[] { "InstanceId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_discovery_instances_AgentId_TenantId",
                table: "discovery_instances",
                columns: new[] { "AgentId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_discovery_instances_TenantId",
                table: "discovery_instances",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ux_discovery_instances_agent_key",
                table: "discovery_instances",
                columns: new[] { "AgentId", "InstanceKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_download_sessions_active",
                table: "download_sessions",
                column: "ReplicaId",
                filter: "\"ReleasedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_download_sessions_ArtifactId_TenantId",
                table: "download_sessions",
                columns: new[] { "ArtifactId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "ix_download_sessions_break_glass",
                table: "download_sessions",
                column: "BreakGlassRequestId",
                filter: "\"BreakGlassRequestId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_download_sessions_BreakGlassRequestId_TenantId",
                table: "download_sessions",
                columns: new[] { "BreakGlassRequestId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "ix_download_sessions_expiry",
                table: "download_sessions",
                column: "ExpiresAt",
                filter: "\"ReleasedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_download_sessions_ReplicaId_TenantId",
                table: "download_sessions",
                columns: new[] { "ReplicaId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "ix_download_sessions_support",
                table: "download_sessions",
                column: "SupportSessionId",
                filter: "\"SupportSessionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_download_sessions_SupportSessionId_TenantId",
                table: "download_sessions",
                columns: new[] { "SupportSessionId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_download_sessions_TenantId",
                table: "download_sessions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_download_sessions_TenantId_AgentId_State",
                table: "download_sessions",
                columns: new[] { "TenantId", "AgentId", "State" });

            migrationBuilder.CreateIndex(
                name: "ix_google_drive_file_metadata_file_id",
                table: "google_drive_file_metadata",
                columns: new[] { "ConnectionId", "FileId" });

            migrationBuilder.CreateIndex(
                name: "ix_google_drive_file_metadata_key",
                table: "google_drive_file_metadata",
                columns: new[] { "TenantId", "ConnectionId", "StorageKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_google_drive_oauth_states_connection",
                table: "google_drive_oauth_states",
                columns: new[] { "TenantId", "ConnectionId" });

            migrationBuilder.CreateIndex(
                name: "ix_google_drive_staging_cleanup",
                table: "google_drive_staging_reservations",
                columns: new[] { "VolumeId", "NodeId", "LeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_login_throttle_entries_WindowStart",
                table: "login_throttle_entries",
                column: "WindowStart");

            migrationBuilder.CreateIndex(
                name: "IX_notification_channels_SecurityOwnerUserId",
                table: "notification_channels",
                column: "SecurityOwnerUserId");

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

            migrationBuilder.CreateIndex(
                name: "IX_password_credentials_NormalizedLoginIdentifier",
                table: "password_credentials",
                column: "NormalizedLoginIdentifier",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_plans_QuotaPolicyReference",
                table: "plans",
                column: "QuotaPolicyReference");

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
                name: "ix_replica_repair_active_source",
                table: "replica_repair_operations",
                column: "SourceReplicaId",
                filter: "\"Status\" IN ('Pending', 'InProgress') AND \"SourceReplicaId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_replica_repair_due",
                table: "replica_repair_operations",
                columns: new[] { "NextRetryAt", "Id" },
                filter: "\"Status\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_replica_repair_operations_ArtifactId_TenantId",
                table: "replica_repair_operations",
                columns: new[] { "ArtifactId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_replica_repair_operations_SourceReplicaId_TenantId",
                table: "replica_repair_operations",
                columns: new[] { "SourceReplicaId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_replica_repair_operations_TenantId",
                table: "replica_repair_operations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ux_replica_repair_active",
                table: "replica_repair_operations",
                columns: new[] { "TenantId", "ArtifactId", "StorageConnectionId" },
                unique: true,
                filter: "\"Status\" IN ('Pending', 'InProgress')");

            migrationBuilder.CreateIndex(
                name: "IX_restore_attempts_AgentId_TenantId",
                table: "restore_attempts",
                columns: new[] { "AgentId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "ix_restore_attempts_leased",
                table: "restore_attempts",
                column: "LeaseExpiresAt",
                filter: "\"Status\" IN ('Leased','Running')");

            migrationBuilder.CreateIndex(
                name: "IX_restore_attempts_LeaseToken",
                table: "restore_attempts",
                column: "LeaseToken",
                unique: true,
                filter: "\"LeaseToken\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_restore_attempts_OperationId_TenantId",
                table: "restore_attempts",
                columns: new[] { "OperationId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_restore_attempts_TenantId",
                table: "restore_attempts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_restore_attempts_TenantId_AgentId_Status",
                table: "restore_attempts",
                columns: new[] { "TenantId", "AgentId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_restore_attempts_TenantId_OperationId_Number",
                table: "restore_attempts",
                columns: new[] { "TenantId", "OperationId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_restore_operations_AgentId_TenantId",
                table: "restore_operations",
                columns: new[] { "AgentId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_restore_operations_ArtifactId_TenantId",
                table: "restore_operations",
                columns: new[] { "ArtifactId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "ix_restore_operations_cleanup_pending",
                table: "restore_operations",
                columns: new[] { "TenantId", "AgentId" },
                filter: "\"CleanupState\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_restore_operations_pending",
                table: "restore_operations",
                columns: new[] { "CreatedAt", "Id" },
                filter: "\"Status\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_restore_operations_ReplicaId_TenantId",
                table: "restore_operations",
                columns: new[] { "ReplicaId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "ix_restore_operations_support",
                table: "restore_operations",
                column: "SupportSessionId",
                filter: "\"SupportSessionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_restore_operations_SupportSessionId_TenantId",
                table: "restore_operations",
                columns: new[] { "SupportSessionId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_restore_operations_TenantId",
                table: "restore_operations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_restore_operations_TenantId_AgentId_Status_CreatedAt",
                table: "restore_operations",
                columns: new[] { "TenantId", "AgentId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_restore_operations_TenantId_ArtifactId_CreatedAt",
                table: "restore_operations",
                columns: new[] { "TenantId", "ArtifactId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_restore_operations_TenantId_ArtifactId_Purpose_IdempotencyK~",
                table: "restore_operations",
                columns: new[] { "TenantId", "ArtifactId", "Purpose", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_restore_operations_TenantId_CreatedAt",
                table: "restore_operations",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_restore_operations_TenantId_Status_CreatedAt",
                table: "restore_operations",
                columns: new[] { "TenantId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "ix_restore_replica_pins_active",
                table: "restore_replica_pins",
                column: "ReplicaId",
                filter: "\"ReleasedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_restore_replica_pins_OperationId_TenantId",
                table: "restore_replica_pins",
                columns: new[] { "OperationId", "TenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_restore_replica_pins_ReplicaId_TenantId",
                table: "restore_replica_pins",
                columns: new[] { "ReplicaId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_restore_replica_pins_TenantId",
                table: "restore_replica_pins",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ix_storage_capacity_snapshots_connection",
                table: "storage_capacity_snapshots",
                columns: new[] { "TenantId", "ConnectionId", "ReportedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_storage_capacity_snapshots_ConnectionId",
                table: "storage_capacity_snapshots",
                column: "ConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_storage_capacity_snapshots_TenantId",
                table: "storage_capacity_snapshots",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_storage_capacity_tasks_ConnectionId_TenantId",
                table: "storage_capacity_tasks",
                columns: new[] { "ConnectionId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_storage_capacity_tasks_TenantId",
                table: "storage_capacity_tasks",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_storage_capacity_tasks_TenantId_AgentId_CreatedAt",
                table: "storage_capacity_tasks",
                columns: new[] { "TenantId", "AgentId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "ux_storage_capacity_tasks_pending",
                table: "storage_capacity_tasks",
                columns: new[] { "TenantId", "ConnectionId", "AgentId" },
                unique: true,
                filter: "\"CompletedAt\" IS NULL");

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

            migrationBuilder.CreateIndex(
                name: "ix_support_access_sessions_admin",
                table: "support_access_sessions",
                columns: new[] { "PlatformAdminId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "ix_support_access_sessions_expiry",
                table: "support_access_sessions",
                column: "ExpiresAt",
                filter: "\"Status\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "ix_support_access_sessions_tenant",
                table: "support_access_sessions",
                columns: new[] { "TenantId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_support_access_sessions_TenantId",
                table: "support_access_sessions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ux_support_access_sessions_active",
                table: "support_access_sessions",
                columns: new[] { "TenantId", "PlatformAdminId" },
                unique: true,
                filter: "\"Status\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "ux_support_access_sessions_key",
                table: "support_access_sessions",
                columns: new[] { "TenantId", "PlatformAdminId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenant_agent_enrollment_policies_ChangedBy",
                table: "tenant_agent_enrollment_policies",
                column: "ChangedBy");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_memberships_UserId",
                table: "tenant_memberships",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_recovery_keys_CreatedBy",
                table: "tenant_recovery_keys",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "ux_tenant_recovery_keys_tenant",
                table: "tenant_recovery_keys",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tenant_storage_usage_calculated",
                table: "tenant_storage_usage",
                column: "CalculatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_storage_usage_PolicyId",
                table: "tenant_storage_usage",
                column: "PolicyId");

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

            migrationBuilder.CreateIndex(
                name: "IX_tenant_wrap_keys_KeyId",
                table: "tenant_wrap_keys",
                column: "KeyId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenant_wrap_keys_TenantId_Version",
                table: "tenant_wrap_keys",
                columns: new[] { "TenantId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_tenant_wrap_keys_active",
                table: "tenant_wrap_keys",
                column: "TenantId",
                unique: true,
                filter: "\"State\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_transfer_checkpoints_AttemptId_TenantId",
                table: "transfer_checkpoints",
                columns: new[] { "AttemptId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_transfer_schedule_windows_ProfileId",
                table: "transfer_schedule_windows",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_upload_bandwidth_grants_TenantId_ExpiresAt",
                table: "upload_bandwidth_grants",
                columns: new[] { "TenantId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_user_sessions_UserId",
                table: "user_sessions",
                column: "UserId");

            // Quartz job store (own schema) and the non-model objects: seed rows, guard functions, triggers.
            ExecuteEmbedded(migrationBuilder, "BackupPlatform.Infrastructure.Scheduling.Schema.quartz-4.6.sql");
            ExecuteEmbedded(migrationBuilder, "BackupPlatform.Infrastructure.Persistence.Schema.baseline-objects.sql");
        }

        private static void ExecuteEmbedded(MigrationBuilder migrationBuilder, string resource)
        {
            using var stream = typeof(Baseline).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"The embedded script {resource} is missing.");
            using var reader = new StreamReader(stream);
            migrationBuilder.Sql(reader.ReadToEnd());
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_capabilities");

            migrationBuilder.DropTable(
                name: "agent_connectivity");

            migrationBuilder.DropTable(
                name: "agent_credentials");

            migrationBuilder.DropTable(
                name: "agent_enrollment_tokens");

            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "backup_destination_outcomes");

            migrationBuilder.DropTable(
                name: "backup_lifecycle_facts");

            migrationBuilder.DropTable(
                name: "backup_schedule_occurrences");

            migrationBuilder.DropTable(
                name: "backup_storage_cleanups");

            migrationBuilder.DropTable(
                name: "backup_transfer_leases");

            migrationBuilder.DropTable(
                name: "bandwidth_profile_assignments");

            migrationBuilder.DropTable(
                name: "discovery_agent_states");

            migrationBuilder.DropTable(
                name: "discovery_databases");

            migrationBuilder.DropTable(
                name: "download_sessions");

            migrationBuilder.DropTable(
                name: "google_drive_file_metadata");

            migrationBuilder.DropTable(
                name: "google_drive_oauth_states");

            migrationBuilder.DropTable(
                name: "google_drive_staging_reservations");

            migrationBuilder.DropTable(
                name: "login_throttle_entries");

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
                name: "password_credentials");

            migrationBuilder.DropTable(
                name: "platform_admins");

            migrationBuilder.DropTable(
                name: "platform_agent_settings");

            migrationBuilder.DropTable(
                name: "platform_secrets");

            migrationBuilder.DropTable(
                name: "quota_reservations");

            migrationBuilder.DropTable(
                name: "replica_repair_operations");

            migrationBuilder.DropTable(
                name: "restore_attempts");

            migrationBuilder.DropTable(
                name: "restore_replica_pins");

            migrationBuilder.DropTable(
                name: "storage_capacity_snapshots");

            migrationBuilder.DropTable(
                name: "storage_capacity_tasks");

            migrationBuilder.DropTable(
                name: "tenant_agent_enrollment_policies");

            migrationBuilder.DropTable(
                name: "tenant_memberships");

            migrationBuilder.DropTable(
                name: "tenant_recovery_keys");

            migrationBuilder.DropTable(
                name: "tenant_storage_usage");

            migrationBuilder.DropTable(
                name: "tenant_subscriptions");

            migrationBuilder.DropTable(
                name: "tenant_wrap_keys");

            migrationBuilder.DropTable(
                name: "transfer_checkpoints");

            migrationBuilder.DropTable(
                name: "transfer_schedule_windows");

            migrationBuilder.DropTable(
                name: "upload_bandwidth_grants");

            migrationBuilder.DropTable(
                name: "upload_bandwidth_limits");

            migrationBuilder.DropTable(
                name: "user_sessions");

            migrationBuilder.DropTable(
                name: "discovery_instances");

            migrationBuilder.DropTable(
                name: "artifact_break_glass_requests");

            migrationBuilder.DropTable(
                name: "notification_events");

            migrationBuilder.DropTable(
                name: "restore_operations");

            migrationBuilder.DropTable(
                name: "plans");

            migrationBuilder.DropTable(
                name: "bandwidth_profiles");

            migrationBuilder.DropTable(
                name: "backup_replicas");

            migrationBuilder.DropTable(
                name: "support_access_sessions");

            migrationBuilder.DropTable(
                name: "quota_policies");

            migrationBuilder.DropTable(
                name: "backup_artifacts");

            migrationBuilder.DropTable(
                name: "storage_connections");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "backup_run_attempts");

            migrationBuilder.DropTable(
                name: "backup_runs");

            migrationBuilder.DropTable(
                name: "backup_jobs");

            migrationBuilder.DropTable(
                name: "agents");

            migrationBuilder.DropTable(
                name: "tenants");
        }
    }
}
