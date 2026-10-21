Status: done

# Spec: Priority 5A.1 — Observability Core (Storage Capacity, Backup Health, Size Anomaly)

## Problem Statement

Backup administrators, operators, and tenant owners lack real-time visibility into the actual physical capacity of their backup destinations, the ongoing reliability of their scheduled backup jobs, and silent data anomalies.

Without automated physical capacity checks, an external storage target (such as an SFTP server filling up or a Google Drive account reaching its quota) can run out of space without warning, causing scheduled backups to fail abruptly during critical backup windows.

Furthermore, operators currently have to piece together job statuses, replica counts, storage quotas, and restore test records across multiple screens to answer a basic question: "Is my database safely backed up and restorable?"

Finally, when unexpected data events occur—such as accidental mass deletion of database tables or runaway processes generating bloated database dumps—the platform records the backup as successful as long as checksums match, leaving administrators oblivious to significant payload deviations until disaster recovery is needed.

## Solution

Deliver an end-to-end Observability Core (Priority 5A.1) that continuously probes destination capacity, computes an actionable health score, and flags statistical payload size anomalies:

1. **Hybrid Storage Capacity Probing (Phase 21):** Automatically inspect destination storage availability through hybrid routing. Cloud and platform-managed storage targets (Google Drive, S3, Central) are probed directly by the Platform Backend; on-premises and private-network targets (Local volumes, private LAN SFTP) are queried by authorized Agents via lightweight `CapacityProbe` tasks. Capture `StorageCapacitySnapshot` records (total, used, free bytes), enforce platform warning (80%) and critical (90%) thresholds, and raise N1 notifications when storage is critically full.
2. **Composite Backup Health Scoring (Phase 22):** Calculate a deterministic 0–100 reliability score and tri-state status (`Healthy`, `Degraded`, `Critical`) for every job and tenant. Weight the score across backup freshness (40%), verified replication (30%), isolated restore verification results (20%), and storage availability (10%). Provide immediate advisory visibility without ever blocking in-flight backups.
3. **Statistical Size Anomaly Detection (Phase 23):** Maintain a rolling baseline of artifact sizes per backup job. Using a moving-average and standard deviation (Z-score) algorithm on successful runs (after a 3-run cold-start period), identify sudden drops (>30% or >2σ) and massive ballooning. Emit a non-blocking `SizeAnomalyWarning`, publish an advisory audit event, and trigger N1 notifications without aborting the backup.

## User Stories

1. As a tenant administrator, I want to see the remaining free disk space and quota for all my configured storage targets, so that I can provision more storage before backups fail due to disk full errors.
2. As a platform operator, I want cloud destinations (Google Drive) to be probed directly by the backend, so that storage availability is verified without needing an agent to wake up.
3. As a systems engineer, I want private on-premises SFTP servers to be probed by an authorized agent on the local network, so that internal storage credentials and network access are never exposed to the public internet.
4. As a tenant administrator, I want to receive an immediate email or webhook alert when any storage target exceeds 90% utilization, so that I can take corrective action before the next scheduled run.
5. As a backup operator, I want storage targets between 80% and 90% utilization to display a warning status in the UI, so that I can plan expansion proactively.
6. As an automated scheduler, I want capacity probes to run automatically after every completed backup transfer, so that storage metrics reflect recent payload additions immediately.
7. As a platform administrator, I want an automatic daily sweep to probe storage targets that have had no recent backup activity, so that capacity metrics stay current even for infrequently used destinations.
8. As a backup operator, I want to see a single composite health score (0–100) and status for each backup job, so that I can instantly evaluate backup reliability.
9. As a tenant owner, I want to see a top-level health summary on my dashboard, so that I know at a glance whether any of my databases are at risk.
10. As a reliability engineer, I want backup freshness to account for 40% of the health score, so that jobs that have missed their schedule or have stale backups are heavily penalized.
11. As a compliance officer, I want verified replica count to account for 30% of the health score, so that single-copy or degraded replica sets are surfaced before a media failure occurs.
12. As a disaster recovery manager, I want automated restore verification results (R1) to account for 20% of the health score, so that unverified backups do not create false confidence.
13. As a storage administrator, I want destination capacity status to account for 10% of the health score, so that full or nearly-full destinations degrade the job's overall rating.
14. As a database administrator, I want an in-flight backup to proceed to completion even if the health score is Critical, so that monitoring calculations never prevent a valid backup from succeeding.
15. As a security officer, I want to be alerted if a backup artifact is suddenly 50% smaller than previous runs, so that I can detect unexpected table drops or truncated dumps before old backups expire.
16. As a database administrator, I want to be alerted if a backup artifact is suddenly 300% larger than normal, so that I can investigate runaway transaction logs or accidental bloat.
17. As an automated backup runner, I want newly created jobs to remain in `BaselinePending` status for the first 3 runs, so that natural initial variance does not trigger false anomaly alerts.
18. As a backup operator, I want size anomaly checks to use a moving average and standard deviation (Z-score) of the last 10 successful backups, so that gradual, legitimate data growth does not trigger warnings.
19. As a database administrator, I want a size anomaly to be recorded as a non-blocking `SizeAnomalyWarning` advisory state, so that my backup artifact is still safely stored and retrievable.
20. As an on-call engineer, I want to receive an N1 notification (`backup_size_anomaly`) when a size anomaly is detected, so that I can verify data integrity promptly.
21. As a tenant administrator, I want to view historical capacity snapshots over time in the storage connection details, so that I can observe storage consumption trends.
22. As an API client, I want to query a job's health breakdown through a dedicated REST endpoint, so that external monitoring systems can ingest the health state.
23. As a frontend user, I want storage targets with unreachable agents to clearly show `CapacityUnavailable` rather than showing 0 bytes or misleading stale numbers, so that I am aware of connectivity issues.
24. As an auditor, I want all capacity threshold breaches and anomaly warnings to be logged in the immutable audit trail, so that we have an official record of operational warnings.

## Implementation Decisions

### Modules to Build / Modify

1. **Storage Capacity Module (`backup-backend`):**
   - Introduce `StorageCapacityProbeService` responsible for dispatching capacity probes.
   - For cloud destinations (Google Drive), query the provider API directly using the backend's connection credentials.
   - For on-prem/LAN destinations (Local, SFTP), dispatch a lightweight `CapacityProbeTask` to an authorized, connected Agent.
   - Introduce `StorageCapacitySnapshot` entity and database repository to record historical point-in-time measurements (`TotalBytes`, `UsedBytes`, `FreeBytes`, `ReportedAt`).
   - Introduce periodic background sweeper (daily) and event-driven trigger (post-transfer completion) for capacity refresh.

2. **Backup Health Module (`backup-backend`):**
   - Introduce `BackupHealthEvaluator` service that calculates composite health states.
   - Formula:
     - Freshness Factor (40 points): Full points if last successful backup is within expected schedule window; decays linearly to 0 if age exceeds 2x schedule period.
     - Replication Factor (30 points): Full points if required verified replica count is satisfied; proportional points if degraded; 0 points if no verified replica exists.
     - Restore Verification Factor (20 points): Full points if latest R1 restore check passed within the last 30 days; 10 points if pending/untested; 0 points if failed.
     - Capacity Factor (10 points): 10 points if destination <80% full; 5 points if 80%-90% (Warning); 0 points if >90% (Critical).
   - Health classification:
     - 80–100: `Healthy`
     - 50–79: `Degraded`
     - 0–49: `Critical`
   - Store or project health summaries for fast dashboard querying without heavy ad-hoc table joins.

3. **Size Anomaly Detection Module (`backup-backend`):**
   - Introduce `BackupSizeAnomalyDetector` invoked during backup attempt completion (`BackupOrchestrator`).
   - Query the last $N$ (up to 10) successful runs for the job.
   - If count < 3, mark run anomaly status as `BaselinePending` and exit without warning.
   - Calculate moving mean ($\mu$) and standard deviation ($\sigma$).
   - If current artifact size deviates by $>2\sigma$ AND $>30\%$ from $\mu$ (in either direction), flag the run with `SizeAnomalyWarning`.
   - Persist anomaly metadata on the backup run record (`IsAnomaly`, `DeviationRatio`, `BaselineMeanBytes`).
   - Atomic integration with Notification Outbox: enqueue `backup_size_anomaly` event.

4. **Agent Transport & Execution (`BackupPlatform.Agent`):**
   - Add handler for `CapacityProbeTaskRequest`:
     - For Local storage: execute disk drive inspection (`DriveInfo`).
     - For SFTP storage: execute lightweight directory/stat query or SFTP `statvfs` extension if supported, falling back to volume stat.
   - Return `CapacityProbeTaskResult` with byte figures or failure reason.

5. **Notification Integration (`src/modules/notifications`):**
   - Register new `NotificationEventTypes`:
     - `storage_capacity_critical`
     - `backup_size_anomaly`
   - Ensure deduplication and rate-limiting rules apply so repeated probes do not storm recipient inboxes.

6. **API Endpoints:**
   - `GET /api/v1/storage/connections/{id}/capacity`: Returns latest `StorageCapacitySnapshot` and historical trend.
   - `POST /api/v1/storage/connections/{id}/probe`: Triggers an on-demand capacity probe.
   - `GET /api/v1/backups/jobs/{id}/health`: Returns health score, breakdown factors, and state.
   - `GET /api/v1/observability/health-summary`: Tenant-wide aggregate health statistics.

## Testing Decisions

### What Makes a Good Test
- Tests must assert observable external contracts: HTTP API responses, database persistence, emitted notification events, and agent task execution messages.
- Tests must NOT assert internal algorithm steps or transient variables.
- Tests must verify edge cases: cold start (<3 runs), zero variance (identical run sizes), missing agents, and storage threshold boundaries (79% vs 80%, 89% vs 90%).

### Modules and Seams to Test

1. **Integration Test Seam: Storage Capacity & Probing (`tests/integration-tests/Storage/`)**
   - Test hybrid routing: Verify Google Drive queries execute directly on backend; verify SFTP queries dispatch task to Agent.
   - Test threshold transitions: Verify passing 80% triggers Warning status; passing 90% enqueues `storage_capacity_critical` in notification outbox.
   - Test unreachable agent: Verify graceful transition to `CapacityUnavailable` when agent is offline.

2. **Integration Test Seam: Health Scoring (`tests/integration-tests/Backup/`)**
   - Test full score calculation against deterministic database fixtures: fresh backup + all replicas verified + restore passed = 100 (`Healthy`).
   - Test degradation: Stale backup + degraded replicas drops score into `Degraded` or `Critical`.
   - Test non-blocking behavior: Assert that scheduled and manual backup attempts complete successfully regardless of the job's health score.

3. **Integration Test Seam: Size Anomaly Detection (`tests/integration-tests/Backup/`)**
   - Test cold start: Verify first 2 runs produce `BaselinePending` and no notification event.
   - Test normal variance: Verify a 5% size change produces no anomaly.
   - Test sudden drop: Verify a 40% size drop (>2σ) generates `SizeAnomalyWarning`, updates the run record, and writes `backup_size_anomaly` to the notification outbox.
   - Test sudden spike: Verify a 300% size jump generates `SizeAnomalyWarning`.
   - Test idempotency: Re-evaluating or re-inspecting runs does not duplicate notification events.

### Prior Art
- `NotificationDetectionIntegrationTests.cs`: Pattern for asserting platform conditions turn into deduplicated notification events.
- `StorageConnectionApiIntegrationTests.cs`: Pattern for storage connection lifecycle and validation.
- `BackupOrchestrationPersistenceTests.cs`: Pattern for testing backup attempt lifecycle and persistence.
- `UsageAccountingIntegrationTests.cs`: Pattern for byte calculations and quota tracking.

## Out of Scope

- **Auto-Update (Phase 25):** Cryptographic package signing, manifest distribution, and agent self-update mechanisms belong to Priority 5A.2.
- **Advanced Audit & SIEM (Phase 28):** External audit streaming, SIEM integration, and cryptographic audit sealing belong to Priority 5A.2.
- **Advanced Reporting & Read-Models (Phase 29):** Dedicated OLAP read-model projections and heavy historical analytical aggregations belong to Priority 5A.2.
- **Blocking / Failing Backups on Anomalies:** Explicitly rejected per ADR 0001; anomalies are strictly non-blocking advisory indicators.
- **Custom Per-Connection Thresholds:** Platform-wide fixed thresholds (80% Warning, 90% Critical) are used; custom percentage overrides are excluded from this phase.

## Further Notes

- **Cold Start Handling:** Jobs with fewer than 3 successful backups are marked as `BaselinePending` for anomaly checks.
- **Agent Offline Handling:** If an on-premises or private LAN destination cannot be probed because all authorized agents are offline, its status is marked `CapacityUnavailable` rather than reporting stale or zeroed values.
- **Notification Deduplication:** N1 notification engine deduplication ensures that an ongoing critical capacity state or recurring size anomaly emits a notification once per threshold crossing/run, preventing alert fatigue.
