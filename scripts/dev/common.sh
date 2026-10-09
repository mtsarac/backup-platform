#!/usr/bin/env bash
# Shared by scripts/dev/*.sh (sourced). Persistent local development stack: nothing here is disposable.
# All mutable state lives outside the repository in $BP_DEV_HOME; secrets stay in owner-only (0600) files.
# shellcheck source=../lib/common.sh
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../lib" && pwd)/common.sh"

BP_DEV_HOME=${BP_DEV_HOME:-$HOME/.local/share/backup-platform-dev}
BP_DEV_DATABASE=${BP_DEV_DATABASE:-backup_platform_dev}
BP_DEV_API_HTTP_PORT=5252     # must equal the redirect URI registered with Google
BP_DEV_API_HTTPS_PORT=7179    # Agent and the Vite proxy (the Agent protocol requires HTTPS)
BP_DEV_WEB_PORT=5173
BP_DEV_MSSQL_CONTAINER=${BP_DEV_MSSQL_CONTAINER:-bp-dev-mssql}
BP_DEV_MSSQL_PORT=14331
BP_DEV_SQL_CREDENTIAL_REFERENCE=sql_dev
BP_DEV_ACCOUNTS_FILE="$REPO_ROOT/docs/local-development-accounts.txt"
BP_DEV_FRONTEND_URL="http://localhost:$BP_DEV_WEB_PORT"
BP_DEV_API_URL="http://localhost:$BP_DEV_API_HTTP_PORT"
BP_DEV_API_TLS_URL="https://localhost:$BP_DEV_API_HTTPS_PORT"
BP_DEV_API_DLL="$REPO_ROOT/backup-backend/src/hosts/api/bin/Release/net10.0/BackupPlatform.Api.dll"

dev_prepare_state() {
    mkdir -p "$BP_DEV_HOME"/{run,log,dp-keys,mssql/staging,mssql/data,agent-identity,agent-bin}
    chmod 700 "$BP_DEV_HOME" "$BP_DEV_HOME/dp-keys" "$BP_DEV_HOME/agent-identity"
    chmod 770 "$BP_DEV_HOME/mssql/staging"
    if [[ ! -f $BP_DEV_HOME/secrets.env ]]; then
        printf 'BP_DEV_MSSQL_SA_PASSWORD=Bp1-%s\nBP_DEV_CERT_PASSWORD=%s\n' "$(openssl rand -hex 20)" "$(openssl rand -hex 16)" > "$BP_DEV_HOME/secrets.env"
        chmod 600 "$BP_DEV_HOME/secrets.env"
    fi
    # shellcheck disable=SC1091
    source "$BP_DEV_HOME/secrets.env"
    if [[ ! -f $BP_DEV_HOME/cert.pfx ]]; then
        openssl req -x509 -newkey rsa:2048 -nodes -keyout "$BP_DEV_HOME/key.pem" -out "$BP_DEV_HOME/cert.pem" -days 800 \
            -subj '/CN=Backup Platform local development' -addext 'subjectAltName=DNS:localhost,IP:127.0.0.1' \
            -addext 'basicConstraints=critical,CA:TRUE' -addext 'keyUsage=critical,digitalSignature,keyEncipherment,keyCertSign' >/dev/null 2>&1
        openssl pkcs12 -export -inkey "$BP_DEV_HOME/key.pem" -in "$BP_DEV_HOME/cert.pem" -out "$BP_DEV_HOME/cert.pfx" -passout "pass:$BP_DEV_CERT_PASSWORD" >/dev/null 2>&1
        chmod 600 "$BP_DEV_HOME/key.pem" "$BP_DEV_HOME/cert.pfx"
    fi
}

# Replaces only the Database segment of the administrative PostgreSQL connection string from docs/test-cred.txt.
dev_connection_string() {
    python3 - "$BACKUP_PLATFORM_TEST_CONNECTION_STRING" "$BP_DEV_DATABASE" <<'PY'
import sys
parts = [p for p in sys.argv[1].split(';') if p.strip() and not p.strip().lower().startswith('database=')]
print(';'.join(parts + ['Database="%s"' % sys.argv[2]]))
PY
}

dev_pid_alive() { [[ -f $BP_DEV_HOME/run/$1.pid ]] && kill -0 "$(<"$BP_DEV_HOME/run/$1.pid")" 2>/dev/null; }

# dev_spawn NAME CMD... : detached background process (survives this shell), pid + log under $BP_DEV_HOME.
dev_spawn() {
    local name=$1; shift
    dev_pid_alive "$name" && return 0
    setsid nohup "$@" >>"$BP_DEV_HOME/log/$name.log" 2>&1 < /dev/null &
    printf '%s\n' "$!" > "$BP_DEV_HOME/run/$name.pid"
}

dev_kill() {
    local name=$1 pid
    dev_pid_alive "$name" || { rm -f "$BP_DEV_HOME/run/$name.pid"; return 0; }
    pid=$(<"$BP_DEV_HOME/run/$name.pid")
    # setsid made the process a group leader: stop its children (dotnet/bun workers) as well.
    kill -- "-$pid" 2>/dev/null || kill "$pid" 2>/dev/null || true
    for _ in $(seq 1 30); do kill -0 "$pid" 2>/dev/null || break; sleep 0.5; done
    kill -0 "$pid" 2>/dev/null && kill -9 -- "-$pid" 2>/dev/null || true
    rm -f "$BP_DEV_HOME/run/$name.pid"
}

dev_wait_http() { # URL SECONDS [--cacert FILE]
    local url=$1 seconds=$2; shift 2
    for _ in $(seq 1 "$seconds"); do
        curl -fsS -o /dev/null --max-time 2 "$@" "$url" 2>/dev/null && return 0
        sleep 1
    done
    return 1
}
