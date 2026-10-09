# Local Development and Test Scripts

Repository root: `/home/msarac/Projects/internship/internship-2/Projects/backup-app`.

Reusable local/test automation lives under `scripts/`. This is the canonical
location. Do not keep permanent reusable helpers under `/tmp`.

## `/tmp` policy

`/tmp` is only for disposable runtime output:

- runtime files,
- staging,
- generated artifacts,
- temporary credentials,
- disposable container data.

A script needed more than once belongs in `scripts/`.

## Session continuity

Before creating a helper:

```text
script already under scripts/?
yes -> reuse/update
no -> will this be reused?
yes -> create under scripts/
no -> /tmp is acceptable
```

## Windows VM

- Address: `192.168.122.218`
- Preferred access: OpenSSH alias `virtual-win11`
- Fallback: `WINDOWS_SSH_USER` (and optional `WINDOWS_SSH_HOST`)
- No password or private key is embedded in scripts.

```bash
scripts/windows/ssh-vm.sh -- echo hello
scripts/windows/run-powershell.sh --command '$PSVersionTable.PSVersion'
scripts/windows/check-vm.sh
scripts/windows/copy-to-vm.sh local-file 'C:\Temp\remote-file'
scripts/windows/test-agent-service.sh status
```

## Credentials

Local connection values live in the private file:

```text
docs/test-cred.txt
```

Rules:

- do not modify `docs/test-cred.txt`,
- do not add it to Git,
- do not copy its contents into tracked files,
- do not hardcode passwords/secrets in scripts,
- do not print secrets to logs/console.

Scripts load it only at runtime through `scripts/lib/common.sh`:

```bash
source scripts/lib/common.sh
load_local_env
```

Successful setup only reports neutral status lines such as:

```text
MSSQL environment configured
PostgreSQL environment configured
Windows VM configuration loaded
```

## Database checks

```bash
scripts/database/test-postgres.sh
scripts/database/test-mssql.sh
scripts/database/check-databases.sh
```

Disposable databases reuse the existing CI starters instead of duplicating
them:

```bash
scripts/database/with-disposable-databases.sh -- ./scripts/database/check-databases.sh
scripts/database/with-disposable-databases.sh --restore -- ./scripts/integration/run-mssql-tests.sh --provider --agent --restore
```

## Test entry points

```bash
scripts/integration/run-backend-tests.sh
scripts/integration/run-backend-tests.sh --connectivity
scripts/integration/run-postgres-tests.sh
scripts/integration/run-postgres-tests.sh --filter 'FullyQualifiedName~ManagedService'
scripts/integration/run-mssql-tests.sh
scripts/integration/run-mssql-tests.sh --provider --agent --restore
scripts/integration/run-full-acceptance.sh
scripts/integration/run-full-acceptance.sh --configured
```

Storage connections smoke (real Central backup -> download -> restore, with the connection upgrade applied to the real replica in between):

```bash
scripts/integration/run-storage-connections-smoke.sh
scripts/integration/run-storage-connections-smoke.sh --no-build
```

Agent service install/uninstall operations reuse:

```text
backup-backend/deploy/windows/
```

`scripts/windows/test-agent-service.sh` is only an orchestration wrapper.

Test runners capture TRX and require an executed, entirely passing selection. Missing evidence, zero selected
tests, skipped/partial tests and failures are rejected even when `dotnet test` returns zero.
`run-full-acceptance.sh` covers database/backend/MSSQL/restore suites. Its optional Windows checks only prove
infrastructure, Running/automatic service state, encrypted-file presence and HTTP reachability, **not**
DPAPI decryptability, Agent authentication, heartbeat, capabilities or frontend Online.

```bash
python3 scripts/integration/test-script-contracts.py
python3 scripts/integration/test-script-contracts.py --windows
```

The Windows contract selection uses the real configured SSH VM: remote errors must propagate, streamed
PowerShell scripts must finish without stdin EOF, literal arguments must not execute, and a binary SFTP transfer
must preserve exact bytes, refuse implicit overwrite and allow explicit `--force` replacement.
`copy-to-vm.sh` uses native SFTP through `ssh-vm.sh`'s shared security options, checks SHA-256 before moving the
staging file, and never changes an existing destination without `--force`.

## Real enrollment acceptance

Reuse `scripts/windows/Onboarding-Smoke.ps1` through `run-powershell.sh --file`, not a copied installer.
It supports `prepare`, `stage`, `install`, `verify`, `uninstall`, `reinstall`, `purge` and `cleanup`.
Use a fresh direct ProgramData child `BackupAgentSmoke-<12 lowercase hex digits>`; `prepare` refuses an existing
service and marks an administrator-only owned root. Copy the published ZIP and disposable trusted TLS certificate
with `copy-to-vm.sh`, then `stage`. Its binary folder deliberately contains spaces to exercise real SCM quoting.
`install` reads a protected runtime token JSON, calls the actual Agent.Host enrollment CLI and deletes that token
file afterward. Default `uninstall` checks preservation; `reinstall` uses the saved identity without a token;
`purge` explicitly checks removal. `cleanup` removes only the owned smoke root and its disposable trust anchor.

For Linux, `scripts/integration/enroll-agent.sh` calls the real published CLI using an owner-only token JSON.
It never echoes the token; `--certificate` adds explicit test trust via `SSL_CERT_FILE`, not a validation bypass.

Prove authenticated backend behavior after starting either platform, rather than treating an encrypted file or
a successful HTTP HEAD as enrollment success:

```bash
python3 scripts/integration/check-enrolled-agent.py \
  --server https://localhost:<api-port> \
  --credentials-file <0600-smoke-login-json> --certificate <trusted-smoke-certificate> \
  --tenant-id <tenant-id> --token-id <token-id>
```

The checker uses real cookie login/antiforgery, authorized token status and Agent details. It requires a consumed
token linked to the intended Agent, Active/Online state, a heartbeat and synchronized capability metadata.
After restart, pass `--expected-agent-id <same-id>` and `--heartbeat-after <UTC-checkpoint-after-restart>` so stale
Online state cannot satisfy acceptance. A bounded timeout fails safely; it is not success or a silent skip.
Drive **Agents → Add Agent** in the real browser as well, confirm token clearing and Online, and check the same
AgentId after restart. Assign the smoke user an explicit tenant Admin membership; PlatformAdmin alone is insufficient.

These are acceptance helpers, not a package downloader, production installer wrapper or systemd installer.


## Browser smoke environment

```bash
scripts/frontend/browser-smoke-env.sh [--no-build]
```

Starts a disposable real stack (fresh PostgreSQL database from `BACKUP_PLATFORM_TEST_CONNECTION_STRING`, EF migrations,
bootstrapped `smoke.admin` PlatformAdmin with a generated password, HTTPS backend API, Vite dev server with the `/api`
proxy). It prints `SMOKE_FRONTEND_URL`, `SMOKE_API_URL`, `SMOKE_AGENT_URL`, `SMOKE_CERTIFICATE_FILE`,
`SMOKE_STATE_FILE` (non-secret database/PID ownership metadata) and
`SMOKE_CREDENTIALS_FILE` (a 0600 JSON file, never echoed). Readiness requires actual HTTP 200 from both API and UI,
not only running processes. It runs until interrupted; on exit it stops both processes, drops the database and
deletes the runtime directory. Start it as a named service (ready log `SMOKE_READY`) and drive the frontend URL.
For a Windows VM opt in with `BP_SMOKE_PUBLIC_HOST=<host IPv4 reachable by VM>`; this binds the API to all interfaces
and adds that IPv4 to the certificate SAN. Default remains loopback-only. Import the disposable certificate through
the owned Windows smoke helper and remove it during cleanup; never disable certificate validation.

Stop with Ctrl+C or SIGTERM to the process ID in `SMOKE_STATE_FILE` so its cleanup trap can run. Original signal/
failure exit codes are preserved (SIGTERM is 143), while runtime files are still removed; database cleanup failure
is reported as failure, not silently accepted. A forced SIGKILL/process-tree kill cannot guarantee trap execution.
In that case the protected state file identifies the exact owned database for recovery; never drop every
`bp_smoke_*` database or delete unrelated runtime roots.

## Quota browser smoke fixture

```bash
python3 scripts/frontend/quota-smoke-fixture.py --server "$SMOKE_API_URL" --certificate "$SMOKE_CERTIFICATE_FILE" \
  setup --credentials-file "$SMOKE_CREDENTIALS_FILE" --state <0600-state-file>
python3 scripts/frontend/quota-smoke-fixture.py --server ... --certificate ... publish --state <state-file> --bytes 2000000000
python3 scripts/frontend/quota-smoke-fixture.py --server ... --certificate ... quota   --state <state-file>
```

Used on top of `scripts/frontend/browser-smoke-env.sh`. `setup` creates, through the real platform/tenant APIs, a tenant, a tenant Admin user
(login/password only in the 0600 state file), an enrolled protocol-only agent and a `Queue` backup job. In the browser the PlatformAdmin assigns a
plan (Platform → Tenant → Plan ata) and the tenant Admin presses "Şimdi çalıştır". `publish` plays the agent for one Pending run and completes it
with a synthetic artifact of `--bytes` bytes (metadata only: no SQL Server, no file), so usage can be driven to any level without a huge backup.
`quota` prints the tenant's own `GET /storage-quota` view. A browser profile shares cookies across tabs: sign out of one account before signing in as
the other. Nothing here is a production agent, provider or database writer.

## Secure download verification (M4)

```bash
scripts/integration/run-download-tests.sh
scripts/integration/run-download-tests.sh --postgres --no-build
scripts/frontend/run-download-smoke.sh
```

The focused runner selects download unit/Agent tests and PostgreSQL download, existing restore pin and Agent deletion-race regressions.
It uses the shared TRX verifier: zero selections, skipped tests and failures are errors; the full suites are not run.

The real smoke runner reuses the disposable database starters, browser stack, enrollment CLI and Windows onboarding/copy helpers.
It publishes both Agent platforms, creates an explicit tenant Owner with a seeded 20 GB subscription, and executes actual MSSQL
`master` backups: Linux Agent → tenant-bound Central storage, and the Windows VM's Agent service → its own local storage.
Both downloads are authorized through the backend, written with bounded buffers, and checked for exact length, artifact SHA-256
and a completed lifecycle with exact transferred bytes. No synthetic artifact is used.

After `DOWNLOAD_SMOKE_READY`, open `SMOKE_FRONTEND_URL` using the Owner credentials in the private `SMOKE_DOWNLOAD_STATE_FILE`.
Use **Artifacts → backup detail → Download Backup** for both artifacts. Save native browser downloads under the owned
`SMOKE_DOWNLOAD_OUTPUT_DIRECTORY`, independently hash the saved files, and compare with `artifacts.central/local.checksum`
in the state file. The runner stays alive for this browser verification; stop it with SIGTERM/Ctrl+C to uninstall its owned Windows
service, remove the disposable certificate, stop the stack and drop only its owned databases/containers/runtime directories.
It refuses to replace an existing `BackupAgent` service.

`browser-smoke-env.sh --download` can also create just the Owner/token fixture and configured Central root.
`download-smoke-fixture.py` exposes `setup`, real-Agent `backup` and bounded `verify` commands; all secrets remain in owner-only
runtime files. `windows/Download-Smoke.ps1` configures only the onboarding-owned service, grants staging access to the SQL service,
protects its temporary SQL environment-injection registry key and deletes the credential input file. Uninstall removes that owned key.
No 20 GB throughput/RSS benchmark is claimed by this small real-backup smoke.


## Support access verification (M6)

```bash
scripts/integration/run-support-tests.sh
scripts/integration/run-support-tests.sh --postgres --no-build
scripts/frontend/run-restore-smoke.sh
```

The focused runner selects the support-session domain tests and the PostgreSQL support lifecycle, data-access (metadata, download, fail-closed audit,
tenant transparency) and restore subsets only, through the shared TRX verifier; the full suites are not run.

The browser smoke reuses `run-restore-smoke.sh` unchanged: it already builds a real verified Central backup of disposable MSSQL data, a Linux Agent that can
restore, a tenant Owner and the `smoke.admin` PlatformAdmin, who has **no membership** in that tenant. Drive Platform → Tenants → the smoke tenant →
**Support Access → Open Session** as `smoke.admin` (credentials in `SMOKE_CREDENTIALS_FILE`): with ViewBackupMetadata + DownloadBackup + RestoreBackup,
browse the backup, download the `.bak` natively and compare its SHA-256 with the artifact checksum, run a precheck and a NewDatabase restore into a new
database name, and read the session's Audit tab. Close the session and confirm a new download is refused. The same admin without a session, and a session
of another tenant, must be denied. Never aim a smoke restore at an existing database.

## Production SFTP verification (M7b)

```bash
scripts/integration/run-sftp-smoke.sh
scripts/integration/run-sftp-smoke.sh --no-build
```

Disposable integration smoke for backend-relayed SFTP storage:

- Launches a disposable SFTP container (`atmoz/sftp:debian`) with runtime-generated Ed25519 host key, random password, and RSA private key with passphrase in 0600 files.
- No host ports published; pins container bridge IPv4 address and passes exact `/32` via `Backups__Sftp__AllowedPrivateNetworks__0` to backend stack. Loopback/link-local addresses are rejected.
- Exercises tenant API connection lifecycle:
  - Validates password authentication and host fingerprint roundtrip.
  - Tests wrong host fingerprint rejection with `host_key_mismatch`.
  - Tests separate connection with `private-key` authentication and passphrase.
- Real MSSQL mapped backup via Linux Agent selecting the SFTP connection (`storageExecutionLocation="Backend"`).
- Verifies remote file exists, length and SHA-256 match artifact; confirms zero probe/temp leakage.
- Streams backend-relayed download, verifies SHA-256 and byte count against artifact.
- Tests disabled connection and platform admin lock: new backup runs denied (`admission_denied`), existing stored replicas remain available/usable and restorable.
- Tests secret rotation: invalid candidate credential rejected with HTTP 409 (`authentication_failed`), leaves existing credentials functioning; valid credential update succeeds.
- Requests artifact deletion through retention API (`DELETE /api/tenants/{id}/backup-artifacts/{id}`); retention worker removes remote SFTP file, confirms absence and quota reduced to zero.
- Cleans up only owned containers, databases, and runtime directories on exit.
- Windows evidence note: SFTP relay/storage operations are backend-relayed; Agent backup execution is portable. Windows VM onboarding remains documented under `scripts/windows/` and was not fabricated.

## Persistent local development stack (`scripts/dev/`)

Not disposable: state lives in `~/.local/share/backup-platform-dev` (override with `BP_DEV_HOME`), the PostgreSQL database is `backup_platform_dev`
in the existing `postgres` container, and accounts are written once to the git-ignored `docs/local-development-accounts.txt` (0600).

```bash
scripts/dev/start.sh [--no-build] [--no-agent]   # idempotent: MSSQL container, DB, migrations, API, seed, Vite, Linux Agent
scripts/dev/status.sh
scripts/dev/stop.sh                              # stops processes + bp-dev-mssql only; keeps every piece of data
scripts/dev/agent.sh start|stop|status
scripts/dev/mssql-test-db.sh create|verify DB|drop-restored DB
scripts/dev/acceptance.py backup|download|restore|delete|all --provider local|google-drive
```

- Frontend `http://localhost:5173` (Vite, `/api` proxied to `https://localhost:7179`); API `http://localhost:5252` (the Google OAuth callback) and `https://localhost:7179` (Agent).
- Google credentials are read from `docs/client_secret_*.json` at API start into the process environment only; `GoogleDrive__FrontendOrigin` allowlists the one
  frontend origin the callback popup may `postMessage` to. In Development only, the callback path is exempt from HTTPS redirection (Google's registered URI is plain `http://localhost`).
- `bp-dev-mssql` is a dedicated container because the shared `mssql` container keeps staging in a root-only Docker volume the host Agent cannot read. Only this stack stops/starts it.
- Dev-only retention override: `DefaultDeleteGracePeriod=10s` so deletion can be exercised by hand (production default: 1 day).

### M7c hardening and operating limits

- Refresh/rotation, OAuth callback and disconnect reuse PostgreSQL tenant locking. All nodes must share PostgreSQL, secret storage and Data Protection keys.
  The lock spans Google HTTP and durable credential persistence; slow Google responses can delay tenant management/quota work. External token rotation
  cannot be atomic with a local secret write: a crash or storage outage can still require reauthorization.
- `reauthorizationRequired` is server-controlled and ordinary configuration updates preserve it. OAuth state is consumed atomically; the Google Drive
  migration Designer now has the full target model matching the snapshot.
- Replica lookup requires tenant/connection/key identity; reupload updates the catalog's complete Drive identity. Replacement uploads preserve the old
  file until commit. Restore relay checks the authenticated target Agent. Staging rejects excess bytes and uses owner-only Unix permissions.
- There is no aggregate/concurrent staging reservation or crash-file sweeper. Use a protected staging volume, restrictive Windows ACLs and operational
  concurrency/free-space monitoring. The current free-space probe does not reliably identify a separately mounted staging volume.
- Completed-object retries verify content, but resumable session/offset state is not persisted across process restarts. Lost completion/session expiry
  and concurrent folder creation can leave duplicate/orphan objects; session-expiry retries have no independent bound.
- Drive reason-code mapping and external-drift telemetry are incomplete. Browser consumers should verify the published SHA-256; streaming can deliver
  bytes before a final integrity failure. Restore checksum verification should remain enabled.
- The earlier `BackendRelayedRestoreSources` migration still has an empty Designer target model; rollback/downgrade remains an operational risk.

Focused PostgreSQL regression command (no MSSQL acceptance suite):

```bash
scripts/integration/run-postgres-tests.sh --filter 'FullyQualifiedName~GoogleDrive|FullyQualifiedName~StorageRelay_RejectsAnotherAgentEvenWithTheCorrectRestoreLease|FullyQualifiedName~StorageConnectionApiIntegrationTests'
```
