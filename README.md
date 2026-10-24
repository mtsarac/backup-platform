# Backup Platform

[![CI](https://github.com/mtsarac/backup-platform/actions/workflows/ci.yml/badge.svg)](https://github.com/mtsarac/backup-platform/actions/workflows/ci.yml)

A multi-tenant backup platform for Microsoft SQL Server and PostgreSQL. A central backend schedules jobs and tracks runs, an agent on the database host does the actual backup and restore, and a web UI manages it all.

```text
Web UI (React)  ──►  Backend API (ASP.NET Core)  ──►  PostgreSQL
                          ▲
                          │ outbound polling + SignalR hints
                       Agent (Windows or Linux)  ──►  MSSQL / PostgreSQL
                          │
                          ▼
        Storage: agent disk, central, SFTP, Google Drive
```

The agent only connects outward. It polls the backend for work and renews a lease while a run is active. SignalR is a hint to poll sooner, never a requirement. Scheduling, concurrency limits, and run state live in the backend's PostgreSQL database.

## What it does

- **Backup and restore** for MSSQL (full backup, restore to a new or existing database) and PostgreSQL (`pg_dump` custom archive). Every backup is checksummed and read back before it counts as verified.
- **Scheduling** with cron expressions and time zones, overlap policies, retries, cancellation, and per-agent concurrency limits.
- **Storage** on the agent's disk, central storage, SFTP, or Google Drive, with several destinations per job, replica health, quotas, and retention.
- **Discovery** of databases on an agent host, including Docker containers.
- **Restore verification** into a disposable database, with an integrity check before cleanup.
- **Downloads** of verified artifacts for tenant owners and admins, checked against SHA-256.
- **Notifications** by email, webhook, and SMS. SMS has not been tested against a real provider.
- **Upload bandwidth limits** per agent, tenant, or job.
- **Agent install** with one command on Windows (service) or Linux (systemd).
- **Multi-tenant access** with Owner, Admin, Operator, and Viewer roles, a separate platform admin, support access, and an audit log. The UI is available in Turkish and English.

Storage connections are added through a guided provider, name, settings, credentials and review flow; steps that do not apply are skipped. Credentials are write-only and cleared when submitted, even if saving fails. Paste multiline private keys into their masked field; their line breaks are preserved, and the clear action removes the whole entry. Saving and testing are separate operations; Google Drive asks for account authorization in its popup before testing. The review keeps connection internals in collapsed, copyable technical details.

## Repository layout

```text
backup-backend/   .NET solution: API, agent, domain modules, providers, tests
backup-frontend/  Vite, React, and TypeScript web UI
scripts/          Local development and test automation
.github/          CI workflows and release packaging
docker-compose.yml  Local stack: PostgreSQL, SQL Server, API, frontend
```

Each of `backup-backend/` and `backup-frontend/` has its own README with the details.

## Quick start

You need the .NET 10 SDK, [bun](https://bun.sh) 1.4+, PostgreSQL 18, and for MSSQL backups a SQL Server you can reach over TCP.

Everything in containers:

```bash
docker compose up --build
```

Set `BOOTSTRAP_ADMIN_PASSWORD` and `PUBLIC_HOST` in your environment first. The first platform admin is created from `BOOTSTRAP_ADMIN_LOGIN` and `BOOTSTRAP_ADMIN_PASSWORD`.

Or run the pieces yourself:

```bash
# backend, from backup-backend/
dotnet tool restore
export BACKUP_PLATFORM_CONNECTION_STRING='<npgsql connection string>'
dotnet ef database update --project src/core/infrastructure
dotnet run --project src/hosts/api -- --bootstrap-user
dotnet run --project src/hosts/api

# frontend, from backup-frontend/
bun install --frozen-lockfile
bun run dev
```

`scripts/dev/start.sh` brings up a full persistent dev stack, including an agent. It reads private credentials from `docs/test-cred.yaml`, which is git-ignored. See [scripts/README.md](scripts/README.md).

## Connecting an agent

1. In the UI, open **Agents → Add Agent** and create a one-time enrollment token. The dialog shows the install commands.
2. Run one on the target machine, for example:

   ```powershell
   # Windows, elevated PowerShell
   & ([scriptblock]::Create((irm 'https://backup.example.com/api/agent-dist/install.ps1'))) -Interactive -Language en
   ```
   ```bash
   # Linux, root with systemd
   curl -fsS https://backup.example.com/api/agent-dist/install.sh | sudo bash -s -- --interactive --language en
   ```

   Interactive mode asks for the server address, token, and agent name, then confirms before installing. The automatic mode carries the token and never prompts.
3. The agent shows as Online once it connects and reports its capabilities.

On Windows the agent runs as the `BackupAgent` service with a DPAPI-protected identity. On Linux it runs as the `backup-agent` systemd unit under an unprivileged account. Both installers refuse to overwrite an existing install, and the server's HTTPS certificate must already be trusted.

The API serves the installers and agent archives from `AgentDistribution__PackagesDirectory`. `AgentDistribution__PublicBaseUrl` is the address shown in the dialog. The installer scripts are not code-signed. Package checksums are not verified beyond trusted HTTPS.

## Testing and CI

Commands for each test suite are in [backup-backend/README.md](backup-backend/README.md) and [backup-frontend/README.md](backup-frontend/README.md).

CI runs on pull requests and on pushes to `main`. The backend job builds with warnings as errors and runs unit, architecture, and fast agent tests. The frontend job runs typecheck, lint, tests, and a production build. Pushing a `vMAJOR.MINOR.PATCH` tag builds release archives for the agent (Windows zip, Linux tarball), the API, and the frontend bundle.

## Not supported yet

- Encryption of artifacts before they leave the agent.
- File and folder backup.
- S3, SMB, and other storage providers.
- Backup health, capacity monitoring, and agent auto-update.
- MSSQL differential and log backups, point-in-time restore, and PostgreSQL WAL archiving.
- Online billing. Plans and subscriptions are assigned by a platform admin.
