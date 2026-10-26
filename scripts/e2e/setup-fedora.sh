#!/usr/bin/env bash
# Runs ON the Fedora VM as root: PostgreSQL test data, a file tree and an SFTP target user. Passwords come from the environment.
set -euo pipefail
: "${PG_PASSWORD:?}" "${SFTP_PASSWORD:?}"
sed -i 's/^#\?listen_addresses.*/listen_addresses = '"'"'127.0.0.1'"'"'/' /var/lib/pgsql/data/postgresql.conf
sed -i -E 's#^(host\s+all\s+all\s+(127\.0\.0\.1/32|::1/128)\s+)ident#\1scram-sha-256#' /var/lib/pgsql/data/pg_hba.conf
systemctl restart postgresql
sudo -u postgres psql -qtc "select 1 from pg_roles where rolname='bp_e2e'" | grep -q 1 || sudo -u postgres psql -qc "create role bp_e2e login createdb password '$PG_PASSWORD'"
sudo -u postgres psql -qtc "select 1 from pg_database where datname='e2e_src'" | grep -q 1 || sudo -u postgres createdb -O bp_e2e e2e_src
PGPASSWORD=$PG_PASSWORD psql -h 127.0.0.1 -U bp_e2e -d e2e_src -q <<SQL
create table if not exists items(id serial primary key, name text, payload text);
insert into items(name,payload) select 'item'||g, md5(g::text) from generate_series(1,50000) g where not exists (select 1 from items);
SQL
mkdir -p /srv/e2e/files/{docs,bin,nested/a/b}
[ -f /srv/e2e/files/bin/big.bin ] || head -c 64M /dev/urandom > /srv/e2e/files/bin/big.bin
for i in $(seq 1 200); do echo "file $i" > /srv/e2e/files/docs/f$i.txt; done
echo "türkçe içerik ğüşiöç" > /srv/e2e/files/nested/a/b/unicode-ğüş.txt
chown -R backup-agent: /srv/e2e 2>/dev/null || true
id e2e-sftp >/dev/null 2>&1 || useradd -m e2e-sftp
echo "e2e-sftp:$SFTP_PASSWORD" | chpasswd
mkdir -p /home/e2e-sftp/backups && chown e2e-sftp: /home/e2e-sftp/backups
(cd /srv/e2e/files && find . -type f -exec sha256sum {} + | sort -k2) > /srv/e2e/files.sha256
echo setup-ok
