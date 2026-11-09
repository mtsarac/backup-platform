"""#57: restore pre-check items are reported once per checkId (PostgreSQL on fedora-e2e, SQL Server on win11-e2e), and a failing pre-check stops the real restore without touching the target.
Run: python3 restore_precheck_dedupe.py [fedora-pg|win-mssql]; OBSERVE=1 records duplicates instead of failing on them (baseline against a pre-fix stack). Throwaway databases are prefixed bp_dedupe57_ and dropped at the end; results go to results.jsonl."""
import collections, json, os, sys
import lib, flows
from lib import ST, CRED, scenario
import gap_windows as gw

PG = "post" + "gres"
NEW, EXIST, MISSING = "bp_dedupe57_new", "bp_dedupe57_exist", "bp_dedupe57_missing"


def fed(q, db=PG):
    rc, out = lib.fedora(f"PGPASSWORD='{ST['fedora']['pg_super_password']}' psql -h 127.0.0.1 -U {PG} -d {db} -tAc \"{q}\""); assert rc == 0, out; return out.strip()


def engines(t):
    ag = t.agents(); fa, wa = ag["fedora-e2e"], ag["win11-e2e"]; ms = CRED["windows11_vm"]["mssql"]
    return {
        "fedora-pg": dict(agent=fa, engine=PG, encrypt=False, cfg=flows.pg_cfg(t, fa, "fedora"), sql=fed,
                          target={"host": "127.0.0.1", "port": 5432, "databaseCredentialReference": flows.login_ref(t, fa, PG, ST["fedora"]["pg_super_password"]), "encrypt": False, "trustServerCertificate": True},
                          count="select count(*) from pg_database where datname='{}'", create="create database {0}", marker="create table marker(v int); insert into marker values (1)",
                          drop="drop database if exists {0} with (force)"),
        "win-mssql": dict(agent=wa, engine="mssql", encrypt=True, cfg=flows.mssql_cfg(t, wa), sql=gw.sql,
                          target={"host": "127.0.0.1", "port": ms["port"], "databaseCredentialReference": flows.login_ref(t, wa, ms["user"], ms["password"]), "encrypt": True, "trustServerCertificate": True},
                          count="select count(*) from sys.databases where name='{}'", create="create database [{0}]", marker="create table marker(v int); insert into marker values (1)",
                          drop="if db_id('{0}') is not null begin alter database [{0}] set single_user with rollback immediate; drop database [{0}] end"),
    }


def check_ids(d):
    ids = [c["checkId"] for c in d["result"]["preCheck"]]
    dups = {k: n for k, n in collections.Counter(ids).items() if n > 1}
    return ids, dups


def run_precheck(t, e, art, db, mode):
    req = {"agentId": e["agent"]["agentId"], "target": e["target"], "databaseName": db, "mode": mode}
    if mode == "ReplaceExistingDatabase": req["confirmOverwrite"] = db
    p = t.call("POST", f"/backup-artifacts/{art}/restore-precheck", req)
    return req, t.wait(f"/restores/{p.get('id', p.get('restoreId'))}", timeout=300)


def one(name, e):
    t = lib.Tenant("a"); sql = e["sql"]; ev = {}
    drop = lambda db: sql(e["drop"].format(db)); exists = lambda db: sql(e["count"].format(db)).splitlines()[0].strip() == "1"
    def seen(label, d):
        ids, dups = check_ids(d)
        ev[label] = {"restoreId": d["id"], "status": d["status"], "failureCode": d.get("failureCode"), "targetState": d.get("targetState"), "checkIds": ids, "duplicates": dups}
        assert os.environ.get("OBSERVE") or not dups, f"{label}: duplicated checkIds {dups}"
        assert ids, f"{label}: no pre-check items"
    for db in (NEW, EXIST, MISSING): drop(db)
    try:
        job, rid, art, _ = flows.run_backup(t, e["agent"], f"dedupe57 {name}", flows.db_job_config(e["engine"], e["cfg"], [flows.local_dest()], encrypt=e["encrypt"]), timeout=600)
        ev["artifact"] = art["id"]; a = art["id"]
        sql(e["create"].format(EXIST)); sql(e["marker"], EXIST)
        # NewDatabase onto a free name: pre-check, then the real restore (the plan re-emits the pre-check checks).
        req, d = run_precheck(t, e, a, NEW, "NewDatabase"); assert d["status"] == "Succeeded", d["status"]; seen("precheck NewDatabase", d)
        s = t.call("POST", f"/backup-artifacts/{a}/restore", req); d = t.wait(f"/restores/{s.get('id', s.get('restoreId'))}", timeout=900)
        assert d["status"] == "Succeeded", f"restore {d['status']}: {json.dumps(d)[:500]}"; seen("restore NewDatabase", d)
        _, d = run_precheck(t, e, a, EXIST, "ReplaceExistingDatabase"); assert d["status"] == "Succeeded", d["status"]; seen("precheck ReplaceExistingDatabase", d)
        # Failure paths: the failed check is present once and the real restore is stopped with the same reason, target untouched.
        for label, db, mode, code in (("NewDatabase onto existing", EXIST, "NewDatabase", "TargetDatabaseExists"), ("ReplaceExistingDatabase onto missing", MISSING, "ReplaceExistingDatabase", "TargetDatabaseNotFound")):
            req, d = run_precheck(t, e, a, db, mode); seen("precheck " + label, d)
            assert d["status"] == "Failed" and d["failureCode"] == code, f"{label}: {d['status']} {d.get('failureCode')}"
            failed = [c for c in d["result"]["preCheck"] if c["status"] == "Failed"]; assert len(failed) == 1, failed; ev["precheck " + label]["failedItem"] = failed[0]
            r = t.raw("POST", f"/backup-artifacts/{a}/restore", req)
            if r.status_code in (200, 201, 202):
                x = r.json(); d = t.wait(f"/restores/{x.get('id', x.get('restoreId'))}", timeout=300); seen("restore " + label, d)
                assert d["status"] == "Failed" and d["failureCode"] == code and d["targetState"] == "NotModified", f"restore {label}: {d['status']} {d.get('failureCode')} {d.get('targetState')}"
            else: ev["restore " + label] = {"http": r.status_code, "body": r.text[:300]}; assert r.status_code in (400, 409, 422), r.status_code
        assert sql("select count(*) from marker", EXIST).splitlines()[0].strip() == "1", "existing target was modified"
        assert not exists(MISSING), "ReplaceExisting created the missing database"
        return ev
    finally:
        for db in (NEW, EXIST, MISSING): drop(db)


if __name__ == "__main__":
    t = lib.Tenant("a"); E = engines(t)
    for name in sys.argv[1:] or E:
        scenario(f"restore-precheck-dedupe{'-observe' if os.environ.get('OBSERVE') else ''}/{name}")(lambda: one(name, E[name]))()
