@AGENTS.md

# Backup Platform — Project Guide

A multi-tenant backup platform with a .NET backend and agent, a React frontend, and pluggable database/storage providers. The shared engineering and attribution rules are in `AGENTS.md`.

## Repository Map

- `backup-backend/`: .NET solution (`BackupPlatform.sln`).
  - `src/core/domain/`: Business entities and invariants; no project-level dependencies on other application layers.
  - `src/core/contracts/`: Shared API/agent contracts and storage layout contracts.
  - `src/core/application/`: Use cases, orchestration, application interfaces.
  - `src/core/infrastructure/`: EF Core persistence, PostgreSQL stores, scheduler integration.
  - `src/modules/`: Feature-level modules; check actual implementation before assuming a module is functional.
  - `src/providers/`: Database backup, storage, and secret provider implementations or placeholders.
  - `src/agent/core/`, `src/agent/transport/`: Agent execution and backend communication.
  - `src/hosts/api/`, `src/hosts/agent/`: API and agent hosts.
  - `tests/`: Unit, architecture, agent, and integration test projects.
- `backup-frontend/`: React, TypeScript, Vite, and Bun.
- `docs/`: Specifications, planning, reviews, and task trackers. Verify whether individual files are Git-tracked or ignored.

## Routine Commands

Run backend commands from `backup-backend/`:

```fish
cd backup-backend
dotnet restore BackupPlatform.sln
dotnet build BackupPlatform.sln -c Release -p:ContinuousIntegrationBuild=true -warnaserror
dotnet test tests/unit-tests -c Release --no-build
dotnet test tests/architecture-tests -c Release --no-build
dotnet test tests/agent-tests -c Release --no-build --filter 'FullyQualifiedName!~ConnectivityServiceTests' -- xUnit.MaxParallelThreads=2
```

Run frontend commands from `backup-frontend/`:

```fish
cd backup-frontend
bun install --frozen-lockfile
bun run typecheck
bun run lint
bun run test
bun run build
```

- `.github/workflows/ci.yml`: Routine fast checks, without external database containers.
- `.github/workflows/integration.yml`: Database-dependent tests; PostgreSQL/MSSQL setup and credentials are required.
- Prefer the workflow files as the source of truth if command names, filters, or requirements change.

## Architectural Boundaries

- Keep domain business logic independent of persistence, web hosting, and provider implementations.
- Application code coordinates domain operations through contracts/abstractions; infrastructure and providers implement external I/O.
- Keep host/composition concerns at host boundaries and platform-specific logic out of shared agent code.
- Respect API/agent wire contracts and provider capabilities. Do not assume a provider supports an operation without checking it.

## Data & Lifecycle Invariants

- Tenant-scoped reads, writes, storage paths, and operational commands must enforce authorization and tenant isolation.
- Long-running work must respect leases, fencing, ownership, and durable recovery across retries, shutdown, and worker takeover.
- An artifact/replica is only reported as verified when the applicable integrity checks succeed; failures must not destroy healthy replicas.
- Retention, restore/download pins, remote cleanup, and orphan tracking must remain consistent with persisted state.
- Quota admission, usage, reservations, and release/reconciliation must remain consistent with the selected policy and concurrent work.
- Apply these invariants to new features as well as existing backup/restore flows; do not rely on the status of any particular roadmap phase.

## Migrations & Workflow

- Use the EF Core CLI with `PlatformDbContext` under `src/core/infrastructure`; inspect current design-time configuration before generating a migration.
- Review generated schema changes and preserve existing data and migration history.
- Work on the user's current authorized branch. Do not hardcode a specific branch or milestone into permanent repository instructions.
- Keep commits focused and use the existing CI checks appropriate to the change. Follow `AGENTS.md` for Git and attribution rules.
