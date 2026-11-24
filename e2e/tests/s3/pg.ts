// PostgreSQL on the Fedora VM: setup helpers (not the flow under test) and data fingerprints for restore comparisons.
import { vm } from './fixture'

export const PG = { user: 'e2e_s3', password: 'E2eS3-Passw0rd', database: 'e2e_s3_src' }

/** (Re)creates the role and source database with a small deterministic data set. */
export function seedSourceDatabase(database = PG.database, rows = 500) {
  vm(`set -e
sudo -n -u postgres psql -qtA -c "select 1 from pg_roles where rolname='${PG.user}'" | grep -q 1 || sudo -n -u postgres psql -q -c "create role ${PG.user} login password '${PG.password}'"
sudo -n -u postgres psql -q -c "alter role ${PG.user} nosuperuser"
sudo -n -u postgres psql -q -c "drop database if exists ${database}" -c "create database ${database} owner ${PG.user}"
export PGPASSWORD='${PG.password}'
psql -q -h 127.0.0.1 -U ${PG.user} ${database} -c "create table customers(id int primary key, name text, balance numeric(12,2))" -c "insert into customers select g, 'customer-' || g, g * 1.5 from generate_series(1, ${rows}) g"`)
}

export const databaseFingerprint = (database: string) =>
  vm(`PGPASSWORD='${PG.password}' psql -qtA -h 127.0.0.1 -U ${PG.user} ${database} -c "select count(*) || ':' || sum(balance) || ':' || md5(string_agg(name, ',' order by id)) from customers"`).trim()
