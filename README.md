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
- **Scheduling**: cron schedules with explicit time zones, missed-run grace, skip/queue overlap policy. The database is the
  source of truth; Quartz.NET only wakes the backend.
- **Retry, cancellation, concurrency**: durable retry attempts on the same run, cooperative cancellation, global/tenant/agent
  concurrency limits enforced from the database, leases for agent work.
- **Local and Central storage**: backups are stored on the agent's disk (Local) or in the platform's central storage.
- **Retention**: per-job retention policy, soft delete with grace period, verified physical deletion by the backend or the owning agent,
  orphan and partial-upload sweeps.
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

`backup-backend/.env.example` documents every configuration key (`Backups__*`, `Agent__*`, test variables). It is documentation,
not a file the applications load.

## Testing

| Layer | Command (from `backup-backend/`) | Needs |
|---|---|---|
| Unit | `dotnet test tests/unit-tests -c Release` | nothing |
| Architecture (dependency rules) | `dotnet test tests/architecture-tests -c Release` | nothing |
| Agent | `dotnet test tests/agent-tests -c Release` | nothing (in-process Kestrel/SignalR); fake-clock tests synchronize on consumer checkpoints and timer deadlines, not real-time settling |
| Integration (PostgreSQL) | `dotnet test tests/integration-tests -c Release --filter "FullyQualifiedName!~MsSql&FullyQualifiedName!~BackupPipelineSmoke"` | `BACKUP_PLATFORM_TEST_CONNECTION_STRING` |
| Real MSSQL | `dotnet test tests/integration-tests -c Release --filter "FullyQualifiedName~IntegrationTests.MsSql\|FullyQualifiedName~BackupPipelineSmoke"` | PostgreSQL variable above plus the MSSQL variables below |
| Real MSSQL + published Agent.Host | `dotnet test tests/integration-tests -c Release --filter "FullyQualifiedName~AgentSmokeTests" -- xUnit.MaxParallelThreads=1` | everything above plus `BACKUP_PLATFORM_TEST_AGENT_HOST_PATH` |
| Windows platform (DPAPI, ACLs, reparse points) | `dotnet test tests/windows-tests -c Release` | Windows; elsewhere every test is reported as *skipped*, never passed |

Environment variables of the integration tests (values are yours; never commit them):

- PostgreSQL: `BACKUP_PLATFORM_TEST_CONNECTION_STRING`, an administrative connection. Every test run creates and drops its own
  uniquely named database.
- SQL Server: `BACKUP_PLATFORM_TEST_MSSQL_HOST`, `_USER` (sysadmin), `_PASSWORD`, optional `_PORT` / `_INSTANCE` /
  `_TRUST_SERVER_CERTIFICATE`, and the staging pair `BACKUP_PLATFORM_TEST_MSSQL_STAGING_SERVER_DIR` (the directory as SQL Server
  sees it) and `BACKUP_PLATFORM_TEST_MSSQL_STAGING_AGENT_DIR` (the same directory as the test process sees it).
- Agent.Host smoke: `BACKUP_PLATFORM_TEST_AGENT_HOST_PATH`, the published host:
  `dotnet publish src/hosts/agent -c Release -r linux-x64 --self-contained true -p:AgentPlatform=Linux -o <dir>`.

The real-MSSQL and PostgreSQL tests **fail** when their variables are missing; they never skip silently. Run the MSSQL provider
suites and the Agent.Host smokes as separate invocations (as CI does): they share one staging directory.
`.github/scripts/start-postgres.sh` and `start-mssql.sh` start the disposable containers CI uses and append the variables to the file
named by `$GITHUB_ENV`.

Connectivity and backup-worker tests drain explicitly tracked operations before advancing to the next fake timer deadline.
They do not depend on loop startup order or serial test collections. Credential/protocol restrictions apply to all three
connectivity loops; a success admitted before a newer rejection cannot clear that rejection. Real transport tests still
exercise Kestrel/SignalR and use bounded waits for observed network conditions.

## Continuous integration and releases

`ci.yml` runs on pull requests and on `main`: Linux build with warnings as errors plus unit, architecture and agent tests;
PostgreSQL integration tests; real SQL Server integration tests (Docker, shared staging directory); Windows build with agent and
Windows platform tests; frontend typecheck, lint, tests and build. `CI result` is the single job to require in branch protection.
Test results are uploaded as TRX/JUnit artifacts. Agent and integration jobs bound collection concurrency to two threads;
collections are not forced to run serially. The PostgreSQL job also generates the release's idempotent migration script
and applies it twice to a disposable database, checking that migration history is unchanged.

The scheduler runtime joins concurrent/repeated stop calls: reconciliation cancellation and cleanup finish before Quartz
shutdown, and Quartz jobs/callbacks finish before the owning provider is disposed. Lifecycle diagnostics are debug-only.
Shutdown intentionally waits for owned work to finish even if the host's graceful-shutdown deadline has elapsed.

Pushing a tag `vMAJOR.MINOR.PATCH[-prerelease]` runs the CI again and, if green, publishes a GitHub Release with
`SHA256SUMS.txt` and:

- `backup-agent-win-x64-<version>.zip` (self-contained Agent.Host, service install/uninstall scripts, config example)
- `backup-agent-linux-x64-<version>.tar.gz` (self-contained Agent.Host; there is **no installer or systemd unit** yet)
- `backup-backend-linux-x64-<version>.tar.gz` (self-contained API plus the idempotent PostgreSQL schema script)
- `backup-frontend-<version>.tar.gz` (static UI files; the backend does not serve them, put them on the same origin as `/api`)

The tag version is stamped into the assemblies (`-p:Version`), which is the software version every Agent reports in its heartbeat.
Running the **Release** workflow manually builds all archives as artifacts without publishing anything.

## Current MVP scope

Implemented:

- MSSQL full backup end to end (pre-check, backup, verify, checksums, storage, replica verification)
- Manual and scheduled runs, retry, cancellation, overlap policy, concurrency limits, agent leases
- Local and Central storage; retention with soft delete, grace period, agent-local deletion and sweeps
- Agent connectivity (polling, SignalR hints, heartbeat, capability sync), credential rotation, Windows Service hosting
- Multi-tenant backend with cookie authentication and antiforgery; React UI for the above

Not yet implemented:

- PostgreSQL backup provider (PostgreSQL is only the platform's own database)
- Restore of any kind
- Differential and transaction-log backups
- S3, SFTP or other remote storage providers
- Notifications; an audit trail module
- File-system and snapshot backup providers
- An Agent-side enrollment command in `Agent.Host` (the backend enrollment API and the UI exist; `Agent.Host` refuses to start
  until an identity is stored), a Linux installer/systemd unit, code signing of release binaries

## Security note

Never commit credentials, connection strings, tokens, certificates or local environment files. Supply secrets through environment
variables or a secret store; `.env*` files (except `.env.example`), `appsettings.*.local.json` and local-only folders are
git-ignored. SQL Server credentials for backups are injected into the Agent process as `BACKUP_AGENT_SECRET_<reference>` and are
never part of a job configuration. CI uses disposable containers with randomly generated, masked passwords.
