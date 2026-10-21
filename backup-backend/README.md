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
usable statvfs reports CapacityUnavailable; the Agent never substitutes its own
host filesystem's capacity.

Capacity and health reads use the same current routing/liveness checks. Expired
or retry-exhausted tasks are unavailable immediately, without GET-side writes;
the periodic worker persists terminal task state. Capacity GET returns the latest
30 measurements, newest first, with `historyLimit: 30`. Snapshot, threshold audit,
notification condition/event, and outbox delivery commit atomically.

Downgrading `StorageCapacityTaskTransport` requires snapshot rows to be
representable by the old schema: only Available or CapacityUnavailable. If any
Warning or Critical row exists, the downgrade refuses the narrower constraint
and PostgreSQL rolls the entire migration back, preserving all rows, the task
table, and migration history. It never rewrites measurements or deletes rows to
make a downgrade pass.


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
