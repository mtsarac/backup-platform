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
The operational cause of a paused transfer (UserRequested, ScheduleRestricted, NetworkRetrying).
_Avoid_: HaltFlag, WaitStatus
