"""Host agent (host-e2e, this PC) round trips: Postgres and MSSQL container e2e_src -> local/sftp/s3 x encryption Off/Optional/Required,
restore into a new database, deterministic digest comparison, encryption metadata (same cell() as matrix.py). Scenarios: host/<engine>/<dest>/<policy>.
Needs state.json host.pg and host.mssql ({user,password}; a dedicated non-sa login), the host Postgres container (published 5432) and bpe2e-host-mssql."""
import sys, subprocess, lib, flows, matrix
from lib import ST, scenario

H = ST["host"]
SQLCMD = "/opt/mssql-tools18/bin/sqlcmd"

def pg_container():
    ids = subprocess.check_output(["docker", "ps", "--filter", "publish=5432", "--filter", "status=running", "-q"], text=True).split()
    assert len(ids) == 1, f"expected one running container publishing 5432, got {ids}"
    return ids[0]

def pg_sql(sql, db="postgres"):
    r = subprocess.run(["docker", "exec", "-e", "PGPASSWORD=" + H["pg"]["password"], pg_container(), "psql", "-h", "127.0.0.1", "-U", H["pg"]["user"], "-d", db, "-tAc", sql], capture_output=True, text=True)
    assert r.returncode == 0, r.stderr[:300]; return r.stdout.strip()

def ms_sql(sql, db="master", user=None):
    u = user or H["mssql"]
    r = subprocess.run(["docker", "exec", "-e", "SQLCMDPASSWORD=" + u["password"], "bpe2e-host-mssql", SQLCMD, "-S", "127.0.0.1", "-U", u["user"], "-C", "-b", "-h", "-1", "-W", "-d", db, "-Q", "set nocount on; " + sql], capture_output=True, text=True)
    assert r.returncode == 0, (r.stdout + r.stderr)[:300]; return r.stdout.strip()

def targets(t, agent):
    pg, ms = H["pg"], H["mssql"]
    pg_ref, ms_ref = flows.login_ref(t, agent, pg["user"], pg["password"]), flows.login_ref(t, agent, ms["user"], ms["password"])
    return {
        "pg": dict(agent=agent, engine="postgres", cfg=dict(host="127.0.0.1", port=5432, database="e2e_src", ref=pg_ref), encrypt=False, src="e2e_src",
                   hash=lambda db: pg_sql(matrix.PGQ, db), drop=lambda db: pg_sql(f'drop database if exists "{db}"'),
                   target=lambda: {"host": "127.0.0.1", "port": 5432, "databaseCredentialReference": pg_ref, "encrypt": False, "trustServerCertificate": True}),
        "mssql": dict(agent=agent, engine="mssql", cfg=dict(host="127.0.0.1", port=1433, database="e2e_src", ref=ms_ref), encrypt=True, src="e2e_src",
                      hash=lambda db: ms_sql(matrix.MSQ, db).splitlines()[0].strip(),
                      drop=lambda db: ms_sql(f"if db_id('{db}') is not null begin alter database [{db}] set single_user with rollback immediate; drop database [{db}] end"),
                      target=lambda: {"host": "127.0.0.1", "port": 1433, "databaseCredentialReference": ms_ref, "encrypt": True, "trustServerCertificate": True}),
    }

def cell(t, tg, dname, dest, policy):
    """matrix.cell with job names under gap-hostspecial- (the shared lab filters cleanup by prefix)."""
    cfg = flows.db_job_config(tg["engine"], tg["cfg"], dest, encrypt=tg["encrypt"], encryptionPolicy=policy)
    job, rid, art, _ = flows.run_backup(t, tg["agent"], f"gap-hostspecial-{tg['engine']}-{dname}-{policy}", cfg)
    db = f"gap_hs_{dname}_{policy.lower()}_{rid[:6]}"
    try:
        flows.restore(t, tg["agent"], art["id"], tg["target"](), db)
        src, got = tg["hash"](tg["src"]), tg["hash"](db)
    finally:
        tg["drop"](db)
    assert src == got, f"restored data differs: src={src} restored={got}"
    a = t.call("GET", f"/backup-artifacts/{art['id']}")
    encrypted, providers = a.get("encryption") is not None, sorted({r["storageProvider"] for r in a["replicas"]})
    assert providers == [dname], f"replica stored on {providers}, expected [{dname!r}]"
    assert encrypted == (policy != "Off"), f"policy {policy} but artifact encrypted={encrypted}"
    return {"artifactId": art["id"], "sizeBytes": art.get("sizeBytes"), "rows": src, "encrypted": encrypted}

def main(only=None):
    t = lib.Tenant("a"); agent = t.agents()["host-e2e"]; tg = targets(t, agent); d = matrix.dests(t)
    for eng, spec in tg.items():
        for dname in matrix.CELL_DESTS:
            for policy in matrix.POLICIES:
                name = f"host/{eng}/{dname}/{policy}"
                if only and only not in name: continue
                scenario(name)(lambda: cell(t, spec, dname, d[dname], policy))()

if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else None)
