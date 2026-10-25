"""Seed win11 test data (MSSQL + PostgreSQL e2e_src DBs and a file tree) using docs/test-cred.yaml; secrets never printed."""
import api, yaml, subprocess, base64
c = yaml.safe_load((api.ROOT / "docs/test-cred.yaml").read_text())["windows11_vm"]
ms, pg = c["mssql"], c["postgresql"]
ps = f"""
$ErrorActionPreference='Stop'
$env:PGPASSWORD='{pg['password']}'
$psql='C:\\Program Files\\PostgreSQL\\18\\bin\\psql.exe'
if(-not (& $psql -h 127.0.0.1 -U {pg['user']} -d postgres -tAc "select 1 from pg_database where datname='e2e_src'")){{ & $psql -h 127.0.0.1 -U {pg['user']} -d postgres -c "create database e2e_src" }}
& $psql -h 127.0.0.1 -U {pg['user']} -d e2e_src -c "create table if not exists items(id serial primary key, name text, payload text); insert into items(name,payload) select 'item'||g, md5(g::text) from generate_series(1,50000) g where not exists (select 1 from items);"
$c="IF DB_ID('e2e_src') IS NULL CREATE DATABASE e2e_src"
sqlcmd -S 127.0.0.1,{ms['port']} -U {ms['user']} -P '{ms['password']}' -C -b -Q $c
$q="IF OBJECT_ID('items') IS NULL BEGIN CREATE TABLE items(id INT IDENTITY PRIMARY KEY, name NVARCHAR(100), payload NVARCHAR(200)); INSERT INTO items(name,payload) SELECT TOP 50000 'item'+CAST(ROW_NUMBER() OVER(ORDER BY (SELECT 1)) AS VARCHAR(10)), CONVERT(VARCHAR(32),HASHBYTES('MD5',CAST(ROW_NUMBER() OVER(ORDER BY (SELECT 1)) AS VARCHAR(10))),2) FROM sys.all_objects a CROSS JOIN sys.all_objects b END"
sqlcmd -S 127.0.0.1,{ms['port']} -U {ms['user']} -P '{ms['password']}' -C -b -d e2e_src -Q $q
New-Item -ItemType Directory -Force C:\\e2e\\files\\docs,C:\\e2e\\files\\nested\\a | Out-Null
if(-not (Test-Path C:\\e2e\\files\\big.bin)){{ $b=New-Object byte[] 67108864; (New-Object Random).NextBytes($b); [IO.File]::WriteAllBytes('C:\\e2e\\files\\big.bin',$b) }}
1..200 | % {{ Set-Content C:\\e2e\\files\\docs\\f$_.txt "file $_" }}
Set-Content C:\\e2e\\files\\nested\\a\\unicode-ğüş.txt 'türkçe içerik' -Encoding UTF8
'win-seed-ok'
"""
enc = base64.b64encode(ps.encode("utf-16le")).decode()
r = subprocess.run(["ssh", "virtual-win11", f"powershell -NoProfile -EncodedCommand {enc}"], capture_output=True, text=True)
print("\n".join(l for l in (r.stdout + r.stderr).splitlines() if "quantum" not in l.lower() and "vulnerable" not in l and "upgraded" not in l and not l.startswith("**"))[-1200:].replace(ms['password'], '***').replace(pg['password'], '***'))
