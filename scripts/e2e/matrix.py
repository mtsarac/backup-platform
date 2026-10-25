"""Backup -> restore -> byte/row comparison for every (agent, engine, destination) combination. Results go to results.jsonl."""
import sys, json, lib, flows
from lib import ST, CRED, scenario

PGQ = "select count(*)||'|'||md5(string_agg(name||payload, ',' order by id)) from items"
MSQ = "select cast(count(*) as varchar(20))+'|'+cast(checksum_agg(checksum(name,payload)) as varchar(40)) from items"
WPSQL = r"C:\Program Files\PostgreSQL\18\bin\psql.exe"

def fed_pg_hash(db):
    f = ST["fedora"]; rc, out = lib.fedora(f"PGPASSWORD='{f['pg_password']}' psql -h 127.0.0.1 -U bp_e2e -d {db} -tAc \"{PGQ}\""); assert rc == 0, out; return out.strip()
def fed_pg_drop(db):
    lib.fedora(f"sudo -u postgres psql -qc 'drop database if exists {db}' 2>&1")
def win_pg_hash(db):
    c = CRED["windows11_vm"]["postgresql"]
    rc, out = lib.win(f"$env:PGPASSWORD='{c['password']}'; & '{WPSQL}' -h 127.0.0.1 -U {c['user']} -d {db} -tAc \"{PGQ}\""); assert "|" in out, out; return out.strip().splitlines()[0].strip()
def win_pg_drop(db):
    c = CRED["windows11_vm"]["postgresql"]; lib.win(f"$env:PGPASSWORD='{c['password']}'; & '{WPSQL}' -h 127.0.0.1 -U {c['user']} -d postgres -c 'drop database if exists {db}'")
def win_ms_hash(db):
    c = CRED["windows11_vm"]["mssql"]
    rc, out = lib.win(f"sqlcmd -S 127.0.0.1,{c['port']} -U {c['user']} -P '{c['password']}' -C -h -1 -W -d {db} -Q \"set nocount on; {MSQ}\""); assert "|" in out, out; return out.strip().splitlines()[0].strip()
def win_ms_drop(db):
    c = CRED["windows11_vm"]["mssql"]
    lib.win(f"sqlcmd -S 127.0.0.1,{c['port']} -U {c['user']} -P '{c['password']}' -C -Q \"if db_id('{db}') is not null begin alter database [{db}] set single_user with rollback immediate; drop database [{db}] end\"")

def dests(t):
    c = ST["connections"]
    conn = lambda p: {"storageProvider": p, "storageConnectionId": c[p], "storageExecutionLocation": "Backend", "isPrimary": True}
    return {"local": [flows.local_dest()], "local-vol2": [flows.local_dest("vol2")], "sftp": [conn("sftp")], "s3": [conn("s3")], "smb": [conn("smb")]}

def targets(t, agents):
    fa, wa = agents["fedora-e2e"], agents["win11-e2e"]
    f = ST["fedora"]; wp = CRED["windows11_vm"]["postgresql"]; wm = CRED["windows11_vm"]["mssql"]
    return {
        "fedora-pg": dict(agent=fa, engine="postgres", cfg=flows.pg_cfg(t, fa, "fedora"), encrypt=False, hash=fed_pg_hash, drop=fed_pg_drop, src="e2e_src",
                         target=lambda: {"host": "127.0.0.1", "port": 5432, "databaseCredentialReference": flows.login_ref(t, fa, "postgres", f["pg_super_password"]), "encrypt": False, "trustServerCertificate": True}),
        "win-pg": dict(agent=wa, engine="postgres", cfg=flows.pg_cfg(t, wa, "win"), encrypt=False, hash=win_pg_hash, drop=win_pg_drop, src="e2e_src",
                      target=lambda: {"host": "127.0.0.1", "port": 5432, "databaseCredentialReference": flows.login_ref(t, wa, wp["user"], wp["password"]), "encrypt": False, "trustServerCertificate": True}),
        "win-mssql": dict(agent=wa, engine="mssql", cfg=flows.mssql_cfg(t, wa), encrypt=True, hash=win_ms_hash, drop=win_ms_drop, src="e2e_src",
                         target=lambda: {"host": "127.0.0.1", "port": wm["port"], "databaseCredentialReference": flows.login_ref(t, wa, wm["user"], wm["password"]), "encrypt": True, "trustServerCertificate": True}),
    }

def db_roundtrip(t, tg, dest_name, dest, tag="", **job_extra):
    cfg = flows.db_job_config(tg["engine"], tg["cfg"], dest, encrypt=tg["encrypt"], **job_extra)
    job, rid, art, det = flows.run_backup(t, tg["agent"], f"matrix {tag}{dest_name}", cfg)
    db = f"r_{dest_name.replace('-', '_')}_{rid[:6]}"
    flows.restore(t, tg["agent"], art["id"], tg["target"](), db)
    src, got = tg["hash"](tg["src"]), tg["hash"](db)
    tg["drop"](db)
    assert src == got, f"restored data differs: src={src} restored={got}"
    return {"artifactId": art["id"], "sizeBytes": art.get("sizeBytes"), "rows": src}

def main(only=None):
    t = lib.Tenant("a"); agents = t.agents(); tg = targets(t, agents); d = dests(t)
    for tname, spec in tg.items():
        for dname, dest in d.items():
            name = f"matrix/{tname}/{dname}"
            if only and only not in name: continue
            scenario(name)(lambda: db_roundtrip(t, spec, dname, dest))()

if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else None)
