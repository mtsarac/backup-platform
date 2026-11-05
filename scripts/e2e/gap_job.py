"""JobGaps (JOB-01/02/03/07/09/10/11/14/15/16, POL-11): job definition, validation, lifecycle, schedule, retry, concurrency, pause/cancel, history, precheck, health, size anomaly.
Scenarios are named gap/<ID>[/detail]; each appends a PASS/FAIL row to results.jsonl (lib.scenario). Throwaway objects: jobs gap-jr-job-*, containers gap-jr-job-*, DB roles/databases gap_jr_*.
Jobs cannot be deleted through the API (no DELETE route), so cleanup() disables them, removes schedules, cancels their active runs and deletes their artifacts.
Usage: python3 gap_job.py [ID-or-scenario-substring ...]"""
import concurrent.futures as cf, datetime as dt, hashlib, json, os, subprocess, sys, threading, time, uuid
from zoneinfo import ZoneInfo
import api, lib, flows, gap_common as g
from lib import ST, scenario

PG = "postgres"
P = "gap-jr-job-"
UTC = dt.timezone.utc
ST_NAMES = {1: "Pending", 2: "Leased", 3: "Running", 4: "Succeeded", 5: "Failed", 6: "Cancelled", 7: "AgentLost", 8: "RetryPending", 9: "Paused"}
C = T = None          # Ctx / tenant A client, set in main()
MINE = []             # job ids created by this script
CONTAINERS = []       # Throwaway objects to remove in cleanup


# ---------- helpers ----------
def uniq(tag): return f"{P}{tag}-{uuid.uuid4().hex[:5]}"
def appdb(sql):
    """Read-only query against the platform database (credentials come from the container's own environment, never printed)."""
    rc, out = g.sh("docker", "exec", "bp-" + PG, "sh", "-c", 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -tAF"|" -c "$0"', sql)
    if rc: raise RuntimeError(f"appdb: {out[:300]}")
    return [l.split("|") for l in out.splitlines() if l.strip()]
def now(): return dt.datetime.now(UTC)
def iso(s): return dt.datetime.fromisoformat(s.replace("Z", "+00:00")) if s else None
def sec(a, b): return round((iso(b) - iso(a)).total_seconds(), 2)
def dest_local(): return {k: v for k, v in flows.local_dest().items() if k != "storageTarget"}
def dest_sftp(): return {"storageProvider": "sftp", "storageConnectionId": ST["connections"]["sftp"], "storageExecutionLocation": "Backend", "isPrimary": True}
def dest_s3(primary=False): return {"storageProvider": "s3", "storageConnectionId": ST["connections"]["s3"], "storageExecutionLocation": "Backend", "isPrimary": primary}
def file_cfg(dests=None, paths=None, source="e2efiles", **extra):
    c = flows.file_job_config(source, dests or [dest_local()], paths=paths or ["docs"], **extra); c.pop("storageTarget", None); return c
def pg_cfg(agent, db="e2e_src", port=5432, user=None, password=None, host="127.0.0.1", dests=None, **extra):
    """Config for a PostgreSQL job on host-e2e (host server) or win11-e2e (local server)."""
    if agent["agentId"] == C.ha["agentId"]:
        cfg = C.host_pg_cfg(db, host, port, user, password)
    else:
        win = g.CRED["windows11_vm"][PG + "ql"]
        cfg = dict(host=host, port=port, database=db, ref=C.ref(agent, user or win["user"], password or win["password"]))
    c = flows.db_job_config(PG, cfg, dests or [dest_local()], encrypt=False, **extra); c.pop("storageTarget", None); return c
def body(agent, cfg, tag="x", **extra): return {"name": uniq(tag), "agentId": agent["agentId"], "configuration": cfg, **extra}
def post(b, session=None, path="/backup-jobs"):
    return (session or T.s).request("POST", T.base + path, json=b, headers={"Idempotency-Key": uuid.uuid4().hex})
def create(agent, cfg, tag="x", **extra):
    r = post(body(agent, cfg, tag, **extra)); assert r.status_code == 201, f"create {r.status_code} {r.text[:400]}"
    j = r.json(); MINE.append(j["id"]); return j
def start(job_id, key=None, expect=(200, 201, 202)):
    r = T.s.request("POST", f"{T.base}/backup-jobs/{job_id}/runs", json={}, headers={"Idempotency-Key": key or uuid.uuid4().hex})
    assert r.status_code in expect, f"start {r.status_code} {r.text[:300]}"; return r
def run_id(r): j = r.json(); return j.get("id", j.get("run", {}).get("id"))
def getrun(rid): return T.call("GET", f"/backup-runs/{rid}")
def runs_of(job_id, **q):
    qs = "&".join(f"{k}={v}" for k, v in {"pageSize": 100, **q}.items()); return T.call("GET", f"/backup-jobs/{job_id}/runs?{qs}")["items"]
def wait_run(rid, timeout=600, stop=lib.TERMINAL, every=0.5):
    end = time.time() + timeout; d = None
    while time.time() < end:
        d = getrun(rid)
        if d["status"] in stop: return d
        time.sleep(every)
    raise TimeoutError(f"run {rid} still {d and d['status']} after {timeout}s")
def arts(d): return [a for at in d["attempts"] for a in at["artifacts"]]
def sig(d): return (d["status"], d["currentAttemptNumber"], d["retry"]["state"], tuple(a["status"] for a in d["attempts"]))
def code_of(r):
    try: b = r.json()
    except Exception: return {"status": r.status_code}
    return {"status": r.status_code, **{k: b.get(k) for k in ("error", "field", "code", "reason", "activeRunId") if isinstance(b, dict) and k in b}}
def diff(exp, act, path=""):
    out = []
    if isinstance(exp, dict):
        for k, v in exp.items():
            if not isinstance(act, dict) or k not in act: out.append(f"{path}{k}: missing in response")
            else: out += diff(v, act[k], f"{path}{k}.")
    elif isinstance(exp, list):
        if not isinstance(act, list) or len(exp) != len(act): out.append(f"{path.rstrip('.')}: sent {exp!r} got {act!r}")
        else:
            for i, (e, a) in enumerate(zip(exp, act)): out += diff(e, a, f"{path.rstrip('.')}[{i}].")
    elif exp != act: out.append(f"{path.rstrip('.')}: sent {exp!r} got {act!r}")
    return out
def job_names(prefix):
    return sorted(j["name"] for j in T.call("GET", "/backup-jobs") if j["name"].startswith(prefix))
def db_job_count(prefix): return int(appdb(f"select count(*) from backup_jobs where \"Name\" like '{prefix}%'")[0][0])


def cleanup():
    """Disable every job this script created, remove schedules, cancel its own active runs, delete its artifacts; remove throwaway containers."""
    out = {"jobs": 0, "cancelled": 0, "artifacts": 0}
    for jid in MINE:
        try:
            T.call("DELETE", f"/backup-jobs/{jid}/schedule", expect=(200, 204, 404)); T.call("POST", f"/backup-jobs/{jid}/disable", expect=(200, 204, 404)); out["jobs"] += 1
            for r in runs_of(jid):
                if r["status"] in ("Pending", "Leased", "Running", "RetryPending", "Paused"):
                    T.raw("POST", f"/backup-runs/{r['id']}/cancel", {}); out["cancelled"] += 1
        except Exception as e: print("cleanup job", jid, e)
    time.sleep(3)
    for jid in MINE:
        try:
            for a in T.call("GET", f"/backup-artifacts?jobId={jid}&pageSize=100")["items"]:
                if a.get("state") == "Available": T.raw("DELETE", f"/backup-artifacts/{a['id']}"); out["artifacts"] += 1
        except Exception as e: print("cleanup artifacts", jid, e)
    for tw in CONTAINERS:
        try: tw.down()
        except Exception as e: print("cleanup container", e)
    return out


# ---------- JOB-01: complete job in one POST ----------

def full_body(agent, kind):
    hp = C.hpg; common = dict(retryPolicy={"maxAttempts": 4, "initialDelaySeconds": 5, "maxDelaySeconds": 60, "backoff": "Fixed", "jitter": 0.1}, overlapPolicy="Queue", enabled=True,
                              retention={"enabled": True, "keepForSeconds": 604800, "deleteGracePeriodSeconds": 86400})
    if kind == "pg":
        cfg = {"engine": PG, "database": "e2e_src", "host": "127.0.0.1", "port": 5432, "integratedSecurity": False, "databaseLogin": {"userName": hp["user"], "password": hp["password"]},
               "encrypt": False, "trustServerCertificate": True, "stagingReference": "default", "storagePrefix": "", "copyOnly": True, "checksumPolicy": "Required", "compressionPolicy": "Required",
               "destinations": [dest_local(), dest_s3()], "requiredVerifiedReplicaCount": 2, "enforceThreeTwoOne": False, "encryptionPolicy": "Optional"}
        sched = {"enabled": True, "cronExpression": "15 3 * * 1-5", "timeZone": "Europe/Berlin", "missedRunGraceSeconds": 3600, "overlapPolicy": "Queue"}
    elif kind == "mssql":
        cfg = {"engine": "mssql", "database": "e2e_src", "host": "127.0.0.1", "port": 1433, "integratedSecurity": False, "databaseLogin": {"userName": "sa", "password": ST["host"]["mssql_sa_password"]},
               "encrypt": True, "trustServerCertificate": True, "stagingReference": "default", "storagePrefix": "gap-jr/job01/mssql", "copyOnly": False, "checksumPolicy": "Preferred", "compressionPolicy": "Disabled",
               "destinations": [dest_local()], "requiredVerifiedReplicaCount": 1, "enforceThreeTwoOne": False, "encryptionPolicy": "Required"}
        sched = {"enabled": True, "cronExpression": "*/30 2-4 * * *", "timeZone": "America/New_York", "missedRunGraceSeconds": 7200, "overlapPolicy": "Queue"}
    else:
        cfg = file_cfg([dest_local(), {**dest_sftp(), "isPrimary": False}], paths=["docs", "bin"], requiredVerifiedReplicaCount=2, encryptionPolicy="Optional")
        cfg["files"].update(includes=["**/*.txt", "**/*.bin"], excludes=["**/*.tmp"], symlinkPolicy="Preserve")
        sched = {"enabled": True, "cronExpression": "0 1 * * 0", "timeZone": "Europe/Istanbul", "missedRunGraceSeconds": 1800, "overlapPolicy": "Queue"}
    return {"name": uniq(f"full-{kind}") , "agentId": agent["agentId"], "configuration": cfg, "schedule": sched, **common}

def check_full(kind, agent):
    b = full_body(agent, kind); r = post(b); assert r.status_code == 201, f"{kind} create {r.status_code} {r.text[:400]}"
    j = r.json(); MINE.append(j["id"]); got = T.call("GET", f"/backup-jobs/{j['id']}"); lst = next(x for x in T.call("GET", "/backup-jobs") if x["id"] == j["id"])
    exp = json.loads(json.dumps(b)); c = exp["configuration"]; login = c.pop("databaseLogin", None)
    issues = diff({k: v for k, v in exp.items() if k not in ("agentId",)}, got) + [f"list-vs-get {x}" for x in diff(got, lst)]
    ref = got["configuration"]["databaseCredentialReference"]
    if login:
        assert ref and ref.startswith("dbc-"), f"typed login not exchanged for a reference: {ref!r}"
        assert "databaseLogin" not in got["configuration"] and "password" not in json.dumps(got).lower(), "typed login echoed back"
    issues = [i for i in issues if not i.startswith("configuration.databaseCredentialReference")]
    # persisted data: platform database row
    row = appdb(f"select \"Name\",\"Enabled\",\"OverlapPolicy\",\"RetryMaxAttempts\",extract(epoch from \"RetryInitialDelay\"),extract(epoch from \"RetryMaxDelay\"),\"RetryBackoff\",\"RetryJitter\",extract(epoch from \"RetentionKeepFor\"),extract(epoch from \"RetentionDeleteGrace\"),\"ScheduleEnabled\",\"ScheduleCron\",\"ScheduleTimeZone\",extract(epoch from \"ScheduleMissedRunGrace\"),\"AgentId\" from backup_jobs where \"Id\"='{j['id']}'")[0]
    cdb = json.loads(appdb(f"select \"Configuration\"::text from backup_jobs where \"Id\"='{j['id']}'")[0][0]); ci = {k.lower(): v for k, v in cdb.items()}
    s, rp, rt = b["schedule"], b["retryPolicy"], b["retention"]
    want = [b["name"], "t", "Queue", str(rp["maxAttempts"]), float(rp["initialDelaySeconds"]), float(rp["maxDelaySeconds"]), "1", float(rp["jitter"]), float(rt["keepForSeconds"]), float(rt["deleteGracePeriodSeconds"]), "t", s["cronExpression"], s["timeZone"], float(s["missedRunGraceSeconds"]), agent["agentId"]]
    have = row[:]; have[4], have[5], have[7], have[8], have[9], have[13] = (float(have[x]) for x in (4, 5, 7, 8, 9, 13))
    dbissues = [f"db col {i}: sent {w!r} stored {h!r}" for i, (w, h) in enumerate(zip(want, have)) if w != h]
    for k in ("engine", "database", "host", "port", "checksumpolicy", "compressionpolicy", "encryptionpolicy", "requiredverifiedreplicacount", "copyonly", "storageprefix", "encrypt", "trustservercertificate", "integratedsecurity"):
        exp_v = {"checksumpolicy": c["checksumPolicy"], "compressionpolicy": c["compressionPolicy"], "encryptionpolicy": c["encryptionPolicy"], "requiredverifiedreplicacount": c["requiredVerifiedReplicaCount"], "copyonly": c["copyOnly"],
                 "storageprefix": c["storagePrefix"], "trustservercertificate": c["trustServerCertificate"], "integratedsecurity": c["integratedSecurity"]}.get(k, c.get(k))
        if ci.get(k) != exp_v: dbissues.append(f"db configuration.{k}: sent {exp_v!r} stored {ci.get(k)!r}")
    if kind == "file":
        fl = {k.lower(): v for k, v in (ci.get("files") or {}).items()}
        if fl != {k.lower(): v for k, v in c["files"].items()}: dbissues.append(f"db files {fl}")
    ndest = len(cdb.get("Destinations") or cdb.get("destinations") or [])
    if ndest != len(c["destinations"]): dbissues.append(f"db destinations {ndest}")
    assert not issues and not dbissues, "; ".join(issues + dbissues)[:1500]
    return {"jobId": j["id"], "fieldsCompared": len(json.dumps(b)), "nextOccurrenceAt": got["nextOccurrenceAt"], "refPrefix": ref[:4] if ref else None}

@scenario("gap/JOB-01/api-full-job-postgres-one-post")
def job01_pg(): return check_full("pg", C.ha)
@scenario("gap/JOB-01/api-full-job-mssql-one-post")
def job01_mssql(): return check_full("mssql", C.ha)
@scenario("gap/JOB-01/api-full-job-file-one-post")
def job01_file(): return check_full("file", C.fa)

@scenario("gap/JOB-01/conditional-field-rules")
def job01_conditional():
    hp = C.hpg; out = {}; bad = []
    ref = C.ref(C.ha, hp["user"], hp["password"]); msref = C.ref(C.ha, "sa", ST["host"]["mssql_sa_password"])
    def mssql(**kw):
        c = {"engine": "mssql", "database": "e2e_src", "host": "127.0.0.1", "port": 1433, "integratedSecurity": False, "databaseCredentialReference": msref, "encrypt": True, "trustServerCertificate": True,
             "stagingReference": "default", "storagePrefix": "", "copyOnly": True, "checksumPolicy": "Required", "compressionPolicy": "Preferred", "destinations": [dest_local()], "requiredVerifiedReplicaCount": 1, "encryptionPolicy": "Off"}
        c.update(kw); return c
    def fcfg(**kw): return {**file_cfg(), **kw}
    cases = [  # label, agent, configuration, expected status, expected field (None = created)
        ("mssql-no-credential", C.ha, mssql(databaseCredentialReference=None), 400, "configuration.databaseLogin"),
        # DatabaseCredentialVault.PrepareAsync: integrated security stores no credential, so a reference sent with it is dropped, not rejected (checked below).
        ("mssql-integrated-with-credential-ref", C.ha, mssql(integratedSecurity=True), 201, None),
        ("mssql-integrated-no-credential", C.ha, mssql(integratedSecurity=True, databaseCredentialReference=None), 201, None),
        ("mssql-sql-login-with-ref", C.ha, mssql(), 201, None),
        ("pg-integrated-security", C.ha, pg_cfg(C.ha, integratedSecurity=True, databaseCredentialReference=None), 400, "configuration.integratedSecurity"),
        ("mssql-credential-ref-of-other-agent", C.ha, mssql(databaseCredentialReference="dbc-00000000000000000000000000000000-123456789abc"), 400, None),
        ("file-job-with-credential", C.fa, fcfg(databaseCredentialReference=msref, integratedSecurity=False), 400, "configuration.databaseLogin"),
        ("file-job-with-port", C.fa, fcfg(port=22), 400, "configuration.host"),
        ("file-job-without-files", C.fa, {k: v for k, v in fcfg().items() if k != "files"} | {"files": None}, 400, "configuration.files"),
        ("pg-job-with-files", C.ha, pg_cfg(C.ha, files={"paths": ["."], "includes": ["**"], "excludes": [], "symlinkPolicy": "Skip"}), 400, "configuration.files"),
        ("file-source-not-configured-on-agent", C.fa, file_cfg(source="gap_jr_nosuchsrc"), None, None),
        ("external-destination-without-connection", C.fa, fcfg(destinations=[{"storageProvider": "sftp", "storageConnectionId": None, "storageExecutionLocation": "Backend", "isPrimary": True}]), 400, "configuration.destinations[0].storageConnectionId"),
        ("agent-executed-external-destination", C.fa, fcfg(destinations=[{**dest_sftp(), "storageExecutionLocation": "Agent"}]), 400, "configuration.destinations[0].storageExecutionLocation"),
        ("external-destination-with-storage-prefix", C.fa, fcfg(destinations=[dest_sftp()], storagePrefix="pfx"), 400, "configuration.storagePrefix"),
        ("three-two-one-without-site-and-copies", C.fa, fcfg(enforceThreeTwoOne=True), 400, "configuration.sourceSite"),
    ]
    for label, ag, cfg, st, field in cases:
        r = post({"name": uniq("cond-" + label), "agentId": ag["agentId"], "configuration": cfg}); o = code_of(r)
        if r.status_code == 201:
            MINE.append(r.json()["id"])
            if label == "mssql-integrated-with-credential-ref":
                stored = T.call("GET", f"/backup-jobs/{r.json()['id']}")["configuration"]
                if stored["integratedSecurity"] is not True or stored["databaseCredentialReference"] is not None: bad.append(f"{label}: stored configuration kept the reference: {stored['integratedSecurity']}/{stored['databaseCredentialReference']}")
        out[label] = o
        if st is not None and (o["status"] != st or (field and o.get("field") != field)): bad.append(f"{label}: expected {st}/{field} got {o}")
    assert not bad, "; ".join(bad)
    return out


# ---------- JOB-02: validation matrix ----------
@scenario("gap/JOB-02/validation-matrix-field-errors-nothing-saved")
def job02_matrix():
    hp = C.hpg; tb = lib.Tenant("b"); foreign = next(iter(tb.call("GET", "/agents")), {"agentId": str(uuid.uuid4())})
    revoked_row = appdb(f"select \"Id\" from agents where \"TenantId\"='{T.id}' and \"Status\" <> 2 limit 1")
    revoked = {"agentId": revoked_row[0][0]} if revoked_row else {"agentId": str(uuid.uuid4())}
    vp = P + "v-" + uuid.uuid4().hex[:6] + "-"
    ok_pg = lambda **kw: pg_cfg(C.ha, **kw)
    ok_file = lambda **kw: {**file_cfg(), **kw}
    def files(**kw): return {**file_cfg(), "files": {"paths": ["docs"], "includes": ["**"], "excludes": [], "symlinkPolicy": "Skip", **kw}}
    sc = lambda **kw: {"enabled": True, "cronExpression": "0 3 * * *", "timeZone": "UTC", **kw}
    dst = lambda *d: {"destinations": list(d)}
    cases = []  # (label, agent, configuration, extra top-level body, expected http status, expected field, expected code or None)
    A = lambda label, cfg, extra, st, field, code=None, agent=None: cases.append((label, agent or C.ha, cfg, extra, st, field, code))
    F = lambda label, cfg, extra, st, field, code=None: cases.append((label, C.fa, cfg, extra, st, field, code))
    for n, v in (("empty", ""), ("whitespace", "   "), ("over-200", "x" * 201), ("control-char", "a\x01b")): A(f"name-{n}", ok_pg(), {"name": v}, 400, "name")
    A("name-null", ok_pg(), {"name": None}, 400, "name"); A("agent-empty-guid", file_cfg(), {"agentId": "00000000-0000-0000-0000-000000000000"}, 400, "agentId")
    for n, v in (("zero", 0), ("negative", -1), ("65536", 65536), ("70000", 70000)): A(f"port-{n}", ok_pg(port=v), {}, 400, "configuration.port")
    for n, v in (("empty", ""), ("129-chars", "d" * 129), ("leading-space", " e2e_src"), ("pg-64-bytes", "d" * 64)): A(f"database-{n}", ok_pg(db=v), {}, 400, "configuration.database")
    for n, v in (("space", "127.0.0.1 x"), ("semicolon", "h;drop"), ("empty", ""), ("slash", "a/b")): A(f"host-{n}", ok_pg(host=v), {}, 400, "configuration.host")
    A("engine-unknown", {**ok_pg(), "engine": "oracle"}, {}, 400, "configuration.engine"); A("engine-uppercase", {**ok_pg(), "engine": "MSSQL"}, {}, 400, "configuration.engine")
    for k, v in (("checksumPolicy", "Sometimes"), ("checksumPolicy", "required"), ("compressionPolicy", "Max"), ("encryptionPolicy", "Always"), ("stagingReference", "custom"), ("storagePrefix", "../x"), ("storagePrefix", "/abs"), ("storagePrefix", "a//b"), ("requiredVerifiedReplicaCount", 0), ("requiredVerifiedReplicaCount", 2), ("requiredVerifiedReplicaCount", -1)):
        A(f"cfg-{k}-{v}", {**ok_pg(), k: v}, {}, 400, f"configuration.{k}")
    A("overlap-unknown", ok_pg(), {"overlapPolicy": "Parallel"}, 400, "overlapPolicy"); A("overlap-numeric", ok_pg(), {"overlapPolicy": "1"}, 400, "overlapPolicy")
    A("overlap-conflicts-with-schedule", ok_pg(), {"overlapPolicy": "Skip", "schedule": sc(overlapPolicy="Queue")}, 400, "overlapPolicy", "conflicting_policy")
    for n, rp, fld in (("attempts-0", {"maxAttempts": 0}, "maxAttempts"), ("attempts-11", {"maxAttempts": 11}, "maxAttempts"), ("delay-0", {"initialDelaySeconds": 0}, "initialDelaySeconds"), ("delay-0.5", {"initialDelaySeconds": 0.5}, "initialDelaySeconds"),
                       ("delay-over-day", {"initialDelaySeconds": 86401, "maxDelaySeconds": 86400}, "initialDelaySeconds"), ("max-below-initial", {"initialDelaySeconds": 10, "maxDelaySeconds": 5}, "maxDelaySeconds"),
                       ("jitter-1.5", {"jitter": 1.5}, "jitter"), ("jitter-neg", {"jitter": -0.1}, "jitter"), ("backoff-unknown", {"backoff": "Linear"}, "backoff")):
        A(f"retry-{n}", ok_pg(), {"retryPolicy": rp}, 400, f"retryPolicy.{fld}")
    for n, v in (("61-minute", "61 * * * *"), ("60-minute", "60 * * * *"), ("3-field", "0 0 1"), ("garbage", "foo bar baz qux quux"), ("empty", ""), ("six-field-seconds", "* * * * * *"), ("step-zero", "*/0 * * * *"), ("month-13", "0 0 1 13 *"), ("dow-8", "0 0 * * 8")):
        A(f"cron-{n}", ok_pg(), {"schedule": sc(cronExpression=v)}, 400, "cronExpression", "invalid_cron")
    for n, v in (("unknown-iana", "Mars/Olympus"), ("windows-name", "Turkey Standard Time"), ("empty", ""), ("traversal", "../../etc/passwd"), ("utc-offset", "UTC+3"), ("too-long", "A" * 65)):
        A(f"timezone-{n}", ok_pg(), {"schedule": sc(timeZone=v)}, 400, "timeZone", "invalid_time_zone")
    for n, v in (("negative", -1), ("below-1-min", 30), ("over-7-days", 700000), ("absurd", 99999999999)): A(f"grace-{n}", ok_pg(), {"schedule": sc(missedRunGraceSeconds=v)}, 400, "missedRunGraceSeconds")
    A("retention-below-minimum", ok_pg(), {"retention": {"enabled": True, "keepForSeconds": 5, "deleteGracePeriodSeconds": 86400}}, 400, "retention")
    A("dest-none", {**ok_pg(), "destinations": []}, {}, 400, "configuration.destinations"); A("dest-17", {**ok_pg(), "destinations": [dest_local()] * 17}, {}, 400, "configuration.destinations")
    A("dest-two-primaries", {**ok_pg(), "destinations": [dest_local(), dest_s3(True)], "requiredVerifiedReplicaCount": 1}, {}, 400, "configuration.destinations", "primary_destination_required")
    A("dest-no-primary", {**ok_pg(), "destinations": [{**dest_local(), "isPrimary": False}]}, {}, 400, "configuration.destinations", "primary_destination_required")
    A("dest-duplicate", {**ok_pg(), "destinations": [dest_local(), {**dest_local(), "isPrimary": False}], "requiredVerifiedReplicaCount": 2}, {}, 400, "configuration.destinations", "duplicate_destination")
    A("dest-unknown-provider", {**ok_pg(), "destinations": [{**dest_local(), "storageProvider": "ftp"}]}, {}, 400, "configuration.destinations[0].storageProvider")
    A("dest-missing-exec-location", {**ok_pg(), "destinations": [{**dest_local(), "storageExecutionLocation": None}]}, {}, 400, "configuration.destinations[0].storageExecutionLocation")
    A("dest-agent-exec-on-s3", {**ok_pg(), "destinations": [{**dest_s3(True), "storageExecutionLocation": "Agent"}]}, {}, 400, "configuration.destinations[0].storageExecutionLocation")
    A("dest-s3-without-connection", {**ok_pg(), "destinations": [{**dest_s3(True), "storageConnectionId": None}]}, {}, 400, "configuration.destinations[0].storageConnectionId")
    for n, kw in (("parent-dir", {"paths": ["../../etc"]}), ("absolute", {"paths": ["/etc"]}), ("drive-letter", {"paths": ["C:/Windows"]}), ("backslash", {"paths": ["a\\b"]}), ("embedded-dotdot", {"paths": ["a/../../b"]}), ("empty-segment", {"paths": ["a//b"]}), ("empty-string", {"paths": [""]}),
                  ("no-paths", {"paths": []}), ("include-dotdot", {"includes": ["../x"]}), ("exclude-absolute", {"excludes": ["/etc/**"]}), ("symlink-follow", {"symlinkPolicy": "Follow"}), ("over-256-paths", {"paths": [f"p{i}" for i in range(257)]})):
        F(f"files-{n}", files(**kw), {}, 400, "configuration.files")
    F("file-source-name-with-slash", ok_file(database="a/b"), {}, 400, "configuration.database"); F("file-source-name-traversal", ok_file(database="../etc"), {}, 400, "configuration.database")
    F("file-host-not-localhost", ok_file(host="example.org"), {}, 400, "configuration.host"); F("file-not-integrated", ok_file(integratedSecurity=False), {}, 400, "configuration.databaseLogin")
    results, bad, observed = {}, [], {}
    def submit(label, agent, cfg, extra, st, field, code, override_agent=None):
        b = {"name": vp + label, "agentId": override_agent or agent["agentId"], "configuration": cfg, **extra}
        r = post(b); o = code_of(r); results[label] = o
        if r.status_code == 201: MINE.append(r.json()["id"])
        if o["status"] != st or (field and o.get("field") != field) or (code and o.get("code") != code): bad.append(f"{label}: expected {st}/{field}/{code} got {o}")
        return o
    for case in cases: submit(*case)
    for label, aid in (("agent-unknown", str(uuid.uuid4())), ("agent-foreign-tenant", foreign["agentId"]), ("agent-revoked", revoked["agentId"])): submit(label, C.ha, file_cfg(), {}, 404, None, None, aid)
    submit("agent-missing", C.ha, file_cfg(), {}, 400, "agentId", None, "00000000-0000-0000-0000-000000000000")
    # shapes the framework rejects before our validation: still a 400 and nothing saved
    for label, raw_body in (("body-not-json-object", "[1,2]"), ("port-as-string", json.dumps({"name": vp + "port-string", "agentId": C.ha["agentId"], "configuration": {**ok_pg(), "port": "abc"}})),
                            ("configuration-missing", json.dumps({"name": vp + "cfg-missing", "agentId": C.ha["agentId"]}))):
        r = T.s.request("POST", T.base + "/backup-jobs", data=raw_body, headers={"Idempotency-Key": uuid.uuid4().hex, "Content-Type": "application/json"}); results[label] = code_of(r)
        if r.status_code != 400: bad.append(f"{label}: expected 400 got {code_of(r)}")
    # permissions
    viewer = api.login(ST["roles"]["viewer-a"]["login"], ST["roles"]["viewer-a"]["password"]); op = api.login(ST["roles"]["operator-a"]["login"], ST["roles"]["operator-a"]["password"])
    anon = api.requests.Session(); anon.verify = False
    good = body(C.ha, ok_pg(), "perm")
    for label, sess, b, st, extra in (("viewer-create", viewer, {**good, "name": vp + "viewer-create"}, 403, None), ("anonymous-create", anon, {**good, "name": vp + "anon-create"}, 401, None),
                                      ("operator-with-retention", op, {**good, "name": vp + "op-retention", "retention": {"enabled": True, "keepForSeconds": 604800}}, 403, "administrator_required")):
        r = post(b, session=sess); o = code_of(r); results[label] = o
        if o["status"] != st or (extra and o.get("code") != extra): bad.append(f"{label}: expected {st}/{extra} got {o}")
    r = post({**good, "name": vp + "operator-create-ok"}, session=op); results["operator-create-ok"] = code_of(r)
    if r.status_code == 201: MINE.append(r.json()["id"])
    else: bad.append(f"operator-create-ok: {code_of(r)}")
    j = create(C.ha, ok_pg(), "perm-target")
    for label, req in (("viewer-update", ("PUT", f"/backup-jobs/{j['id']}", {"name": "x", "agentId": C.ha["agentId"], "configuration": ok_pg()})), ("viewer-start-run", ("POST", f"/backup-jobs/{j['id']}/runs", {})), ("viewer-disable", ("POST", f"/backup-jobs/{j['id']}/disable", None)), ("viewer-set-schedule", ("PUT", f"/backup-jobs/{j['id']}/schedule", sc()))):
        r = viewer.request(req[0], T.base + req[1], json=req[2], headers={"Idempotency-Key": uuid.uuid4().hex}); results[label] = code_of(r)
        if r.status_code != 403: bad.append(f"{label}: expected 403 got {code_of(r)}")
    cb = lib.Tenant("b")
    for label, m, p, b in (("tenant-b-get-job", "GET", f"/backup-jobs/{j['id']}", None), ("tenant-b-update-job", "PUT", f"/backup-jobs/{j['id']}", {"name": "x", "agentId": foreign["agentId"], "configuration": file_cfg()}), ("tenant-b-start-run", "POST", f"/backup-jobs/{j['id']}/runs", {}), ("tenant-b-disable", "POST", f"/backup-jobs/{j['id']}/disable", None)):
        r = cb.s.request(m, cb.base + p, json=b, headers={"Idempotency-Key": uuid.uuid4().hex}); results[label] = code_of(r)
        if r.status_code != 404: bad.append(f"{label}: expected 404 got {code_of(r)}")
    # nothing saved: every case name is unique, so the job list and the platform database must contain none of the rejected names
    saved_api = job_names(vp); saved_db = db_job_count(vp)
    accepted = {k for k, v in results.items() if v["status"] == 201}
    leaked = [n for n in saved_api if n[len(vp):] not in accepted]
    if leaked: bad.append(f"rejected requests were saved: {leaked}")
    if saved_db != len(accepted): bad.append(f"db job count {saved_db} != accepted {len(accepted)}")
    assert not bad, f"{len(bad)} deviations: " + " | ".join(bad)[:3500]
    return {"cases": len(results), "accepted201": sorted(accepted), "savedJobsApi": len(saved_api), "savedJobsDb": saved_db}

@scenario("gap/JOB-03/lifecycle-delete")
def job03():
    j = create(C.fa, file_cfg(), "lifecycle"); jid = j["id"]
    rid = run_id(start(jid)); d = wait_run(rid, 300)
    assert d["status"] == "Succeeded" and len(arts(d)) == 1, f"run {rid}: {sig(d)}"
    aid = arts(d)[0]["id"]
    cfg = file_cfg(checksumPolicy="Preferred")
    updated = T.call("PUT", f"/backup-jobs/{jid}", {"name": j["name"]+"-edited", "agentId": C.fa["agentId"], "configuration": cfg, "overlapPolicy":"Queue"})
    stored = T.call("GET", f"/backup-jobs/{jid}")
    assert stored["name"] == j["name"]+"-edited" and stored["overlapPolicy"] == "Queue" and stored["configuration"]["checksumPolicy"] == "Preferred"
    T.call("POST", f"/backup-jobs/{jid}/disable")
    disabled = T.call("GET", f"/backup-jobs/{jid}")
    denied = T.raw("POST", f"/backup-jobs/{jid}/runs", {})
    assert disabled["enabled"] is False and denied.status_code in (404,409)
    assert getrun(rid)["status"] == "Succeeded" and T.call("GET", f"/backup-artifacts/{aid}")["id"] == aid
    T.call("POST", f"/backup-jobs/{jid}/enable")
    assert T.call("GET", f"/backup-jobs/{jid}")["enabled"] is True
    # Product decision (#49 K1-A): a job is never deleted, only disabled, so its run and artifact history stays. DELETE must be refused and change nothing.
    deletion = T.raw("DELETE", f"/backup-jobs/{jid}")
    assert deletion.status_code == 405, f"job DELETE must be refused with 405, got HTTP {deletion.status_code}; job={jid}"
    assert T.call("GET", f"/backup-jobs/{jid}")["enabled"] is True and getrun(rid)["status"] == "Succeeded" and T.call("GET", f"/backup-artifacts/{aid}")["id"] == aid
    T.call("POST", f"/backup-jobs/{jid}/disable")
    return {"jobId":jid,"runId":rid,"artifactId":aid,"deleteStatus":deletion.status_code}

@scenario("gap/JOB-07/next-occurrence-timezones")
def job07():
    results = {}
    for zone in ("Europe/Istanbul", "Europe/Berlin", "America/New_York"):
        j = create(C.fa, file_cfg(), "zone", schedule={"enabled":True,"cronExpression":"0 12 * * *","timeZone":zone})
        got = T.call("GET",f"/backup-jobs/{j['id']}")
        local = now().astimezone(ZoneInfo(zone)); candidate = local.replace(hour=12,minute=0,second=0,microsecond=0)
        if candidate <= local: candidate += dt.timedelta(days=1)
        expected = candidate.astimezone(UTC); actual = iso(got["nextOccurrenceAt"])
        assert actual == expected, f"{zone}: expected {expected} got {actual}"
        results[zone] = actual.isoformat()
    return results

@scenario("gap/JOB-07/2027-dst-next-occurrences")
def job07_dst():
    cases=[("America/New_York",3,14,2,30),("Europe/Berlin",3,28,2,30),("Europe/Berlin",10,31,2,30)]
    results={}
    for zone,month,day,hour,minute in cases:
        tz=ZoneInfo(zone); wall=dt.datetime(2027,month,day,hour,minute)
        candidates=sorted({wall.replace(tzinfo=tz,fold=f).astimezone(UTC) for f in (0,1)})
        valid=[v for v in candidates if v.astimezone(tz).replace(tzinfo=None)==wall]
        if valid: expected=valid[0]
        else:
            expected=candidates[0]
            while expected.astimezone(tz).replace(tzinfo=None) < wall: expected+=dt.timedelta(minutes=1)
        j=create(C.fa,file_cfg(),"dst",schedule={"enabled":True,"cronExpression":f"{minute} {hour} {day} {month} *","timeZone":zone})
        actual=iso(T.call("GET",f"/backup-jobs/{j['id']}")["nextOccurrenceAt"])
        key=f"{zone} {month}-{day}"
        results[key]={"expected":expected.isoformat(),"actual":actual.isoformat() if actual else None}
        assert actual==expected, f"{key}: expected={expected.isoformat()} actual={actual}"
    return results

@scenario("gap/JOB-07/local-time-real-trigger")
def job07_trigger():
    target=(now()+dt.timedelta(minutes=2)).replace(second=0,microsecond=0)
    local=target.astimezone(ZoneInfo("Asia/Tokyo"))
    cron=f"{local.minute} {local.hour} {local.day} {local.month} *"
    j=create(C.fa,file_cfg(),"trigger",schedule={"enabled":True,"cronExpression":cron,"timeZone":"Asia/Tokyo"})
    actual=iso(T.call("GET",f"/backup-jobs/{j['id']}")["nextOccurrenceAt"])
    assert actual==target, f"next={actual}, expected={target}"
    end=time.time()+240; occurrences=[]
    while time.time()<end:
        occurrences=T.call("GET",f"/backup-jobs/{j['id']}/schedule/occurrences")
        if occurrences: break
        time.sleep(2)
    assert occurrences, f"no occurrence by {now().isoformat()} job={j['id']}"
    o=occurrences[0]; assert iso(o["scheduledAt"])==target and o["outcome"]=="RunCreated" and o["runId"], o
    d=wait_run(o["runId"],300)
    assert d["status"]=="Succeeded" and iso(d["scheduledFor"])==target, f"run={o['runId']} {sig(d)}"
    return {"jobId":j["id"],"runId":o["runId"],"targetUtc":target.isoformat(),"local":local.isoformat(),"occurrence":o}


@scenario("gap/JOB-09/closed-port-retry-exhaustion")
def job09():
    policy = {"maxAttempts":3,"initialDelaySeconds":2,"maxDelaySeconds":2,"backoff":"Fixed","jitter":0}
    j = create(C.wa, pg_cfg(C.wa,port=5999), "retry", retryPolicy=policy)
    rid = run_id(start(j["id"])); d = wait_run(rid, 300)
    attempts = d["attempts"]
    assert d["status"] == "Failed" and d["retry"]["state"] == "Exhausted" and len(attempts) == 3, f"{rid}: {sig(d)} reason={d.get('reasonCode')}"
    assert all(a["reasonCode"] == "DatabaseUnavailable" for a in attempts), f"{rid}: {[(a['reasonCode'],a['retryable']) for a in attempts]}"
    gaps = [sec(attempts[i]["completedAt"],attempts[i+1]["createdAt"]) for i in range(2)]
    assert all(x >= 2 for x in gaps), f"{rid}: gaps={gaps}"
    r = T.raw("POST",f"/backup-runs/{rid}/retry",{})
    assert r.status_code == 409 and r.json().get("reason") == "attempts_exhausted", code_of(r)
    return {"runId":rid,"reasonCode":d["reasonCode"],"state":d["retry"]["state"],"gapsSeconds":gaps}

@scenario("gap/JOB-09/permanent-failure-no-retry")
def job09_permanent():
    j=create(C.ha,pg_cfg(C.ha,db="gap_jr_missing_9301"),"permanent",retryPolicy={"maxAttempts":3,"initialDelaySeconds":2,"maxDelaySeconds":2,"backoff":"Fixed","jitter":0})
    rid=run_id(start(j["id"])); d=wait_run(rid,300)
    assert d["status"]=="Failed" and d["reasonCode"] in ("InvalidConfiguration","DatabaseNotFound") and d["retry"]["state"]=="NotRetryable" and len(d["attempts"])==1, f"{rid}: {sig(d)}, reason={d['reasonCode']}"
    time.sleep(65); again=getrun(rid)
    assert sig(again)==sig(d), f"{rid}: initial={sig(d)} after65s={sig(again)}"
    return {"jobId":j["id"],"runId":rid,"reasonCode":d["reasonCode"],"attempts":len(d["attempts"]),"retryState":d["retry"]["state"],"watchedSeconds":65}

@scenario("gap/JOB-09/retry-after-temporary-outage-recovers")
def job09_recover():
    tw=g.Throwaway("job-retry",75); tw.user="gapjrretry"; CONTAINERS.append(tw)
    g.sh("docker","network","create","--subnet","10.77.75.0/24",tw.net,check=True)
    g.sh("docker","run","-d","--name",tw.name,"--network",tw.net,"--ip",tw.ip,"-p","127.0.0.1:15475:5432","-e",f"POSTGRES_USER={tw.user}","-e",f"POSTGRES_PASSWORD={tw.password}","--shm-size=256m",tw.image,check=True)
    tw.wait(); tw.seed(rows=5000)
    cfg=C.host_pg_cfg("gap_jr_src","127.0.0.1",15475,tw.user,tw.password)
    j=create(C.ha,flows.db_job_config(PG,cfg,[dest_local()],encrypt=False),"recovers",retryPolicy={"maxAttempts":3,"initialDelaySeconds":12,"maxDelaySeconds":12,"backoff":"Fixed","jitter":0})
    g.sh("docker","stop",tw.name,check=True)
    rid=run_id(start(j["id"]))
    # attempt 1 will fail while container is stopped
    end=time.time()+90; first_failed=False
    while time.time()<end:
        d=getrun(rid)
        if len(d["attempts"])>=1 and d["attempts"][0]["status"]=="Failed":
            first_failed=True; break
        time.sleep(1)
    assert first_failed, f"first attempt did not fail: {sig(d)}"
    assert d["attempts"][0]["reasonCode"]=="DatabaseUnavailable" and d["retry"]["state"]=="Scheduled", f"state={d['retry']}"
    # start container before next attempt is due
    g.sh("docker","start",tw.name,check=True); tw.wait()
    d=wait_run(rid,180)
    assert d["status"]=="Succeeded" and len(d["attempts"])==2 and len(arts(d))==1, f"{rid}: {sig(d)}"
    assert d["attempts"][0]["status"]=="Failed" and d["attempts"][1]["status"]=="Succeeded"
    return {"jobId":j["id"],"runId":rid,"attempts":2,"attempt1":d["attempts"][0]["reasonCode"],"attempt2":d["attempts"][1]["status"],"artifactId":arts(d)[0]["id"]}

@scenario("gap/JOB-09/manual-retry-conflict-matrix")
def job09_manual_rules():
    # create a file job, run it to success -> 409 "succeeded"
    j=create(C.fa,file_cfg(),"mrules"); r1=run_id(start(j["id"])); wait_run(r1,180)
    c1=code_of(T.raw("POST",f"/backup-runs/{r1}/retry",{}))
    assert c1["status"]==409 and c1.get("reason")=="succeeded", c1
    # cancel a run -> 409 "cancelled"
    r2=run_id(start(j["id"])); T.call("POST",f"/backup-runs/{r2}/cancel",{}); wait_run(r2,180)
    c2=code_of(T.raw("POST",f"/backup-runs/{r2}/retry",{}))
    assert c2["status"]==409 and c2.get("reason")=="cancelled", c2
    return {"succeededRunRetry":c1,"cancelledRunRetry":c2}


@scenario("gap/JOB-15/unsupported-engine-admission")
def job15_capability():
    cfg=flows.db_job_config("mssql",{"host":"localhost","port":1433,"database":"e2e_src","ref":None},[dest_local()],integratedSecurity=True,databaseCredentialReference=None)
    j=create(C.fa,cfg,"capability")
    r=T.raw("POST",f"/backup-jobs/{j['id']}/runs",{})
    assert r.status_code==409 and r.json().get("error")=="admission_denied", f"job={j['id']} {code_of(r)}"
    assert not runs_of(j["id"]), f"admission created run for {j['id']}"
    return {"jobId":j["id"],"response":code_of(r)}


@scenario("gap/JOB-15/missing-source-precheck")
def job15():
    j = create(C.fa,file_cfg(paths=["gap_jr_missing_path_98371"]),"missing")
    started=time.monotonic(); rid=run_id(start(j["id"])); d=wait_run(rid,300)
    assert d["status"] == "Failed" and not arts(d), f"{rid}: {sig(d)}"
    return {"runId":rid,"reasonCode":d["reasonCode"],"reason":d["reason"],"elapsedSeconds":round(time.monotonic()-started,1),"artifacts":len(arts(d))}

@scenario("gap/JOB-14/history-artifact-relations")
def job14():
    j = create(C.fa,file_cfg(),"history"); rid=run_id(start(j["id"])); d=wait_run(rid,300)
    assert d["status"] == "Succeeded" and len(arts(d)) == 1, f"{rid}: {sig(d)}"
    a=T.call("GET",f"/backup-artifacts/{arts(d)[0]['id']}")
    listed=next(r for r in runs_of(j["id"]) if r["id"] == rid)
    last=T.call("GET",f"/backup-jobs/{j['id']}")["lastRun"]
    assert listed["status"] == d["status"] and last["id"] == rid
    assert a["runId"] == rid and a["jobId"] == j["id"] and a["attemptId"] == d["attempts"][0]["id"]
    row=appdb(f"select r.\"Status\",a.\"Number\",a.\"Status\",count(x.\"Id\") from backup_runs r join backup_run_attempts a on a.\"RunId\"=r.\"Id\" left join backup_artifacts x on x.\"AttemptId\"=a.\"Id\" where r.\"Id\"='{rid}' group by 1,2,3")[0]
    assert row == ["4","1","4","1"], f"db={row}"
    return {"jobId":j["id"],"runId":rid,"artifactId":a["id"],"databaseRow":row}

@scenario("gap/JOB-15/precheck-credentials-and-permissions")
def job15_credentials():
    # 1. Wrong DB password -> AuthenticationFailed
    cfg_badpass=C.host_pg_cfg("e2e_src","127.0.0.1",5432,C.hpg["user"],"wrong_password_9871")
    j1=create(C.ha,flows.db_job_config(PG,cfg_badpass,[dest_local()],encrypt=False),"badpass")
    r1=run_id(start(j1["id"])); d1=wait_run(r1,180)
    assert d1["status"]=="Failed" and d1["reasonCode"]=="AuthenticationFailed" and len(arts(d1))==0, f"badpass {r1}: {sig(d1)}"
    # 2. Role without connect privilege in throwaway container
    tw=g.Throwaway("job-perm",76); tw.user="gapjrperm"; CONTAINERS.append(tw)
    g.sh("docker","network","create","--subnet","10.77.76.0/24",tw.net,check=True)
    g.sh("docker","run","-d","--name",tw.name,"--network",tw.net,"--ip",tw.ip,"-p","127.0.0.1:15476:5432","-e",f"POSTGRES_USER={tw.user}","-e",f"POSTGRES_PASSWORD={tw.password}","--shm-size=256m",tw.image,check=True)
    tw.wait(); tw.seed(rows=100)
    tw.sql("create user gap_limited with password 'limpass'; revoke connect on database gap_jr_src from gap_limited; revoke connect on database postgres from gap_limited")
    cfg_lim=C.host_pg_cfg("gap_jr_src","127.0.0.1",15476,"gap_limited","limpass")
    j2=create(C.ha,flows.db_job_config(PG,cfg_lim,[dest_local()],encrypt=False),"limited")
    r2=run_id(start(j2["id"])); d2=wait_run(r2,180)
    assert d2["status"]=="Failed" and d2["reasonCode"] in ("AuthenticationFailed","PermissionDenied") and len(arts(d2))==0, f"limited {r2}: {sig(d2)}"
    return {"wrongPasswordReason":d1["reasonCode"],"limitedRoleReason":d2["reasonCode"],"artifactsCreated":len(arts(d1))+len(arts(d2))}


@scenario("gap/JOB-16/success-then-failure-health")
def job16():
    cfg=file_cfg(); j=create(C.fa,cfg,"health",schedule={"enabled":True,"cronExpression":"0 0 1 1 *","timeZone":"UTC"})
    before=T.call("GET",f"/backup-jobs/{j['id']}/health")
    good=run_id(start(j["id"])); gd=wait_run(good,300)
    assert gd["status"] == "Succeeded", f"{good}: {sig(gd)}"
    healthy=T.call("GET",f"/backup-jobs/{j['id']}/health")
    badcfg=file_cfg(paths=["gap_jr_missing_path_98371"])
    T.call("PUT",f"/backup-jobs/{j['id']}",{"name":j["name"],"agentId":C.fa["agentId"],"configuration":badcfg})
    failed=run_id(start(j["id"])); fd=wait_run(failed,300)
    assert fd["status"] == "Failed", f"{failed}: {sig(fd)}"
    after=T.call("GET",f"/backup-jobs/{j['id']}/health")
    summary=T.call("GET","/observability/health-summary")
    dashboard=T.call("GET","/dashboard")
    assert after["factors"]["freshness"]["reason"] == healthy["factors"]["freshness"]["reason"] == "backup_fresh"
    assert after["factors"]["replication"] == healthy["factors"]["replication"]
    assert T.call("GET",f"/backup-jobs/{j['id']}")["lastRun"]["id"] == failed
    assert any(x["jobId"] == j["id"] and x["state"] == after["state"] for x in summary["jobs"])
    return {"jobId":j["id"],"success":good,"failure":failed,"failureCode":fd["reasonCode"],"freshnessBefore":before["factors"]["freshness"],"freshnessAfter":after["factors"]["freshness"],"dashboardFailures24h":dashboard["runs"]["failedLast24Hours"]}

@scenario("gap/JOB-11/cancel-idempotency")
def job11():
    j=create(C.fa,file_cfg(paths=["bin"],dests=[dest_sftp()]),"cancel")
    T.call("PUT",f"/upload-bandwidth/jobs/{j['id']}",{"bytesPerSecond":2*1024*1024})
    try:
        rid=run_id(start(j["id"])); end=time.time()+120
        while time.time()<end:
            d=getrun(rid)
            if d["status"] == "Running": break
            time.sleep(.5)
        assert d["status"] == "Running", f"{rid}: {sig(d)}"
        T.call("POST",f"/backup-runs/{rid}/pause",{})
        paused=wait_run(rid,180,stop=("Paused",)+lib.TERMINAL)
        assert paused["status"]=="Paused" and paused["pauseReason"], f"{rid}: pause {sig(paused)} reason={paused.get('pauseReason')}"
        T.call("POST",f"/backup-runs/{rid}/resume",{})
        resumed=wait_run(rid,180,stop=("Running",)+lib.TERMINAL)
        assert resumed["status"]=="Running", f"{rid}: resume {sig(resumed)}"
        T.call("POST",f"/backup-runs/{rid}/cancel",{})
        d=wait_run(rid,180)
        assert d["status"] == "Cancelled" and not any(r["verificationState"]=="Verified" for a in arts(d) for r in a["replicas"]), f"{rid}: {sig(d)}"
        results={x:code_of(T.raw("POST",f"/backup-runs/{rid}/{x}",{})) for x in ("pause","resume","cancel")}
        assert results["pause"]["status"]==409 and results["resume"]["status"]==409 and results["cancel"]["status"]==200, f"run={rid} after cancel: {results}"
        # pause then cancel-while-paused
        r2=run_id(start(j["id"])); end=time.time()+120
        while time.time()<end:
            d=getrun(r2)
            if d["status"]=="Running": break
            time.sleep(.5)
        T.call("POST",f"/backup-runs/{r2}/pause",{})
        p2=wait_run(r2,180,stop=("Paused",)+lib.TERMINAL)
        assert p2["status"]=="Paused", f"r2 pause: {sig(p2)}"
        T.call("POST",f"/backup-runs/{r2}/cancel",{})
        c2=wait_run(r2,180)
        assert c2["status"]=="Cancelled" and c2["attempts"][-1]["status"]=="Cancelled" and not any(r["verificationState"]=="Verified" for a in arts(c2) for r in a["replicas"]), f"r2 cancel-while-paused: {sig(c2)}"
        return {"runId":rid,"run2Id":r2,"afterCancel":results,"cancelWhilePaused":c2["status"]}
    finally:
        T.raw("PUT",f"/upload-bandwidth/jobs/{j['id']}",{"bytesPerSecond":None})


@scenario("gap/JOB-10/concurrent-agent-limits-skip-queue")
def job10():
    cfg=file_cfg(dests=[dest_sftp()],paths=["bin"])
    jobs=[create(C.fa,cfg,"concurrent",overlapPolicy=p) for p in ("Skip","Queue")]
    for j in jobs: T.call("PUT",f"/upload-bandwidth/jobs/{j['id']}",{"bytesPerSecond":4*1024*1024})
    try:
        r1=run_id(start(jobs[0]["id"]))
        skip=T.raw("POST",f"/backup-jobs/{jobs[0]['id']}/runs",{})
        assert skip.status_code == 409 and skip.json().get("error") == "overlap_skipped" and skip.json().get("activeRunId") == r1, code_of(skip)
        r2=run_id(start(jobs[1]["id"])); r3=run_id(start(jobs[1]["id"]))
        ids=[r1,r2,r3]; seen_pending=False; maximum=0; end=time.time()+300; final={}
        while time.time()<end:
            details=[getrun(x) for x in ids]
            active=sum(a["status"] in ("Leased","Running") for d in details for a in d["attempts"])
            maximum=max(maximum,active)
            seen_pending |= any(d["status"]=="Pending" for d in details)
            assert active<=1, f"agent {C.fa['agentId']} active={active}, runs={ids}"
            if all(d["status"] in lib.TERMINAL for d in details): final=details; break
            time.sleep(.5)
        assert final and seen_pending and all(d["status"]=="Succeeded" and len(arts(d))==1 for d in final), f"runs={ids} pending={seen_pending} statuses={[sig(d) for d in details]}"
        assert all(len({a["number"] for a in d["attempts"]}) == len(d["attempts"]) for d in final)
        return {"runs":ids,"maxLeasedOrRunning":maximum,"pendingObserved":seen_pending,"artifactIds":[arts(d)[0]["id"] for d in final]}
    finally:
        for j in jobs: T.raw("PUT",f"/upload-bandwidth/jobs/{j['id']}",{"bytesPerSecond":None})

@scenario("gap/POL-11/growth-advisory-restore")
def pol11():
    tw=g.Throwaway("job-anomaly",72); tw.user="gapjruser"; CONTAINERS.append(tw)
    g.sh("docker","network","create","--subnet","10.77.72.0/24",tw.net,check=True)
    g.sh("docker","run","-d","--name",tw.name,"--network",tw.net,"--ip",tw.ip,"-p","127.0.0.1:15472:5432","-e",f"POSTGRES_USER={tw.user}","-e",f"POSTGRES_PASSWORD={tw.password}","--shm-size=256m",tw.image,check=True)
    tw.wait(); tw.seed(rows=10000)
    cfg=C.host_pg_cfg("gap_jr_src","127.0.0.1",15472,tw.user,tw.password)
    j=create(C.ha,flows.db_job_config(PG,cfg,[dest_local()],encrypt=False),"anomaly")
    baseline=[]
    for i in range(3):
        rid=run_id(start(j["id"])); d=wait_run(rid,600)
        assert d["status"]=="Succeeded" and len(arts(d))==1, f"baseline {rid}: {sig(d)}"
        baseline.append(arts(d)[0]["sizeBytes"])
    tw.sql("insert into items(name,payload) select 'name-'||g, md5(g::text)||md5((g*7)::text)||md5((g*13)::text) from generate_series(10001,200000) g","gap_jr_src")
    digest=tw.digest("gap_jr_src")
    rid=run_id(start(j["id"])); d=wait_run(rid,600)
    assert d["status"]=="Succeeded" and d["sizeAnomalyStatus"]=="SizeAnomalyWarning" and d["sizeAnomalyDeviationRatio"]>0.3, f"{rid}: status={d['status']} anomaly={d.get('sizeAnomalyStatus')} ratio={d.get('sizeAnomalyDeviationRatio')}"
    a=T.call("GET",f"/backup-artifacts/{arts(d)[0]['id']}")
    assert a["artifactChecksum"]==a["replicas"][0]["checksum"] and a["replicas"][0]["verificationState"]=="Verified"
    _,r=g.start_restore(T,a["id"],g.restore_req(C.ha,C.host_pg_target("127.0.0.1",15472,tw.user,tw.password),"gap_jr_restored"))
    restored=g.wait_restore(T,r.get("id",r.get("restoreId")),600)
    assert restored["status"]=="Succeeded" and tw.digest("gap_jr_restored")==digest, f"restore={restored['status']}"
    return {"jobId":j["id"],"runId":rid,"artifactId":a["id"],"baselineBytes":baseline,"newBytes":a["sizeBytes"],"ratio":d["sizeAnomalyDeviationRatio"],"restoreId":restored["id"],"digest":digest}


def main():
    global C, T
    C = g.Ctx(); T = C.t
    tests = [job01_pg,job01_mssql,job01_file,job01_conditional,job02_matrix,job03,job07,job07_dst,job07_trigger,job09,job09_permanent,job09_recover,job09_manual_rules,job10,job11,job14,job15,job15_capability,job15_credentials,job16,pol11]
    try:
        for f in tests:
            if len(sys.argv) == 1 or any(x in f.scenario for x in sys.argv[1:]): f()
    finally:
        print("cleanup", json.dumps(cleanup()), flush=True)

if __name__ == "__main__": main()
