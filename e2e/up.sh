#!/usr/bin/env bash
# One command: stack up (docker compose, project bpe2e) -> migrations -> bootstrap PlatformAdmin -> seed -> e2e/.state.json.
set -euo pipefail
cd "$(dirname "$0")/.."
[ -f .env.e2e ] || { cp .env.example .env.e2e; sed -i 's/^POSTGRES_PORT=.*/POSTGRES_PORT=5433/' .env.e2e; }  # 5432 is commonly taken on dev laptops
while IFS="=" read -r k v; do [[ "$k" =~ ^[A-Z_]+$ ]] && export "$k=$v"; done < .env.e2e  # not `source`: values may hold spaces
export E2E_FEATURE_WINDOWSVSS=true
DC="docker compose -p bpe2e --env-file .env.e2e"
$DC up -d --build ${BUILD_FLAGS:-}

host="${PUBLIC_HOST:-192.168.122.1}"
echo "waiting for API at https://$host:${API_PORT:-5252} ..."
for _ in $(seq 1 90); do
  [ "$(curl -sk -o /dev/null -w '%{http_code}' "https://$host:${API_PORT:-5252}/api/auth/antiforgery")" = 200 ] && break; sleep 2
done

# Idempotent: the CLI refuses when a platform admin already exists.
$DC exec -T \
  -e BACKUP_PLATFORM_BOOTSTRAP_LOGIN="${BOOTSTRAP_ADMIN_LOGIN:-admin}" \
  -e BACKUP_PLATFORM_BOOTSTRAP_PASSWORD="${BOOTSTRAP_ADMIN_PASSWORD}" \
  -e BACKUP_PLATFORM_BOOTSTRAP_DISPLAY_NAME="${BOOTSTRAP_ADMIN_NAME:-Platform Admin}" \
  -e BACKUP_PLATFORM_BOOTSTRAP_EMAIL="${BOOTSTRAP_ADMIN_EMAIL:-admin@example.test}" \
  api dotnet BackupPlatform.Api.dll --bootstrap-user || echo "(bootstrap skipped: admin already exists)"

cd e2e
bun install --frozen-lockfile 2>/dev/null || bun install
bun seed.ts
echo "Frontend https://$host:${FRONTEND_PORT:-5173}   API https://$host:${API_PORT:-5252}   run: cd e2e && bun run test"
