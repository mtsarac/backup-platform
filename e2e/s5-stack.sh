#!/usr/bin/env bash
# Isolated S5 stack (compose project bps5, see docker-compose.e2e-s5.yml): backend + its own MinIO, for specs that need real artifact bytes.
# Run them:  E2E_STATE_FILE=.state-s5.json E2E_BASE_URL=https://localhost:7273 E2E_API_URL=https://localhost:7352 PUBLIC_HOST=localhost bun run test tests/s5-real
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p e2e/.s5-certs
[ -f e2e/.s5-certs/public.crt ] || {
  openssl req -x509 -newkey rsa:2048 -nodes -days 30 -subj "/CN=bps5-minio" -addext "subjectAltName=DNS:bps5-minio,DNS:minio" \
    -keyout e2e/.s5-certs/private.key -out e2e/.s5-certs/public.crt 2>/dev/null
  chmod 644 e2e/.s5-certs/private.key
}
export PUBLIC_HOST=localhost API_PORT=7352 FRONTEND_PORT=7273 FRONTEND_HTTP_PORT=7274 POSTGRES_PORT=5435 POSTGRES_PASSWORD=postgres
DC="docker compose -p bps5 -f docker-compose.yml -f docker-compose.e2e-s5.yml"
$DC up -d --build
echo "waiting for API ..."
for _ in $(seq 1 90); do
  [ "$(curl -sk -o /dev/null -w '%{http_code}' https://localhost:7352/api/auth/antiforgery)" = 200 ] && break; sleep 2
done
$DC exec -T \
  -e BACKUP_PLATFORM_BOOTSTRAP_LOGIN=admin -e BACKUP_PLATFORM_BOOTSTRAP_PASSWORD='PlatformAdmin123!' \
  -e BACKUP_PLATFORM_BOOTSTRAP_DISPLAY_NAME='Platform Admin' -e BACKUP_PLATFORM_BOOTSTRAP_EMAIL=admin@example.test \
  api dotnet BackupPlatform.Api.dll --bootstrap-user || echo "(bootstrap skipped: admin already exists)"
cd e2e
E2E_STATE_FILE=.state-s5.json PUBLIC_HOST=localhost E2E_BASE_URL=https://localhost:7273 E2E_API_URL=https://localhost:7352 bun seed.ts
