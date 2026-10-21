#!/usr/bin/env bash
set +x
set -euo pipefail
# shellcheck source=scripts/lib/common.sh
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../lib" && pwd)/common.sh"

provider=false
agent=false
restore=false
selected=false
build_args=()
while (($#)); do
    case "$1" in
        --provider) provider=true; selected=true ;;
        --agent) agent=true; selected=true ;;
        --restore) restore=true; selected=true ;;
        --no-build) build_args+=(--no-build) ;;
        -h|--help)
            printf '%s\n' 'Usage: run-mssql-tests.sh [--provider] [--agent] [--restore] [--no-build]' 'Default: provider then actual-host agent smoke. Selectors are additive; --restore alone runs only restore acceptance.' 'Requires PostgreSQL, MSSQL host/user/password and both explicit staging paths. Restore additionally requires RESTORE_TARGET_PORT.' 'Agent/restore publish a self-contained Linux Agent.Host into a disposable directory unless BACKUP_PLATFORM_TEST_AGENT_HOST_PATH is supplied.'
            exit 0 ;;
        *) fail 'Unknown option; use --help.' ;;
    esac
    shift
done
if [[ "$selected" == false ]]; then
    provider=true
    agent=true
fi
load_local_env
require_command dotnet
for name in BACKUP_PLATFORM_TEST_CONNECTION_STRING BACKUP_PLATFORM_TEST_MSSQL_HOST BACKUP_PLATFORM_TEST_MSSQL_USER BACKUP_PLATFORM_TEST_MSSQL_PASSWORD BACKUP_PLATFORM_TEST_MSSQL_STAGING_SERVER_DIR BACKUP_PLATFORM_TEST_MSSQL_STAGING_AGENT_DIR; do
    [[ -n "${!name:-}" ]] || fail "Set $name."
done
# The agent path is local to the test process; never turn a Windows server path
# into a guessed Linux mount. SQL Server must already have access to its path.
agent_staging=$BACKUP_PLATFORM_TEST_MSSQL_STAGING_AGENT_DIR
[[ "$agent_staging" == /* && "$agent_staging" != *\\* ]] || fail 'STAGING_AGENT_DIR must be an explicit absolute Linux path visible to the local test process.'
mkdir -p -- "$agent_staging"
if [[ "$restore" == true ]]; then
    target_port=${BACKUP_PLATFORM_TEST_MSSQL_RESTORE_TARGET_PORT:-}
    [[ "$target_port" =~ ^[0-9]{1,5}$ ]] || fail 'Set BACKUP_PLATFORM_TEST_MSSQL_RESTORE_TARGET_PORT to the second SQL instance port.'
    ((10#$target_port >= 1 && 10#$target_port <= 65535)) || fail 'Restore target port must be between 1 and 65535.'
    source_port=${BACKUP_PLATFORM_TEST_MSSQL_PORT:-1433}
    [[ "$source_port" =~ ^[0-9]{1,5}$ ]] || fail 'MSSQL source port must be numeric for restore acceptance.'
    ((10#$target_port != 10#$source_port)) || fail 'Restore target port must differ from the source port.'
fi
cd -- "$REPO_ROOT/backup-backend"
if [[ "$agent" == true || "$restore" == true ]]; then
    if [[ -z "${BACKUP_PLATFORM_TEST_AGENT_HOST_PATH:-}" ]]; then
        require_command uname
        [[ "$(uname -s)" == Linux ]] || fail 'Automatic Agent.Host publishing requires Linux.'
        case "$(uname -m)" in
            x86_64) runtime=linux-x64 ;;
            aarch64|arm64) runtime=linux-arm64 ;;
            *) fail 'Unsupported Linux architecture; supply a published Agent.Host path.' ;;
        esac
        make_runtime_dir
        run_redacted dotnet publish src/hosts/agent -c Release -r "$runtime" --self-contained true -p:AgentPlatform=Linux -o "$RUNTIME_DIR/agent-host"
        export BACKUP_PLATFORM_TEST_AGENT_HOST_PATH="$RUNTIME_DIR/agent-host/BackupPlatform.Agent.Host"
    fi
    [[ "$BACKUP_PLATFORM_TEST_AGENT_HOST_PATH" == /* && -x "$BACKUP_PLATFORM_TEST_AGENT_HOST_PATH" ]] || fail 'AGENT_HOST_PATH must be an absolute executable published Agent.Host path.'
fi
if [[ "$provider" == true ]]; then
    filter='FullyQualifiedName~IntegrationTests.MsSql|FullyQualifiedName~BackupPipelineSmoke'
    [[ $(uname -s) == Linux ]] && filter="(${filter})&FullyQualifiedName!~MsSqlWindowsAuthenticationIntegrationTests"
    run_dotnet_tests tests/integration-tests -c Release "${build_args[@]}" --filter "$filter" -- xUnit.MaxParallelThreads=2
fi
if [[ "$agent" == true ]]; then
    run_dotnet_tests tests/integration-tests -c Release "${build_args[@]}" --filter 'FullyQualifiedName~AgentSmokeTests' -- xUnit.MaxParallelThreads=1
fi
if [[ "$restore" == true ]]; then
    run_dotnet_tests tests/integration-tests -c Release "${build_args[@]}" --filter 'FullyQualifiedName~MsSqlRestoreAcceptanceTests' -- xUnit.MaxParallelThreads=1
fi
