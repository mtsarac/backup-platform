#!/usr/bin/env bash
# Isolated auth-slice stack (compose project bps1, see docker-compose.e2e-auth.yml): passkeys (RP id localhost) and account e-mail via mailpit.
# Run the S1 specs against it:  E2E_STATE_FILE=.state-auth.json E2E_BASE_URL=https://localhost:6273 E2E_API_URL=https://localhost:6273 E2E_MAILPIT_URL=http://127.0.0.1:8026 bun run test tests/s1
set -euo pipefail
cd "$(dirname "$0")/.."
export PUBLIC_HOST=localhost FRONTEND_PORT=6273 FRONTEND_HTTP_PORT=6274 POSTGRES_PORT=5434 POSTGRES_PASSWORD=postgres
DC="docker compose -p bps1 -f docker-compose.yml -f docker-compose.e2e-auth.yml"
$DC up -d --build
echo "waiting for API ..."
for _ in $(seq 1 90); do
  [ "$(curl -sk -o /dev/null -w '%{http_code}' https://localhost:6273/api/auth/antiforgery)" = 200 ] && break; sleep 2
done
$DC exec -T postgres psql -U postgres -d backup_platform_dev -q -c 'TRUNCATE login_throttle_entries;'
$DC exec -T \
  -e BACKUP_PLATFORM_BOOTSTRAP_LOGIN=admin -e BACKUP_PLATFORM_BOOTSTRAP_PASSWORD='PlatformAdmin123!' \
  -e BACKUP_PLATFORM_BOOTSTRAP_DISPLAY_NAME='Platform Admin' -e BACKUP_PLATFORM_BOOTSTRAP_EMAIL=admin@example.test \
  api dotnet BackupPlatform.Api.dll --bootstrap-user || echo "(bootstrap skipped: admin already exists)"
cd e2e
E2E_STATE_FILE=.state-auth.json PUBLIC_HOST=localhost E2E_BASE_URL=https://localhost:6273 E2E_API_URL=https://localhost:6273 bun seed.ts
# platform e-mail transport -> mailpit (SMTP 1025 on the compose network, no TLS)
E2E_API_URL=https://localhost:6273 bun -e "
import { Api } from './api'
const p = await Api.signIn({ login: 'admin', password: 'PlatformAdmin123!' })
await p.call('PUT', '/api/platform/notification-transports/email', { enabled: true, host: 'bps1-mailpit', port: 1025, security: 'none', fromAddress: 'platform@e2e.test', fromName: 'E2E Platform' })
await p.dispose()"
echo "ready. Mailpit UI http://127.0.0.1:8026"
