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

## Browser smoke environment

```bash
scripts/frontend/browser-smoke-env.sh [--no-build]
```

Starts a disposable real stack (fresh PostgreSQL database from `BACKUP_PLATFORM_TEST_CONNECTION_STRING`, EF migrations,
bootstrapped `smoke.admin` PlatformAdmin with a generated password, HTTPS backend API, Vite dev server with the `/api`
proxy). It prints `SMOKE_FRONTEND_URL`, `SMOKE_API_URL` and `SMOKE_CREDENTIALS_FILE` (a 0600 JSON file, never echoed)
and runs until interrupted; on exit it stops both processes, drops the database and deletes the runtime directory.
Start it as a named service (ready log `SMOKE_READY`) and drive the frontend URL with a browser.

