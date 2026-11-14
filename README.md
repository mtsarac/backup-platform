# Backup Platform

[![CI](https://github.com/mtsarac/backup-platform/actions/workflows/ci.yml/badge.svg)](https://github.com/mtsarac/backup-platform/actions/workflows/ci.yml)
![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)
![React 19](https://img.shields.io/badge/React-19-61DAFB?logo=react)
![PostgreSQL 18](https://img.shields.io/badge/PostgreSQL-18-4169E1?logo=postgresql)
![Tailwind CSS 4](https://img.shields.io/badge/Tailwind_CSS-4-06B6D4?logo=tailwindcss)
![Bun](https://img.shields.io/badge/Bun-1.4+-fbf0df?logo=bun)

A multi-tenant backup platform designed for Microsoft SQL Server, PostgreSQL, and file systems. A central control plane handles job scheduling, storage topologies, and fleet telemetry, while lightweight outward-connecting agents execute zero-relay payload transfers directly to target storage.

```text
┌────────────────────────────────────────────────────────┐
│                     Control Plane                      │
│  Web UI (React 19)  ──►  API & Scheduler (ASP.NET 10)  │
│                                  │                     │
│                            PostgreSQL 18               │
└──────────────────────────────────▲─────────────────────┘
                                   │ Outbound polling + lease renewals
┌──────────────────────────────────▼─────────────────────┐
│                 Target Host / Infrastructure           │
│  Backup Agent (Windows / Linux) ──► MSSQL / PG / Files │
└──────────────────────────────────┬─────────────────────┘
                                   │ AgentDirect (Zero-relay transfer)
                                   ▼
          Storage: Local Disk, Central, SFTP, SMB, S3, Google Drive
```

The agent connects strictly outbound—no incoming firewall ports required. It claims scheduled tasks via atomic leases, streams encrypted artifacts directly to storage targets, and posts verified checksums back to the backend.

---

## Subsystem Navigation

Detailed guides and implementation notes live in the respective subdirectories:

| Subsystem | Stack | Primary Responsibilities | Guide |
|---|---|---|---|
| **Backend** | .NET 10, ASP.NET Core, EF Core, Quartz.NET | Multi-tenant Web API, scheduler, leases, telemetry, providers | [`backup-backend/README.md`](backup-backend/README.md) |
| **Frontend** | React 19, TypeScript, Tailwind 4, TanStack Query | Web dashboard, Passkey/WebAuthn auth, design system, i18n (TR/EN) | [`backup-frontend/README.md`](backup-frontend/README.md) |
| **Automation & Scripts** | Bash, PowerShell, Docker Compose | Dev environment harnesses, E2E runner, agent package builders | [`scripts/README.md`](scripts/README.md) |
| **Architecture & Domain** | Markdown, ADR | Ubiquitous vocabulary, naming constraints, architectural decisions | [`GLOSSARY.md`](GLOSSARY.md), [`docs/adr/`](docs/adr/) |
| **Agent Engineering Skills**| Markdown | Issue tracker workflows, triage labels, and domain rules for AI tools | [`docs/agents/`](docs/agents/) |

---

## Core Capabilities

### 1. Backup & Verification
- **Databases**: Native full backup for Microsoft SQL Server; `pg_dump` custom format for PostgreSQL. Every artifact is verified against SHA-256 and read back before completion.
- **File Backups**: Structured tree backups with inclusion/exclusion filters and symlink policies, committed against a SHA-256 manifest.
- **Snapshots**: Consistent file-level backups via OS snapshot integration—Windows VSS shadow copies, and Linux Btrfs, ZFS, or LVM thin snapshots.
- **Automated Verification**: End-to-end restore verification into temporary disposable databases with checksum and integrity assertions.

### 2. Storage & Direct Transfers (`AgentDirect`)
- **Supported Providers**: Local agent disk, platform-managed central storage, SFTP, SMB shares, S3-compatible object storage, and Google Drive.
- **Zero-Relay Architecture**: Payloads transfer directly between the agent and the target storage provider. The central API never relays bulk backup bytes.
- **Transfer Resilience**: Resumable transfer checkpoints, time-based bandwidth throttling schedules, and automatic exponential backoff for network stalls.

### 3. Security & Multi-Tenancy
- **Access Control**: Strict separation between `PlatformAdmin` (fleet management, storage configuration) and `UserAdmin` (tenant backup jobs and artifact ownership).
- **Authentication**: Passkey / FIDO2 (WebAuthn) support, rate-limiting, and distributed failed-login lockouts.
- **Envelope Encryption**: Agent-side client encryption before bytes reach storage. Supports tenant-managed offline recovery keys.
- **Audit & Sharing**: Time-limited, revocable `BackupShare` tickets for authorized downloads, with instant multi-node revocation and audit trails.

### 4. Fleet Management & Observability
- **Security Posture**: Outward-only HTTPS/WSS communication; DPAPI credential isolation on Windows; systemd sandboxing with unprivileged service accounts on Linux.
- **Fleet Updates**: Cryptographically signed release manifests enabling atomic, in-place self-updates across agent hosts.
- **Health & Telemetry**: Periodic storage `CapacityProbe` execution, multi-metric `BackupHealthState` rollups, and advisory `SizeAnomalyWarning` baseline deviation tracking.

---

## Quick Start

### All-in-One Local Stack (Docker Compose)

Launch the full platform (PostgreSQL, ASP.NET Core API, and React frontend) in containers:

```bash
# 1. Copy sample environment
cp .env.example .env

# 2. Start all services
docker compose up --build
```

Access the web interface at `http://localhost:3000` (or your configured `PUBLIC_HOST`). The bootstrap platform administrator credentials are set via `BOOTSTRAP_ADMIN_LOGIN` and `BOOTSTRAP_ADMIN_PASSWORD` in `.env`.

> For native development (running .NET and Bun locally without containers) or launching the local E2E stack, consult [`backup-backend/README.md`](backup-backend/README.md) and [`scripts/README.md`](scripts/README.md).

---

## Connecting an Agent

1. Navigate to **Agents → Add Agent** in the web UI and generate a single-use enrollment token.
2. Run the platform installer on the target host:

   **Windows (Elevated PowerShell):**
   ```powershell
   & ([scriptblock]::Create((irm 'https://backup.example.com/api/agent-dist/install.ps1'))) -Interactive -Language en
   ```

   **Linux (Systemd, root):**
   ```bash
   curl -fsS https://backup.example.com/api/agent-dist/install.sh | sudo bash -s -- --interactive --language en
   ```

3. The agent registers its identity, reports discovered databases, and appears as **Online** on the dashboard.

*For offline deployment, RPM/DEB packaging, and snapshot permission tuning, see [`scripts/README.md`](scripts/README.md).*

---

## Testing & Continuous Integration

- **Fast CI**: Automated pull-request verification running unit tests, architectural boundary enforcement, and frontend linter/typecheck/tests.
- **Full Integration**: Target tests for MSSQL, PostgreSQL, and storage provider drivers run on scheduled or manually dispatched runners.

Detailed test execution instructions are documented in [`backup-backend/README.md`](backup-backend/README.md) and [`backup-frontend/README.md`](backup-frontend/README.md).
