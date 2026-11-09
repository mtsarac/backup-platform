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

### Idempotent job creation

`POST /api/tenants/{id}/backup-jobs` **requires exactly one valid `Idempotency-Key`** (1-128 printable ASCII); a missing, repeated or malformed header answers `400` (`field: Idempotency-Key`) before anything is read or written. The job stores the key and a fingerprint of the normalised request: an HMAC-SHA256 over tenant, key and request, a typed database login included, keyed with a platform secret (`platform-job-idempotency-key-v1`: 32 random bytes in the shared secret store, created once under a PostgreSQL advisory lock, so every API node and restart uses the same key). A leaked job row therefore lets nobody test password guesses offline. An unconditional unique index on (tenant, key) backs it, so a disabled job keeps its key. Same key and same request answers `200` with the original job (no credential exchange, audit entry or schedule synchronisation happens again); same key and another request answers `409 idempotency_conflict`. Concurrent identical requests on any API node produce exactly one job.

A typed login is stored under a reference derived from the request, after a claim row in `job_credential_claims` was written. A retry reuses the one credential. A request that creates nothing (validation error, conflict, unknown agent) deletes the credential it stored; a creation that is interrupted after storing it and never retried is removed by `JobCredentialSweepWorker` (every minute, every node) once its claim is older than one hour and no job references the credential. The sweep locks each stale claim row (`FOR UPDATE SKIP LOCKED`) from selection until the credential is deleted and the claim removed, in one transaction; a retry's claim waits on that row, then claims and rewrites the credential afresh, and a claim refreshed before the selection is not stale at all. A job therefore never ends up referencing a credential a sweep deleted. `PUT` is not idempotent. Jobs created before the migration have no key and stay valid. Other tenants' keys never match.

### Engine admission and SQL Server on Linux

A backup run is admitted only if the job's agent reported `<engine>.backup.full` (`mssql`, `postgresql`, `file-system`) as Supported; otherwise the start answers `409 agent_engine_unsupported` and no run is created. A Windows agent always reports SQL Server. A **Linux agent reports SQL Server only with a valid `Mapped` staging area**: `Agent__Backup__Staging__Mapping=Mapped`, an existing, writable `AgentDirectory`, and a `ServerDirectory` (the same directory as the SQL Server process sees it) that differs from it. `SameHost` staging on Linux does not advertise SQL Server. This is capability only; the pre-check at the start of every SQL Server run still proves the login and that the server really sees the staging path (`server-unreachable`, `authentication-failed`, `staging-mapping-mismatch`, ...) and fails the run fast without an artifact.

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

SFTP login and host-key failures are reported apart, in the connection test and in a run. The client sends no credentials until the server's key matches the pinned `hostKeyFingerprint`. A key that does not match gives the test code `host_key_mismatch` and the run failure code `StorageHostKeyMismatch` (reason `storage-host-key-mismatch`); a server that refuses the user, password or key gives `authentication_failed` and `StorageAuthenticationFailed` (reason `storage-authentication-failed`). Neither is retried, and neither message carries a credential, the fingerprint or the library's exception text. `PermissionDenied` stays for a missing permission (for example on the remote directory). `AuthenticationFailed` is the database login only.

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


### Scheduled upload bandwidth

Tenant administrators manage weekly profiles through
`/api/tenants/{tenantId}/bandwidth-profiles` and assign them using the upload-bandwidth
`tenant/profile`, `agents/{agentId}/profile`, or `jobs/{jobId}/profile` routes.
The most specific assignment wins: job, then agent, then tenant. A static ceiling
is a hard cap that a profile never lifts. The selected profile's current window
value is combined with the static ceiling of the same scope by taking the lower
one (outside its windows the static ceiling alone applies, or no limit when there
is none), and the static ceilings of the other scopes stay in force. A zero window
pauses the transfer whatever the static ceilings are.

Job and agent ceilings cap one transfer and the agent they belong to; the tenant
ceiling (static, profile, or the lower of both) is an aggregate shared by all
concurrent uploads of the tenant across its agents, split by the slice mechanism
whichever scope the profile is on. An agent runs one backup attempt at a time and
one run writes its destinations one after another, so the agent ceiling is also
the aggregate of that agent's uploads; a test fails if destinations start to
upload in parallel.

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

### Email notifications

Email notifications stay off until three things are set. `Notifications:PublicBaseUrl` (environment `Notifications__PublicBaseUrl`) is the absolute http(s) URL recipients reach the API at; confirmation and unsubscribe links are built from it, and without it the policy reports `transports.email.available=false`, channel tests answer `409 transport_not_configured` and deliveries fail with `public_url_not_configured`. The platform email transport (SMTP host, port, security, sender and optional login) must be saved by a PlatformAdmin under `/platform/notification-transports`; credentials require TLS (plain SMTP is accepted only without them). `Notifications:AllowedPrivateNetworks` (array of CIDR ranges, for example `Notifications__AllowedPrivateNetworks__0=192.168.0.0/16`) is needed whenever the SMTP relay or a webhook/SMS target sits on a private, loopback or link-local address: such addresses are refused with `network_denied` before any connection, and the list is empty by default.

### Feature flags

A feature that can be switched off is one line in `Features` (`src/core/contracts/Flags/FeatureFlags.cs`, shared by the API and the agent) with its default. The operator overrides it with configuration `Features:{key}` (environment `Features__{key}=true|false`). A disabled feature's routes are not registered (404), handlers on shared routes ask `FeatureFlags.IsEnabled`, and the UI reads every flag once at startup from the anonymous `GET /api/features`.

| Key | Default | Off means |
|---|---|---|
| `sms` | off | SMS transport, secrets and channels are hidden or rejected; queued deliveries wait until re-enabled |
| `supportSessions` | off | `/api/platform/support-sessions*` and `/api/tenants/{id}/support-access*` not registered; no UI entry |
| `smb` | off | SMB connections are hidden or rejected, new backup admissions and credential issuance are blocked, and SMB is not offered as a provider |
| `sizeAnomaly` | off | runs are not evaluated, the health summary lists none, the `backup_size_anomaly` event cannot be selected |
| `windowsVss` / `linuxSnapshots` | off | agent-side: the agent refuses to start with a file source whose `SnapshotKind` is `Vss` / `Lvm`, `Btrfs` or `Zfs` (set `Features__windowsVss=true` in the agent's environment). Windows VSS uses the native requester API and needs `NT SERVICE\BackupAgent` in Backup Operators (`Install-BackupAgentService.ps1 -EnableVss`, see `deploy/release/README-agent-windows.txt`); a missing membership is reported as `PermissionDenied`, other snapshot errors as `SnapshotFailed` / `InvalidConfiguration`, never as `StagingInvalid` |
| `externalSecrets` | off | `Secrets:Provider` other than `Database` (Vault, AWS Secrets Manager) stops the API from starting |
| `replicaRepair` | **on** | policy exception so multi-destination repair keeps working; off removes the manual repair routes and stops automatic repair |

Disabling SMB or SMS retains existing resources. Re-enabling SMS permits queued deliveries to resume; already-issued SMB credentials cannot be recalled by changing the flag.

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

For hard storage limits, admission reserves an estimate from the previous artifact.
After staging and hashing, the agent requests an atomic adjustment for the actual
artifact and its counted destinations before writing to any destination. If the
remaining capacity is insufficient, the run fails without writing a destination;
concurrent runs cannot reserve the same capacity.

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

S3 traffic that leaves the API process (the connection test, downloads the API
streams, retention deletes, verification and repair; agents upload directly) goes
through an address rule: the endpoint host is resolved once, every
answer must be public (loopback, private, link-local including `169.254.169.254`,
carrier-grade NAT, multicast and reserved space are refused, also when hidden in
an IPv4-mapped, NAT64 or 6to4 IPv6 address), and the connection is made to the
checked address, so a changing DNS answer cannot reach a private host. Redirects
and the environment proxy are not followed. A refused endpoint answers the test
with `not_testable` / `agent_side_storage` whether or not the port is open, since
the agent on the customer's network is the one that reaches a private endpoint.
`Backups:S3:AllowedPrivateNetworks` (array of CIDR ranges, for example
`Backups__S3__AllowedPrivateNetworks__0=192.168.0.0/16`) lets the API itself reach
an on-prem S3 service such as MinIO; it is empty by default. The connect step
ends after `Backups:S3:ConnectionTimeoutSeconds` (default 10) and a connection test
after 30 seconds, each with a `timeout` code. Agent-executed S3 transfers are not
restricted by this rule.

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
| PostgreSQL integration | `dotnet test tests/integration-tests -c Release --filter "FullyQualifiedName!~MsSql&FullyQualifiedName!~BackupPipelineSmoke&FullyQualifiedName!~StorageCapacityAgentHostSmokeTests" -- xUnit.MaxParallelThreads=4` | Docker and current/older client tools; see scripts README |
| MSSQL integration | `dotnet test tests/integration-tests -c Release --filter "FullyQualifiedName~IntegrationTests.MsSql\|FullyQualifiedName~BackupPipelineSmoke"` | Docker |
| Windows | `dotnet test tests/windows-tests -c Release` | Windows host |

**Frozen features' tests.** Tests of Windows VSS, Linux snapshots, SMS, backup size anomaly, external secrets, SMB and support sessions carry `[Trait("Category", "Frozen")]` and are left out of every `dotnet test` run: `Directory.Build.props` points all test projects at `tests/default.runsettings` (`Category!=Frozen`), and a `--filter` you pass is combined with it. Run them on demand with `scripts/integration/run-frozen-tests.sh`, or per project with `dotnet test tests/unit-tests -p:IncludeFrozenTests=true --filter "Category=Frozen"` (the Windows VSS test: `tests/windows-tests`, on Windows). Replica repair stays on by default, so its tests are not frozen. A new test of a frozen feature takes the same trait.

The integration fixtures start their own PostgreSQL 18 Alpine, SQL Server 2025 (plus a second one for the cross-instance restore row) and SFTP (`atmoz/sftp`) containers with Testcontainers, and the real-Agent.Host smokes publish the agent from this repository once per test run (`dotnet publish` without the Go direct-download helper, which these tests never use). Docker and the .NET SDK are the only prerequisites; no variable has to be set.

To run the real-agent tests against your own disposable SQL Server instead, set `BACKUP_PLATFORM_TEST_MSSQL_HOST`, `_PORT`, `_USER`, `_PASSWORD`, `_STAGING_SERVER_DIR` and `_STAGING_AGENT_DIR` together (the login must be sysadmin; SQL Server must write the staging directory and the agent must read and delete the same files through the agent-side path; these tests create and drop databases). The cross-instance restore row then also needs `BACKUP_PLATFORM_TEST_MSSQL_RESTORE_TARGET_PORT`, a second instance on the same host with the same login and staging mount. `BACKUP_PLATFORM_TEST_AGENT_HOST_PATH` points at an already published host instead of publishing one. For your own SFTP server set `BACKUP_PLATFORM_TEST_SFTP_HOST`, `_PORT`, `_USER`, `_PASSWORD` and `_FINGERPRINT` (`SHA256:` host key), and optionally `_BASE_DIR` (default `/upload`) and `_PRIVATE_KEY_FILE`.

On Linux, from the repository root, `scripts/database/with-disposable-databases.sh --restore -- ./scripts/integration/run-mssql-tests.sh --provider --agent --restore` provisions disposable servers through the variables above; see the scripts README for image prerequisites. A Docker host-port collision during Testcontainers startup is an environment bind failure, not a SQL authentication failure; identify the process or container occupying the reported host port before rerunning.

Other integration test variables:
- `BACKUP_PLATFORM_TEST_POSTGRES_TOOLS_DIRECTORY`: absolute directory containing runnable PostgreSQL 18 `pg_dump` and `pg_restore`; if unset, the process `PATH` is searched. Physical tests additionally need matching-major `pg_basebackup`, `pg_verifybackup`, and `pg_waldump`.
- `BACKUP_PLATFORM_TEST_POSTGRES_OLD_TOOLS_DIRECTORY`: required absolute directory containing older `pg_dump` and `pg_restore` for compatibility-refusal tests (PostgreSQL 16 clients work against the test server).
- Physical tests also require remote replication access for the test login in `pg_hba.conf`. Limit collection parallelism on high-core hosts to avoid exhausting the database connection limit.

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

## Upload stall detection

A storage that accepts connections but never answers raises no error of its own.
Every upload attempt therefore has a no-progress timer: bytes read from the
artifact or reported by the provider re-arm it, and when it fires the transfer
pauses as `PausedByNetworkError` (resumable, checkpoint kept). The default is two
minutes; set `Agent__Backup__UploadStallTimeout` (for example `00:05:00`) to
allow links that need longer for one storage chunk (one S3 part of at least
8 MiB). The run's own destination timeout (6 hours) still bounds the whole copy.

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
