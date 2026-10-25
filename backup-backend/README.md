# Backend

The .NET 10 solution behind Backup Platform: the multi-tenant API, the agent that runs on database hosts, and the modules and providers they share. Open `BackupPlatform.sln`.

## Layout

```text
src/
  hosts/
    api/             ASP.NET Core API, scheduler, background workers
    agent/           Agent executable (Windows service or Linux systemd unit)
  core/
    domain/          Entities and rules, no framework dependencies
    application/     Use cases and ports
    contracts/       DTOs shared by the API and the agent
    infrastructure/  EF Core, PostgreSQL, migrations
  modules/           Backend features: agents, audit, backups, identity,
                     managed-service, notifications, scheduling, storage, tenancy
  providers/
    backup/          mssql, postgresql, file-system
    storage/         central, local, sftp, google-drive, s3, smb
    secret/          data-protection (Database / Vault / AWS Secrets Manager)
  agent/             core, transport, platform-windows, platform-linux
tests/               unit, architecture, agent, integration, windows
deploy/              Agent installers and release README files
```

The architecture tests enforce the dependency direction between these layers, so a new reference that breaks it fails the build.

## How it fits together

- PostgreSQL is the only source of truth for schedules, run state, leases, and concurrency. Quartz.NET only fires timers.
- Agents authenticate with an enrolled identity, poll for tasks, and renew a lease while they work. A stale agent cannot change a run that a newer one owns.
- Backup and restore engines sit behind provider interfaces. Adding an engine means adding a provider and an agent capability, not changing the pipeline.
- Secrets are stored encrypted with ASP.NET Data Protection and are not logged or returned by the API.
- Successful runs also record advisory size metadata (`sizeAnomalyStatus`, `sizeAnomalyDeviationRatio`, `sizeAnomalyBaselineBytes`, and derived `isSizeAnomaly`) in run list/detail responses. The first three successes establish a baseline; later runs compare against the last ten successful runs of that tenant/job. Only deviations greater than both 30% and two standard deviations raise `SizeAnomalyWarning`. Artifacts, checksums, and success status are unchanged. The warning audit entry and policy-admitted `backup_size_anomaly` event/deliveries commit with completion; email/webhook delivery stays asynchronous through the existing N1 outbox.

### Advisory backup health

Tenant viewers can read `GET /api/tenants/{tenantId}/backup-jobs/{jobId}/health`
and `GET /api/tenants/{tenantId}/observability/health-summary`. The versioned
equivalents are `GET /api/v1/backups/jobs/{id}/health` and
`GET /api/v1/observability/health-summary?tenantId={tenantId}`; membership (or
platform administrator access) is checked before reading that tenant's data.

The deterministic score weights freshness (40), usable verified replicas of the
latest successful backup (30), latest isolated restore verification (20), and
the fullest configured destination (10). Freshness uses consecutive UTC
occurrences of the saved cron schedule, including its time zone. Jobs without a
schedule get an explicit unavailable freshness factor, not an assumed daily
schedule. Missing, failed, unknown-quota, future-dated, or over-24-hour-old
capacity observations earn no capacity points. Capacity probes and health share
the same thresholds: below 80% earns ten points, 80% up to (but excluding) 90%
earns five, and 90% or above is Critical and earns zero.

Scores of at least 80 are Healthy, 50–79 Degraded, and below 50 Critical.
Untested or expired (over 30 days) successful restore verification earns ten
points; a failed latest test earns zero. Empty tenants return null average and
state rather than a fabricated healthy score. Summaries batch latest evidence
per job/connection and include tenant-owned capacity risks. `recentSizeAnomalies`
contains the latest 20 successful `SizeAnomalyWarning` runs completed during the
seven days ending at `evaluatedAt` (inclusive), ordered by `completedAt` then
`runId` descending. Each item exposes `runId`, `jobId`, `jobName`, `completedAt`,
`sizeBytes`, `baselineMeanBytes`, and signed `deviationRatio`.
`recentSizeAnomaliesTotal` counts all matching runs, including those beyond the
list limit; legacy runs without advisory metadata are not warnings. The job detail
screen explains all four factors. Health reads never change backup admission,
leases, execution, or completion.

### Capacity task routing and history

On-prem probes return HTTP 202 with `status: "Pending"` and a task identifier.
New Agents advertise the `storage.capacity-probe` capability; task envelopes use
`type: "capacity-probe"`. A Local connection targets its unique configured job
Agent, including jobs using the provider's default connection. Multiple distinct
job writers are ambiguous and unavailable. Without a job writer, exactly one
authorized compatible online Agent is required; an offline writer never falls
back to a different host. SFTP may use any authorized compatible online Agent.
Named Local volumes resolve only through the Agent's configured local roots.
SFTP private-network access requires the operator's narrow
`Agent:Backup:Sftp:AllowedPrivateNetworks` allowlist. An SFTP server without
usable statvfs falls back to the server's own `df -Pk` over an SSH exec channel
(same pinned host key); if that fails too it reports CapacityUnavailable. The
Agent never substitutes its own host filesystem's capacity.

S3 and SMB expose no capacity or quota to the platform, so their probe and GET
return `status: "NotMeasurable"` immediately: nothing is queued to an Agent and
no snapshot is stored. Capacity whose total or used/free amount is unknown is
never evaluated as Available; it is CapacityUnavailable.

Capacity and health reads use the same current routing/liveness checks. Expired
or retry-exhausted tasks are unavailable immediately, without GET-side writes;
the periodic worker persists terminal task state. Capacity GET returns the latest
30 measurements, newest first, with `historyLimit: 30`. Snapshot, threshold audit,
notification condition/event, and outbox delivery commit atomically.
Snapshots older than 90 days are pruned in the same transaction as each new snapshot; a
connection's newest snapshot and its newest measured snapshot are always kept.

Downgrading `StorageCapacityTaskTransport` requires snapshot rows to be
representable by the old schema: only Available or CapacityUnavailable. If any
Warning or Critical row exists, the downgrade refuses the narrower constraint
and PostgreSQL rolls the entire migration back, preserving all rows, the task
table, and migration history. It never rewrites measurements or deletes rows to
make a downgrade pass.


### Scheduled upload bandwidth

Tenant administrators manage weekly profiles through
`/api/tenants/{tenantId}/bandwidth-profiles` and assign them using the upload-bandwidth
`tenant/profile`, `agents/{agentId}/profile`, or `jobs/{jobId}/profile` routes.
The most specific assignment wins: job, then agent, then tenant. The selected
profile replaces all static ceilings, including outside its windows (unlimited).
Static job, agent and tenant ceilings apply together only without a profile.
Only a selected tenant profile shares its rate among tenant uploads; job and
agent profiles use their own scope without taking a tenant share.

Windows use the selected IANA time zone, include their start and exclude their
end. An end earlier than its start means the following day; equal endpoints
are invalid. Overlaps are rejected across midnight and the Saturday/Sunday
week boundary. Both occurrences of a repeated DST hour are evaluated, and a
skipped hour changes the limit at the actual clock jump. Agent upload-policy
refreshes are shortened to the next effective transition.

A window's `bytesPerSecond: 0` explicitly pauses transfers; positive rates must
still meet the normal minimum, and zero is not valid for static ceilings.
The agent cancels active direct uploads when their policy enters a pause window;
lease responses also signal schedule pauses for all upload topologies. The
existing checkpoint and staged artifact are retained, completion records
`Paused` / `TimeRule`, and the attempt releases its lease. Resume is manual,
obtains a fresh lease, and pauses again without uploading if the window is
still active. Leaving a pause window does not automatically resume a run.

Operators pause and resume a run with `POST /api/tenants/{tenantId}/backup-runs/{runId}/pause` and `/resume`
(also `POST /api/backups/runs/{runId}/pause` and `/resume`, which resolve the tenant from the run). Each
request writes a `backup_run.pause_requested` or `backup_run.resumed` audit event with the acting user
in the same transaction as the change.

## Run it

You need the .NET 10 SDK and PostgreSQL 18.

```bash
dotnet restore BackupPlatform.sln
dotnet build BackupPlatform.sln -c Release -p:ContinuousIntegrationBuild=true -warnaserror

dotnet tool restore
export BACKUP_PLATFORM_CONNECTION_STRING='Host=localhost;Port=5432;Database=backup_platform;Username=...;Password=...'
dotnet ef database update --project src/core/infrastructure
dotnet run --project src/hosts/api -- --bootstrap-user   # creates the first platform admin
dotnet run --project src/hosts/api
dotnet run --project src/hosts/agent
```

`--bootstrap-user` reads `BACKUP_PLATFORM_BOOTSTRAP_LOGIN`, `BACKUP_PLATFORM_BOOTSTRAP_PASSWORD`, and `BACKUP_PLATFORM_BOOTSTRAP_DISPLAY_NAME`. Other settings are listed in `.env.example`. The .NET tools do not load dotenv files, so export variables or use a secret store.

To enroll an agent, see "Connecting an agent" in the [root README](../README.md).

The `Dockerfile` builds the API image. `docker-compose.yml` in the repository root starts it with PostgreSQL, SQL Server, and the frontend.

## 3-2-1 and protected external storage

The existing tenant storage-connection APIs and connection wizard support `s3`
and `smb`; credentials use the same encrypted secret store, write-only secret
rotation API and tenant authorization as other connections. A referenced
connection cannot change its endpoint/bucket/share or lose its credentials.
Disabling a connection prevents new admissions, not retention or existing reads.

Enable `configuration.enforceThreeTwoOne`, set `sourceSite`, and require at least
three verified replicas. Connections accept optional `site` and `mediaClass`
(`disk`, `object-storage`, or `tape`). Site labels are operator-supplied physical
failure domains, not inferred from a hostname or ownership. Defaults are disk
for Local/Central/SFTP/SMB and object-storage for S3/Drive. A remote site counts
only when a stored replica independently passes checksum read-back verification.
Same-site, deleted or failed copies do not satisfy the off-site requirement; a
successful connection probe alone never establishes compliance. Site/media facts are snapshotted at run
admission; changing a connection never rewrites an old run's compliance.
Policy failure keeps every successfully verified copy available and accounted
for while reporting the run failed. Agents must advertise `backup.three-two-one`
and the relevant `storage.s3`/`storage.smb` capabilities.

S3 configuration requires an HTTPS `endpoint` (origin only), `bucket`, and
signing `region`. Secrets are `accessKey`, `secretKey`, and optional
`sessionToken`. Transfers go directly from the agent to the S3-compatible
service using AWS Signature V4, multipart upload and conditional publication;
the backend records a quota-bounded upload intent before bytes move. Each part
is staged privately on the executing host and checked with Content-MD5; the
complete input SHA-256/size is validated before publication, then the published
object is read back for independent verification. Failed uploads abort their
multipart session. Give credentials access only to the intended bucket and
`tenants/{tenantId:N}/connections/{connectionId:N}/` prefix.

For WORM, create a versioned bucket with Object Lock enabled, set
`objectLockMode` to `COMPLIANCE` or `GOVERNANCE`, and `retentionDays` to 1–36500.
The provider verifies bucket protection and the service's returned object
retention; it never sends a governance-bypass header. Deletion addresses the
actual version and refuses unexpired retention/legal hold rather than hiding
the object behind a delete marker. Existing service-side retention survives
credential rotation and later connection changes. Credentials need bucket
access, multipart upload/abort, object read/delete-version and retention/legal
hold read permissions, plus PutObjectRetention when protection is enabled.
S3 connection tests authenticate bucket access and protection without creating
an unaccounted locked probe; the UI distinguishes this from a write/delete test.
Configure the bucket's incomplete-multipart lifecycle rule as crash cleanup.

SMB configuration is `host` and `share`; `username`, `password`, and optional
`domain` are secret fields. Windows uses an isolated outbound logon session,
never global `net use` credentials; apply SMB signing/encryption policy on the
Windows client and NAS. Linux needs no mount privilege or `cifs-utils`: the agent
uses a managed SMB client (SMBLibrary, LGPL-3.0, linked dynamically; see
`THIRD-PARTY-SMBLIBRARY.txt`) over TCP 445 with NTLMv2 and SMB 2.1 to 3.1.1. SMB 3
encryption (AES-128-CCM) is used whenever the server offers or requires it; a
server without encryption is accepted. Not supported on Linux: Kerberos-only
servers and servers that allow only AES-GCM. Content is written to a hidden
`.partial` file, validated and renamed (atomic publish); reparse points are
never followed; each operation opens its own session and a read stream keeps
its session until closed. Failures are distinguishable: rejected credentials,
missing share, access refused, unreachable server. Writes are verified by
read-back. SMB tests from the backend are explicitly NotTestable;
the agent authenticates the share when executing the backup.

## Agent Auto-Update and Deployment

Signed self-update is supported for `linux-x64`, `linux-arm64`, and `win-x64` using ECDsa P-256 release metadata (`AgentUpdateManifest`). Packages are streamed bounded to disk without whole-memory buffering, pinned against operator public keys, and extracted only after exact length and digest match. Untrusted archive entries (path traversal, symlinks/hardlinks, duplicates, unsafe POSIX modes, ZIP64/multi-volume structures, and decompression bombs) fail before publication. Releases never ship or overwrite configuration or identity files.

Updates are deferred whenever active work is queued or executing (backup, restore, download, cleanup, deletion, or probe); active jobs are never interrupted.

### Linux (systemd)
- Executable: `/opt/backup-platform-agent/supervisor/BackupPlatform.Agent.Host --supervise-update /etc/backup-platform-agent/update-settings.json`
- Service account: unprivileged `backup-agent:backup-agent` with `Restart=always`
- Releases: `/opt/backup-platform-agent/releases/{version}`
- Active pointer: atomic symlink `/opt/backup-platform-agent/current -> releases/{version}`
- Supervisor: tracks child startup lifecycle. If an unacknowledged release crashes or fails to signal readiness within 60 seconds, the supervisor atomically rolls `/opt/backup-platform-agent/current` back to `.previous` and kills failed child instances.

### Windows (Windows Service)
- Service: `BackupAgent` running as `NT SERVICE\BackupAgent` with `start= delayed-auto`
- Executable ACL: protected read/execute (`(OI)(CI)RX`) owned by Administrators/SYSTEM; the service account never has write permissions to its executable or binaries.
- Privileged Helper: fixed Scheduled Task `BackupAgentUpdate` running as `SYSTEM` pointing strictly to the original protected binary `BackupPlatform.Agent.Host.exe --apply-update "<settingsPath>"`. The service account holds `GRGX` permissions (can trigger via `schtasks /Run /TN BackupAgentUpdate`, but cannot edit or replace the action).
- Staging & Rollback: downloads stage into protected `%ProgramData%\BackupAgent.updates`. The helper revalidates package digest and signature before extraction, stages into `releases\{version}`, waits for old host clean termination via lease lock, updates `binPath`, starts the updated service, and monitors the startup health acknowledgement. If startup fails or times out, the helper rolls back `binPath` to the previous release executable and restarts the service.

## Tests

Run from this directory.

| Suite | Command | Needs |
|---|---|---|
| Unit | `dotnet test tests/unit-tests -c Release` | nothing |
| Architecture | `dotnet test tests/architecture-tests -c Release` | nothing |
| Agent | `dotnet test tests/agent-tests -c Release` | nothing |
| PostgreSQL integration | `dotnet test tests/integration-tests -c Release --filter "FullyQualifiedName!~MsSql&FullyQualifiedName!~BackupPipelineSmoke&FullyQualifiedName!~StorageCapacityAgentHostSmokeTests" -- xUnit.MaxParallelThreads=4` | administrative PostgreSQL connection and current/older client tools; see scripts README |
| MSSQL integration | `dotnet test tests/integration-tests -c Release --filter "FullyQualifiedName~IntegrationTests.MsSql\|FullyQualifiedName~BackupPipelineSmoke"` | PostgreSQL and MSSQL variables |
| Windows | `dotnet test tests/windows-tests -c Release` | Windows host |

Integration test variables:

- `BACKUP_PLATFORM_TEST_CONNECTION_STRING`: administrative PostgreSQL connection with `CREATEDB`. Each run creates and drops its own database.
- `BACKUP_PLATFORM_TEST_MSSQL_HOST`, `_USER`, `_PASSWORD`: SQL Server connection.
- `BACKUP_PLATFORM_TEST_MSSQL_STAGING_SERVER_DIR`, `_AGENT_DIR`: the same staging folder as SQL Server sees it and as the agent sees it.
- `BACKUP_PLATFORM_TEST_POSTGRES_TOOLS_DIRECTORY`: current `pg_dump`/`pg_restore`, plus matching-major `pg_basebackup`/`pg_verifybackup`/`pg_waldump` for physical tests.
- `BACKUP_PLATFORM_TEST_POSTGRES_OLD_TOOLS_DIRECTORY`: older client binaries for compatibility-refusal tests.
- Physical tests also require remote replication access for the test login in `pg_hba.conf`. Limit collection parallelism on high-core hosts to avoid exhausting the database connection limit.
- Published-host capacity smokes run separately with `BACKUP_PLATFORM_TEST_AGENT_HOST_PATH` and staging paths; the SFTP variant additionally needs an operator-approved server and pinned host key.

CI runs the unit, architecture, and a fast subset of the agent tests. For the helper scripts that start disposable databases and run the integration suites, see [scripts/README.md](../scripts/README.md).

## Windows file permissions

File-system manifests store each regular file and directory's effective Windows
DACL. The Windows platform adapter freezes inherited rules into a protected DACL,
so restoring under a more permissive parent does not grant extra access. Files
receive their ACL inside private restore staging before installation; newly
created directories receive theirs after their children are installed. Existing
destination directories keep their current ACL rather than changing permissions
on unrelated contents.

The agent account must be able to read source ACLs and install the restored
objects. Ownership and privileged SACL/audit settings are not changed. Windows
ACL manifests require the Windows adapter to restore; another platform rejects
them before replacing selected files instead of silently discarding access
restrictions. Unix permission bits continue to use the native Unix path.

ACL regressions are covered by the platform-safe file-provider unit tests and
`WindowsFilePermissionsTests` in the Windows suite; the latter requires Windows
and exercises real inherited allow/deny rules against a broader restore parent.

## Retained transfer cleanup and S3 recovery

Paused transfers retain a durable local journal and pins for the exact staged
artifact. The normal staging sweep asks the authenticated backend whether that
tenant/agent's attempt is terminal before releasing those pins. Paused, queued
for resume, actively held, unknown, and unreachable attempts remain protected.
After cancellation or another terminal outcome, the next successful sweep
releases the journal and applies the usual staging minimum-age cleanup.
Keep staging cleanup enabled to reclaim cancelled retained transfers.

S3 resumes verify every checkpointed part across all `ListParts` pages.
Temporary service failures (HTTP 500/502/503/504/429), request timeouts, and
response-body disconnects enter the same checkpoint-based network recovery
path as connection failures; checksum mismatches remain terminal integrity
errors rather than being retried as network faults.

File backup admission and task claiming both require the agent to advertise
`file-system.backup.full`; unsupported agents cannot receive a file-system job.
S3 connection tests fail closed if the provider cannot authenticate bucket access;
an access check never claims that backup bytes or retention have been verified.

## File-source configuration

Configure agent-local roots, for example
`Agent__Backup__FileSources__documents__RootDirectory=/srv/documents`.
The job wizard selects the `documents` reference and root-relative paths, not an
arbitrary host path. Include/exclude wildcards and symlink Skip/Preserve apply to
that selection. Optional root settings are `SnapshotKind`, `SnapshotSource`,
`SnapshotRelativePath`, and `LvmSize`; snapshot sources require the native tools
and privileges described in [scripts/README.md](../scripts/README.md).
Without a snapshot, locked, unreadable, or changing files fail the backup rather
than silently producing an incomplete artifact.

Restore uses the original configured reference or another configured root plus a
relative target directory. The wizard requires a successful precheck and explicit
confirmation before overwriting. Selected files are manifest/checksum-verified
before publication; changing the selection or destination invalidates precheck.
Preserved symlinks must have confined relative targets without intermediate
symlink components. Link chains are rejected rather than resolved, including
chains planned by the selected manifest and paths hidden by `.`/`..` normalization.
All selected links are checked before replacing any destination file.

## Sealed audit and backup reports

Enable new audit signatures with `Audit:SigningKeyPemPath` (absolute path to an
operator-provisioned ECDSA P-256 private key) and `Audit:SigningKeyId`. Keep the key
outside the database and repository. Retain previous public PEM keys under
`Audit:VerificationKeys:{keyId}` when rotating; never reuse a key ID for a
different key. Without a signing key, records are explicitly unsealed.

Platform administrators can inspect `/api/platform/audit/integrity`, with
optional `after` and `limit` (1–1000), to distinguish Verified, Invalid,
UnknownKey and Unsealed records. Per-record signatures detect changed payloads,
not deletion of whole records; export records and public keys to independently
controlled storage for deletion-resistant audit retention.

Tenant viewers can query `/api/tenants/{tenantId}/reports/backups` with optional
`from`/`to` timestamps. The default interval is the last 30 days; the maximum is
366 days. Results include succeeded, failed and cancelled totals plus daily and
per-job outcomes, restricted to that tenant.
