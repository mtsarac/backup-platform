#!/usr/bin/env bash
# Random sample databases (bpsample_*) with real rows on the four backup targets, for exercising backup/restore by hand:
#   laptop Docker SQL Server (container mssql) and PostgreSQL (container postgres), Windows VM SQL Server MSSQLSERVER25 and PostgreSQL.
#   sample-databases.sh create [--target local-mssql|local-pg|vm-mssql|vm-pg|all] [--count N]   N databases per target (default 3, sizes ~5/25/70 MB, cycling)
#   sample-databases.sh list   [--target ...]
#   sample-databases.sh drop   [--target ...]   drops only databases named bpsample_*; nothing else is ever touched
# Credentials come from docs/test-cred.yaml at runtime and are handed to the client containers by environment, never printed.
set +x
set -euo pipefail
# shellcheck source=scripts/lib/common.sh
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../lib" && pwd)/common.sh"

action=${1:-}; shift || true
target=all count=3
while (($#)); do
    case $1 in
        --target) target=${2:?--target needs a value}; shift 2 ;;
        --count) count=${2:?--count needs a value}; [[ $count =~ ^[1-9][0-9]?$ ]] || fail '--count must be 1-99.'; shift 2 ;;
        *) fail 'Unknown option.' ;;
    esac
done
[[ $action == create || $action == list || $action == drop ]] || fail 'Usage: sample-databases.sh create|list|drop [--target local-mssql|local-pg|vm-mssql|vm-pg|all] [--count N]'
[[ $target =~ ^(all|local-mssql|local-pg|vm-mssql|vm-pg)$ ]] || fail 'Unknown target.'

load_local_env
local_mssql_password=$BACKUP_PLATFORM_TEST_MSSQL_PASSWORD
local_mssql_user=$BACKUP_PLATFORM_TEST_MSSQL_USER
local_pg_password=$BACKUP_PLATFORM_TEST_POSTGRES_PASSWORD
vm_host=${WINDOWS_SSH_HOST:-192.168.122.218}
unset BACKUP_PLATFORM_TEST_MSSQL_HOST BACKUP_PLATFORM_TEST_MSSQL_PORT BACKUP_PLATFORM_TEST_MSSQL_USER BACKUP_PLATFORM_TEST_MSSQL_PASSWORD  # explicit environment wins in local-env.py
BP_MSSQL_PROFILE=windows load_local_env
vm_mssql_password=$BACKUP_PLATFORM_TEST_MSSQL_PASSWORD
vm_mssql_user=$BACKUP_PLATFORM_TEST_MSSQL_USER
vm_mssql_host=$BACKUP_PLATFORM_TEST_MSSQL_HOST,$BACKUP_PLATFORM_TEST_MSSQL_PORT
vm_pg_password=$BACKUP_PLATFORM_TEST_WINDOWS_POSTGRES_PASSWORD
vm_pg_user=$BACKUP_PLATFORM_TEST_WINDOWS_POSTGRES_USER

words_a=(amber brisk cobalt dusty ember fuzzy golden hazel indigo jolly keen lunar mellow nimble opal plush quartz rustic sunny tidal umber vivid witty zesty)
words_b=(anchor bridge canyon delta engine forest garden harbor island jungle kernel lagoon meadow nebula orchard pebble quarry river summit tunnel valley willow)
scales=(1 5 15)   # orders = 10000 x scale, customers = 2000 x scale
rand_name() { printf 'bpsample_%s_%s_%s' "${words_a[RANDOM % ${#words_a[@]}]}" "${words_b[RANDOM % ${#words_b[@]}]}" "$(openssl rand -hex 2)"; }

# Local servers: clients run inside the owned Docker containers. The VM SQL Server is reached over the libvirt bridge; the VM PostgreSQL accepts only
# local connections (pg_hba.conf is left untouched), so psql runs on the VM through the owned SSH helper with a disposable script that streams via stdin.
mssql() { # host user password
    docker exec -i -e SQLCMDPASSWORD="$3" mssql /opt/mssql-tools18/bin/sqlcmd -S "$1" -U "$2" -C -b -h -1 -W
}
ps_quote() { printf "'%s'" "${1//\'/\'\'}"; }
vm_psql() { # user password [psql args]; SQL on stdin
    local user=$1 password=$2 sql='' arg args='' script
    shift 2
    [[ " $* " == *" -c "* ]] || sql=$(cat)
    for arg in "$@"; do args+=" $(ps_quote "$arg")"; done
    script="$RUNTIME_DIR/vm-psql.ps1"
    printf '%s\n' "\$env:PGPASSWORD = $(ps_quote "$password")" "\$sql = @'" "$sql" "'@" \
        "\$sql | & 'C:\\Program Files\\PostgreSQL\\18\\bin\\psql.exe' -h localhost -U $(ps_quote "$user") -v ON_ERROR_STOP=1 -q -At$args" \
        "if (\$LASTEXITCODE -ne 0) { exit \$LASTEXITCODE }" > "$script"
    local status=0
    "$REPO_ROOT/scripts/windows/run-powershell.sh" --file "$script" 2>&1 | { grep -vE '^\*\*|^#< CLIXML|^<Objs|^$' || true; } || status=${PIPESTATUS[0]}
    rm -f -- "$script"
    return "$status"
}
pg() { # host user password [psql args]
    local host=$1 user=$2 password=$3; shift 3
    if [[ $host == "$vm_host" ]]; then vm_psql "$user" "$password" "$@"; return; fi
    docker exec -i -e PGPASSWORD="$password" postgres psql -h "$host" -U "$user" -v ON_ERROR_STOP=1 -q -At "$@"
}

mssql_create() { # host user password name scale
    local name=$4 scale=$5 customers=$((2000 * $5)) orders=$((10000 * $5))
    mssql "$1" "$2" "$3" <<SQL
SET NOCOUNT ON;
CREATE DATABASE [$name];
GO
USE [$name];
CREATE TABLE dbo.Customers (Id int IDENTITY PRIMARY KEY, Name nvarchar(80) NOT NULL, Email nvarchar(120) NOT NULL, Country nchar(2) NOT NULL, CreatedAt datetime2 NOT NULL);
CREATE TABLE dbo.Orders (Id int IDENTITY PRIMARY KEY, CustomerId int NOT NULL REFERENCES dbo.Customers(Id), Amount decimal(12,2) NOT NULL, Status varchar(12) NOT NULL, PlacedAt datetime2 NOT NULL, Notes nvarchar(400) NOT NULL);
CREATE INDEX IX_Orders_CustomerId ON dbo.Orders(CustomerId);
CREATE TABLE dbo.Blobs (Id int IDENTITY PRIMARY KEY, Payload varbinary(8000) NOT NULL);
WITH n AS (SELECT TOP ($customers) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
INSERT dbo.Customers (Name, Email, Country, CreatedAt)
SELECT CONCAT(N'Müşteri ', i, N' ', LEFT(CONVERT(varchar(36), NEWID()), 8)), CONCAT(LOWER(LEFT(CONVERT(varchar(36), NEWID()), 10)), N'@example.test'),
       SUBSTRING(N'TRDEUSGBFR', 1 + 2 * (ABS(CHECKSUM(NEWID())) % 5), 2), DATEADD(day, -(ABS(CHECKSUM(NEWID())) % 900), SYSUTCDATETIME()) FROM n;
WITH n AS (SELECT TOP ($orders) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
INSERT dbo.Orders (CustomerId, Amount, Status, PlacedAt, Notes)
SELECT 1 + ABS(CHECKSUM(NEWID())) % $customers, CAST(ABS(CHECKSUM(NEWID())) % 1000000 AS decimal(12,2)) / 100,
       CHOOSE(1 + (i * 7919) % 4, 'new', 'paid', 'shipped', 'cancelled'), DATEADD(minute, -(ABS(CHECKSUM(NEWID())) % 1000000), SYSUTCDATETIME()),
       REPLICATE(CONVERT(nvarchar(36), NEWID()), 5) FROM n;
INSERT dbo.Blobs (Payload) SELECT TOP (${scale}00) CRYPT_GEN_RANDOM(8000) FROM sys.all_objects;
GO
SQL
}

pg_create() { # host user password name scale
    local name=$4 customers=$((2000 * $5)) orders=$((10000 * $5))
    pg "$1" "$2" "$3" -d postgres -c "CREATE DATABASE $name"
    pg "$1" "$2" "$3" -d "$name" <<SQL
CREATE TABLE customers (id serial PRIMARY KEY, name text NOT NULL, email text NOT NULL, country char(2) NOT NULL, created_at timestamptz NOT NULL);
CREATE TABLE orders (id serial PRIMARY KEY, customer_id int NOT NULL REFERENCES customers(id), amount numeric(12,2) NOT NULL, status text NOT NULL, placed_at timestamptz NOT NULL, notes text NOT NULL);
CREATE INDEX ix_orders_customer_id ON orders(customer_id);
CREATE TABLE events (id bigserial PRIMARY KEY, payload jsonb NOT NULL, created_at timestamptz NOT NULL DEFAULT now());
INSERT INTO customers (name, email, country, created_at)
SELECT 'Customer ' || i || ' ' || left(md5(random()::text), 6), left(md5(random()::text), 10) || '@example.test',
       (ARRAY['TR','DE','US','GB','FR'])[1 + floor(random() * 5)::int], now() - random() * interval '900 days' FROM generate_series(1, $customers) i;
INSERT INTO orders (customer_id, amount, status, placed_at, notes)
SELECT 1 + floor(random() * $customers)::int, round((random() * 10000)::numeric, 2), (ARRAY['new','paid','shipped','cancelled'])[1 + floor(random() * 4)::int],
       now() - random() * interval '700 days', repeat(md5(random()::text), 10) FROM generate_series(1, $orders);
INSERT INTO events (payload) SELECT jsonb_build_object('id', i, 'kind', (ARRAY['click','view','buy'])[1 + floor(random() * 3)::int], 'token', md5(random()::text)) FROM generate_series(1, $orders) i;
CREATE VIEW paid_totals AS SELECT customer_id, sum(amount) AS total FROM orders WHERE status = 'paid' GROUP BY customer_id;
SQL
}

mssql_list() { mssql "$1" "$2" "$3" <<'SQL'
SET NOCOUNT ON;
SELECT d.name, CAST(SUM(m.size) * 8 / 1024 AS int) FROM sys.databases d JOIN sys.master_files m ON m.database_id = d.database_id WHERE d.name LIKE N'bpsample[_]%' GROUP BY d.name ORDER BY d.name;
GO
SQL
}
pg_list() { pg "$1" "$2" "$3" -d postgres -c "SELECT datname, pg_database_size(datname) / 1048576 FROM pg_database WHERE datname LIKE 'bpsample\_%' ORDER BY 1"; }

mssql_drop() { # host user password
    local db
    while read -r db _; do
        [[ $db == bpsample_* ]] || continue
        mssql "$1" "$2" "$3" <<SQL
ALTER DATABASE [$db] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
DROP DATABASE [$db];
GO
SQL
        printf '  dropped %s\n' "$db"
    done < <(mssql_list "$1" "$2" "$3")
}
pg_drop() {
    local db
    while read -r db; do
        db=${db%%|*}
        [[ $db == bpsample_* ]] || continue
        pg "$1" "$2" "$3" -d postgres -c "DROP DATABASE $db WITH (FORCE)"
        printf '  dropped %s\n' "$db"
    done < <(pg_list "$1" "$2" "$3")
}

run() { # label kind host user password
    local label=$1 kind=$2; shift 2
    printf '%s\n' "== $label"
    case $action in
        list) "${kind}_list" "$@" | sed 's/^/  /' ;;
        drop) "${kind}_drop" "$@" ;;
        create)
            local i name
            for ((i = 0; i < count; i++)); do
                name=$(rand_name)
                "${kind}_create" "$@" "$name" "${scales[i % ${#scales[@]}]}"
                printf '  created %s (scale x%s)\n' "$name" "${scales[i % ${#scales[@]}]}"
            done ;;
    esac
}

[[ $target != all && $target != local-mssql ]] || run 'laptop SQL Server (Docker)' mssql localhost,1433 "$local_mssql_user" "$local_mssql_password"
[[ $target != all && $target != local-pg ]] || run 'laptop PostgreSQL (Docker)' pg localhost "postgres" "$local_pg_password"
[[ $target != all && $target != vm-mssql ]] || run 'Windows VM SQL Server (MSSQLSERVER25)' mssql "$vm_mssql_host" "$vm_mssql_user" "$vm_mssql_password"
[[ $target != all && $target != vm-pg ]] || run 'Windows VM PostgreSQL' pg "$vm_host" "$vm_pg_user" "$vm_pg_password"
