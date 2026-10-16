# Backup Platform

[![CI](https://github.com/mtsarac/backup-platform/actions/workflows/ci.yml/badge.svg)](https://github.com/mtsarac/backup-platform/actions/workflows/ci.yml)

A centralized backup platform for Microsoft SQL Server databases. An ASP.NET Core multi-tenant backend coordinates schedules and tracks runs, an agent runs alongside SQL Server on Windows or Linux, and a React interface manages jobs, runs, and artifacts.

## Architecture

```text
Web UI (React, same-origin /api)
  ↓
ASP.NET Core Backend ── Quartz.NET (timer triggers only)
  ↓
PostgreSQL
  ↕   outbound polling + SignalR hints
Backup Agent
  ↓
MSSQL
  ↓
Storage (Local on agent / Central / SFTP / Google Drive)
```

The agent polls the backend for work and renews execution leases. SignalR provides advisory availability hints so connectivity drops do not interrupt operations. Scheduling, concurrency limits, and state tracking rely entirely on PostgreSQL.

## Repository layout

```text
.github/          CI workflows and release packaging scripts
backup-backend/   ASP.NET Core API, Agent host, domain modules, providers, tests
backup-frontend/  Vite, React, and TypeScript web interface
```

## Requirements

| Component | Prerequisites |
|---|---|
| Backend and Agent | .NET 10 SDK |
| Database | PostgreSQL 18 (user needs `CREATEDB` for integration tests) |
| Frontend | [bun](https://bun.sh) 1.4+ |
| MSSQL backups and integration tests | SQL Server with a sysadmin account reachable over TCP (Docker works well) |
| Windows platform tests | Windows host (`net10.0-windows`) |

## Getting started

### Backend

Run from `backup-backend/`:

```bash
dotnet restore BackupPlatform.sln
dotnet build BackupPlatform.sln -c Release -p:ContinuousIntegrationBuild=true -warnaserror

dotnet tool restore
export BACKUP_PLATFORM_CONNECTION_STRING='<npgsql connection string>'
dotnet ef database update --project src/core/infrastructure
dotnet run --project src/hosts/api -- --bootstrap-user
dotnet run --project src/hosts/api
dotnet run --project src/hosts/agent
```

Configuration keys are documented in `backup-backend/.env.example`.

### Frontend

Run from `backup-frontend/`:

```bash
bun install --frozen-lockfile
bun run dev          # Proxies /api to BACKUP_API_PROXY_TARGET (default https://localhost:5001)
bun run typecheck
bun run lint
bun run test
bun run build
```

## Features

- MSSQL full backups with pre-checks, checksum verification, and multi-destination uploads (local disk, central storage, SFTP, Google Drive).
- MSSQL full restores with target safety checks (`WITH MOVE`), new-database mode, explicit overwrite confirmation, and cooperative cancellation.
- Cron schedules with time zones, missed-run grace periods, overlap policies, durable retry attempts, and database-level concurrency limits.
- Multi-destination storage management, quota limits, soft-delete retention grace periods, and cleanup sweeps.
- Authenticated native streaming downloads for tenant owners and admins with SHA-256 verification and retention holds during transfer.
- Outbound-only agent polling, one-time enrollment tokens, DPAPI-protected service identity on Windows, and user-scoped storage on Linux.

## Agent enrollment

1. In the web interface, open **Agents → Add Agent** and create a one-time enrollment token. The dialog shows ready-to-run commands for Windows and Linux in two modes. The language switch in the top bar also sets the installer language.
2. Run the command on the target machine.

   - **Guided** (interactive): omits the token. The installer asks for the server address, the enrollment token (hidden), and the agent name, shows a summary, and installs only after explicit confirmation.
   - **Automatic** (non-interactive): the command carries the token and never prompts. Missing or invalid values fail before anything is changed.

   ```powershell
   # Windows, elevated PowerShell
   & ([scriptblock]::Create((irm 'https://backup.example.com/api/agent-dist/install.ps1'))) -Interactive -Language en
   ```
   ```bash
   # Linux, root with systemd
   curl -fsS https://backup.example.com/api/agent-dist/install.sh | sudo bash -s -- --interactive --language en
   ```

   Windows installs the `BackupAgent` service under `NT SERVICE\BackupAgent` with a DPAPI-protected identity. Linux installs the `backup-agent.service` systemd unit under an unprivileged `backup-agent` account (identity in `/var/lib/backup-agent/identity`, binaries in `/opt/backup-agent`). Both refuse to replace an existing service, identity, or binary tree. The HTTPS certificate must already be trusted; validation is never disabled.
3. The web interface updates to Online once the agent connects and synchronizes capabilities.

The API serves the installers and the agent archives from `AgentDistribution__PackagesDirectory` (an absolute path holding `backup-agent-win-x64.zip` and `backup-agent-linux-x64.tar.gz`, both built by `.github/scripts/package-release.sh`; `scripts/dev/publish-agent-dist.sh` stages them for development). `AgentDistribution__PublicBaseUrl` is the public HTTPS address shown in the dialog. Without a package directory the installers report the package as unavailable.

Manual enrollment from an extracted release archive remains available through `BackupPlatform.Agent.Host enroll --server <url> --token <token> --non-interactive` (see the archive README). The installer scripts are not code-signed and do not verify package checksums beyond trusted HTTPS.

## Testing

Run tests from `backup-backend/`:

| Scope | Command | Requirements |
|---|---|---|
| Unit tests | `dotnet test tests/unit-tests -c Release` | None |
| Architecture rules | `dotnet test tests/architecture-tests -c Release` | None |
| Agent tests | `dotnet test tests/agent-tests -c Release` | None |
| Fast agent subset (CI) | `dotnet test tests/agent-tests -c Release --filter "FullyQualifiedName!~ConnectivityServiceTests" -- xUnit.MaxParallelThreads=2` | None |
| PostgreSQL integration | `dotnet test tests/integration-tests -c Release --filter "FullyQualifiedName!~MsSql&FullyQualifiedName!~BackupPipelineSmoke"` | `BACKUP_PLATFORM_TEST_CONNECTION_STRING` |
| Real MSSQL tests | `dotnet test tests/integration-tests -c Release --filter "FullyQualifiedName~IntegrationTests.MsSql\|FullyQualifiedName~BackupPipelineSmoke"` | PostgreSQL and MSSQL variables |
| Windows platform tests | `dotnet test tests/windows-tests -c Release` | Windows host |

Integration test environment variables:
- `BACKUP_PLATFORM_TEST_CONNECTION_STRING`: Administrative PostgreSQL connection string. Each test run creates and drops an isolated database.
- `BACKUP_PLATFORM_TEST_MSSQL_HOST`, `_USER`, `_PASSWORD`: SQL Server connection details.
- `BACKUP_PLATFORM_TEST_MSSQL_STAGING_SERVER_DIR`, `_AGENT_DIR`: Staging directories as seen by SQL Server and by the agent process.

## Continuous integration and releases

Fast CI runs on pull requests and pushes to `main`:
- `Backend`: Release build with warnings as errors, unit tests, architecture tests, and fast agent tests.
- `Frontend`: Frozen-lockfile install, typecheck, linter, unit tests, and production build.

Pushing a version tag (`vMAJOR.MINOR.PATCH`) builds release archives for the agent (Windows zip, Linux tarball), the backend API, and the static frontend bundle.

## Current scope

Supported:
- MSSQL full backup pipeline and verification
- MSSQL full restore with cross-instance and overwrite support
- Manual and scheduled jobs with overlap and concurrency controls
- Local disk, central storage, SFTP, and Google Drive storage destinations
- Multi-destination jobs and quota accounting
- Windows and Linux agent execution with outbound polling
- Multi-tenant API and React management UI

Planned:
- Additional database engines (PostgreSQL)
- Differential and transaction log backups
- S3 and object storage providers
- Alerting and notification webhooks
- Linux systemd service packages
