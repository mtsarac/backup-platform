#!/usr/bin/env bash
# Runs ON the Fedora VM as root: seeds (or drops) the ~1 GB PostgreSQL source DB e2e_big for the large-backup cell. Repeatable:
# g inside the payload subquery keeps it per-row (uncorrelated = one payload for every row). setseed makes the rows deterministic, and an existing DB with the right row count is left alone. Usage: seed-large-fedora.sh [seed|drop]
set -euo pipefail
ROWS=${ROWS:-1000000}   # 1 KiB hex payload per row -> ~1.1 GB of table data
PSQL="sudo -u postgres psql -qtA"
if [ "${1:-seed}" = drop ]; then $PSQL -c "drop database if exists e2e_big"; echo drop-ok; exit 0; fi
have=$($PSQL -d e2e_big -c "select count(*) from big" 2>/dev/null || echo 0)
if [ "$have" = "$ROWS" ]; then echo "seed-ok (already $ROWS rows)"; exit 0; fi
$PSQL -c "drop database if exists e2e_big" && $PSQL -c "create database e2e_big owner bp_e2e"
$PSQL -v ON_ERROR_STOP=1 -d e2e_big <<SQL
select setseed(0.42);
create table big(id int primary key, name text, payload text);
alter table big owner to bp_e2e;
insert into big select g, 'row'||g, (select string_agg(md5(random()::text||g||k), '') from generate_series(1,32) k)  from generate_series(1,$ROWS) g;
SQL
$PSQL -d e2e_big -c "select pg_size_pretty(pg_database_size('e2e_big'))"
echo seed-ok
