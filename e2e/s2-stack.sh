#!/usr/bin/env bash
# Isolated S2 stack (compose project bps2, see docker-compose.e2e-s2.yml): real S3/SFTP/Central round-trips, Mailpit SMTP, HTTPS webhook/SMS sink, sms+smb flags.
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p e2e/.s2-certs
if [ ! -f e2e/.s2-certs/public.crt ] || ! openssl x509 -in e2e/.s2-certs/public.crt -noout -text 2>/dev/null | grep -q "192.168.122.1"; then
  openssl req -x509 -newkey rsa:2048 -nodes -days 30 -subj "/CN=bps2" \
    -addext "subjectAltName=DNS:bps2-minio,DNS:minio,DNS:bps2-webhook,DNS:webhook,IP:127.0.0.1,IP:192.168.122.1" \
    -keyout e2e/.s2-certs/private.key -out e2e/.s2-certs/public.crt 2>/dev/null
  chmod 644 e2e/.s2-certs/private.key
fi

docker rm -f e2e-s2-sftp e2e-s2-minio 2>/dev/null || true

export PUBLIC_HOST=localhost API_PORT=7552 FRONTEND_PORT=7473 FRONTEND_HTTP_PORT=7474 POSTGRES_PORT=5436 POSTGRES_PASSWORD=postgres
BUILD_ARG=""
docker image inspect bpe2e-api:latest >/dev/null 2>&1 || BUILD_ARG="--build"
DC="docker compose -p bps2 -f docker-compose.yml -f docker-compose.e2e-s2.yml"
$DC up -d $BUILD_ARG

echo "waiting for bps2 API at https://localhost:7552 ..."
for _ in $(seq 1 90); do
  [ "$(curl -sk -o /dev/null -w '%{http_code}' https://localhost:7552/api/auth/antiforgery)" = 200 ] && break; sleep 1
done

$DC exec -T \
  -e BACKUP_PLATFORM_BOOTSTRAP_LOGIN=admin -e BACKUP_PLATFORM_BOOTSTRAP_PASSWORD='PlatformAdmin123!' \
  -e BACKUP_PLATFORM_BOOTSTRAP_DISPLAY_NAME='Platform Admin' -e BACKUP_PLATFORM_BOOTSTRAP_EMAIL=admin@example.test \
  api dotnet BackupPlatform.Api.dll --bootstrap-user || echo "(bootstrap skipped: admin already exists)"

for _ in $(seq 1 30); do
  docker exec -e MC_CONFIG_DIR=/tmp/mc bps2-minio sh -c \
    '/opt/bitnami/minio-client/bin/mc --insecure alias set s2 https://127.0.0.1:9000 "$MINIO_ROOT_USER" "$MINIO_ROOT_PASSWORD" >/dev/null && /opt/bitnami/minio-client/bin/mc --insecure mb --ignore-existing s2/s2bucket >/dev/null' 2>/dev/null && break
  sleep 1
done

cd e2e
E2E_STATE_FILE=.state-s2.json PUBLIC_HOST=localhost E2E_BASE_URL=https://localhost:7473 E2E_API_URL=https://localhost:7552 bun seed.ts

# Re-up api with S2_CENTRAL_TENANT bound to the seeded E2E Tenant id so Central BackendRoundTrip resolves /tmp/s2-central/e2e-s2.
TENANT_ID=$(bun -e "console.log(JSON.parse(await Bun.file('.state-s2.json').text()).tenantId.replaceAll('-', ''))")
cd ..
S2_CENTRAL_TENANT="$TENANT_ID" $DC up -d api
$DC restart webhook
docker exec -u 0 bps2-api sh -c 'mkdir -p /tmp/s2-central/e2e-s2 && chmod 777 /tmp/s2-central /tmp/s2-central/e2e-s2'
for _ in $(seq 1 60); do
  [ "$(curl -sk -o /dev/null -w '%{http_code}' https://localhost:7552/api/auth/antiforgery)" = 200 ] && break; sleep 1
done
echo "bps2 ready: UI https://localhost:7473  API https://localhost:7552  Mailpit http://127.0.0.1:8027  Webhook http://127.0.0.1:8028"
