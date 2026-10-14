#!/usr/bin/env bash
# Disposable acceptance data in the dedicated bp-dev-mssql container only.
#   create            creates bp_google_drive_acceptance (+ dbo.AcceptanceRows with five known rows) if absent
#   create-big        creates bp_google_drive_acceptance_big (~120 MB random rows) for interrupted-upload acceptance
#   verify DATABASE   prints state, row count and a content checksum; DATABASE must be bp_google_drive_acceptance[_*]
#   drop-restored DATABASE   drops a restored copy (bp_google_drive_acceptance_<suffix>); the source database is refused
set +x
set -euo pipefail
# shellcheck source=common.sh
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"
dev_prepare_state
source_db=bp_google_drive_acceptance
sql() { docker exec -i -e SQLCMDPASSWORD="$BP_DEV_MSSQL_SA_PASSWORD" "$BP_DEV_MSSQL_CONTAINER" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -h -1 -W; }
valid_name() { [[ $1 =~ ^bp_google_drive_acceptance(_[A-Za-z0-9]+)?$ ]] || fail 'Only bp_google_drive_acceptance[_suffix] databases are permitted.'; }
case ${1:-} in
    create)
        sql <<SQL
SET NOCOUNT ON;
IF DB_ID(N'$source_db') IS NULL CREATE DATABASE [$source_db];
GO
USE [$source_db];
IF OBJECT_ID(N'dbo.AcceptanceRows') IS NULL
BEGIN
  CREATE TABLE dbo.AcceptanceRows (Id int PRIMARY KEY, Marker nvarchar(100) NOT NULL, Amount decimal(12,2) NOT NULL, Payload nvarchar(max) NOT NULL);
  INSERT dbo.AcceptanceRows VALUES (1, N'google_drive_row_1', 10.50, REPLICATE(N'a', 4000)), (2, N'google_drive_row_2', 20.25, REPLICATE(N'b', 4000)),
    (3, N'google_drive_row_3', 30.00, REPLICATE(N'c', 4000)), (4, N'google_drive_row_4', 40.75, REPLICATE(N'd', 4000)), (5, N'türkçe_ğüşiöç_5', 50.10, REPLICATE(N'e', 4000));
END
GO
SQL
        printf 'Test database %s ready\n' "$source_db" ;;
    verify)
        valid_name "${2:?database}"
        sql <<SQL | paste -sd' ' -
SET NOCOUNT ON;
SELECT state_desc FROM sys.databases WHERE name=N'$2';
SELECT COUNT(*) FROM [$2].dbo.AcceptanceRows;
SELECT CHECKSUM_AGG(CHECKSUM(Id, Marker, Amount, Payload)) FROM [$2].dbo.AcceptanceRows;
GO
SQL
        ;;
    drop-restored)
        valid_name "${2:?database}"; [[ $2 != "$source_db" ]] || fail 'Refusing to drop the source database.'
        sql <<SQL
IF DB_ID(N'$2') IS NOT NULL BEGIN ALTER DATABASE [$2] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$2]; END
GO
SQL
        printf 'Dropped %s\n' "$2" ;;
    create-big)
        # ~120 MB of incompressible rows: long enough to interrupt a real Drive upload. Idempotent; drop with drop-restored.
        sql <<SQL
SET NOCOUNT ON;
IF DB_ID(N'${source_db}_big') IS NULL CREATE DATABASE [${source_db}_big];
GO
USE [${source_db}_big];
IF OBJECT_ID(N'dbo.Blobs') IS NULL
BEGIN
  CREATE TABLE dbo.Blobs (Id int IDENTITY PRIMARY KEY, Payload varbinary(8000) NOT NULL);
  INSERT dbo.Blobs (Payload) SELECT TOP (15000) CRYPT_GEN_RANDOM(8000) FROM sys.all_objects a CROSS JOIN sys.all_objects b;
END
GO
SQL
        printf 'Test database %s_big ready\n' "$source_db" ;;
    *) fail 'Usage: mssql-test-db.sh create | create-big | verify DATABASE | drop-restored DATABASE' ;;
esac
