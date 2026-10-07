#!/usr/bin/env bash
set +x
set -euo pipefail
# shellcheck source=../lib/common.sh
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../lib" && pwd)/common.sh"

build_args=()
while (($#)); do
    case "$1" in
        --no-build) build_args+=(--no-build) ;;
        -h|--help)
            printf '%s\n' 'Usage: run-postgres-tests.sh [--no-build]' 'Requires BACKUP_PLATFORM_TEST_CONNECTION_STRING; runs the existing PostgreSQL filter in Release.'
            exit 0 ;;
        *) fail 'Unknown option; use --help.' ;;
    esac
    shift
done
load_local_env
require_command dotnet
[[ -n "${BACKUP_PLATFORM_TEST_CONNECTION_STRING:-}" ]] || fail 'Set BACKUP_PLATFORM_TEST_CONNECTION_STRING.'
cd -- "$REPO_ROOT/backup-backend"
run_dotnet_tests tests/integration-tests -c Release "${build_args[@]}" --filter 'FullyQualifiedName!~MsSql&FullyQualifiedName!~BackupPipelineSmoke' -- xUnit.MaxParallelThreads=2
