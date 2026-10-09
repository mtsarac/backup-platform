#!/usr/bin/env bash
# Starts (idempotently) the persistent local development stack:
#   dedicated MSSQL container (bp-dev-mssql) -> PostgreSQL database -> migrations -> API -> accounts/plan bootstrap -> Vite -> Linux Agent.
# Never stops, recreates or resets any other container or database. Re-running is safe.
set +x
set -euo pipefail
# shellcheck source=common.sh
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

build=true agent=true
while (($#)); do
    case "$1" in
        --no-build) build=false ;;
        --no-agent) agent=false ;;
        -h|--help)
            printf '%s\n' 'Usage: scripts/dev/start.sh [--no-build] [--no-agent]' \
                'Needs docs/test-cred.txt and docs/client_secret_*.json (read at runtime, never printed). State: $BP_DEV_HOME (default ~/.local/share/backup-platform-dev).'
            exit 0 ;;
        *) fail 'Unknown option; use --help.' ;;
    esac
    shift
done

load_local_env
for tool in docker dotnet bun openssl python3 curl jq; do require_command "$tool"; done
[[ -n ${BACKUP_PLATFORM_TEST_CONNECTION_STRING:-} ]] || fail 'BACKUP_PLATFORM_TEST_CONNECTION_STRING is not available from docs/test-cred.txt.'
dev_prepare_state

# A browser re-download leaves 'name(1).json' duplicates: prefer the original file name.
google_json=$(compgen -G "$REPO_ROOT/docs/client_secret_*.json" | sort | grep -v '(' | head -n1 || true)
[[ -n $google_json ]] || fail 'docs/client_secret_*.json is missing.'
[[ $(stat -c %a "$google_json") == 600 ]] || chmod 600 "$google_json"
jq -e '.web.client_id and .web.client_secret and (.web.redirect_uris | index("'"$BP_DEV_API_URL"'/api/platform/google-drive/oauth/callback"))' "$google_json" >/dev/null \
    || fail "The Google OAuth client does not register $BP_DEV_API_URL/api/platform/google-drive/oauth/callback; add exactly that Authorized redirect URI in Google Cloud Console."

# 1. Dedicated SQL Server: the shared 'mssql' container keeps its staging in a root-only Docker volume the host Agent cannot read,
#    so the Agent's Mapped staging needs a bind mount. This container is owned by this stack and is only ever stopped, never removed.
if ! docker container inspect "$BP_DEV_MSSQL_CONTAINER" >/dev/null 2>&1; then
    MSSQL_SA_PASSWORD=$BP_DEV_MSSQL_SA_PASSWORD docker run --detach --name "$BP_DEV_MSSQL_CONTAINER" --restart unless-stopped \
        --user "$(id -u):10001" --env HOME=/var/opt/mssql --env ACCEPT_EULA=Y --env MSSQL_PID=Developer --env MSSQL_SA_PASSWORD \
        --publish "127.0.0.1:$BP_DEV_MSSQL_PORT:1433" \
        --volume "$BP_DEV_HOME/mssql/staging:/var/opt/mssql/staging" --volume "$BP_DEV_HOME/mssql/data:/var/opt/mssql/data" \
        mcr.microsoft.com/mssql/server:2025-latest >/dev/null
elif [[ $(docker inspect --format '{{.State.Running}}' "$BP_DEV_MSSQL_CONTAINER") != true ]]; then
    docker start "$BP_DEV_MSSQL_CONTAINER" >/dev/null
fi
for _ in $(seq 1 90); do
    docker logs "$BP_DEV_MSSQL_CONTAINER" 2>&1 | grep -q 'SQL Server is now ready for client connections' && break
    sleep 2
done
docker exec "$BP_DEV_MSSQL_CONTAINER" bash -c 'echo probe > /var/opt/mssql/staging/.probe'
[[ $(<"$BP_DEV_HOME/mssql/staging/.probe") == probe ]] || fail 'SQL Server staging bind mount is not readable by the Agent side.'
rm -f "$BP_DEV_HOME/mssql/staging/.probe"
printf 'MSSQL ready (%s on 127.0.0.1:%s)\n' "$BP_DEV_MSSQL_CONTAINER" "$BP_DEV_MSSQL_PORT"

# 2. Development database: created once in the existing PostgreSQL container, never dropped by any dev script.
postgres_container=${POSTGRES_CONTAINER:-postgres}
if [[ $(docker exec "$postgres_container" psql -U postgres -Atc "SELECT 1 FROM pg_database WHERE datname='$BP_DEV_DATABASE'") != 1 ]]; then
    docker exec "$postgres_container" psql -U postgres -v ON_ERROR_STOP=1 -q -c "CREATE DATABASE \"$BP_DEV_DATABASE\"" >/dev/null
    printf 'Created PostgreSQL database %s\n' "$BP_DEV_DATABASE"
fi
BACKUP_PLATFORM_CONNECTION_STRING=$(dev_connection_string)
export BACKUP_PLATFORM_CONNECTION_STRING

# 3. Build + migrate (EF migrations are idempotent).
cd -- "$REPO_ROOT/backup-backend"
if [[ $build == true || ! -f $BP_DEV_API_DLL ]]; then
    run_redacted dotnet build src/hosts/api -c Release --nologo -v q >/dev/null
fi
run_redacted dotnet tool restore >/dev/null
run_redacted dotnet ef database update --project src/core/infrastructure --configuration Release --no-build >/dev/null
printf 'Database migrated\n'

# 4. First PlatformAdmin: the real CLI, only when no credential exists yet; the password lives only in the accounts file.
if [[ ! -f $BP_DEV_ACCOUNTS_FILE ]] || ! grep -q '^platform_admin_login:' "$BP_DEV_ACCOUNTS_FILE"; then
    python3 "$REPO_ROOT/scripts/dev/bootstrap.py" init-accounts --accounts-file "$BP_DEV_ACCOUNTS_FILE" --database "$BP_DEV_DATABASE" \
        --frontend "$BP_DEV_FRONTEND_URL" --backend "$BP_DEV_API_URL" \
        --mssql-port "$BP_DEV_MSSQL_PORT" --sql-reference "$BP_DEV_SQL_CREDENTIAL_REFERENCE"
    BACKUP_PLATFORM_BOOTSTRAP_LOGIN=$(python3 "$REPO_ROOT/scripts/dev/bootstrap.py" get --accounts-file "$BP_DEV_ACCOUNTS_FILE" platform_admin_login) \
    BACKUP_PLATFORM_BOOTSTRAP_PASSWORD=$(python3 "$REPO_ROOT/scripts/dev/bootstrap.py" get --accounts-file "$BP_DEV_ACCOUNTS_FILE" platform_admin_password) \
    BACKUP_PLATFORM_BOOTSTRAP_DISPLAY_NAME='Local PlatformAdmin' \
        run_redacted dotnet "$BP_DEV_API_DLL" --bootstrap-user >/dev/null
    printf 'PlatformAdmin bootstrapped\n'
fi

# 5. API. Google credentials are read from the JSON file into this process environment only.
# Dev only: a requested deletion becomes physical within seconds (production default: 1 day grace) so retention can be exercised by hand.
if ! dev_pid_alive api; then
    : > "$BP_DEV_HOME/log/api.log"
    GoogleDrive__ClientId=$(jq -r .web.client_id "$google_json") GoogleDrive__ClientSecret=$(jq -r .web.client_secret "$google_json") \
    GoogleDrive__RedirectUri="$BP_DEV_API_URL/api/platform/google-drive/oauth/callback" GoogleDrive__FrontendOrigin="$BP_DEV_FRONTEND_URL" \
    GoogleDrive__StagingDirectory="$BP_DEV_HOME/gdrive-staging" \
    Secrets__DataProtection__KeysDirectory="$BP_DEV_HOME/dp-keys" \
    ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="$BP_DEV_API_URL;$BP_DEV_API_TLS_URL" \
    ASPNETCORE_Kestrel__Certificates__Default__Path="$BP_DEV_HOME/cert.pfx" ASPNETCORE_Kestrel__Certificates__Default__Password="$BP_DEV_CERT_PASSWORD" \
    Backups__Retention__MinimumDeleteGracePeriod=00:00:01 Backups__Retention__DefaultDeleteGracePeriod=00:00:10 Backups__Retention__ScanInterval=00:00:05 \
        dev_spawn api dotnet "$BP_DEV_API_DLL"
fi
mkdir -p "$BP_DEV_HOME/gdrive-staging"; chmod 700 "$BP_DEV_HOME/gdrive-staging"
dev_wait_http "$BP_DEV_API_TLS_URL/api/auth/antiforgery" 90 --cacert "$BP_DEV_HOME/cert.pem" || { tail -n 20 "$BP_DEV_HOME/log/api.log" >&2; fail 'API did not become ready.'; }
printf 'API ready (%s, %s)\n' "$BP_DEV_API_URL" "$BP_DEV_API_TLS_URL"

# 6. Tenant, users, memberships, 5 GB plan + subscription, agent enrollment switch: idempotent, through the real HTTP API.
python3 "$REPO_ROOT/scripts/dev/bootstrap.py" seed --accounts-file "$BP_DEV_ACCOUNTS_FILE" --server "$BP_DEV_API_TLS_URL" --certificate "$BP_DEV_HOME/cert.pem"

# 7. Frontend (Vite, same-origin /api proxy to the HTTPS API; the dev certificate is private to this machine).
if ! dev_pid_alive web; then
    (cd -- "$REPO_ROOT/backup-frontend" && BACKUP_API_PROXY_TARGET=$BP_DEV_API_TLS_URL BACKUP_API_PROXY_INSECURE=true \
        dev_spawn web bun run dev --host localhost --port "$BP_DEV_WEB_PORT" --strictPort)
fi
dev_wait_http "$BP_DEV_FRONTEND_URL/" 60 || fail 'Frontend did not become ready.'
printf 'Frontend ready (%s)\n' "$BP_DEV_FRONTEND_URL"

agent_args=(start); [[ $build == true ]] || agent_args+=(--no-build)
[[ $agent == false ]] || "$REPO_ROOT/scripts/dev/agent.sh" "${agent_args[@]}"
printf 'DEV_READY\n'
