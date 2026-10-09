#!/usr/bin/env bash
# Starts a disposable real stack for browser smoke tests: fresh PostgreSQL database, migrations, bootstrapped
# PlatformAdmin, the HTTPS backend API and the Vite dev server (same-origin /api proxy). Runs until interrupted;
# cleanup drops the database and removes every runtime file. Credentials are generated per run and only written
# to a 0600 file whose path is printed (never the secret itself).
set +x
set -euo pipefail
# shellcheck source=../lib/common.sh
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../lib" && pwd)/common.sh"

build=true
download=false
retention=false
while (($#)); do
    case "$1" in
        --no-build) build=false ;;
        --download) download=true ;;
        --retention-smoke) retention=true ;;
        -h|--help)
            printf '%s\n' 'Usage: browser-smoke-env.sh [--no-build] [--download]' \
                'Needs BACKUP_PLATFORM_TEST_CONNECTION_STRING (admin PostgreSQL) from the private file/environment, dotnet, bun and openssl.' \
                'Prints SMOKE_FRONTEND_URL, SMOKE_API_URL and SMOKE_CREDENTIALS_FILE (JSON: login, password) then waits.' \
                '--download creates a real tenant Owner/enrollment fixture and configures a tenant-bound Central root; it does not synthesize backup files.' \
                'Optional: BP_SMOKE_POSTGRES_CONTAINER (default postgres) is used only to drop the database afterwards.'
            exit 0 ;;
        *) fail 'Unknown option; use --help.' ;;
    esac
    shift
done

load_local_env
require_command dotnet
require_command bun
require_command openssl
require_command python3
[[ -n "${BACKUP_PLATFORM_TEST_CONNECTION_STRING:-}" ]] || fail 'Set BACKUP_PLATFORM_TEST_CONNECTION_STRING.'
# Only the owned disposable stack opts into a short deletion grace period.
export Backups__Retention__Enabled=$retention
if [[ $retention == true ]]; then
    export Backups__Retention__ScanInterval=00:00:01
    export Backups__Retention__MinimumDeleteGracePeriod=00:00:01
    export Backups__Retention__DefaultDeleteGracePeriod=00:00:01
    export Backups__Retention__StorageSweepEnabled=false
fi

make_runtime_dir
work_dir=$RUNTIME_DIR
# Explicit opt-in for a VM-accessible acceptance stack; the default remains loopback-only.
public_host=${BP_SMOKE_PUBLIC_HOST:-127.0.0.1}
[[ $public_host =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail 'BP_SMOKE_PUBLIC_HOST must be an IPv4 address.'
bind_host=127.0.0.1
[[ $public_host == 127.0.0.1 ]] || bind_host=0.0.0.0
database="bp_smoke_$(python3 -c 'import uuid; print(uuid.uuid4().hex[:12])')"
# Non-secret ownership metadata also identifies this disposable database after a forced shutdown.
printf '{"database":"%s","processId":%s}\n' "$database" "$$" > "$work_dir/smoke-state.json"
read -r api_port web_port < <(python3 -c '
import socket
sockets = [socket.socket() for _ in range(2)]
for sock in sockets: sock.bind(("127.0.0.1", 0))
print(*(sock.getsockname()[1] for sock in sockets))
')
# Replace only the Database segment of the administrative connection string.
BACKUP_PLATFORM_CONNECTION_STRING=$(python3 - "$BACKUP_PLATFORM_TEST_CONNECTION_STRING" "$database" <<'PY'
import sys
parts = [p for p in sys.argv[1].split(';') if p.strip() and not p.strip().lower().startswith('database=')]
print(';'.join(parts + ['Database="%s"' % sys.argv[2]]))
PY
)
export BACKUP_PLATFORM_CONNECTION_STRING
login="smoke.admin"
password="Smoke-$(python3 -c 'import secrets; print(secrets.token_hex(12))')!"
cert_password=$(python3 -c 'import secrets; print(secrets.token_hex(12))')
pids=()
database_created=false

smoke_cleanup() {
    local pid
    for pid in "${pids[@]}"; do kill "$pid" 2>/dev/null || true; done
    for pid in "${pids[@]}"; do wait "$pid" 2>/dev/null || true; done
    if [[ $database_created == true ]]; then
        local container=${BP_SMOKE_POSTGRES_CONTAINER:-postgres}
        if docker container inspect "$container" >/dev/null 2>&1; then
            docker exec "$container" psql -U postgres -v ON_ERROR_STOP=1 -q \
                -c "DROP DATABASE IF EXISTS \"$database\" WITH (FORCE)" >/dev/null 2>&1 \
                && printf 'Smoke database dropped\n' || { printf 'Smoke database cleanup failed: %s\n' "$database" >&2; return 1; }
        else
            printf 'Smoke database left behind (no container %s): %s\n' "$container" "$database" >&2
            return 1
        fi
    fi
}
# Pass the original status explicitly; a failing subshell here would trip errexit before file cleanup.
trap 'rc=$?; if ! smoke_cleanup; then if (( rc == 0 )); then rc=1; fi; fi; cleanup_runtime "$rc"' EXIT

cd -- "$REPO_ROOT/backup-backend"
if [[ $build == true ]]; then
    run_redacted dotnet build src/hosts/api -c Release >/dev/null
fi
api_dll="$REPO_ROOT/backup-backend/src/hosts/api/bin/Release/net10.0/BackupPlatform.Api.dll"
[[ -f $api_dll ]] || fail 'API build output missing; run without --no-build.'

database_created=true
run_redacted dotnet tool restore >/dev/null
run_redacted dotnet ef database update --project src/core/infrastructure --configuration Release --no-build >/dev/null
printf 'Smoke database migrated\n'

BACKUP_PLATFORM_BOOTSTRAP_LOGIN=$login BACKUP_PLATFORM_BOOTSTRAP_PASSWORD=$password \
    BACKUP_PLATFORM_BOOTSTRAP_DISPLAY_NAME='Smoke PlatformAdmin' run_redacted dotnet "$api_dll" --bootstrap-user >/dev/null
printf 'Smoke PlatformAdmin bootstrapped\n'

openssl req -x509 -newkey rsa:2048 -nodes -keyout "$work_dir/key.pem" -out "$work_dir/cert.pem" -days 1 \
    -subj '/CN=Backup Platform disposable smoke' -addext "subjectAltName=DNS:localhost,IP:127.0.0.1,IP:$public_host" \
    -addext 'basicConstraints=critical,CA:TRUE' -addext 'keyUsage=critical,digitalSignature,keyEncipherment,keyCertSign' >/dev/null 2>&1
openssl pkcs12 -export -inkey "$work_dir/key.pem" -in "$work_dir/cert.pem" -out "$work_dir/cert.pfx" -passout "pass:$cert_password" >/dev/null 2>&1
python3 - "$work_dir/credentials.json" "$login" "$password" <<'PY'
import json, os, sys
fd = os.open(sys.argv[1], os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
with os.fdopen(fd, 'w') as handle:
    json.dump({'login': sys.argv[2], 'password': sys.argv[3]}, handle)
PY

ASPNETCORE_URLS="https://${bind_host}:${api_port}" \
    ASPNETCORE_Kestrel__Certificates__Default__Path="$work_dir/cert.pfx" \
    ASPNETCORE_Kestrel__Certificates__Default__Password="$cert_password" \
    Backups__Scheduling__Enabled=false \
    dotnet "$api_dll" >"$work_dir/api.log" 2>&1 &
pids+=($!)
ready=false
for _ in $(seq 1 60); do
    kill -0 "${pids[0]}" 2>/dev/null || fail 'Smoke API exited during startup.'
    if python3 - "$api_port" "$work_dir/cert.pem" <<'PY'
import ssl, sys, urllib.request
try:
    with urllib.request.urlopen('https://127.0.0.1:%s/api/auth/antiforgery' % sys.argv[1],
                                context=ssl.create_default_context(cafile=sys.argv[2]), timeout=1) as response:
        if response.status != 200: sys.exit(1)
except (OSError, ValueError):
    sys.exit(1)
PY
    then ready=true; break; fi
    sleep 0.5
done
[[ $ready == true ]] || fail 'Smoke API did not become HTTP-ready.'

if [[ $download == true ]]; then
    python3 "$REPO_ROOT/scripts/frontend/download-smoke-fixture.py" \
        --server "https://127.0.0.1:$api_port" --certificate "$work_dir/cert.pem" \
        --state "$work_dir/download-state.json" setup --credentials-file "$work_dir/credentials.json"
    read -r tenant_id tenant_scope < <(python3 - "$work_dir/download-state.json" <<'PY'
import json, sys
state = json.load(open(sys.argv[1]))
print(state['tenantId'], state['tenantScope'])
PY
)
    mkdir -p "$work_dir/central"
    kill "${pids[0]}"
    wait "${pids[0]}" || true
    env "Backups__Storage__Central__RootDirectory=$work_dir/central" \
        "Backups__Storage__Central__TenantScopes__${tenant_id}=$tenant_scope" \
        "ASPNETCORE_URLS=https://${bind_host}:${api_port}" \
        "ASPNETCORE_Kestrel__Certificates__Default__Path=$work_dir/cert.pfx" \
        "ASPNETCORE_Kestrel__Certificates__Default__Password=$cert_password" \
        Backups__Scheduling__Enabled=false \
        dotnet "$api_dll" >>"$work_dir/api.log" 2>&1 &
    pids[0]=$!
    ready=false
    for _ in $(seq 1 60); do
        kill -0 "${pids[0]}" 2>/dev/null || fail 'Configured download smoke API exited during startup.'
        if python3 - "$api_port" "$work_dir/cert.pem" <<'PY'
import ssl, sys, urllib.request
try:
    with urllib.request.urlopen('https://127.0.0.1:%s/api/auth/antiforgery' % sys.argv[1],
                                context=ssl.create_default_context(cafile=sys.argv[2]), timeout=1) as response:
        if response.status != 200: sys.exit(1)
except (OSError, ValueError):
    sys.exit(1)
PY
        then ready=true; break; fi
        sleep 0.5
    done
    [[ $ready == true ]] || fail 'Configured download smoke API did not become HTTP-ready.'
    printf 'SMOKE_DOWNLOAD_STATE_FILE=%s\n' "$work_dir/download-state.json"
    printf 'SMOKE_CENTRAL_ROOT=%s\n' "$work_dir/central"
fi

cd -- "$REPO_ROOT/backup-frontend"
BACKUP_API_PROXY_TARGET="https://127.0.0.1:${api_port}" BACKUP_API_PROXY_INSECURE=true \
    bun run dev --host localhost --port "$web_port" --strictPort >"$work_dir/web.log" 2>&1 &
pids+=($!)
ready=false
for _ in $(seq 1 60); do
    kill -0 "${pids[1]}" 2>/dev/null || fail 'Smoke frontend exited during startup.'
    if python3 - "$web_port" <<'PY'
import sys, urllib.request
try:
    with urllib.request.urlopen('http://localhost:%s/' % sys.argv[1], timeout=1) as response:
        if response.status != 200: sys.exit(1)
except OSError:
    sys.exit(1)
PY
    then ready=true; break; fi
    sleep 0.5
done
[[ $ready == true ]] || fail 'Smoke frontend did not become HTTP-ready.'

printf 'SMOKE_API_URL=https://127.0.0.1:%s\n' "$api_port"
printf 'SMOKE_AGENT_URL=https://%s:%s\n' "$public_host" "$api_port"
printf 'SMOKE_CERTIFICATE_FILE=%s\n' "$work_dir/cert.pem"
printf 'SMOKE_FRONTEND_URL=http://localhost:%s\n' "$web_port"
printf 'SMOKE_CREDENTIALS_FILE=%s\n' "$work_dir/credentials.json"
printf 'SMOKE_STATE_FILE=%s\n' "$work_dir/smoke-state.json"
printf 'SMOKE_READY\n'
# Run until interrupted; fail early if either process dies.
while kill -0 "${pids[0]}" 2>/dev/null && kill -0 "${pids[1]}" 2>/dev/null; do sleep 1; done
printf 'A smoke process exited; see %s\n' "$work_dir" >&2
exit 1
