#!/usr/bin/env bash
# Stops the development processes (Agent, frontend, API) and the dedicated bp-dev-mssql container.
# Deliberately keeps everything persistent: PostgreSQL database, accounts, Google connection, Data Protection keys, MSSQL data, agent identity.
# Never touches any other container.
set +x
set -euo pipefail
# shellcheck source=scripts/dev/common.sh
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"
for name in agent web api; do dev_kill "$name"; done
if docker container inspect "$BP_DEV_MSSQL_CONTAINER" >/dev/null 2>&1; then docker stop "$BP_DEV_MSSQL_CONTAINER" >/dev/null; fi
printf 'Development stack stopped (data kept in %s and database %s)\n' "$BP_DEV_HOME" "$BP_DEV_DATABASE"
