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
