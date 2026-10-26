#!/usr/bin/env bash
# Isolated stack for the Database Auto Discovery acceptance: its own PostgreSQL (platform database), SQL Server and PostgreSQL target containers
# (all named bp-disc-*), API, Vite and a Linux Agent. Touches nothing of the persistent dev stack (no shared container, database, port or state directory).
#   discovery-stack.sh up | down | status | restart-api | stop-agent | start-agent | kill-agent
# State and secrets live in $BP_DISC_HOME (default /tmp/bp-discovery-acceptance, 0700); secrets are never printed.
set +x
set -euo pipefail
# shellcheck source=scripts/lib/common.sh
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../lib" && pwd)/common.sh"

H=${BP_DISC_HOME:-/tmp/bp-discovery-acceptance}
API_PORT=${BP_DISC_API_PORT:-7391} WEB_PORT=${BP_DISC_WEB_PORT:-5391}
PG_PORT=55431 MSSQL_PORT=55433 TARGET_PG_PORT=55435
PUBLIC_HOST=${BP_DISC_PUBLIC_HOST:-192.168.122.1}   # libvirt bridge: lets the Windows VM reach this API
API_DLL="$REPO_ROOT/backup-backend/src/hosts/api/bin/Release/net10.0/BackupPlatform.Api.dll"
AGENT_DIR="$H/agent-bin"

pid_alive() { [[ -f $H/run/$1.pid ]] && kill -0 "$(<"$H/run/$1.pid")" 2>/dev/null; }
spawn() { local name=$1; shift; pid_alive "$name" && return 0; setsid nohup "$@" >>"$H/log/$name.log" 2>&1 < /dev/null & echo $! > "$H/run/$name.pid"; }
reap() { local name=$1 pid; pid_alive "$name" || { rm -f "$H/run/$name.pid"; return 0; }; pid=$(<"$H/run/$name.pid"); kill -- "-$pid" 2>/dev/null || kill "$pid" 2>/dev/null || true
    for _ in $(seq 1 30); do kill -0 "$pid" 2>/dev/null || break; sleep 0.5; done; kill -0 "$pid" 2>/dev/null && kill -9 -- "-$pid" 2>/dev/null || true; rm -f "$H/run/$name.pid"; }
wait_https() { for _ in $(seq 1 90); do curl -fsS -o /dev/null --max-time 2 --cacert "$H/cert.pem" "$1" 2>/dev/null && return 0; sleep 1; done; return 1; }
secrets() { # shellcheck disable=SC1091
    source "$H/secrets.env"; }

start_api() {
    secrets
    export BACKUP_PLATFORM_CONNECTION_STRING="Host=127.0.0.1;Port=$PG_PORT;Database=bp_discovery;Username=postgres;Password=$PG_PASSWORD"
    : > "$H/log/api.log"
    ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="https://127.0.0.1:$API_PORT;https://$PUBLIC_HOST:$API_PORT" \
    ASPNETCORE_Kestrel__Certificates__Default__Path="$H/cert.pfx" ASPNETCORE_Kestrel__Certificates__Default__Password="$CERT_PASSWORD" \
    Secrets__DataProtection__KeysDirectory="$H/dp-keys" Backups__Scheduling__Enabled=false Backups__Retention__Enabled=false \
        spawn api dotnet "$API_DLL"
    wait_https "https://127.0.0.1:$API_PORT/api/auth/antiforgery" || { tail -n 20 "$H/log/api.log" >&2; fail 'API did not become ready.'; }
}

start_agent() {
    secrets
    export SSL_CERT_FILE="$H/cert.pem"
    export Agent__Backend__BaseUri="https://127.0.0.1:$API_PORT" Agent__CredentialStoreDirectory="$H/agent-identity"
    export Agent__Connectivity__HeartbeatInterval=00:00:05 Agent__Connectivity__PollInterval=00:00:05 Agent__Connectivity__MinimumPollSpacing=00:00:01
    cd -- "$AGENT_DIR"
    spawn agent ./BackupPlatform.Agent.Host
    sleep 2
    pid_alive agent || { tail -n 20 "$H/log/agent.log" >&2; fail 'Agent exited during startup.'; }
}

container() { # name image port internal-port env... ; idempotent, owned names only
    local name=$1 image=$2 port=$3 internal=$4; shift 4
    docker container inspect "$name" >/dev/null 2>&1 && { docker start "$name" >/dev/null; return 0; }
    docker run --detach --name "$name" --publish "127.0.0.1:$port:$internal" "$@" "$image" >/dev/null
}

case ${1:-} in
up)
    for tool in docker dotnet bun openssl python3 curl; do require_command "$tool"; done
    mkdir -p "$H"/{run,log,dp-keys,agent-identity}; chmod 700 "$H"
    if [[ ! -f $H/secrets.env ]]; then
        printf 'PG_PASSWORD=%s\nMSSQL_PASSWORD=Disc1-%s\nTARGET_PG_PASSWORD=%s\nCERT_PASSWORD=%s\n' "$(openssl rand -hex 12)" "$(openssl rand -hex 12)" "$(openssl rand -hex 12)" "$(openssl rand -hex 12)" > "$H/secrets.env"
        chmod 600 "$H/secrets.env"
    fi
    secrets
    container bp-disc-platform-pg postgres:18 "$PG_PORT" 5432 -e "POSTGRES_PASSWORD=$PG_PASSWORD"
    container bp-disc-pg postgres:18 "$TARGET_PG_PORT" 5432 -e "POSTGRES_PASSWORD=$TARGET_PG_PASSWORD"
    container bp-disc-mssql mcr.microsoft.com/mssql/server:2025-latest "$MSSQL_PORT" 1433 -e ACCEPT_EULA=Y -e MSSQL_PID=Developer -e "MSSQL_SA_PASSWORD=$MSSQL_PASSWORD"
    for name in bp-disc-platform-pg bp-disc-pg; do
        for _ in $(seq 1 60); do docker exec "$name" pg_isready -U postgres >/dev/null 2>&1 && break; sleep 1; done
    done
    docker exec bp-disc-platform-pg psql -U postgres -Atc "SELECT 1 FROM pg_database WHERE datname='bp_discovery'" | grep -q 1 || docker exec bp-disc-platform-pg psql -U postgres -q -c 'CREATE DATABASE bp_discovery'
    for db in discapp discanalytics; do
        docker exec bp-disc-pg psql -U postgres -Atc "SELECT 1 FROM pg_database WHERE datname='$db'" | grep -q 1 || docker exec bp-disc-pg psql -U postgres -q -c "CREATE DATABASE $db"
    done
    for _ in $(seq 1 90); do docker logs bp-disc-mssql 2>&1 | grep -q 'SQL Server is now ready for client connections' && break; sleep 2; done
    for db in DiscSales DiscHr; do
        # The first logins right after "ready" can still fail while the instance finishes initialising.
        for attempt in $(seq 1 30); do
            docker exec -e SQLCMDPASSWORD="$MSSQL_PASSWORD" bp-disc-mssql /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -Q "IF DB_ID('$db') IS NULL CREATE DATABASE [$db]" >/dev/null 2>&1 && break
            (( attempt < 30 )) || fail 'SQL Server did not accept the SA login.'
            sleep 2
        done
    done
    printf 'Containers ready\n'

    if [[ ! -f $H/cert.pem ]]; then
        openssl req -x509 -newkey rsa:2048 -nodes -keyout "$H/key.pem" -out "$H/cert.pem" -days 2 -subj '/CN=Backup Platform discovery acceptance' \
            -addext "subjectAltName=DNS:localhost,IP:127.0.0.1,IP:$PUBLIC_HOST" -addext 'basicConstraints=critical,CA:TRUE' \
            -addext 'keyUsage=critical,digitalSignature,keyEncipherment,keyCertSign' >/dev/null 2>&1
        openssl pkcs12 -export -inkey "$H/key.pem" -in "$H/cert.pem" -out "$H/cert.pfx" -passout "pass:$CERT_PASSWORD" >/dev/null 2>&1
        chmod 600 "$H/key.pem" "$H/cert.pfx"
    fi
    (cd -- "$REPO_ROOT/backup-backend" && run_redacted dotnet build src/hosts/api -c Release --nologo -v q >/dev/null)
    export BACKUP_PLATFORM_CONNECTION_STRING="Host=127.0.0.1;Port=$PG_PORT;Database=bp_discovery;Username=postgres;Password=$PG_PASSWORD"
    (cd -- "$REPO_ROOT/backup-backend" && dotnet tool restore >/dev/null && run_redacted dotnet ef database update --project src/core/infrastructure --configuration Release --no-build >/dev/null)
    printf 'Database migrated\n'
    accounts="$H/accounts.yaml"
    python3 "$REPO_ROOT/scripts/dev/bootstrap.py" init-accounts --accounts-file "$accounts" --database bp_discovery --frontend "http://localhost:$WEB_PORT" \
        --backend "https://127.0.0.1:$API_PORT" --mssql-port "$MSSQL_PORT" --sql-reference disc_mssql >/dev/null
    BACKUP_PLATFORM_BOOTSTRAP_LOGIN=$(python3 "$REPO_ROOT/scripts/dev/bootstrap.py" get --accounts-file "$accounts" platform_admin_login) \
    BACKUP_PLATFORM_BOOTSTRAP_PASSWORD=$(python3 "$REPO_ROOT/scripts/dev/bootstrap.py" get --accounts-file "$accounts" platform_admin_password) \
    BACKUP_PLATFORM_BOOTSTRAP_DISPLAY_NAME='Discovery PlatformAdmin' run_redacted dotnet "$API_DLL" --bootstrap-user >/dev/null
    start_api
    printf 'API ready (https://127.0.0.1:%s, https://%s:%s)\n' "$API_PORT" "$PUBLIC_HOST" "$API_PORT"
    python3 "$REPO_ROOT/scripts/dev/bootstrap.py" seed --accounts-file "$accounts" --server "https://127.0.0.1:$API_PORT" --certificate "$H/cert.pem"

    (cd -- "$REPO_ROOT/backup-frontend" && BACKUP_API_PROXY_TARGET="https://127.0.0.1:$API_PORT" BACKUP_API_PROXY_INSECURE=true \
        spawn web bun run dev --host localhost --port "$WEB_PORT" --strictPort)
    for _ in $(seq 1 60); do curl -fsS -o /dev/null --max-time 2 "http://localhost:$WEB_PORT/" 2>/dev/null && break; sleep 1; done
    printf 'Frontend ready (http://localhost:%s)\n' "$WEB_PORT"

    run_redacted dotnet publish "$REPO_ROOT/backup-backend/src/hosts/agent" -c Release -r linux-x64 --self-contained true -p:AgentPlatform=Linux -o "$AGENT_DIR" --nologo -v q >/dev/null
    python3 "$REPO_ROOT/scripts/dev/bootstrap.py" enrollment-token --accounts-file "$accounts" --server "https://127.0.0.1:$API_PORT" --certificate "$H/cert.pem" --output "$H/run/enroll-token.json"
    "$REPO_ROOT/scripts/integration/enroll-agent.sh" --host "$AGENT_DIR/BackupPlatform.Agent.Host" --server "https://127.0.0.1:$API_PORT" --token-file "$H/run/enroll-token.json" \
        --store-directory "$H/agent-identity" --name disc-linux-agent --certificate "$H/cert.pem"
    rm -f "$H/run/enroll-token.json"
    start_agent
    printf 'Agent running\nDISC_READY accounts=%s cert=%s\n' "$accounts" "$H/cert.pem" ;;
restart-api) reap api; start_api; printf 'API restarted\n' ;;
stop-agent) reap agent; printf 'Agent stopped\n' ;;
kill-agent) [[ -f $H/run/agent.pid ]] && kill -9 "$(<"$H/run/agent.pid")" 2>/dev/null || true; rm -f "$H/run/agent.pid"; printf 'Agent killed\n' ;;
start-agent) start_agent; printf 'Agent started\n' ;;
status) for n in api web agent; do pid_alive "$n" && printf '%s: running\n' "$n" || printf '%s: stopped\n' "$n"; done; docker ps --filter name=bp-disc- --format '{{.Names}} {{.Ports}}' ;;
down)
    for n in agent web api; do reap "$n"; done
    docker rm --force --volumes bp-disc-platform-pg bp-disc-pg bp-disc-mssql >/dev/null 2>&1 || true
    rm -rf -- "$H"; printf 'Discovery acceptance stack removed\n' ;;
*) fail 'Usage: discovery-stack.sh up|down|status|restart-api|stop-agent|start-agent|kill-agent' ;;
esac
