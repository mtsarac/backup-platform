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
scripts/integration/run-mssql-tests.sh
scripts/integration/run-mssql-tests.sh --provider --agent --restore
scripts/integration/run-full-acceptance.sh
scripts/integration/run-full-acceptance.sh --configured
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

