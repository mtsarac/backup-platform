"""Gap tests POL-03..POL-06 (hard quota admission, reservation, plan/subscription changes, usage reconcile/rebuild) in the private tenant gap-sp-quota.
Usage: python3 gap_quota.py [pol03 pol04 pol05 pol06 cleanup ...]. Scenario names gap/POL-0x/<detail>; results go to results.jsonl through lib.scenario.
Only the private tenant's override/plan/subscription is touched, never tenant A. No secrets are printed."""
import sys, time, json, threading, uuid, shutil, pathlib
import gap_sp_common as g, api, lib, flows
from lib import scenario, TERMINAL

MB = g.MB; AN = "quota"
ADM = api.login()
PLANS = {}  # name -> plan id, plans created by this script (disabled again by cleanup)


def tb(t): return f"{api.BASE}/api/platform/tenants/{t.id}"
def jr(r): return r.json() if r.content else None


def ov(t, kind, b=None):
    r = ADM.put(tb(t) + "/subscription/storage-limit-override", json={"kind": kind, **({"bytes": b} if b is not None else {})})
    assert r.ok, f"override {kind}: {r.status_code} {r.text[:200]}"
    return r.json()


def Q(t): return t.call("GET", "/storage-quota")
def PQ(t): r = ADM.get(tb(t) + "/quota"); assert r.ok, r.text[:200]; return r.json()
def PU(t): r = ADM.get(tb(t) + "/storage-usage"); assert r.ok, r.text[:200]; return r.json()
def sub(t): return ADM.get(tb(t) + "/subscription").json()["subscription"]


def settle(t, timeout=240, reserved=0):
    """Waits until reservedBytes == reserved (reservation released after the run ends)."""
    end = time.time() + timeout; q = Q(t)
    while time.time() < end:
        q = Q(t)
        if q["reservedBytes"] == reserved: return q
        time.sleep(1)
    raise TimeoutError(f"reservedBytes stayed {q['reservedBytes']}")


def mkjob(t, ag, subdir, mb, dests=None, name=None, size=None):
    g.put_file(AN, f"{subdir}/f.bin", size if size is not None else int(mb * MB))
    return g.new_job(t, ag, name or subdir, g.gcfg(dests or [g.gdest("local")], paths=[subdir]), retryPolicy={"maxAttempts": 1})


def nruns(t, job): return t.call("GET", f"/backup-jobs/{job['id']}/runs?pageSize=1")["totalCount"]


def post_run(t, job):
    r = t.raw("POST", f"/backup-jobs/{job['id']}/runs", {})
    rid = None
    if r.status_code < 300:
        j = r.json(); rid = j.get("id", j.get("run", {}).get("id"))
    return r.status_code, (r.text[:200] if r.status_code >= 300 else ""), rid


def watch(t, rid, interval=0.1, timeout=900):
    """Polls storage-quota + run until the run ends. Returns detail, samples [(t, status, reserved, used, state)]."""
    t0 = time.time(); s = []
    while time.time() - t0 < timeout:
        q = Q(t); d = t.call("GET", f"/backup-runs/{rid}")
        s.append((round(time.time() - t0, 2), d["status"], q["reservedBytes"], q["usedBytes"], q["state"]))
        if d["status"] in TERMINAL: return d, s
        time.sleep(interval)
    raise TimeoutError(rid)


def peak(s): return max(x[2] for x in s)
def art_of(d): return g.arts(d)[0] if g.arts(d) else None


def est(t, job):
    """The admission estimate: size of the job's most recent artifact (1 counted copy under LogicalArtifactBytes)."""
    items = [a for a in t.call("GET", "/backup-artifacts?pageSize=100")["items"] if a["jobId"] == job["id"]]
    return max(items, key=lambda a: (a["createdAt"], a["id"]))["sizeBytes"]


def reason(d): a = d["attempts"][-1]; return f"{a['status']}/{a.get('reasonCode')}"


def seed(t, job):
    """Runs the job once (the caller has no hard limit) and returns the artifact size."""
    rid, d = g.run_to_end(t, job, 600)
    assert d["status"] == "Succeeded", f"seed {d['status']} {reason(d)}"
    return art_of(d)["sizeBytes"]


def admit_expect(t, job, want_code, limit, extra=None):
    """Starts a run expecting a refusal; returns evidence and asserts no run/reservation was created."""
    n0 = nruns(t, job); q0 = Q(t)
    st, txt, rid = post_run(t, job)
    n1 = nruns(t, job); q1 = Q(t)
    assert rid is None and st == 409 and want_code in txt, f"expected 409 {want_code}, got {st} {txt}"
    assert n1 == n0, f"a run was created despite the refusal ({n0}->{n1})"
    assert q1["reservedBytes"] == 0 and q1["usedBytes"] == q0["usedBytes"], f"refusal left reservation/usage: {q1}"
    return {"http": st, "body": txt, "runsBefore": n0, "runsAfter": n1, "reserved": q1["reservedBytes"]}


# ------------------------------------------------------------------ POL-03
@scenario("gap/POL-03/below-equal-above-boundary")
def pol03_boundary(t, ag):
    ov(t, "none")
    job = mkjob(t, ag, "b64", 64, name="pol03-b")
    E = seed(t, job); out = {"E0": E}
    q = settle(t); U = q["usedBytes"]; out["U0"] = U
    # --- L = U+E-1 -> refused
    L = U + E - 1; ov(t, "limited", L); q = Q(t)
    assert q["limitBytes"] == L and q["remainingBytes"] == L - U and q["reservedBytes"] == 0, q
    out["below"] = {"limit": L, "U": U, "E": E, "state": q["state"], "newBackupsAllowed": q["newBackupsAllowed"], **admit_expect(t, job, "quota_exceeded", L)}
    # --- L = U+E -> equal: the code refuses only when used+reserved+estimate > limit
    L = U + E; ov(t, "limited", L)
    st, txt, rid = post_run(t, job); assert rid, f"equal boundary refused: {st} {txt}"
    d, s = watch(t, rid)
    q2 = settle(t); E2 = art_of(d)["sizeBytes"] if art_of(d) else None
    out["equal"] = {"limit": L, "http": st, "run": d["status"], "reason": reason(d), "peakReserved": peak(s), "reservedAfter": q2["reservedBytes"], "artifactSize": E2,
                    "usedAfter": q2["usedBytes"], "usedDelta": q2["usedBytes"] - U, "stateAfter": q2["state"], "samples": len(s),
                    "statesDuringRun": sorted({x[4] for x in s}), "statusesSeen": sorted({x[1] for x in s})}
    assert peak(s) == E, f"reserved during run {peak(s)} != estimate {E}"
    if d["status"] == "Succeeded":
        assert q2["usedBytes"] - U == E2 and E2 <= E, out["equal"]
    else:
        assert d["status"] == "Failed" and d["attempts"][-1].get("reasonCode") == "QuotaExceeded" and q2["usedBytes"] == U, out["equal"]
    out["afterEqualRefusal"] = admit_expect(t, job, "quota_exceeded", L) if q2["usedBytes"] > U else None
    # Actual compressed size can vary by a few bytes; admission and successful completion are separate checks.
    E = est(t, job); U = q2["usedBytes"]; L = U + E + 1; ov(t, "limited", L)
    st, txt, rid = post_run(t, job); assert rid, f"above boundary refused: {st} {txt}"
    d, s = watch(t, rid); q3 = settle(t)
    A3 = art_of(d)["sizeBytes"] if art_of(d) else None
    out["above"] = {"limit": L, "http": st, "run": d["status"], "reason": reason(d), "peakReserved": peak(s), "reservedAfter": q3["reservedBytes"], "usedDelta": q3["usedBytes"] - U, "artifactSize": A3, "estimate": E}
    assert peak(s) == E and q3["reservedBytes"] == 0, out["above"]
    if d["status"] == "Succeeded": assert q3["usedBytes"] - U == A3, out["above"]
    else: assert d["attempts"][-1].get("reasonCode") == "QuotaExceeded" and q3["usedBytes"] == U, out["above"]
    # tenant view == platform view
    pq = PQ(t); assert {k: pq[k] for k in ("limitBytes", "usedBytes", "reservedBytes", "state", "newBackupsAllowed", "denialCode")} == {k: q3[k] for k in ("limitBytes", "usedBytes", "reservedBytes", "state", "newBackupsAllowed", "denialCode")}, (pq, q3)
    ov(t, "none")
    return out


@scenario("gap/POL-03/concurrent-admission-reservation")
def pol03_concurrent(t, ag):
    ov(t, "none")
    j1 = mkjob(t, ag, "c1", 48, name="pol03-c1"); j2 = mkjob(t, ag, "c2", 48, name="pol03-c2")
    E1 = seed(t, j1); E2 = seed(t, j2); out = {"E1": E1, "E2": E2, "rounds": []}
    for rnd, (a, b) in enumerate([(j1, j2), (j2, j1), (j1, j2)]):
        U = settle(t)["usedBytes"]; E1 = est(t, j1); E2 = est(t, j2)
        # headroom for exactly one: limit = U + E1 + E2 - 1; the third round gives headroom for both (limit = U+E1+E2)
        both = rnd == 2; L = U + E1 + E2 - (0 if both else 1); ov(t, "limited", L)
        # Freeze execution at the exact admission limit because staged archives can grow by a few bytes
        if both:
            rc, _ = g.stop_agent(AN)
            assert rc == 0, "could not stop the private quota agent"
        try:
            bar = threading.Barrier(2); res = {}
            def go(k, j):
                bar.wait(); res[k] = (time.time(), post_run(t, j))
            th = [threading.Thread(target=go, args=(k, j)) for k, j in (("a", a), ("b", b))]
            [x.start() for x in th]; [x.join() for x in th]
            outs = {k: v[1] for k, v in res.items()}; ok = [k for k, v in outs.items() if v[2]]; bad = [k for k, v in outs.items() if not v[2]]
            q = Q(t); r = {"round": rnd, "limit": L, "U": U, "both": both, "admitted": ok, "refused": {k: outs[k][:2] for k in bad}, "reservedRightAfter": q["reservedBytes"]}
            if both:
                assert len(ok) == 2, f"both fit the limit but {len(ok)} admitted: {outs}"
                assert q["reservedBytes"] == E1 + E2, r
                ov(t, "limited", L + MB)
            else:
                assert len(ok) == 1 and outs[bad[0]][0] == 409 and "quota_exceeded" in outs[bad[0]][1], f"exactly one admission expected: {outs}"
        finally:
            if both: g.ensure_agent(AN, AN)
        real = 0
        for k in ok:
            d, s = watch(t, outs[k][2]); assert d["status"] == "Succeeded", f"{k} {d['status']} {reason(d)}"; real += art_of(d)["sizeBytes"]
        q = settle(t); r["usedDelta"] = q["usedBytes"] - U; r["reservedAfter"] = q["reservedBytes"]; r["artifactBytes"] = real
        assert r["usedDelta"] == real and r["reservedAfter"] == 0, r
        out["rounds"].append(r)
    ov(t, "none")
    return out


def sftp_conn(t):
    """SFTP connection to the shared Fedora SFTP inside the private tenant (configuration copied read-only from the e2e connection of tenant A, secret from lab state)."""
    for c in t.call("GET", "/storage-connections")["items"]:
        if c["name"].startswith("gap-sp-sftp"): return c["id"]
    good = next(c for c in lib.Tenant("a").call("GET", "/storage-connections")["items"] if c["id"] == lib.ST["connections"]["sftp"])["configuration"]
    return t.call("POST", "/storage-connections", {"providerType": "sftp", "name": f"gap-sp-sftp {uuid.uuid4().hex[:4]}", "configuration": good,
                                                   "secret": {"password": lib.ST["fedora"]["sftp_password"]}})["id"]


@scenario("gap/POL-03/two-destinations-copy-counting")
def pol03_two_dest(t, ag):
    ov(t, "none")
    cid = sftp_conn(t)
    job = mkjob(t, ag, "d2", 24, dests=[g.gdest("local"), g.gdest("sftp", conn=cid, primary=False)], name="pol03-d2")
    rid, d = g.run_to_end(t, job, 600); assert d["status"] == "Succeeded", f"seed {d['status']} {reason(d)}"
    a = art_of(d); E = a["sizeBytes"]; full = t.call("GET", f"/backup-artifacts/{a['id']}")
    reps = [(r["storageProvider"], r["sizeBytes"], r["status"]) for r in full["replicas"]]
    assert len(full["replicas"]) == 2, reps
    q = settle(t); U = q["usedBytes"]; out = {"artifactSize": E, "replicas": reps, "U": U}
    # LogicalArtifactBytes: the artifact counts once (2 replicas) -> estimate = E, not 2E
    L = U + E - 1; ov(t, "limited", L); out["below"] = admit_expect(t, job, "quota_exceeded", L)
    L = U + E; ov(t, "limited", L)
    st, txt, rid = post_run(t, job); assert rid, f"U+E refused for 2 destinations (copies counted as 2?): {st} {txt}"
    d, s = watch(t, rid); q2 = settle(t)
    out["equal"] = {"limit": L, "run": d["status"], "reason": reason(d), "peakReserved": peak(s), "usedDelta": q2["usedBytes"] - U, "reservedAfter": q2["reservedBytes"]}
    assert peak(s) == E and q2["reservedBytes"] == 0, out["equal"]
    if d["status"] == "Succeeded": assert q2["usedBytes"] - U == art_of(d)["sizeBytes"], out["equal"]
    else: assert d["attempts"][-1].get("reasonCode") == "QuotaExceeded" and q2["usedBytes"] == U, out["equal"]
    ov(t, "none")
    return out


@scenario("gap/POL-03/state-thresholds-80-90-100")
def pol03_thresholds(t, ag):
    ov(t, "none")
    jb = mkjob(t, ag, "t-big", 40, name="pol03-tb"); js = mkjob(t, ag, "t-small", 1, name="pol03-ts")
    seed(t, jb); Es = seed(t, js)
    U = settle(t)["usedBytes"]; out = {"U": U, "Es": Es, "cases": []}
    def case(label, L, want, allowed):
        ov(t, "limited", L); q = Q(t); p = PQ(t)
        c = {"label": label, "limit": L, "pct": round(U * 100 / L, 4), "state": q["state"], "platformState": p["state"], "newBackupsAllowed": q["newBackupsAllowed"],
             "denial": q["denialCode"], "enforcement": q["enforcementMode"], "warn/crit": (q["warningThreshold"], q["criticalThreshold"])}
        out["cases"].append(c)
        assert q["state"] == p["state"] == want and q["newBackupsAllowed"] == allowed and q["enforcementMode"] == "BlockNewBackups", c
    w = U * 100 // 80; c = U * 100 // 90
    case("used/limit>=80% (limit=floor(U*100/80))", w, "Warning", True)
    case("just below 80% (limit+1)", w + 1, "Normal", True)
    case("used/limit>=90% (limit=floor(U*100/90))", c, "Critical", True)
    case("just below 90% (limit+1)", c + 1, "Warning", True)
    case("limit=U+1 (99.99..%)", U + 1, "Critical", True)
    case("limit=U (100%)", U, "OverQuota", False)
    # exact-percent cases when U is divisible
    # Warning/Critical do not block admission by themselves: Critical limit admits the small job while headroom >= its estimate
    ov(t, "limited", c); head = c - U
    st, txt, rid = post_run(t, js)
    out["criticalAdmission"] = {"limit": c, "headroom": head, "estimate": Es, "http": st}
    if head >= Es:
        assert rid, f"state Critical blocked a run that fits: {st} {txt}"
        d, s = watch(t, rid); assert d["status"] == "Succeeded", reason(d); settle(t)
        out["criticalAdmission"]["run"] = d["status"]
    else:
        assert not rid and st == 409
    ov(t, "none")
    return out


# ------------------------------------------------------------------ POL-04
def store_files():
    """Every file under the private agent's local store and staging directory: path -> size."""
    out = {}
    for d in ("local-store", "staging"):
        for f in (g.agent_root(AN) / d).rglob("*"):
            if f.is_file(): out[f"{d}/{f.relative_to(g.agent_root(AN) / d)}"] = f.stat().st_size
    return out


def new_files(before): a = store_files(); return {k: v for k, v in a.items() if k not in before}


def run_limited(t, job, limit, interval=0.1):
    ov(t, "limited", limit); st, txt, rid = post_run(t, job)
    if not rid: return st, txt, None, None
    d, s = watch(t, rid, interval); return st, txt, d, s


@scenario("gap/POL-04/real-size-grows-hold-fits")
def pol04_grow_fits(t, ag):
    ov(t, "none"); job = mkjob(t, ag, "g1", 2, name="pol04-g1"); E = seed(t, job)
    g.put_file(AN, "g1/f.bin", 6 * MB)  # real artifact ~6 MiB > estimate ~2 MiB
    U = settle(t)["usedBytes"]; L = U + 10 * MB
    st, txt, d, s = run_limited(t, job, L); assert d, f"admission refused: {st} {txt}"
    q = settle(t); A = art_of(d)
    out = {"estimate": E, "U": U, "limit": L, "run": d["status"], "reason": reason(d), "peakReserved": peak(s), "reservedAfter": q["reservedBytes"], "artifactSize": A and A["sizeBytes"],
           "usedDelta": q["usedBytes"] - U, "reservedSeries": sorted({x[2] for x in s})}
    assert d["status"] == "Succeeded" and q["usedBytes"] - U == A["sizeBytes"] and q["reservedBytes"] == 0, out
    assert peak(s) >= A["sizeBytes"], f"hold did not grow to the staged size: {out}"
    ov(t, "none"); return out


@scenario("gap/POL-04/real-size-exceeds-limit-fails-late")
def pol04_exceeds(t, ag):
    ov(t, "none"); job = mkjob(t, ag, "g2", 2, name="pol04-g2"); E = seed(t, job)
    g.put_file(AN, "g2/f.bin", 40 * MB)
    U = settle(t)["usedBytes"]; L = U + 10 * MB; before = store_files()
    st, txt, d, s = run_limited(t, job, L); assert d, f"admission refused (estimate {E} fits {L - U}): {st} {txt}"
    q = settle(t); at = d["attempts"][-1]
    out = {"estimate": E, "U": U, "limit": L, "run": d["status"], "reason": reason(d), "reasonText": (at.get("reason") or "")[:160], "attempts": len(d["attempts"]), "peakReserved": peak(s),
           "reservedAfter": q["reservedBytes"], "usedDelta": q["usedBytes"] - U, "artifacts": len(g.arts(d)), "newFilesInStore": new_files(before)}
    assert d["status"] == "Failed" and at.get("reasonCode") == "QuotaExceeded", out
    assert q["reservedBytes"] == 0 and q["usedBytes"] == U and not out["newFilesInStore"] and not out["artifacts"], out
    # raise the limit: the same job succeeds
    L2 = U + 60 * MB; st, txt, d2, s2 = run_limited(t, job, L2); assert d2, f"after raising the limit: {st} {txt}"
    q2 = settle(t); out["afterRaise"] = {"limit": L2, "run": d2["status"], "peakReserved": peak(s2), "usedDelta": q2["usedBytes"] - U, "reservedAfter": q2["reservedBytes"]}
    assert d2["status"] == "Succeeded" and q2["usedBytes"] - U == art_of(d2)["sizeBytes"] and q2["reservedBytes"] == 0, out
    ov(t, "none"); return out


@scenario("gap/POL-04/equal-limit-plus-few-bytes")
def pol04_equal_bytes(t, ag):
    """Limit exactly used+estimate (admitted by the <= rule) while the real artifact is a few hundred bytes larger (one extra tiny file)."""
    ov(t, "none"); job = mkjob(t, ag, "g3", 8, name="pol04-g3"); E = seed(t, job)
    g.put_file(AN, "g3/tiny.bin", 100)
    U = settle(t)["usedBytes"]; L = U + E; before = store_files()
    st, txt, d, s = run_limited(t, job, L); assert d, f"equal limit refused: {st} {txt}"
    q = settle(t); at = d["attempts"][-1]
    out = {"estimate": E, "U": U, "limit": L, "run": d["status"], "reason": reason(d), "reservedAfter": q["reservedBytes"], "usedDelta": q["usedBytes"] - U,
           "artifactSize": art_of(d) and art_of(d)["sizeBytes"], "newFilesInStore": new_files(before)}
    # either outcome is consistent as long as nothing is half-counted: report which one the code does
    if d["status"] == "Failed": assert at.get("reasonCode") == "QuotaExceeded" and q["usedBytes"] == U and not out["newFilesInStore"], out
    else: assert q["usedBytes"] - U == out["artifactSize"] <= E, out
    ov(t, "none"); return out


def big_job(t, ag, name, mb):
    free = shutil.disk_usage(g.agent_root(AN)).free // MB; assert free > mb * 4, f"only {free} MiB free"
    ov(t, "none"); job = mkjob(t, ag, name, mb, name="pol04-" + name); E = seed(t, job); return job, E


def midrun(t, job, limit):
    """Starts an admitted run and returns its id once it has a reservation and is in progress (agent working)."""
    ov(t, "limited", limit); st, txt, rid = post_run(t, job); assert rid, f"admission refused: {st} {txt}"
    end = time.time() + 120
    while time.time() < end:
        q = Q(t); d = t.call("GET", f"/backup-runs/{rid}")
        if q["reservedBytes"] > 0 and d["status"] == "Running": time.sleep(1.5); return rid, q
        assert d["status"] not in TERMINAL, f"run ended before mid-flight: {d['status']}"
        time.sleep(0.3)
    raise TimeoutError("run never reached Running with a reservation")


@scenario("gap/POL-04/cancel-midflight-releases-reservation")
def pol04_cancel(t, ag):
    job, E = big_job(t, ag, "cx", 250)
    U = settle(t)["usedBytes"]; L = U + E + 20 * MB; before = store_files()
    rid, q = midrun(t, job, L); mid = {"reserved": q["reservedBytes"], "used": q["usedBytes"]}
    r = t.raw("POST", f"/backup-runs/{rid}/cancel", {}); assert r.status_code < 300, f"cancel {r.status_code} {r.text[:200]}"
    d = t.wait(f"/backup-runs/{rid}", timeout=300, stop_on=TERMINAL); t_end = time.time()
    q2 = settle(t); time.sleep(5); q3 = Q(t)
    out = {"estimate": E, "U": U, "limit": L, "mid": mid, "cancelHttp": r.status_code, "run": d["status"], "reservedAfter": q2["reservedBytes"], "reservedAfter+5s": q3["reservedBytes"],
           "usedDelta": q3["usedBytes"] - U, "artifacts": len(g.arts(d)), "newFilesInStore": new_files(before)}
    assert d["status"] == "Cancelled" and q3["reservedBytes"] == 0 and q3["usedBytes"] == U and not out["newFilesInStore"], out
    ov(t, "none"); return out


@scenario("gap/POL-04/agent-stopped-midrun-reservation-released")
def pol04_agent_stop(t, ag):
    job, E = big_job(t, ag, "ak", 250)
    U = settle(t)["usedBytes"]; L = U + E + 20 * MB; before = store_files()
    rid, q = midrun(t, job, L); mid = {"reserved": q["reservedBytes"], "used": q["usedBytes"]}
    t0 = time.time(); rc, o = g.stop_agent(AN); assert rc == 0, o
    timeline = []; d = None
    try:
        while time.time() - t0 < 900:
            q = Q(t); d = t.call("GET", f"/backup-runs/{rid}"); timeline.append((round(time.time() - t0), d["status"], q["reservedBytes"], q["usedBytes"]))
            if d["status"] in TERMINAL and q["reservedBytes"] == 0: break
            time.sleep(10)
    finally:
        g.ensure_agent(AN, AN)  # restart own agent (stopped transient unit is recreated)
    d = t.call("GET", f"/backup-runs/{rid}"); q2 = settle(t, 300)
    comp = [timeline[0]] + [b for a, b in zip(timeline, timeline[1:]) if b[1:3] != a[1:3]]
    out = {"estimate": E, "U": U, "limit": L, "mid": mid, "run": d["status"], "reasons": [f"{a['status']}/{a.get('reasonCode')}" for a in d["attempts"]], "timeline(s,status,reserved,used)": comp,
           "secondsToTerminal": next((x[0] for x in timeline if x[1] in TERMINAL), None), "secondsToReleased": next((x[0] for x in timeline if x[1] in TERMINAL and x[2] == 0), None),
           "reservedAfter": q2["reservedBytes"], "usedDelta": q2["usedBytes"] - U, "newFilesInStore": new_files(before)}
    assert d["status"] in TERMINAL and q2["reservedBytes"] == 0 and q2["usedBytes"] == U, out
    ov(t, "none"); return out


def platform(t, method, path, body=None):
    r = ADM.request(method, tb(t) + path, json=body)
    assert r.ok, f"{method} {path}: {r.status_code} {r.text[:300]}"
    return jr(r)


def plan(name, limit):
    r = ADM.post(api.BASE + "/api/platform/plans", json={"name": f"gap-sp-plan-quota-{name}-{uuid.uuid4().hex[:6]}", "storageLimitBytes": limit, "unlimited": False, "enabled": True})
    assert r.ok, f"plan create {r.status_code} {r.text[:200]}"
    p = r.json(); PLANS[p["planId"]] = p["name"]; return p


def assign(t, pid, **extra):
    return platform(t, "PUT", "/subscription", {"planId": pid, **extra})


def views(t):
    q = Q(t); p = PQ(t); s = sub(t)
    keys = ("limitBytes", "unlimited", "usedBytes", "reservedBytes", "state", "newBackupsAllowed", "denialCode", "subscriptionStatus", "plan")
    assert all(q[k] == p[k] for k in keys), (q, p)
    assert q["limitBytes"] == s["effectiveStorageLimit"]["bytes"] and q["unlimited"] == s["effectiveStorageLimit"]["unlimited"] and q["plan"]["name"] == s["planName"], (q, s)
    return {k: q[k] for k in keys if k not in ("plan",)} | {"plan": q["plan"]["name"], "source": s["effectiveStorageLimit"]["source"], "override": s["storageLimitOverride"]}


@scenario("gap/POL-05/plan-override-suspend-resume-expiry")
def pol05_plans(t, ag):
    from datetime import datetime, timedelta, timezone
    import hashlib
    ov(t, "none")
    j = mkjob(t, ag, "plans", 2, name="pol05-plans")
    E = seed(t, j); U = settle(t)["usedBytes"]
    sm = plan("small", U + E - 1); big = plan("big", U + E + 10 * MB)
    out = {"U": U, "estimate": E, "smallLimit": sm["storageLimitBytes"], "bigLimit": big["storageLimitBytes"], "views": []}
    def view(label, limit, status="Active", allowed=True):
        v = views(t); v["step"] = label; out["views"].append(v)
        assert v["limitBytes"] == limit and v["subscriptionStatus"] == status and v["newBackupsAllowed"] == allowed, v
        return v
    try:
        view("unlimited", None)
        assign(t, sm["planId"]); view("small", U + E - 1)
        out["smallDenied"] = admit_expect(t, j, "quota_exceeded", U + E - 1)
        ov(t, "unlimited"); v = view("unlimited override of small", None); assert v["source"] == "override"
        ov(t, "limited", U + E + 5 * MB); v = view("limited override of small", U + E + 5 * MB); assert v["source"] == "override"
        ov(t, "none"); v = view("none restores small", U + E - 1); assert v["source"] == "plan"
        assign(t, big["planId"]); view("big", U + E + 10 * MB)
        st, txt, rid = post_run(t, j); assert rid, (st, txt)
        d, samples = watch(t, rid); assert d["status"] == "Succeeded", reason(d); settle(t)
        a = art_of(d); out["bigRun"] = {"status": d["status"], "size": a["sizeBytes"], "peakReserved": peak(samples)}
        platform(t, "POST", "/subscription/suspend")
        v = view("suspended", U + E + 10 * MB, "Suspended", False); assert v["denialCode"] == "subscription_required"
        out["suspendedAdmission"] = admit_expect(t, j, "subscription_required", v["limitBytes"])
        av = t.call("GET", f"/backup-artifacts/{a['id']}/download-availability")
        dl = t.raw("POST", f"/backup-artifacts/{a['id']}/downloads", {})
        assert dl.status_code == 200, f"download session {dl.status_code} {dl.text[:200]}"
        sig = t.s.get(api.BASE + dl.json()["downloadUrl"]); out["suspendedDownload"] = {"available": av, "sessionHttp": dl.status_code, "direct": dl.json()["direct"], "signallingHttp": sig.status_code}
        assert sig.status_code == 200 and "sdp" in sig.text
        rel = "quota-restore-" + uuid.uuid4().hex[:6]
        req = {"agentId": ag["agentId"], "target": {"host": "localhost", "port": None, "integratedSecurity": True, "databaseCredentialReference": None, "encrypt": False, "trustServerCertificate": False},
               "databaseName": "gapfiles", "mode": "NewDatabase", "options": {"files": {"relativeDirectory": rel}}}
        res = t.call("POST", f"/backup-artifacts/{a['id']}/restore", req)
        rd = t.wait(f"/restores/{res.get('id', res.get('restoreId'))}", timeout=300)
        root = g.agent_root(AN) / "files"; original = root / "plans/f.bin"; restored = root / rel / "plans/f.bin"
        digest = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
        assert rd["status"] == "Succeeded" and restored.exists() and digest(original) == digest(restored), f"restore {rd['status']} exists={restored.exists()}"
        out["suspendedRestore"] = {"status": rd["status"], "sha256": digest(restored), "bytes": restored.stat().st_size}
        shutil.rmtree(root / rel)
        platform(t, "POST", "/subscription/resume"); view("resumed", U + E + 10 * MB)
        assign(t, lib.ST["plans"]["unlimited"]); view("unlimited again", None)
        ov(t, "limited", U + E + 12 * MB); v = view("limited override of unlimited", U + E + 12 * MB); assert v["source"] == "override"
        ov(t, "none"); view("unlimited none", None)
        expiry = (datetime.now(timezone.utc) + timedelta(seconds=8)).isoformat()
        assign(t, sm["planId"], endsAt=expiry)
        time.sleep(10); v = view("expired", U + E - 1, "Expired", False); assert v["denialCode"] == "subscription_required"
        out["expiredAdmission"] = admit_expect(t, j, "subscription_required", v["limitBytes"])
        out["expiredArtifact"] = {"state": t.call("GET", f"/backup-artifacts/{a['id']}")["state"], "availability": t.call("GET", f"/backup-artifacts/{a['id']}/download-availability")["available"]}
        assert out["expiredArtifact"]["state"] == "Available"
    finally:
        assign(t, lib.ST["plans"]["unlimited"]); ov(t, "none")
        for pid in list(PLANS):
            r = ADM.put(api.BASE + f"/api/platform/plans/{pid}/enabled", json={"enabled": False})
            assert r.ok, f"disable plan: {r.status_code} {r.text[:200]}"
    return out


def all_artifacts(t):
    page = 1; items = []
    while True:
        p = t.call("GET", f"/backup-artifacts?page={page}&pageSize=100"); items += p["items"]
        if len(items) >= p["totalCount"]: return items
        page += 1


def counted(t):
    items = all_artifacts(t)
    return sum(a["sizeBytes"] for a in items if a["state"] == "Available" and any(r["status"] == "Available" for r in a["replicas"])), items


@scenario("gap/POL-06/reconcile-rebuild-idempotence-unknown")
def pol06_idempotent(t, ag):
    ov(t, "none"); settle(t)
    expected, items = counted(t); before = PU(t); steps = []
    for action in ("reconcile", "rebuild", "reconcile", "rebuild", "reconcile", "rebuild"):
        r = platform(t, "POST", f"/storage-usage/{action}"); u = PU(t)
        steps.append({"action": action, "response": r, "revision": u["revision"]})
        assert r["usedBytes"] == expected and r["reservedBytes"] == 0 and u["usedBytes"] == expected, (expected, r, u)
    unknown = ADM.post(api.BASE + f"/api/platform/tenants/{uuid.uuid4()}/storage-usage/reconcile")
    assert unknown.status_code == 404, (unknown.status_code, unknown.text[:200])
    listing = ADM.get(api.BASE + "/api/platform/storage-usage", params={"search": "gap-sp-quota", "pageSize": 100}).json()
    assert any(x["tenantId"] == t.id for x in listing["items"])
    return {"expectedLogicalBytes": expected, "availableArtifacts": sum(a["state"] == "Available" for a in items), "beforeRevision": before["revision"], "steps": steps, "unknownHttp": unknown.status_code}


@scenario("gap/POL-06/reconcile-while-running-and-delete")
def pol06_concurrent(t, ag):
    ov(t, "none"); j = mkjob(t, ag, "reconcile", 90, name="pol06-running"); E = seed(t, j)
    U = settle(t)["usedBytes"]; ov(t, "limited", U + E + 10 * MB)
    before = {a["id"]: a["state"] for a in all_artifacts(t)}
    st, txt, rid = post_run(t, j); assert rid, (st, txt)
    observed = []; done = threading.Event()
    def recalc():
        while not done.is_set():
            for action in ("reconcile", "rebuild"):
                try:
                    r = platform(t, "POST", f"/storage-usage/{action}"); observed.append((action, r["usedBytes"], r["reservedBytes"]))
                except Exception as e: observed.append(("error", str(e)))
    th = threading.Thread(target=recalc); th.start()
    try: d, s = watch(t, rid)
    finally: done.set(); th.join()
    assert d["status"] == "Succeeded", reason(d)
    q = settle(t); expected, items = counted(t); after = {a["id"]: a["state"] for a in items}
    assert all(after.get(k) == v for k, v in before.items()) and q["usedBytes"] == expected, (q, expected)
    assert observed and not any(x[0] == "error" for x in observed), observed
    target = art_of(d)["id"]; before_del = after
    bar = threading.Barrier(2); result = {}
    def delete(): bar.wait(); r = t.raw("DELETE", f"/backup-artifacts/{target}"); result["delete"] = (r.status_code, r.text[:200])
    def rebuild(): bar.wait(); result["rebuild"] = platform(t, "POST", "/storage-usage/rebuild")
    a = threading.Thread(target=delete); b = threading.Thread(target=rebuild); a.start(); b.start(); a.join(); b.join()
    assert result["delete"][0] in (200, 202, 204), result
    end = time.time() + 400
    while time.time() < end:
        platform(t, "POST", "/storage-usage/reconcile"); q = Q(t); expected, items = counted(t)
        if q["usedBytes"] == expected: break
        time.sleep(5)
    after_del = {a["id"]: a["state"] for a in items}
    assert all(after_del.get(k) == v for k, v in before_del.items() if k != target), (before_del, after_del)
    assert q["usedBytes"] == expected and q["reservedBytes"] == 0, (q, expected)
    ov(t, "none")
    return {"backup": d["status"], "peakReserved": peak(s), "operationsDuringRun": len(observed), "samples": observed[:4], "beforeCount": len(before), "afterCount": len(after),
            "deleteHttp": result["delete"][0], "targetState": after_del.get(target, "absent"), "rebuild": result["rebuild"], "settledUsed": q["usedBytes"], "independentSum": expected}


def suspended_download(t, ag, provider):
    import hashlib
    ov(t, "none")
    if provider == "sftp": cid = sftp_conn(t)
    else:
        good = next(c for c in lib.Tenant("a").call("GET", "/storage-connections")["items"] if c["id"] == lib.ST["connections"]["s3"])["configuration"]
        cid = t.call("POST", "/storage-connections", {"providerType": "s3", "name": f"gap-sp-s3-{uuid.uuid4().hex[:4]}", "configuration": good,
                                                      "secret": {"accessKey": api.env["E2E_MINIO_USER"], "secretKey": api.env["E2E_MINIO_PASSWORD"]}})["id"]
    job = mkjob(t, ag, f"{provider}-download", 2, dests=[g.gdest(provider, conn=cid)], name=f"pol05-download-{provider}")
    rid, d = g.run_to_end(t, job, 300)
    assert d["status"] == "Succeeded", reason(d)
    a = t.call("GET", f"/backup-artifacts/{art_of(d)['id']}")
    platform(t, "POST", "/subscription/suspend")
    try:
        p = t.call("POST", f"/backup-artifacts/{a['id']}/downloads", {"replicaId": a["replicas"][0]["id"]})
        if p["direct"]:
            # An agent-held replica (private SFTP) is not streamed by the API: GET .../content is only the WebRTC signalling endpoint, so the bytes are taken by a real browser.
            import gap_art
            gap_art.T = t; owner = g._load()[AN]
            got = gap_art.direct(a, {"login": owner["login"], "password": owner["password"]})
            return {"direct": True, "bytes": got["bytes"], "sha256": got["sha256"], "expected": a["artifactChecksum"], "subscription": Q(t)["subscriptionStatus"]}
        r = t.s.get(api.BASE + p["downloadUrl"], stream=True)
        h = hashlib.sha256(); n = 0
        for chunk in r.iter_content(1048576): h.update(chunk); n += len(chunk)
        assert r.status_code == 200 and n == a["sizeBytes"] and h.hexdigest() == a["artifactChecksum"] == p["checksum"], (r.status_code, n, a["sizeBytes"], h.hexdigest())
        return {"status": r.status_code, "bytes": n, "direct": p["direct"], "sha256": h.hexdigest(), "expected": a["artifactChecksum"], "subscription": Q(t)["subscriptionStatus"]}
    finally:
        platform(t, "POST", "/subscription/resume")


@scenario("gap/POL-05/suspended-s3-download-bytes")
def pol05_download(t, ag): return suspended_download(t, ag, "s3")


@scenario("gap/POL-05/suspended-sftp-download-bytes")
def pol05_download_sftp(t, ag): return suspended_download(t, ag, "sftp")


@scenario("gap/POL-03/exact-80-and-90-percent")
def pol03_exact_thresholds(t, ag):
    ov(t, "none")
    job = mkjob(t, ag, "precise", 0, size=1024, name="pol03-precise")
    values = []
    for i in range(144):
        g.put_file(AN, "precise/f.bin", 1024 + i)
        seed(t, job); U = settle(t)["usedBytes"]; values.append(U % 36)
        if U % 36 == 0: break
    assert U % 36 == 0, {"attempts": len(values), "residues": values}
    cases = []
    for label, limit, expected in (
        ("79.99%", (U * 10000 + 7998) // 7999, "Normal"),
        ("80%", U * 5 // 4, "Warning"),
        ("89.99%", (U * 10000 + 8998) // 8999, "Warning"),
        ("90%", U * 10 // 9, "Critical"),
    ):
        ov(t, "limited", limit); q = Q(t)
        cases.append({"label": label, "used": U, "limit": limit, "state": q["state"], "enforcement": q["enforcementMode"], "numerator": U * 10000})
        assert q["state"] == expected and q["enforcementMode"] == "BlockNewBackups", cases[-1]
    ov(t, "none")
    return {"attempts": len(values), "cases": cases}


@scenario("gap/POL-06/cleanup-private-tenant")
def pol06_cleanup(t, ag):
    ov(t, "none")
    ss = sub(t)
    if ss["planId"] != lib.ST["plans"]["unlimited"]:
        assign(t, lib.ST["plans"]["unlimited"])
    jobs = t.call("GET", "/backup-jobs?pageSize=100")
    disabled = []
    for j in jobs:
        assert j["name"].startswith("gap-sp-"), j["name"]
        r = t.raw("POST", f"/backup-jobs/{j['id']}/disable", {})
        assert r.status_code in (200, 204), (j["id"], r.status_code, r.text[:150])
        disabled.append(j["id"])
    arts = all_artifacts(t); deletes = []
    for a in arts:
        if a["state"] == "Available":
            r = t.raw("DELETE", f"/backup-artifacts/{a['id']}")
            deletes.append((a["id"], r.status_code))
            assert r.status_code in (200, 202, 204), (a["id"], r.status_code, r.text[:150])
    end = time.time() + 400
    while time.time() < end:
        q = Q(t); remains = [a for a in all_artifacts(t) if a["state"] == "Available"]
        if q["usedBytes"] == 0 and q["reservedBytes"] == 0 and not remains: break
        time.sleep(5)
    assert q["usedBytes"] == q["reservedBytes"] == 0 and not remains, (q, len(remains))
    connections = []
    for c in t.call("GET", "/storage-connections")["items"]:
        if c["name"].startswith(("gap-sp-sftp", "gap-sp-s3")):
            r = t.raw("DELETE", f"/storage-connections/{c['id']}")
            connections.append({"id": c["id"], "http": r.status_code, "response": r.text[:200]})
    partials = {k: v for k, v in store_files().items() if ".partial" in k}
    return {"jobsDisabled": len(disabled), "deleteRequested": len(deletes), "finalQuota": {"used": q["usedBytes"], "reserved": q["reservedBytes"]}, "connections": connections, "remainingPartialFiles": partials, "subscription": {"plan": sub(t)["planName"], "override": sub(t)["storageLimitOverride"]}}


# ------------------------------------------------------------------ driver
def setup():
    t = g.ensure_tenant(AN); ag = g.ensure_agent(AN, AN)
    return t, ag


if __name__ == "__main__":
    t, ag = setup(); fns = [v for k, v in list(globals().items()) if k.startswith("pol0") and hasattr(v, "scenario")]
    for sel in sys.argv[1:] or ["pol03"]:  # prefix of the function name (pol03, pol04_cancel, ...)
        for fn in [f for f in fns if f.__name__.startswith(sel)]:
            print("==", fn.scenario, flush=True); fn(t, ag)
