"""JobRestoreGaps: restore/verification scenarios RST-04..RST-13 (plan docs/BACKUP_PLATFORM_TEST_PLANI.md). Run: python3 gap_job_restore.py [name-filter].
All restores go to throwaway PostgreSQL containers (gap-jr-*, network 10.77.71.0/24) or to new gap_jr_* databases on the host MSSQL container; lab source e2e_src is only read."""
import fcntl, json, os, subprocess, sys, tempfile, threading, time, uuid
import api, lib, flows
from lib import ST, scenario
from gap_common import *

SRC_DIGEST = None
CTX = {}


def ctx():
    if "c" not in CTX: CTX["c"] = Ctx()
    return CTX["c"]


def rt():
    """The shared throwaway restore target (PostgreSQL 18, superuser)."""
    if "rt" not in CTX: CTX["rt"] = Throwaway("rt", 71).up()
    return CTX["rt"]


def src_artifact(policy="Off", dest=None, name="src", database="e2e_src"):
    """A fresh artifact of a lab database (default e2e_src) on the host agent (cached per database+policy+destination)."""
    k = ("art", database, policy, json.dumps(dest, sort_keys=True))
    if k not in CTX:
        c = ctx()
        cfg = flows.db_job_config("postgres", c.host_pg_cfg(database), dest or [flows.local_dest()], encrypt=False, encryptionPolicy=policy)
        job, rid, art, d = backup_ok(c.t, c.ha, f"{name}-{policy}", cfg)
        CTX.setdefault("created", []).append((job["id"], art["id"]))
        CTX[k] = art
    return CTX[k]


def src_digest():
    global SRC_DIGEST
    SRC_DIGEST = SRC_DIGEST or host_pg_digest("e2e_src")
    return SRC_DIGEST


def restores_total(t, agent):
    return t.call("GET", f"/restores?agentId={agent['agentId']}&pageSize=1")["totalCount"]


def populate(tw, db, rows=100):
    """A pre-populated target: same table name as the artifact but different content, plus an extra table that the restore must not 'merge' with."""
    tw.sql(f'create database "{db}"')
    tw.sql(f"create table items(id serial primary key, name text not null, payload text not null); insert into items(name,payload) select 'old-'||g, 'old-payload-'||g from generate_series(1,{rows}) g; create table keepme(v int); insert into keepme values (42)", db)
    return tw.digest(db)


def rollback_dbs(tw): return [d for d in tw.dbs() if d.startswith("bp_old_")]
def scratch_dbs(tw): return [d for d in tw.dbs() if d.startswith("bp_") ]


@scenario("gap/RST-04")
def rst04():
    c = ctx(); t = c.t; tw = rt(); art = src_artifact(); out = {}
    want = src_digest(); db = "gap_jr_pre"
    tw.sql(f'drop database if exists "{db}" with (force)')
    old = populate(tw, db); out["oldDigest"] = old; tgt = tw.target(c)
    n0 = restores_total(t, c.ha)
    # a. NewDatabase over an existing database: precheck and restore both refuse, data untouched
    _, p = start_restore(t, art["id"], restore_req(c.ha, tgt, db), "restore-precheck")
    pd = wait_restore(t, p["id"]); out["newOverExisting.precheck"] = fail_view(pd)
    assert pd["status"] == "Failed" and pd["failureCode"] == "TargetDatabaseExists", pd
    _, r = start_restore(t, art["id"], restore_req(c.ha, tgt, db)); rd = wait_restore(t, r["id"]); out["newOverExisting.restore"] = fail_view(rd)
    assert rd["status"] == "Failed" and rd["failureCode"] == "TargetDatabaseExists" and rd["targetState"] == "NotModified", rd
    assert tw.digest(db) == old, "existing database changed by a refused NewDatabase restore"
    # b. ReplaceExistingDatabase without / with wrong confirmation: rejected at the API, no restore row, data untouched
    n1 = restores_total(t, c.ha)
    for label, conf in (("missing", None), ("wrong", "gap_jr_other"), ("case", db.upper()), ("empty", ""), ("space", db + " ")):
        r = t.raw("POST", f"/backup-artifacts/{art['id']}/restore", restore_req(c.ha, tgt, db, "ReplaceExistingDatabase", confirm=conf))
        out[f"replace.confirm.{label}"] = (r.status_code, (r.json() or {}).get("code") if r.content else None)
        assert r.status_code == 400 and r.json()["field"] == "confirmOverwrite", f"{label}: {r.status_code} {r.text[:200]}"
    r = t.raw("POST", f"/backup-artifacts/{art['id']}/restore", restore_req(c.ha, tgt, db, "NewDatabase", confirm=db))
    out["newDatabase.withConfirm"] = r.status_code; assert r.status_code == 400 and r.json()["code"] == "not_applicable", r.text[:200]
    r = t.raw("POST", f"/backup-artifacts/{art['id']}/restore-precheck", {**restore_req(c.ha, tgt, db, "ReplaceExistingDatabase"), "confirmOverwrite": db})
    out["precheck.withConfirmOverwrite.status"] = r.status_code
    assert restores_total(t, c.ha) == n1, "a rejected request created a restore"
    assert tw.digest(db) == old
    # c. replace a database that does not exist
    r = t.call("POST", f"/backup-artifacts/{art['id']}/restore", restore_req(c.ha, tgt, "gap_jr_nodb", "ReplaceExistingDatabase", confirm="gap_jr_nodb")); d = wait_restore(t, r["id"])
    out["replaceMissing"] = fail_view(d); assert d["status"] == "Failed" and d["failureCode"] == "TargetDatabaseNotFound", d
    assert not tw.exists("gap_jr_nodb")
    # d. protected names never restorable, even with confirmation
    for prot in ("postgres", "template1", "template0"):
        r = t.raw("POST", f"/backup-artifacts/{art['id']}/restore", restore_req(c.ha, tgt, prot, "ReplaceExistingDatabase", confirm=prot))
        if r.status_code == 400: out[f"protected.{prot}"] = ("400", r.json().get("code")); continue
        d = wait_restore(t, r.json()["id"]); out[f"protected.{prot}"] = fail_view(d)
        assert d["status"] == "Failed" and d["failureCode"] in ("TargetProtected", "InvalidConfiguration", "PreCheckFailed"), d
    assert tw.digest(db) == old and tw.exists("postgres") and tw.exists("template1")
    # e. active connections on the target: refused unless TerminateActiveConnections is asked for
    sess = subprocess.Popen(["docker", "exec", "-e", f"PGPASSWORD={tw.password}", tw.name, "psql", "-U", tw.user, "-d", db, "-c", "select pg_sleep(120)"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        time.sleep(2)
        r = t.call("POST", f"/backup-artifacts/{art['id']}/restore", restore_req(c.ha, tgt, db, "ReplaceExistingDatabase", confirm=db)); d = wait_restore(t, r["id"])
        out["replaceWithActiveSession"] = fail_view(d); assert d["status"] == "Failed" and d["failureCode"] == "TargetInUse", d
        assert tw.digest(db) == old, "target changed although the restore was refused (TargetInUse)"
        r = t.call("POST", f"/backup-artifacts/{art['id']}/restore", restore_req(c.ha, tgt, db, "ReplaceExistingDatabase", options={"terminateActiveConnections": True}, confirm=db))
        d = wait_restore(t, r["id"]); out["replaceTerminating"] = fail_view(d)
        assert d["status"] == "Succeeded" and d["targetState"] == "Online", d
    finally:
        sess.kill()
    # f. the replaced content is the artifact's, the previous content survives under a platform rollback name
    assert tw.digest(db) == want, f"replace restore content differs: {tw.digest(db)} != {want}"
    assert tw.sql("select count(*) from information_schema.tables where table_name='keepme'", db) == "0", "old extra table survived the replacement (merge instead of replace)"
    rb = rollback_dbs(tw); out["rollbackDbs"] = rb; assert len(rb) == 1, rb
    tw.sql(f'alter database "{rb[0]}" allow_connections true')
    assert tw.digest(rb[0]) == old, "rollback copy does not hold the previous content"
    tw.sql(f'alter database "{rb[0]}" allow_connections false')
    cl = d["cleanup"]; out["cleanup"] = {k: cl[k] for k in ("kind", "state", "databaseName", "canRequest", "retainedUntil")}
    assert cl["kind"] == "RollbackCopy" and cl["databaseName"] == rb[0] and cl["canRequest"] is False, cl
    # g. removing the rollback copy: administrator only, exact name, and not inside the retention guard
    rem = f"/restores/{d['id']}/rollback-removal"
    r = t.raw("POST", rem, {"confirmDatabaseName": "nope"}); out["rollbackRemoval.wrongName"] = r.status_code; assert r.status_code == 400, r.text[:200]
    r = t.raw("POST", rem, {"confirmDatabaseName": rb[0]}); out["rollbackRemoval.rightNameInsideRetention"] = r.status_code
    assert r.status_code == 409 and r.json()["error"] == "retention_not_elapsed", r.text[:200]
    op = lib.Tenant.__new__(lib.Tenant); op.id = t.id; op.base = t.base; op.s = api.login(ST["roles"]["operator-a"]["login"], ST["roles"]["operator-a"]["password"])
    r = op.raw("POST", rem, {"confirmDatabaseName": rb[0]}); out["rollbackRemoval.operator"] = r.status_code; assert r.status_code == 403, r.status_code
    vw = lib.Tenant.__new__(lib.Tenant); vw.id = t.id; vw.base = t.base; vw.s = api.login(ST["roles"]["viewer-a"]["login"], ST["roles"]["viewer-a"]["password"])
    r = vw.raw("POST", f"/backup-artifacts/{art['id']}/restore", restore_req(c.ha, tgt, "gap_jr_viewer")); out["viewerRestore"] = r.status_code; assert r.status_code == 403
    for x in (db, *rb): tw.sql(f'drop database if exists "{x}" with (force)')
    return out


@scenario("gap/RST-05")
def rst05():
    c = ctx(); a = src_artifact(); b = lib.Tenant("b"); t = c.t
    req = restore_req(c.ha, c.host_pg_target(), "gap_jr_foreign")
    statuses = {}
    for suffix in ("restore", "restore-precheck", "restore-verifications", "restore-discovery"):
        body = req if suffix in ("restore", "restore-precheck") else {"agentId": c.ha["agentId"], "target": req["target"]}
        r = b.raw("POST", f"/backup-artifacts/{a['id']}/{suffix}", body)
        statuses[suffix] = r.status_code
        assert r.status_code in (403, 404), (suffix, r.status_code)
        assert a["artifactChecksum"] not in r.text and a["databaseName"] not in r.text
    r = b.s.post(t.base + f"/backup-artifacts/{a['id']}/restore", json=req, headers={"Idempotency-Key": uuid.uuid4().hex})
    statuses["tenantAPath"] = r.status_code
    assert r.status_code in (403, 404), r.status_code
    return {"artifactId": a["id"], "http": statuses}


@scenario("gap/RST-06")
def rst06():
    c = ctx(); t = c.t; tw = rt(); art = src_artifact()
    rep = next(r for r in art["replicas"] if r["storageProvider"] == "local")
    path = local_store_path(rep)
    with open(path, "rb") as f: original = f.read()
    assert hashlib.sha256(original).hexdigest() == art["artifactChecksum"]
    out = {}
    try:
        for kind in ("bit", "truncate"):
            modified = bytearray(original)
            if kind == "bit": modified[len(modified)//2] ^= 1
            else: del modified[len(modified)//2:]
            with open(path, "wb") as f: f.write(modified)
            db = "gap_jr_bad_" + kind
            _, started = start_restore(t, art["id"], restore_req(c.ha, tw.target(c), db, replica_id=rep["id"]))
            d = wait_restore(t, started["id"])
            out[kind] = {"id": d["id"], **fail_view(d)}
            assert d["status"] == "Failed" and d["failureCode"] in ("ChecksumMismatch", "ReplicaUnavailable", "ArtifactTransferFailed"), out[kind]
            assert not tw.exists(db)
    finally:
        with open(path, "wb") as f: f.write(original)
    assert hashlib.sha256(open(path, "rb").read()).hexdigest() == art["artifactChecksum"]
    return {"artifactId": art["id"], "outcomes": out}


@scenario("gap/RST-09")
def rst09():
    c = ctx(); t = c.t; tw = rt(); art = src_artifact()
    before = set(tw.dbs())
    req = {"agentId": c.ha["agentId"], "target": tw.target(c)}
    _, started = start_restore(t, art["id"], req, "restore-verifications")
    d = wait_restore(t, started["id"])
    verification = (d.get("result") or {}).get("verification") or {}
    assert d["status"] == "Succeeded" and d["verificationOutcome"] == "Restored", fail_view(d)
    assert verification.get("cleanup") in ("Dropped", "NotCreated"), verification
    assert set(tw.dbs()) == before, (before, tw.dbs())
    return {"id": d["id"], "artifactId": art["id"], "verification": verification, "cleanup": d.get("cleanup"), "remainingDbs": sorted(set(tw.dbs())-before)}


@scenario("gap/RST-11")
def rst11():
    c = ctx(); t = c.t; tw = rt(); art = src_artifact(); out = {}
    db = "gap_jr_check"
    for mode, exists in (("NewDatabase", True), ("ReplaceExistingDatabase", False)):
        if exists: populate(tw, db)
        elif tw.exists(db): tw.sql(f'drop database "{db}" with (force)')
        _, p = start_restore(t, art["id"], restore_req(c.ha, tw.target(c), db, mode), "restore-precheck")
        d = wait_restore(t, p["id"])
        out[mode] = {"id": d["id"], **fail_view(d)}
        assert d["status"] == "Failed" and d["targetState"] == "NotModified", out[mode]
    return {"artifactId": art["id"], "prechecks": out}


@scenario("gap/RST-10")
def rst10():
    c = ctx(); t = c.t; tw = rt(); art = src_artifact()
    rep = next(r for r in art["replicas"] if r["storageProvider"] == "local")
    path = local_store_path(rep)
    before = set(tw.dbs())
    with open(path, "rb") as f: original = f.read()
    try:
        damaged = bytearray(original); damaged[len(damaged)//2] ^= 1
        with open(path, "wb") as f: f.write(damaged)
        req = {"agentId": c.ha["agentId"], "target": tw.target(c), "replicaId": rep["id"]}
        _, started = start_restore(t, art["id"], req, "restore-verifications")
        d = wait_restore(t, started["id"])
        assert d["status"] == "Failed" and d["verificationOutcome"] == "Failed", fail_view(d)
        assert set(tw.dbs()) == before, (before, tw.dbs())
        return {"id": d["id"], "failureCode": d["failureCode"], "cleanup": d["cleanup"], "remainingDbs": sorted(set(tw.dbs())-before)}
    finally:
        with open(path, "wb") as f: f.write(original)


@scenario("gap/RST-13")
def rst13():
    c = ctx(); t = c.t; tw = rt(); art = src_artifact()
    db = "gap_jr_crosscheck"
    try:
        _, started = start_restore(t, art["id"], restore_req(c.ha, tw.target(c), db))
        d = wait_restore(t, started["id"])
        assert d["status"] == "Succeeded" and d["targetState"] == "Online", fail_view(d)
        actual = tw.digest(db); expected = src_digest()
        assert actual == expected, (actual, expected)
        run = t.call("GET", f"/backup-runs/{art['runId']}")
        assert run["status"] == "Succeeded" and any(a["id"] == art["id"] for a in arts(run))
        rep = next(r for r in art["replicas"] if r["id"] == d["replicaId"])
        assert rep["verificationState"] == "Verified"
        with open(local_store_path(rep), "rb") as f: digest = hashlib.sha256(f.read()).hexdigest()
        assert digest == art["artifactChecksum"] == rep["checksum"]
        admin = api.login(); actions = []
        for page in range(1, 31):
            r = admin.get(f"{api.BASE}/api/platform/audit", params={"tenantId": t.id, "page": page, "pageSize": 100})
            r.raise_for_status(); data = r.json()
            actions += [x["action"] for x in data["items"] if x.get("targetId") == d["id"]]
            if len(data["items"]) < 100: break
        assert "restore.requested" in actions, actions
        return {"runId": art["runId"], "artifactId": art["id"], "restoreId": d["id"], "digest": actual, "replicaSha256": digest, "auditActions": actions}
    finally:
        if tw.exists(db): tw.sql(f'drop database "{db}" with (force)')


@scenario("gap/RST-08")
def rst08():
    c = ctx(); t = c.t; tw = rt(); art = src_artifact()
    db = "gap_jr_cancel"
    old = populate(tw, db)
    target = tw.target(c)
    sh("docker", "pause", tw.name, check=True)
    try:
        _, started = start_restore(t, art["id"], restore_req(c.ha, target, db, "ReplaceExistingDatabase", confirm=db))
        rid = started["id"]
        t.call("POST", f"/restores/{rid}/cancel", {})
    finally:
        sh("docker", "unpause", tw.name, check=True)
    d = wait_restore(t, rid, timeout=240)
    assert d["status"] == "Cancelled", fail_view(d)
    assert tw.digest(db) == old, "cancel changed existing target"
    return {"id": rid, **fail_view(d), "oldDigest": old, "note": "target paused before execution; mid-restore disconnect not exercised"}


@scenario("gap/RST-12")
def rst12():
    c = ctx(); t = c.t; tw = rt(); art = src_artifact()
    rep = art["replicas"][0]; path = local_store_path(rep)
    sh("docker", "pause", tw.name, check=True)
    try:
        _, started = start_restore(t, art["id"], {"agentId": c.ha["agentId"], "target": tw.target(c)}, "restore-verifications")
        rid = started["id"]
        assert started["replicaPinned"], started
        deletion = t.call("DELETE", f"/backup-artifacts/{art['id']}")
        during = t.call("GET", f"/restores/{rid}")
        assert during["replicaPinned"] and os.path.isfile(path), (during["status"], deletion["state"])
        assert deletion["state"] == "DeletePending", deletion["state"]
    finally:
        sh("docker", "unpause", tw.name, check=True)
        t.raw("POST", f"/backup-artifacts/{art['id']}/cancel-delete", {})
    d = wait_restore(t, rid, timeout=240)
    assert d["status"] == "Succeeded" and not d["replicaPinned"], fail_view(d)
    return {"id": rid, "pinnedDuringDeletion": True, "stateDuring": deletion["state"], "finalStatus": d["status"], "pinnedAfter": d["replicaPinned"], "note": "manual deletion request, not retention expiry"}


@scenario("gap/RST-07")
def rst07():
    import files_providers
    c = ctx(); t = c.t; tw = rt()
    private, public = files_providers.gen_rsa()
    wrong, _ = files_providers.gen_rsa()
    ref = os.path.join(api.ROOT, "scripts/bpe-reference/bpe_decrypt.py")
    job = art = None
    with open("/tmp/gap-recovery-key.lock", "w") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        assert t.call("GET", "/encryption")["tenantRecovery"] is None, "existing recovery key must not be overwritten"
        try:
            t.call("PUT", "/encryption/recovery-key", {"publicKeyPem": public})
            cfg = flows.db_job_config("postgres", c.host_pg_cfg(), [flows.local_dest()], encrypt=False, encryptionPolicy="Required")
            job, rid, art, run = backup_ok(t, c.ha, "recovery", cfg)
            assert any(x["type"] == "tenant-recovery" for x in art["encryption"]["recipients"])
            with tempfile.TemporaryDirectory(prefix="gap-jr-") as tmp:
                source = os.path.join(tmp, "cipher.bpe")
                plain = os.path.join(tmp, "plain.dump")
                with open(local_store_path(art["replicas"][0]), "rb") as f, open(source, "wb") as out: out.write(f.read())
                def decrypt(key):
                    fd = os.memfd_create("gap-recovery-key")
                    try:
                        os.write(fd, key); os.lseek(fd, 0, os.SEEK_SET)
                        return subprocess.run(["python3", ref, "--in", source, "--out", plain, "--key", f"/proc/self/fd/{fd}"], pass_fds=(fd,), capture_output=True)
                    finally: os.close(fd)
                bad = decrypt(wrong)
                assert bad.returncode != 0 and not os.path.exists(plain), "wrong key produced plaintext"
                good = decrypt(private)
                assert good.returncode == 0 and os.path.exists(plain), "correct recovery key did not decrypt"
                with open(plain, "rb") as f: data = f.read()
                assert hashlib.sha256(data).hexdigest() == art["sourceChecksum"]
                db = "gap_jr_recovered"; tw.sql(f'create database "{db}"')
                r = subprocess.run(["docker", "exec", "-i", "-e", f"PGPASSWORD={tw.password}", tw.name, "pg_restore", "--exit-on-error", "--no-owner", "--no-acl", "-U", tw.user, "-d", db], input=data, capture_output=True)
                assert r.returncode == 0, f"pg_restore exited {r.returncode}"
                got = tw.digest(db); assert got == src_digest(), got
                return {"artifactId": art["id"], "runId": rid, "wrongKeyExit": bad.returncode, "correctKeyExit": good.returncode, "plaintextSha256": art["sourceChecksum"], "digest": got}
        finally:
            t.raw("DELETE", "/encryption/recovery-key")
            if art: t.raw("DELETE", f"/backup-artifacts/{art['id']}")
            if job: t.raw("POST", f"/backup-jobs/{job['id']}/disable", {})


@scenario("gap/RST-08/disconnect")
def rst08_disconnect():
    c = ctx(); t = c.t; tw = rt(); src = "gap_rst08_big"
    # The default source restores in well under a second, so Restoring could not be observed; a few hundred MB of random rows keeps the stage open for seconds on the throttled target.
    host_pg_drop(src); host_pg(f'create database "{src}"')
    host_pg("create table big(id serial primary key, v text not null); insert into big(v) select md5(random()::text) || md5(random()::text) || md5(random()::text) || md5(random()::text) from generate_series(1,1500000)", src)
    art = src_artifact(database=src, name="rst08-big")
    db = "gap_jr_disconnect"; old = populate(tw, db)
    sh("docker", "update", "--cpus", "0.20", tw.name, check=True)
    _, started = start_restore(t, art["id"], restore_req(c.ha, tw.target(c), db, "ReplaceExistingDatabase", confirm=db))
    rid = started["id"]; stages = []
    deadline = time.time() + 180
    while time.time() < deadline:
        d = t.call("GET", f"/restores/{rid}")
        stage = (d.get("progress") or {}).get("stage")
        if not stages or stages[-1] != stage: stages.append(stage)
        if stage == "Restoring": break
        if d["status"] in ("Succeeded", "Failed", "Cancelled"): raise AssertionError(f"restore ended before Restoring observed: {stages} {fail_view(d)}")
        time.sleep(.05)
    else: raise TimeoutError(f"Restoring not observed: {stages}")
    sh("docker", "kill", tw.name, check=True)
    d = wait_restore(t, rid, timeout=180)
    sh("docker", "start", tw.name, check=True); tw.wait()
    assert d["status"] == "Failed", fail_view(d)
    assert tw.digest(db) == old, "target content changed after connection loss"
    return {"id": rid, "stages": stages, **fail_view(d), "preservedDigest": old}


def main(only=None):
    items = [(f.scenario, f) for f in (rst04, rst05, rst06, rst07, rst08, rst08_disconnect, rst09, rst10, rst11, rst12, rst13)]
    try:
        for name, f in items:
            if only and only not in name: continue
            f()
    finally:
        if "created" in CTX:
            for job_id, artifact_id in CTX["created"]:
                CTX["c"].t.raw("DELETE", f"/backup-artifacts/{artifact_id}")
                CTX["c"].t.raw("POST", f"/backup-jobs/{job_id}/disable", {})
        if "rt" in CTX and os.environ.get("KEEP") != "1": CTX["rt"].down()


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else None)
