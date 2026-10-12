@AGENTS.md

# Project Context — Backup Platform

Multi-tenant database backup, replication, retention and restore platform.

## Architecture & Directories

- `backup-backend/`: .NET 10 solution (`BackupPlatform.sln`).
  - `src/core/contracts/`: Shared message contracts, layout rules (`ManagedBackupLayout`).
  - `src/core/domain/`: Core aggregates, entities, guard clauses (`BackupGuard`), zero external dependencies.
  - `src/core/application/`: Orchestrators, services, store interfaces (`IBackupRunStore`, `IReplicaRepairStore`).
  - `src/core/infrastructure/`: EF Core `PlatformDbContext`, PostgreSQL stores, Quartz scheduler.
  - `src/modules/`: Modular services (`tenancy`, `identity`, `agents`, `managed-service`, `storage`, `backups`, `audit`, `scheduling`, `notifications`).
  - `src/providers/`: Storage (`central`, `sftp`, `google-drive`, `local`), Backup (`mssql`, `file-system`), Secret (`data-protection`).
  - `src/hosts/api/`: ASP.NET Core REST API, SignalR hub, background workers.
  - `src/hosts/agent/`: Standalone agent daemon (`BackupPlatform.Agent.Host`).
  - `src/agent/core/`: Agent backup/restore pipelines and staging cleaner.
  - `src/agent/transport/`: Agent-backend communication.
  - `tests/unit-tests/`: Fast unit tests (domain, services, configurations).
  - `tests/architecture-tests/`: NetArchTest architecture boundary tests.
  - `tests/agent-tests/`: Agent task and pipeline tests.
  - `tests/integration-tests/`: Integration tests requiring PostgreSQL / MSSQL.
- `backup-frontend/`: React 19, TypeScript, Vite, Tailwind CSS, Bun runtime.
- `docs/`: Technical specifications, review reports, and hardening trackers.

## Build & Test Commands

### Backend (`cd backup-backend`)
- Build: `dotnet build BackupPlatform.sln`
- Release build (CI): `dotnet build BackupPlatform.sln -c Release -p:ContinuousIntegrationBuild=true -warnaserror`
- Fast Unit Tests: `dotnet test tests/unit-tests`
- Fast Architecture Tests: `dotnet test tests/architecture-tests`
- Fast Agent Tests: `dotnet test tests/agent-tests --filter "FullyQualifiedName!~ConnectivityServiceTests" -- xUnit.MaxParallelThreads=2`

### Frontend (`cd backup-frontend`)
- Install: `bun install --frozen-lockfile`
- Typecheck: `bun run typecheck`
- Lint: `bun run lint`
- Tests: `bun run test`
- Build: `bun run build`

## Test Separation

- **Fast CI (`ci.yml`):** Unit tests, architecture tests, fast agent tests, frontend typecheck/lint/test/build. No external database containers needed. Run these locally for routine verification.
- **Integration Tests (`integration.yml`):**
  - PostgreSQL suite: `dotnet test tests/integration-tests --filter "FullyQualifiedName!~MsSql&FullyQualifiedName!~BackupPipelineSmoke"`. Requires `BACKUP_PLATFORM_TEST_CONNECTION_STRING` with database creation privileges.
  - MSSQL + Agent.Host suite: Requires running PostgreSQL and SQL Server test containers.

## Critical Invariants

- **Tenant Isolation:** Every query, mutation and path resolution must enforce `TenantId`. Never allow cross-tenant access.
- **Lease Fencing:** Writers and workers hold timed leases (`LeaseToken`, `LeaseExpiresAt`). Stale/expired lease owners must never publish replicas, complete operations, or delete remote objects.
- **Backup & Restore Safety:** Verification before publication. Readback SHA-256 and size verification required before marking a replica `Verified`. Never delete verified replicas during cleanup.
- **Retention & Cleanup:** Pinned artifacts (`RetentionPinnedAt`) survive retention expiration. Deletions must verify absence or persist durable orphan records (`Failed`, `DeletePending`) for subsequent retention sweeps.
- **Quota Accounting:** Quota evaluated before run/repair admission; unverified/orphan storage tracked against tenant usage.

## Dependency Boundaries

- `Domain` depends only on `Contracts`; never reference EF Core, ASP.NET Core, or storage provider SDKs.
- `Application` depends on `Domain` and `Contracts`; consumes store/provider interfaces, never concrete infrastructure.
- `Infrastructure` implements application interfaces using EF Core and PostgreSQL.
- `Agent.Core` never references ASP.NET Core or EF Core.
- Storage and backup providers are isolated adapters behind abstraction interfaces.

## Database Migrations

- Tooling: Entity Framework Core CLI (`dotnet ef`).
- Context: `PlatformDbContext` in `src/core/infrastructure`.
- Migration command:
  `BACKUP_PLATFORM_CONNECTION_STRING="Host=localhost;Database=dummy" dotnet ef migrations add <MigrationName> --project src/core/infrastructure --startup-project src/core/infrastructure`
- Never modify persistent user data or discard migration history.

## Workflow & Git

- Development on feature or fix branches (`fix/backup-safety-hardening`).
- Make small, atomic commits with conventional commit messages (`fix(scope): description`).
- Never perform automated push, pull request creation, release tagging, or branch merging unless explicitly instructed.
