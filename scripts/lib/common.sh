#!/usr/bin/env bash
# Shared by entry points; safe to source more than once.
set +x
set -euo pipefail
if [[ ${BP_COMMON_LOADED:-0} == 1 ]]; then return 0; fi
BP_COMMON_LOADED=1
REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd -P)"
export REPO_ROOT
BP_CREDENTIAL_FILE="${BP_CREDENTIAL_FILE:-${REPO_ROOT}/docs/test-cred.txt}"
export BP_CREDENTIAL_FILE
umask 077
BP_CLEANUP_DIRS=()
BP_CLEANUP_CONTAINERS=()

fail() { printf 'Error: %s\n' "$1" >&2; exit "${2:-1}"; }
require_command() { command -v "$1" >/dev/null 2>&1 || fail "Required command missing: $1" 127; }
register_cleanup_dir() { BP_CLEANUP_DIRS+=("$1"); }
cleanup_runtime() {
    local status=${1:-$?} path container failed=0
    trap - EXIT
    for container in "${BP_CLEANUP_CONTAINERS[@]}"; do
        if docker container inspect "$container" >/dev/null 2>&1 && \
            ! docker rm --force --volumes "$container" >/dev/null 2>&1; then
            printf 'Cleanup failed for owned container: %s\n' "$container" >&2
            failed=1
        fi
    done
    for path in "${BP_CLEANUP_DIRS[@]}"; do
        if ! rm -rf -- "$path"; then
            printf 'Cleanup failed for runtime directory: %s\n' "$path" >&2
            failed=1
        fi
    done
    if (( status == 0 && failed != 0 )); then status=1; fi
    exit "$status"
}
trap cleanup_runtime EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
make_runtime_dir() {
    RUNTIME_DIR="$(mktemp -d "${TMPDIR:-/tmp}/bp-local.XXXXXXXX")"
    register_cleanup_dir "$RUNTIME_DIR"
}
load_local_env() {
    require_command python3
    [[ -f $BP_CREDENTIAL_FILE ]] || printf 'Private credentials absent; using explicit environment overrides.\n' >&2
    local config_dir key value
    make_runtime_dir
    config_dir=$RUNTIME_DIR
    python3 "${REPO_ROOT}/scripts/lib/local-env.py" load "$BP_CREDENTIAL_FILE" > "${config_dir}/environment"
    while IFS= read -r -d '' key && IFS= read -r -d '' value; do
        export "$key=$value"
    done < "${config_dir}/environment"
    rm -- "${config_dir}/environment"
}
run_redacted() {
    require_command python3
    "$@" 2>&1 | python3 -u "${REPO_ROOT}/scripts/lib/local-env.py" redact "$BP_CREDENTIAL_FILE"
}
run_dotnet_tests() {
    # VSTest can return success for an empty filter or an entirely skipped suite.
    # Acceptance requires recorded, actually executed tests, not just exit code zero.
    make_runtime_dir
    local results_directory=$RUNTIME_DIR
    run_redacted dotnet test --logger 'trx;LogFileName=result.trx' --results-directory "$results_directory" "$@"
    python3 "${REPO_ROOT}/scripts/lib/assert-test-results.py" "${results_directory}/result.trx"
}
run_database_probe() {
    require_command dotnet
    make_runtime_dir
    run_redacted dotnet run --project "${REPO_ROOT}/scripts/lib/database-probe/DatabaseProbe.csproj" \
        -c Release --artifacts-path "${RUNTIME_DIR}/probe" -- "$1"
}
