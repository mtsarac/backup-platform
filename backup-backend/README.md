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
    backup/          mssql, postgresql (file-system is an empty skeleton)
    storage/         central, local, sftp, google-drive
    secret/          data-protection
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

## Tests

Run from this directory.

| Suite | Command | Needs |
|---|---|---|
| Unit | `dotnet test tests/unit-tests -c Release` | nothing |
| Architecture | `dotnet test tests/architecture-tests -c Release` | nothing |
| Agent | `dotnet test tests/agent-tests -c Release` | nothing |
| PostgreSQL integration | `dotnet test tests/integration-tests -c Release --filter "FullyQualifiedName!~MsSql&FullyQualifiedName!~BackupPipelineSmoke"` | `BACKUP_PLATFORM_TEST_CONNECTION_STRING` |
| MSSQL integration | `dotnet test tests/integration-tests -c Release --filter "FullyQualifiedName~IntegrationTests.MsSql\|FullyQualifiedName~BackupPipelineSmoke"` | PostgreSQL and MSSQL variables |
| Windows | `dotnet test tests/windows-tests -c Release` | Windows host |

Integration test variables:

- `BACKUP_PLATFORM_TEST_CONNECTION_STRING`: administrative PostgreSQL connection with `CREATEDB`. Each run creates and drops its own database.
- `BACKUP_PLATFORM_TEST_MSSQL_HOST`, `_USER`, `_PASSWORD`: SQL Server connection.
- `BACKUP_PLATFORM_TEST_MSSQL_STAGING_SERVER_DIR`, `_AGENT_DIR`: the same staging folder as SQL Server sees it and as the agent sees it.

CI runs the unit, architecture, and a fast subset of the agent tests. For the helper scripts that start disposable databases and run the integration suites, see [scripts/README.md](../scripts/README.md).
