#!/usr/bin/env bash
set +x
set -euo pipefail
# shellcheck source=../lib/common.sh
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../lib" && pwd)/common.sh"

build_args=()
extra_filter=
while (($#)); do
    case "$1" in
        --no-build) build_args+=(--no-build) ;;
        --filter)
            (($# >= 2)) && [[ -n $2 && $2 != -* ]] || fail '--filter needs a nonempty dotnet test filter expression.'
            extra_filter=$2
            shift ;;
        -h|--help)
            printf '%s\n' 'Usage: run-postgres-tests.sh [--no-build] [--filter EXPRESSION]' \
                'Explicit PostgreSQL acceptance: requires BACKUP_PLATFORM_TEST_CONNECTION_STRING; runs the existing PostgreSQL filter in Release.' \
                '--filter narrows that selection (e.g. FullyQualifiedName~ManagedService); it never widens it.' \
                'Builds only the integration test project and its dependencies; --no-build explicitly reuses existing Release output.'
            exit 0 ;;
        *) fail 'Unknown option; use --help.' ;;
    esac
    shift
done
load_local_env
require_command dotnet
[[ -n "${BACKUP_PLATFORM_TEST_CONNECTION_STRING:-}" ]] || fail 'Set BACKUP_PLATFORM_TEST_CONNECTION_STRING.'
cd -- "$REPO_ROOT/backup-backend"
filter='FullyQualifiedName!~MsSql&FullyQualifiedName!~BackupPipelineSmoke'
[[ -z $extra_filter ]] || filter="(${filter})&(${extra_filter})"
run_dotnet_tests tests/integration-tests -c Release "${build_args[@]}" --filter "$filter" -- xUnit.MaxParallelThreads=2
