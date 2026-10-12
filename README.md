# Backup Platform

[![CI](https://github.com/mtsarac/backup-platform/actions/workflows/ci.yml/badge.svg)](https://github.com/mtsarac/backup-platform/actions/workflows/ci.yml)

A centrally managed backup platform. A multi-tenant ASP.NET Core backend schedules and tracks backup work, a
cross-platform **Agent** runs it next to the database, and a React UI manages everything. The first (and currently only)
backup source is **Microsoft SQL Server, full backups**.

## Project overview

- **Centralized management**: tenants, users, agents, backup jobs, runs, artifacts and replicas in one PostgreSQL-backed backend.
- **Agent architecture for Windows and Linux**: one `Agent.Host` with a Windows (DPAPI-protected identity, Windows Service)
  and a Linux platform implementation. The agent pulls work; the backend never connects into the customer network.
- **MSSQL Full Backup pipeline**: pre-check, `BACKUP DATABASE`, `RESTORE VERIFYONLY`, source checksum, artifact
  checksum, storage upload and replica verification, with result metadata stored per run.
- **MSSQL Full Restore**: verified artifact selection, staged transfer, checksum re-verification, typed backup inspection,
  target-safe `WITH MOVE`, new-name and explicit overwrite restores, progress, cancellation and tenant restore history through the API.
- **Scheduling**: cron schedules with explicit time zones, missed-run grace, skip/queue overlap policy. The database is the
  source of truth; Quartz.NET only wakes the backend.
- **Retry, cancellation, concurrency**: durable retry attempts on the same run, cooperative cancellation, global/tenant/agent
  concurrency limits enforced from the database, leases for agent work.
- **Local and Central storage**: backups are stored on the agent's disk (Local) or in the platform's central storage.
- **Storage connections**: every destination is a tenant-scoped `StorageConnection` (provider type, ownership `PlatformManaged`/`TenantManaged`, quota scope label,
  enabled flag, safe configuration, secret reference, provider capabilities). Central storage is the platform-managed "Managed Storage" connection; replicas, jobs and quota scope
  selectors (`all`, `providerType`, `ownershipType`, `quotaScope`, `storageConnection`) refer to connections. See [Storage connections](#storage-connections).
- **Retention**: per-job retention policy, soft delete with grace period, verified physical deletion by the backend or the owning agent,
  orphan and partial-upload sweeps.
- **Secure backup download**: tenant Owner/Admin-only, user-bound sessions, native browser streaming, SHA-256 validation, and expiring retention holds.
- **Multi-tenant backend**: tenant-scoped API and data, tenant roles (Owner/Admin/Operator/Viewer) plus a separate platform administrator.
- **React management UI**: dashboard, agents and enrollment, backup jobs (creation wizard), runs, artifacts.

Features that are not built yet are listed under [Current MVP scope](#current-mvp-scope).

## Architecture

```text
Web UI (React, same-origin /api)
  ↓
ASP.NET Core Backend ── Quartz.NET (wake-up only)
  ↓
PostgreSQL
  ↕   authoritative polling + SignalR hints
Backup Agent
  ↓
MSSQL
  ↓
Storage (Local on the agent / Central)
```

The agent polls the backend for work and renews its lease; SignalR is only a best-effort "work available" hint, so
correctness never depends on it. Scheduling decisions (due occurrences, grace, overlap) are made from PostgreSQL state,
never from process memory, so several backend nodes can run.

## Repository structure

```text
.github/
  workflows/ci.yml, release.yml    CI and tag-driven release
  scripts/                         CI helpers (disposable PostgreSQL / SQL Server, TRX report, release packaging)
  dependabot.yml, pull_request_template.md
backup-backend/
  BackupPlatform.sln               Directory.Build.props, Directory.Packages.props, dotnet-tools.json
  .env.example                     every backend/agent/test setting, documented (no values)
  src/core/                        domain, application, contracts, infrastructure (EF Core / PostgreSQL)
  src/modules/                     identity, tenancy, agents, backups, storage (scheduling, audit, notifications: placeholders)
  src/agent/                       core, transport, platform-windows, platform-linux
  src/providers/                   backup (abstractions, mssql), storage (abstractions, local, central), secret, snapshot
  src/hosts/                       api (ASP.NET Core backend), agent (Agent.Host)
  tests/                           unit, architecture, agent, integration, windows-tests, windows-test-probe
  deploy/                          windows service install scripts, release packaging templates
backup-frontend/                   Vite + React + TypeScript UI (bun)
```

## Requirements

| For | You need |
|---|---|
| Backend and Agent | .NET 10 SDK |
| Running the backend | PostgreSQL (CI uses 18). The login needs `CREATEDB` only for the integration tests |
| Frontend | [bun](https://bun.sh) 1.4+ (`bun.lock` is the lockfile) |
| MSSQL backups and the real-MSSQL tests | SQL Server reachable over TCP with a sysadmin login; Docker is one convenient way |
| Windows platform tests | a Windows machine (`net10.0-windows`); some tests need an elevated session or symlink privilege |

## Development

Backend (from `backup-backend/`):

```bash
dotnet restore BackupPlatform.sln
dotnet build BackupPlatform.sln -c Release -p:ContinuousIntegrationBuild=true -warnaserror   # the CI build

dotnet tool restore                                                   # dotnet-ef
export BACKUP_PLATFORM_CONNECTION_STRING='<npgsql connection string>'  # never commit this
dotnet ef database update --project src/core/infrastructure           # create/upgrade the schema
dotnet run --project src/hosts/api -- --bootstrap-user                 # one-time first administrator, see .env.example
dotnet run --project src/hosts/api                                     # the backend
dotnet run --project src/hosts/agent                                   # an Agent (needs a stored identity, see below)
```

Frontend (from `backup-frontend/`):

```bash
bun install --frozen-lockfile
bun run dev          # Vite dev server; proxies /api to BACKUP_API_PROXY_TARGET (default https://localhost:5001,
                     # BACKUP_API_PROXY_INSECURE=true for a self-signed certificate)
bun run typecheck
bun run lint
bun run test
bun run build
```

`backup-backend/.env.example` documents every configuration key (`Backups__*`, `Restores__*`, `Agent__*`, test variables). It is documentation,
not a file the applications load.

## MSSQL restore

Restore is a separate `RestoreOperation` / `RestoreAttempt`, not a backup run, and produces no artifact. The target agent executes
the restore using its existing credential-reference resolver; SQL credentials never enter persisted tasks. No automatic restore retry
is performed: a failed or partially restored target requires an explicit new decision.

| Tenant API (prefix `/api/tenants/{tenantId}`) | Access / behavior |
|---|---|
| `POST /backup-artifacts/{artifactId}/restore` | Admin/Operator; `Idempotency-Key` required; 202 for a new operation, 200 for an identical replay, 409 for a changed request using the same key |
| `GET /restores` | Viewer or higher; paginated history, filters `status`, `artifactId`, `agentId` |
| `GET /restores/{restoreId}` | Viewer or higher; attempts, progress, file mappings, inspection, warnings and resulting database state |
| `POST /restores/{restoreId}/cancel` | Admin/Operator; pending work ends immediately; running work receives cooperative SQL cancellation through lease renewal |

Browser writes also require the existing antiforgery token. A new restore request can be:

```json
{
  "agentId": "<target-agent-id>",
  "target": {
    "host": "sql-target.example",
    "port": 1433,
    "databaseCredentialReference": "restore_sql"
  },
  "databaseName": "RecoveredDatabase"
}
```

`mode` defaults to `NewDatabase`, which refuses an existing target. Overwriting requires
`mode: "ReplaceExistingDatabase"` and `confirmOverwrite` equal to the requested database name. Disconnecting target sessions additionally
requires `options.terminateActiveConnections: true`. System databases and protected HA targets are refused.

The backend selects an eligible replica, or the caller supplies `replicaId`. Deleted replicas cannot be restored; verified
`DeletePending` replicas remain eligible within their existing retention grace period. The operation pins its chosen replica from
creation through terminal completion. Replica-row locks, deletion-claim rechecks and PostgreSQL triggers prevent physical deletion
while a pin is active. Backup and restore claims never run concurrently on the same agent, even when the backup agent limit is raised.

Before agent hand-off, the backend probes sources it can read and can atomically move the restore pin to another eligible replica.
This does not verify the physical checksum: the agent still verifies the staged bytes before touching the target. Agent-local files
and Central storage mounted only on the agent remain the agent's responsibility. Once a restore is handed out, it never switches
source or starts a second restore automatically. Disabled storage connections remain readable for already-stored backups.

The agent streams the replica into its configured restore staging, keeping `AgentPath` separate from the SQL-visible `DatabaseServerPath`.
An agent-local replica must belong to the executing agent. Central storage must actually be mounted/configured on that agent;
backend accessibility alone does not make a SQL-visible path. Cross-agent local transfer is explicitly refused as
`agent_transfer_required`, not silently assumed available. Cross-instance restore is supported when the executing agent can reach
the source storage and the target SQL Server can read its configured staging.

Checksum re-verification and `RESTORE VERIFYONLY` default to enabled. `options.verifyChecksum` and `options.verifyBackup` can disable
them explicitly; the result records both verification flags and warns when checksum verification is disabled. A changed physical
checksum stops execution before the target is touched. Typed
`HEADERONLY` / `FILELISTONLY` metadata drives version checks and one `WITH MOVE` destination per logical data/log file.
Generated destinations use the target's own data/log directories, never source physical paths; replacement can reuse matching files
already owned by the target. Header start/end dates retain the source server's wall-clock time without inventing a target timezone;
operation and attempt timestamps remain UTC-aware. Multi-set, differential/log, explicitly encrypted backup sets and
FILESTREAM/memory-optimized/full-text file layouts are not supported.

Cancellation is not a health guarantee. Results distinguish `NotModified`, `Online`, `PartiallyRestored` and `Unknown`, and retain
the observed SQL database state. A database left `RESTORING` needs an explicit replacement restore or manual removal; it is never
silently dropped. A failed/cancelled replace attempts to undo a `SINGLE_USER` change in `finally`, and reports any recovery failure.
A running attempt whose lease is lost reports `Unknown` regardless of its last advisory progress report. Staged copies are cleaned
on success, failure and cancellation. The SQL Server service account must be able to read files created by the agent in staging:
configure appropriate directory/file ACLs (and Unix group/umask), not just agent write access.

Backend policy is configured with `Restores__LeaseDuration` (default 120 seconds), `Restores__UnclaimedExpiry` (one hour) and
`Restores__SweepInterval` (15 seconds). Unclaimed expired requests release their pins rather than unexpectedly restoring much later.
The Restore Wizard uses the same source eligibility, precheck and explicit overwrite contracts as the API.

## Automatic replica repair

Discovery schedules missing destinations from the backup run's stored configuration, globally or for one tenant. Retryable work
stays Pending with its retry schedule; terminal, cancelled and exhausted repair history is never automatically restarted.
Manual and automatic repairs share database-enforced limits of four InProgress operations globally and two per tenant.
Both claim paths serialize admission across backend nodes; expired InProgress leases keep their slots until recovery.
Waiting operations stay Pending without consuming attempts, and a saturated tenant does not block another tenant's due work.
The chosen backend source is durably pinned under a live fenced lease before opening it. Repair verifies source SHA-256 and
destination readback, and never deletes an existing destination object that rejected the upload with AlreadyExists.
Repair quota checks belong to the managed-service admission policy and count only additional bytes within the configured scope; they do not reserve repair capacity.

Apply the `ReplicaFallbackPinning` migration before running replica fallback: its PostgreSQL guards permit only live, pre-transfer
download source changes, reject restore source changes after hand-off, and revalidate moved restore pins.

## Secure backup download

All download routes require an **Owner or Admin membership in the requested tenant**. Operator, Viewer, and a standalone
PlatformAdmin grant cannot download. Sessions are bound to their requesting user and artifact; knowing a session URL grants
no access, and browser writes use the existing antiforgery token.

| API suffix below `/api/tenants/{tenantId}/backup-artifacts/{artifactId}` | Behavior |
|---|---|
| `GET /download-availability` | Per-replica availability with stable reasons, expected artifact SHA-256 and size |
| `POST /downloads` | Optional `{ "replicaId": "..." }`; returns the session, native download URL, safe filename, checksum and size |
| `GET /downloads/{sessionId}` | `Preparing`, `Ready`, `Streaming`, `Completed`, `Failed` or `Cancelled`, failure code and transferred bytes |
| `GET /downloads/{sessionId}/content` | Single-use, authenticated native attachment stream; no JavaScript Blob buffering |

New sessions accept only matching, verified, **Available** replicas: `DeletePending`, `Deleting`, failed deletion and deleted
replicas are refused. Central reads use the existing optional `storage.read` provider capability and tenant-bound configured
storage resolver. The backend verifies an opened seekable file before response headers and hashes again while transferring;
corrupt bytes can never produce a successful `Completed` session. A generated `backup-{artifactId}.bak` filename never uses
persisted filesystem paths or browser input.
For non-seekable sources, the final byte is withheld until exact EOF and SHA-256 verification. A corrupt, short or oversized
source therefore cannot finish a successful response with the declared Content-Length: it returns an error before streaming,
or aborts an incomplete body afterward. A corrupt prefix may already have been sent; clients must reject incomplete downloads.
`TransferredBytes` counts bytes actually written, not bytes read for verification. Streaming uses one pooled 64 KiB buffer,
without a whole-file memory buffer or an additional disk spool.

Before headers are sent, an open failure or seekable checksum mismatch can fall back to another matching, verified, Available
backend replica. The live session's hold switches atomically before the alternative is opened; tenant, user, checksum and size
remain unchanged. A prepared session may still read its held DeletePending copy, but it cannot acquire a new DeletePending source.
Failures after response bytes begin abort the HTTP transfer rather than concatenate or restart it.

Agent-local bytes use the existing outbound agent polling and authenticated callback architecture, not an inbound connection
to the customer host. A capable owning agent prepares a stream; the backend spools it to a private temporary file with bounded
memory and independently verifies length and SHA-256 before marking the session `Ready`. Browser status polling precedes the
native attachment request. This is a separate download transport: restore still reads the provider into agent staging, and
cross-agent restore transfer is not implied.

Each durable session also holds its chosen replica against physical retention deletion. Creation serializes on artifact and
replica row locks; both backend and agent delete claims recheck live download holds alongside restore pins. Unused sessions expire
after 15 minutes, active preparation/streaming uses a 60-second renewable lease, and an expiry worker releases abandoned holds.
Disconnected browsers cancel the stream and clean temporary resources; lost process leases fail cleanly instead of retaining
bytes forever. A restarted process cannot resurrect an unverified or missing temporary local stream.

Request/start/terminal lifecycle events are written atomically with their session changes to the append-only audit trail.
Events are grouped by `sessionId` and identify the tenant and requesting user. `download.streaming` records `startedAt`;
terminal events record `completedAt`, `result`, `transferredBytes` and a safe failure code. The event timestamps define the
session timeline without a second mutable audit record. Audit failure prevents transfer authorization; renewal, transfer,
expiry and cleanup failures are operationally logged using safe codes and exception types, never storage paths or secrets.
Streaming awaits downstream writes for backpressure and uses bounded buffers. Downloads neither reserve quota nor change
artifact usage accounting, backup execution, restore execution or the job's retention policy.


## Testing

| Layer | Command (from `backup-backend/`) | Needs |
|---|---|---|
| Unit | `dotnet test tests/unit-tests -c Release` | nothing |
| Architecture (dependency rules) | `dotnet test tests/architecture-tests -c Release` | nothing |
| Agent | `dotnet test tests/agent-tests -c Release` | nothing (in-process Kestrel/SignalR); fake-clock tests synchronize on consumer checkpoints and timer deadlines, not real-time settling |
| Fast agent subset | `dotnet test tests/agent-tests -c Release --filter "FullyQualifiedName!~ConnectivityServiceTests" -- xUnit.MaxParallelThreads=2` | nothing; this is the agent subset used by Fast CI |
| Connectivity acceptance | `dotnet test tests/agent-tests -c Release --filter "FullyQualifiedName~ConnectivityServiceTests"` | nothing; local/full acceptance only, not a routine PR gate |
| Integration (PostgreSQL) | `dotnet test tests/integration-tests -c Release --filter "FullyQualifiedName!~MsSql&FullyQualifiedName!~BackupPipelineSmoke"` | `BACKUP_PLATFORM_TEST_CONNECTION_STRING` |
| Real MSSQL | `dotnet test tests/integration-tests -c Release --filter "FullyQualifiedName~IntegrationTests.MsSql\|FullyQualifiedName~BackupPipelineSmoke"` | PostgreSQL variable above plus the MSSQL variables below |
| Real MSSQL + published Agent.Host | `dotnet test tests/integration-tests -c Release --filter "FullyQualifiedName~AgentSmokeTests" -- xUnit.MaxParallelThreads=1` | everything above plus `BACKUP_PLATFORM_TEST_AGENT_HOST_PATH` |
| Local restore acceptance | `dotnet test tests/integration-tests -c Release --filter "FullyQualifiedName~MsSqlRestoreAcceptanceTests" -- xUnit.MaxParallelThreads=1` | PostgreSQL, published Agent.Host, two SQL instances with the same test login and shared staging; target port below |
| Windows platform (DPAPI, ACLs, reparse points) | `dotnet test tests/windows-tests -c Release` | Windows; elsewhere every test is reported as *skipped*, never passed |

Environment variables of the integration tests (values are yours; never commit them):

- PostgreSQL: `BACKUP_PLATFORM_TEST_CONNECTION_STRING`, an administrative connection. Every test run creates and drops its own
  uniquely named database.
- SQL Server: `BACKUP_PLATFORM_TEST_MSSQL_HOST`, `_USER` (sysadmin), `_PASSWORD`, optional `_PORT` / `_INSTANCE` /
  `_TRUST_SERVER_CERTIFICATE`, and the staging pair `BACKUP_PLATFORM_TEST_MSSQL_STAGING_SERVER_DIR` (the directory as SQL Server
  sees it) and `BACKUP_PLATFORM_TEST_MSSQL_STAGING_AGENT_DIR` (the same directory as the test process sees it).
- Agent.Host smoke: `BACKUP_PLATFORM_TEST_AGENT_HOST_PATH`, the published host:
  `dotnet publish src/hosts/agent -c Release -r linux-x64 --self-contained true -p:AgentPlatform=Linux -o <dir>`.
- Cross-instance restore acceptance additionally requires `BACKUP_PLATFORM_TEST_MSSQL_RESTORE_TARGET_PORT`: a second disposable
  SQL instance on the same test host, using the same test login/password and SQL-visible staging mount. The acceptance backs up
  a disposable source, deletes it, restores under a different name, verifies exact rows and ONLINE/MULTI_USER, explicitly overwrites
  the disposable target, then proves checksum mismatch prevents another restore. This local-only acceptance is not added to CI.

The real-MSSQL and PostgreSQL tests **fail** when their variables are missing; they never skip silently. Run the MSSQL provider
suites and the Agent.Host smokes as separate invocations (as CI does): they share one staging directory.
`.github/scripts/start-postgres.sh` and `start-mssql.sh` start the disposable containers CI uses and append the variables to the file
named by `$GITHUB_ENV`.

Connectivity and backup-worker tests drain explicitly tracked operations before advancing to the next fake timer deadline.
They do not depend on loop startup order or serial test collections. Credential/protocol restrictions apply to all three
connectivity loops; a success admitted before a newer rejection cannot clear that rejection. Real transport tests still
exercise Kestrel/SignalR and use bounded waits for observed network conditions.

## Agent onboarding

1. A tenant **Owner or Admin** opens **Agents → Add Agent**, selects Windows or Linux, and creates a one-time token.
   PlatformAdmin alone does not grant tenant enrollment permission; Operator and Viewer cannot create tokens.
2. Prepare the published Agent files on the target machine. The frontend shows the real command and token expiry;
   it does not provide an Agent download endpoint. Use the executable folder (`bin/` in release archives).
3. Run the displayed command against a trusted HTTPS backend. On Windows, use an elevated terminal and an
   administrator-controlled permanent folder:

   ```powershell
   .\BackupPlatform.Agent.Host.exe enroll --server 'https://backup.example.com' --token '<one-time-token>' --non-interactive
   ```

   This invokes the existing Windows installer, enrolls under `NT SERVICE\BackupAgent`, and starts the delayed-auto
   service. The permanent identity is DPAPI CurrentUser protected for that service account, not the interactive user.
   No credential copying or database edits are required.

   On Linux, run both commands as the same Unix account:

   ```bash
   ./BackupPlatform.Agent.Host enroll --server 'https://backup.example.com' --token '<one-time-token>' --store-directory "$HOME/.local/share/backup-agent" --non-interactive
   ./BackupPlatform.Agent.Host --store-directory "$HOME/.local/share/backup-agent"
   ```

   The store is owner-only (directory 0700, files 0600); the second command runs in the foreground. There is no
   bundled systemd installer. `enroll` without automation options also supports interactive input with a hidden token.
4. The wizard monitors token consumption and the linked Agent, then shows Online after heartbeat and capability sync.
   Token plaintext is creation-only and is cleared on consumption, expiry, revocation or closing the wizard; it is
   never put in browser storage. Monitoring is bounded and can be retried, rather than polling indefinitely.

Enrollment refuses an existing identity and preserves it on failure. Windows uninstall defaults to retaining the
identity and saved server for reinstall; `Uninstall-BackupAgentService.ps1 -RemoveIdentity` (alias `-PurgeIdentity`)
explicitly removes the local identity. Reinstall with `Install-BackupAgentService.ps1 -BinaryDirectory <published-folder>`
without a token to verify and reuse the same AgentId. Local purge does not revoke the backend credential or delete its
Agent record. TLS certificate validation is always enabled; do not disable it to make installation succeed.

## Continuous integration and releases

`ci.yml` (**Fast CI**) runs on pull requests and pushes to `main`, with only two Linux jobs:

- **Backend:** restore, strict Release build, unit tests, architecture tests, and the fast agent subset excluding
  `ConnectivityServiceTests`.
- **Frontend:** frozen-lockfile install, typecheck, lint, tests and production build.

Require the **Backend** and **Frontend** checks in branch protection; the old `CI result` check no longer exists.
Superseded PR runs are cancelled, while `main` runs are allowed to finish. NuGet and bun caches use the official cache
action. Fast CI starts no database containers or Windows runners and uploads no test artifacts.

Heavy acceptance is manual and is not required for ordinary PRs:

- `integration.yml` (**Full Integration**): PostgreSQL integration, the idempotent migration script applied twice,
  real MSSQL provider/BackupPipelineSmoke tests, then published Agent.Host smokes in a separate invocation.
- `windows.yml` (**Windows Validation**): strict Windows build and Windows platform tests.

These workflows run only through `workflow_dispatch`; failure diagnostics are retained for three days, and successful
test runs upload no artifacts. Connectivity tests remain available through the local commands above; they are not silently
retried or marked as passed. Windows VM/service-account acceptance remains manual.

Pushing a tag `vMAJOR.MINOR.PATCH[-prerelease]` runs Fast CI and, if green, publishes a GitHub Release with
`SHA256SUMS.txt` and:

- `backup-agent-win-x64-<version>.zip` (self-contained Agent.Host, service install/uninstall scripts, config example)
- `backup-agent-linux-x64-<version>.tar.gz` (self-contained Agent.Host; there is **no installer or systemd unit** yet)
- `backup-backend-linux-x64-<version>.tar.gz` (self-contained API plus the idempotent PostgreSQL schema script)
- `backup-frontend-<version>.tar.gz` (static UI files; the backend does not serve them, put them on the same origin as `/api`)

The tag version is stamped into the assemblies (`-p:Version`), which is the software version every Agent reports in its heartbeat.
Running the **Release** workflow manually builds all archives as artifacts without publishing anything.
Manual workflow dispatch becomes available after the workflow files exist on the repository's default branch. Before a
release, run Full Integration and Windows Validation against the intended release commit when that acceptance is needed;
they are intentionally not automatic release gates.

## Storage connections

Every tenant owns two system default connections, created by the migration and on demand: **Local** (agent disk, tenant-managed, scope `tenant`) and **Managed Storage**
(the Central provider, platform-managed, scope `managed`). Existing replicas were assigned to them by the upgrade, so downloads, restores, retention and quota are unchanged.
SFTP and Google Drive are backend-relayed tenant-managed providers, with pinned SFTP host identity and tenant/connection-bound Drive object ownership.

| API | Access / behavior |
|---|---|
| `GET /api/tenants/{tenantId}/storage-providers`, `/storage-connections[/{id}]` | Viewer or higher; own tenant only (another tenant's id is `404`); every view carries server-computed `permissions` |
| `POST/PUT/DELETE /api/tenants/{tenantId}/storage-connections[/{id}]`, `PUT/DELETE .../secret`, `POST .../test`, `POST .../enable`, `.../disable` | Owner/Admin, tenant-managed connections only |
| `GET /api/platform/storage-connections` (+ per tenant, per id) | PlatformAdmin; secret-safe summaries of every tenant's connections |
| `POST /api/platform/tenants/{tenantId}/storage-connections`, `PUT/DELETE/test/secret` on platform-managed ones | PlatformAdmin manages managed targets; for tenant-managed ones only enable/disable and the quota scope label |

Credentials are written only through the secret provider (`ISecretStore`, ASP.NET Data Protection over a ciphertext table) and a response, audit event or log never contains a
value. Create, update, test, enable/disable, secret rotation/removal, quota scope changes and deletion are audited (`storage_connection.*`).
**Disabling** a connection blocks new manual and scheduled runs that target it (`409 admission_denied`, code `storage_connection_disabled`); runs already admitted finish, and
every replica already stored on it stays Available, counted in usage, downloadable, restorable and subject to retention. A new provider adds a descriptor
(`StorageProviderDescriptor`) and a connection tester; the quota engine, views and permissions need no provider-specific code.

Managed Storage reuses the existing Central root: configure `Backups__Storage__Central__RootDirectory` and
`Backups__Storage__Central__TenantScopes__<tenantId>` on the backend, with the matching
`Agent__Backup__CentralStorage__RootDirectory` / `TenantScope` on the Agent. Physical paths stay operator configuration, not tenant API fields.
Local and Central each allow one connection per tenant until distinct physical destinations are supported.

Persist and back up `Secrets__DataProtection__KeysDirectory`; backend nodes that share ciphertext must share the key ring.
Losing the keys makes existing credentials unreadable. API responses expose only secret presence, field names and the last update time.

Run `scripts/integration/run-storage-connections-smoke.sh` for real Agent/MSSQL acceptance: Central backup, connection/isolation checks,
migration Down/Up with the stored replica, checksum-verified download, SQL-verified restore, disabled-target access and re-enabled backup.
Tenant-managed enable/disable has no platform-override marker yet: an authorized tenant can re-enable a connection disabled by PlatformAdmin.

## Multi-storage quota and orphan recovery

Finite `BlockNewBackups` quotas fail closed for a counted job with no size history (`409 quota_size_unknown`).
History supplies the artifact size; the current destination snapshot supplies the counted copies: once for logical accounting, every scoped destination for physical accounting.
Hard-quota execution requires `backup.quota-bounded-upload`; the task carries `maxArtifactBytes`, and the Agent refuses an oversized artifact before writing any destination.
The backend also fences relay upload lengths. Reservations remain live across pending runs, retries and long transfers, and convert to occupied bytes without double charging.

Bootstrapping a new counted job requires an administrator's explicit, controlled use of an existing WarnOnly policy or unlimited subscription override to establish history,
then restoration of the intended hard policy. The platform never weakens the policy automatically. Growth beyond the reserved historical size fails before upload.

Before a remote PUT, the backend durably records its tenant/attempt/destination/key and conservative expected bytes as an unverified cleanup-bound replica.
Failed completion preserves those records even when no destination verifies; successful completion preserves good replicas and retains unresolved objects for cleanup.
Unverified replicas are never download/restore sources or cancellable deletions. Existing retention leases, retry/backoff and PostgreSQL upload locks recover cleanup after restart
without deleting beside an active remote writer; bytes fall only after verified absence. SFTP cleanup checks both the final key and its deterministic `.partial` sibling.
Drive stores the owned file ID in its durable catalog before readback verification, so corrupt objects can be deleted by identity without being promoted to Verified.

`Succeeded` still means a usable backup was produced, not that replication policy was satisfied. The dashboard labels that KPI explicitly and independently alerts on
`replicationInsufficientLast24Hours`; completed destination outcomes keep historical execution compliance independent of later retention.
There is still no aggregate transfer deadline: at most 16 sequential destinations each retain the existing 6-hour timeout, with independent lease renewal and cooperative cancellation.


## Current MVP scope

Implemented:

- MSSQL full backup end to end (pre-check, backup, verify, checksums, storage, replica verification)
- MSSQL full restore through the API and real Agent execution, including cross-instance/new-name/explicit replace, retention pins and safe staging
- Manual and scheduled runs, retry, cancellation, overlap policy, concurrency limits, agent leases
- Local, Central, SFTP and Google Drive storage; multi-destination execution, hard-quota admission and durable orphan recovery; retention with verified deletion
- Agent connectivity (polling, SignalR hints, heartbeat, capability sync), credential rotation, Windows Service hosting
- Real Agent enrollment CLI, Windows service-context provisioning/install/start, Linux owner-only enrollment and Add Agent onboarding wizard
- Multi-tenant backend with cookie authentication and antiforgery; React UI for backup management (restore is currently API-only)

Not yet implemented:

- PostgreSQL backup provider (PostgreSQL is only the platform's own database)
- Differential/log-chain restore, point-in-time recovery, automated restore testing and PostgreSQL restore
- Differential and transaction-log backups
- S3 or other additional remote storage providers
- Notifications; an audit trail module
- File-system and snapshot backup providers
- A Linux installer/systemd unit and code signing of release binaries

## Security note

Never commit credentials, connection strings, tokens, certificates or local environment files. Supply secrets through environment
variables or a secret store; `.env*` files (except `.env.example`), `appsettings.*.local.json` and local-only folders are
git-ignored. SQL Server credentials for backups are injected into the Agent process as `BACKUP_AGENT_SECRET_<reference>` and are
never part of a job configuration. CI uses disposable containers with randomly generated, masked passwords.
