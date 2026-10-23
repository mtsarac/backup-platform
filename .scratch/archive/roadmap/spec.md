Status: done

# Spec: Priority 6 — Bandwidth Profiles, Transfer Pause/Resume and Network Recovery

## Problem Statement

Backup operations can saturate production network bandwidth during business hours, degrading user-facing applications. Conversely, off-peak hours leave substantial bandwidth unused. Furthermore, large backup transfers over unstable WAN connections (such as Direct SFTP and Google Drive uploads) frequently fail entirely upon temporary network drops or require long, uninterrupted windows, with no capability for operators to pause, schedule around shift hours, or automatically resume from the last byte offset.

## Solution

A comprehensive bandwidth governance and transfer recovery system that:
1. Enables operators to define weekly time-of-day Bandwidth Profiles in an explicit IANA Time Zone, allowing strict speed caps during working hours and unlimited transfer at night.
2. Evaluates profile hierarchy with explicit specificity: `Job > Agent > Tenant`.
3. Allows manual and automated transfer pausing without holding persistent database locks or lease fencing, closing connections cleanly and persisting durable Transfer Checkpoints.
4. Provides automatic resume and network failure recovery across Direct Transfer providers (Google Drive and SFTP) from the last verified byte chunk rather than restarting transfers from scratch.

## User Stories

1. As a platform administrator, I want to define recurring weekly bandwidth profiles with distinct hourly limits (e.g., 2 MB/s on weekdays 08:00-18:00, unlimited otherwise) in a specific IANA time zone, so that backups do not saturate corporate internet connections during work hours.
2. As a tenant administrator, I want to attach a bandwidth profile to specific backup jobs, so that high-priority database backups get dedicated bandwidth while bulk archival jobs are throttled.
3. As a tenant administrator, I want job-level bandwidth limits to override agent and tenant defaults, so that emergency or high-priority runs can proceed at full speed without changing fleet-wide limits.
4. As an operator observing a slow network or high server load, I want to pause an ongoing backup transfer from the dashboard, so that critical business traffic is immediately relieved.
5. As an operator, I want to resume a paused backup run, so that the transfer continues from its last verified byte offset without restarting the multi-gigabyte upload from the beginning.
6. As a backup agent executing a direct upload to SFTP or Google Drive, I want temporary network disconnections to be automatically retried up to 3 times before entering a recoverable paused state, so that transient glitches do not fail long-running backup attempts.
7. As a security and infrastructure auditor, I want paused transfers to release database locks and agent leases immediately, so that no stale leases or orphaned database transactions block system scalability.
8. As a tenant administrator, I want daylight saving time (DST) transitions to be handled automatically according to the profile's configured time zone, so that morning business hour throttling always takes effect at 08:00 local wall-clock time regardless of seasonal clock shifts.

## Implementation Decisions

- **Bandwidth Profile Entity & Model**:
  - `BandwidthProfile` entity containing recurring `TransferScheduleWindow` rules (DayOfWeek, StartTime, EndTime, LimitBytesPerSecond) and an explicit IANA TimeZone identifier (e.g. `Europe/Istanbul`).
  - Hierarchical resolution: Job-level profile overrides Agent-level profile, which overrides Tenant-level profile. In the absence of a profile, the existing static `UploadBandwidthLimit` applies.
- **Authoritative Pacing Evaluation**:
  - The backend resolves the currently active rate limit for an attempt during upload policy queries (`/api/agent/tasks/{attemptId}/upload-policy`).
  - Next refresh interval (`RefreshAfter`) is dynamically calculated to align with the nearest schedule window transition, ensuring timely rate limit adjustments as wall-clock time crosses window boundaries.
- **Durable Checkpointed Pause**:
  - When an attempt is paused, the agent flushes in-flight bytes, records the verified uploaded byte count or provider session URI as a `TransferCheckpoint`, and notifies the backend via task progress.
  - The attempt transitions to `BackupStatus.Paused` with `TransferPauseReason`.
  - The agent lease is released cleanly. No database transactions or active database connections are kept open while an attempt is paused.
- **Provider Resumable Recovery**:
  - Google Drive provider persists and resumes via Resumable Upload Session URI and range query.
  - SFTP provider writes at byte offset using seek/append mode from the verified checkpoint.
  - On network drop during streaming, agent retries up to 3 times before signaling paused state.

## Testing Decisions

- **External Behavior Focus**: Tests exercise public HTTP endpoints (`/api/bandwidth-profiles`, `/api/agent/tasks/{attemptId}/upload-policy`, `/api/backups/runs/{runId}/pause`, `/api/backups/runs/{runId}/resume`) and verify output pacer rates, attempt statuses, and resume offsets.
- **Determinism**: Time-dependent schedule transitions and DST clock shifts tested using mock `TimeProvider` instances rather than real-time sleeps.
- **Integration Prior Art**: Modeled after `UploadPacerTests` and `PostgreSqlStorageTransferTests`.

## Out of Scope

- Client-side packet-level traffic shaping / OS tc-netem throttling outside the application stream.
- Bandwidth profiling for download / restore operations (scope strictly covers backup upload transfers).
- Online billing or metered bandwidth invoicing.

## Further Notes

- Maintains complete backward compatibility with existing static `UploadBandwidthLimit` configurations.

## Verification

- Backend Release build succeeded; Windows Agent host also cross-built successfully.
- Backend unit: 4,360 passed; agent: 626 passed; architecture: 159 passed. Queue-renewal regression synchronizes on fake-timer registration instead of assuming thread-pool continuations finish after a short sleep.
- PostgreSQL-backed integration: 1,895 passed with actual PostgreSQL 18/16 tools and four parallel workers. Physical/fetch and streaming/stream backups passed manifest/WAL verification.
- Published Linux Agent.Host connected to the real HTTPS test API, declared capabilities, polled, and completed a local filesystem capacity probe (one additional smoke test passed).
- Frontend: typecheck, all 759 tests (85 files), and production build passed. Actual browser verification covered file selection, restore precheck invalidation, explicit pause-window selection, disabled rate controls, successful profile save, and desktop/mobile dialog layout.
- Real filesystem smoke covered full/selected restore, SHA-256, filters, symlink, Unix permissions, and overwrite refusal.
- Linux `.deb` built and inspected; systemd unit syntax verified. Package installation and RPM production were not exercised.
- Windows suite on Linux: four platform-safe tests passed, 47 Windows-only tests explicitly skipped. The Windows VM was unreachable; real VSS, Windows ACL/SMB, and service execution remain unverified. Live Drive/SFTP/SMB, Vault/AWS, native Linux snapshot disks, and MSSQL integration were not exercised.
- Actual HTTPS MinIO smoke passed for COMPLIANCE and GOVERNANCE: 17,825,915-byte three-part uploads, checksum readback, independently checked retention, and service-enforced version-specific deletion refusal.
- Actual signed-update smoke downloaded/staged the published payload, rejected forged/corrupt releases without changing active state, and observed 2,089 concurrent executable opens over 102 atomic activations and 101 rollbacks without a missing current release.
- Final reviewed Linux source was republished and packaged as `backup-platform-agent_0.0.1_amd64.deb` (30,995,624 bytes; SHA-256 `005e8f1bbf2011b5ab0403c64a13c38d245036aff8436fbf1385aabecb23a0cd`). Version `0.0.1` is disposable smoke metadata, not a published release. The published host again passed its real HTTPS backend/capacity lifecycle smoke.
- Final Standards review found one swallowed progress-callback exception in physical backup. It was removed; the existing failure/cleanup boundary now handles it. Three real PostgreSQL physical integration cases passed afterward, including the new regression that a failed progress sink cannot publish an artifact.
- Final Spec review identified four corrections: automatic TimeRule pause, preserving the winning assignment scope when a profile is reused, profile override of static defaults, and symlink-chain confinement. All implementations and focused checks pass, including the joined real HTTP/worker/PostgreSQL dump automatic pause lifecycle and malicious-archive filesystem smoke. Broad integration suite revalidated with 1,895 passing tests.
- Migration consistency regression initially found missing EF target-model metadata on `ScheduledTransferPause`. Its generated designer metadata is now attached to that migration (no empty follow-up migration); the snapshot/target/configured-model regression passes without weakening the test.
