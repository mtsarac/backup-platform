#!/usr/bin/env bash
set +x
set -euo pipefail
# shellcheck source=../lib/common.sh
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../lib" && pwd)/common.sh"

configured=false
while (($#)); do
    case "$1" in
        --configured) configured=true ;;
        -h|--help)
            printf '%s\n' 'Usage: run-full-acceptance.sh [--configured]' \
                'Default: create disposable CI PostgreSQL/MSSQL databases and a second restore SQL instance, then run configured acceptance.' \
                'Configured: real database probes, full backend/agent connectivity tests, PostgreSQL, MSSQL provider + actual-host smoke and cross-instance restore.' \
                'Windows checks cover SSH/runtime/SQL service, Running BackupAgent with automatic startup and identity-file presence, plus HTTPS reachability only.' \
                'These Windows checks do NOT establish DPAPI decryptability, agent authentication, heartbeat, capability sync or frontend Online. Use Onboarding-Smoke.ps1 plus check-enrolled-agent.py and the real frontend for onboarding acceptance.' \
                'Requires Docker for default mode; configured mode requires explicit database credentials, staging pair and restore target port. Both modes require the configured Windows VM and running enrolled service.'
            exit 0 ;;
        *) fail 'Unknown option; use --help.' ;;
    esac
    shift
done
load_local_env
if [[ "$configured" == false ]]; then
    run_redacted "$REPO_ROOT/scripts/database/with-disposable-databases.sh" --restore -- "$REPO_ROOT/scripts/integration/run-full-acceptance.sh" --configured
    exit 0
fi
for name in BACKUP_PLATFORM_TEST_CONNECTION_STRING BACKUP_PLATFORM_TEST_MSSQL_HOST BACKUP_PLATFORM_TEST_MSSQL_USER BACKUP_PLATFORM_TEST_MSSQL_PASSWORD BACKUP_PLATFORM_TEST_MSSQL_STAGING_SERVER_DIR BACKUP_PLATFORM_TEST_MSSQL_STAGING_AGENT_DIR BACKUP_PLATFORM_TEST_MSSQL_RESTORE_TARGET_PORT; do
    [[ -n "${!name:-}" ]] || fail "Set $name for configured full acceptance."
done
run_redacted "$REPO_ROOT/scripts/database/check-databases.sh"
run_redacted "$REPO_ROOT/scripts/integration/run-backend-tests.sh" --connectivity
run_redacted "$REPO_ROOT/scripts/integration/run-postgres-tests.sh"
# Keep all SQL selections sequential in one process, reusing its disposable host.
run_redacted "$REPO_ROOT/scripts/integration/run-mssql-tests.sh" --provider --agent --restore
run_redacted "$REPO_ROOT/scripts/windows/check-vm.sh" --require-agent --check-backend
run_redacted "$REPO_ROOT/scripts/windows/test-agent-service.sh" check --require-agent --check-backend
