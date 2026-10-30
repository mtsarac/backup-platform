"""~1 GB cell: PostgreSQL on Fedora -> SFTP, restore into a new database, row count + content hash compared with the source.
Records backup/restore duration and every status or progress-stage change, including any Paused state or stall (no progress change for STALL_S).
Seed the source first: ssh virtual-fedora 'bash -s' < seed-large-fedora.sh   (remove it: ssh virtual-fedora 'bash -s -- drop' < seed-large-fedora.sh).
One pass, no retries. Run: python3 large.py"""
import time, lib, flows, matrix
from lib import ST, scenario

STALL_S = 60
BIG = "e2e_big"
PGQ = "select count(*)||'|'||md5(string_agg(md5(name||payload), ',' order by id)) from big"  # per-row md5 keeps the aggregate far below the 1 GB string limit


def big_hash(db):
    f = ST["fedora"]; rc, out = lib.fedora(f"PGPASSWORD='{f['pg_password']}' psql -h 127.0.0.1 -U bp_e2e -d {db} -tAc \"{PGQ}\""); assert rc == 0, out; return out.strip()


def watch(t, rid, events, timeout=2400):
    """Polls a run to a terminal state (Paused is recorded, not treated as the end) and logs status/progress changes and stalls."""
    start = last_change = time.time(); prev = None
    while time.time() - start < timeout:
        d = t.call("GET", f"/backup-runs/{rid}"); a = d["attempts"][-1] if d["attempts"] else {}
        key = (d["status"], a.get("progressStage"), a.get("progressPercent"))
        now = time.time()
        if key != prev:
            events.append({"t": round(now - start, 1), "status": key[0], "stage": key[1], "percent": key[2]}); prev, last_change = key, now
        elif now - last_change >= STALL_S and key[0] not in lib.TERMINAL:
            events.append({"t": round(now - start, 1), "stall": f"no change in {key} for {int(now - last_change)}s"}); last_change = now
        if d["status"] in lib.TERMINAL: return d, now - start
        time.sleep(2)
    raise TimeoutError(f"run {rid} still {prev} after {timeout}s")


@scenario("matrix/large/fedora-pg/sftp/~1GB")
def large_cell():
    t = lib.Tenant("a"); fa = t.agents()["fedora-e2e"]; d = matrix.dests(t)["sftp"]
    cfg = flows.pg_cfg(t, fa, "fedora"); cfg["database"] = BIG
    job = t.call("POST", "/backup-jobs", {"name": f"large {BIG} sftp", "agentId": fa["agentId"], "overlapPolicy": "Skip",
                                          "configuration": flows.db_job_config("postgres", cfg, d, encrypt=False, encryptionPolicy="Off")})
    src = big_hash(BIG); events = []
    run = t.call("POST", f"/backup-jobs/{job['id']}/runs", {}); rid = run.get("id", run.get("run", {}).get("id"))
    det, backup_s = watch(t, rid, events)
    detail = {"backupSeconds": round(backup_s, 1), "sourceRows": src, "events": events}
    try:
        assert det["status"] == "Succeeded", f"backup run {det['status']}: {det.get('reasonCode')} {det.get('reason')}"
        art = [a for at in det["attempts"] for a in at["artifacts"]][0]; detail["sizeBytes"] = art["sizeBytes"]
        ref = lib.ST["fedora"]; target = {"host": "127.0.0.1", "port": 5432, "databaseCredentialReference": flows.login_ref(t, fa, "postgres", ref["pg_super_password"]), "encrypt": False, "trustServerCertificate": True}
        db = f"r_big_{rid[:6]}"; t0 = time.time()
        try:
            flows.restore(t, fa, art["id"], target, db)
            detail["restoreSeconds"] = round(time.time() - t0, 1)
            got = big_hash(db); detail["restoredRows"] = got
            assert src == got, f"restored data differs: src={src} restored={got}"
        finally:
            matrix.fed_pg_drop(db)
    except Exception as e:
        e.args = (f"{e} | detail={detail}",); raise
    return detail


if __name__ == "__main__":
    large_cell()
