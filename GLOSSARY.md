# Backup Platform Domain

Core domain language for backup lifecycle, observability, storage transfer, and agent fleet operations.

## Language

### Observability & Storage Health (Priority 5A)

**CapacityProbe**:
A lightweight operation executed by either the Platform Backend or an Agent to query physical disk or quota availability of a storage target.
_Avoid_: DiskCheck, QuotaPoller, SpaceInspector

**StorageCapacitySnapshot**:
Point-in-time metrics recording total, used, and free bytes reported for a specific storage target or connection.
_Avoid_: DiskStats, StorageUsageRecord

**BackupHealthState**:
Composite operational health status (Healthy, Degraded, Critical) reflecting backup age, replication count, restore verification, and storage availability.
_Avoid_: HealthScoreOnly, BackupStatusFlag

**TenantHealthState**:
The BackupHealthState of a whole tenant: the worst state among its backup jobs. A tenant with no backup jobs has no state.
_Avoid_: AverageHealth, OverallScore

**SizeAnomalyWarning**:
An advisory warning generated when an artifact's physical size diverges significantly from its historical baseline without failing the backup attempt.
_Avoid_: BackupFailure, SizeMismatchError, CorruptedSize

**AgentUpdateManifest**:
Cryptographically signed release metadata specifying version, package digest, minimum supported version, and signature for agent fleet self-update.
_Avoid_: ReleasePayload, UpdateScript, AutoPatch

### Bandwidth & Transfer Recovery (Priority 6)

**BandwidthProfile**:
A named schedule defining upload rate limits (bytes/sec) across recurring time-of-day and day-of-week windows in an explicit IANA time zone.
_Avoid_: SpeedLimitRule, ThrottleSchedule

**TransferScheduleWindow**:
An active time window within a BandwidthProfile governing the maximum permitted upload bandwidth during specific hours.
_Avoid_: PacingInterval, TimeSlot

**TransferCheckpoint**:
Durable metadata recording the verified byte offset or resumable session URI reached during a transfer, enabling safe resumption after network drops or pause.
_Avoid_: UploadState, ByteCounter

**TransferPauseReason**:
The operational cause of a paused transfer: `Operator` (manual pause), `TimeRule` (a closed transfer window), or `PausedByNetworkError` (three retries exhausted).
_Avoid_: HaltFlag, WaitStatus

### Direct Storage Transfers

**AgentDirect**:
Artifact payload I/O performed by the agent against SFTP, Google Drive, S3 or SMB. The backend brokers authorization, tasks, leases, quota and metadata; it is not an external-storage payload relay.
_Avoid_: BackendRelay, ProxyUpload

### Accounts & Access

**PlatformAdmin** (shown as "Platform Yöneticisi"):
The account role that operates the platform: tenants, users, plans, storage, shares shown under `Paylaşılan Yedekler`. It is not a tenant member and reaches tenant backup content only through a BackupShare.
_Avoid_: Super admin, Operator, Support user

**UserAdmin** (shown as "Kullanıcı Yöneticisi"):
The account role of a tenant's own administrator. It manages every tenant it is a member of, with the same rights in each; an account with no membership is disabled.
_Avoid_: Owner, Admin, Operator, Viewer, Tenant role

**BackupShare** (shown as "Paylaşım"; lists `Paylaşımlarım` and `Paylaşılan Yedekler`):
A time-limited, revocable grant that lets a PlatformAdmin download one artifact of a tenant as plaintext. Created by a UserAdmin or a PlatformAdmin with a reason, audited, notified, checked at session creation and stream start; revoking it cancels downloads in progress on every node. It never covers restore and never exposes a key.
_Avoid_: Support session, Emergency access, Temporary access

**Restore boundary**:
Restore is a tenant management operation available to both roles. It never accepts a share and never returns bytes or keys to the caller, but restoring to a controlled target exposes the plaintext there; every restore is audited. See `SECURITY.md`.
_Avoid_: Restore share, Shared restore

### User-Facing Names

Plain-language labels shown in the interface in place of technical domain terms. The domain terms stay unchanged in code and APIs; only the displayed label differs.

**Agent** (shown as "Yedekleme bilgisayarı"):
The installed program on a customer machine that runs backups and moves payload to storage.
_Avoid_: Ajan, Client in user-facing text

**Artifact** (shown as "Yedek dosyası"):
The stored output of one successful backup run.
_Avoid_: Artifact in user-facing text

**Backup run** (shown as "Yedekleme denemesi"):
One execution of a backup job, successful or not.
_Avoid_: Run, Çalıştırma in user-facing text

**Discovery** (shown as "Veritabanı bulma"):
Scanning an agent's machine for databases that can be backed up.
_Avoid_: Keşif in user-facing text

**Tenant recovery key** (shown as "Kendi kurtarma anahtarınız"):
An optional public key added to future encrypted backups. The tenant keeps the private key; removing the public key does not change existing backups. It has no relation to BackupShare: a share never hands out a key.
_Avoid_: Tenant anahtarı in user-facing recovery settings

**Wrap key** (shown as "Şifreleme anahtarı"):
The application's key for opening encrypted backups. Rotation protects future backups with a new key; retiring an older key removes online decryption for its backups and requires offline recovery.
_Avoid_: TWK in default user-facing text

### Storage Providers

**StorageProvider**:
The kind of external or local place a backup replica is stored: `local` (the agent's own disk), `central` (the platform's managed storage), `sftp`, `google-drive`, `s3` or `smb`.
_Avoid_: StorageType, StorageKind, ProviderName

**AgentExecuted**:
A StorageProvider whose physical removal and repair only an agent performs because the backend holds no credential for it: `sftp` and `google-drive`. `s3` and `smb` are external but still handled by the backend today.

### Backup Lifecycle & Verification

**FileBackup**:
A copy-only directory archive verified against a SHA-256 manifest and restored into the source root. The API rejects non-copy-only or unchecksummed file backup configurations.
_Avoid_: FolderSync, DirectoryBackup, FileMirror

**DatabaseBackup**:
A full database backup artifact for MSSQL or PostgreSQL (`pg_dump` custom format), checksummed and read back before verification completes.
_Avoid_: DBSnapshot, SQLDump, DBExport

**RestoreVerification**:
An automated post-backup verification process that restores an artifact into a disposable temporary database, executes an integrity check, and cleans up.
_Avoid_: TestRestore, DryRunRestore, SyntheticCheck

### Fleet & Enrollment

**AgentLease**:
A time-bounded lease renewed periodically by an agent during an active backup run to guard against stalls, detect crashes, and enforce concurrency limits.
_Avoid_: JobLock, AgentHeartbeatLock, ExecutionToken

**EnrollmentToken**:
A single-use, time-limited token created in the UI to authenticate and register a new agent instance with the platform.
_Avoid_: RegistrationKey, AgentSecret, InviteCode
