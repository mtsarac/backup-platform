#!/usr/bin/env bash
# Read-only status of the development stack.
set +x
set -euo pipefail
# shellcheck source=scripts/dev/common.sh
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"
for name in api web agent; do
    if dev_pid_alive "$name"; then printf '%-8s running (pid %s)\n' "$name" "$(<"$BP_DEV_HOME/run/$name.pid")"; else printf '%-8s stopped\n' "$name"; fi
done
printf '%-8s %s\n' mssql "$(docker inspect --format '{{.State.Status}}' "$BP_DEV_MSSQL_CONTAINER" 2>/dev/null || echo absent)"
printf '%-8s %s\n' postgres "$(docker exec "${POSTGRES_CONTAINER:-postgres}" psql -U postgres -Atc "SELECT 'database present: ' || count(*) FROM pg_database WHERE datname='$BP_DEV_DATABASE'" 2>/dev/null || echo unavailable)"
if [[ -f $BP_DEV_HOME/cert.pem ]]; then
    printf '%-8s %s\n' api-http "$(curl -s -o /dev/null -w '%{http_code}' --max-time 3 --cacert "$BP_DEV_HOME/cert.pem" "$BP_DEV_API_TLS_URL/api/auth/antiforgery" || true)"
fi
printf '%-8s %s\n' frontend "$(curl -s -o /dev/null -w '%{http_code}' --max-time 3 "$BP_DEV_FRONTEND_URL/" || true)"
printf 'Frontend %s | API %s | accounts %s | logs %s\n' "$BP_DEV_FRONTEND_URL" "$BP_DEV_API_URL" "$BP_DEV_ACCOUNTS_FILE" "$BP_DEV_HOME/log"
