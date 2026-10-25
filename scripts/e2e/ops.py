"""Schedules, retention, artifact deletion, pause/resume/cancel and bandwidth limits against the real stack."""
import sys, time, json, uuid, lib, flows
from lib import ST, scenario

def small_file_job(t, agent, name, dest=None, **extra):
    cfg = flows.file_job_config("e2efiles", dest or [flows.local_dest()], paths=["docs"], **extra)
    return t.call("POST", "/backup-jobs", {"name": f"{name} {uuid.uuid4().hex[:6]}", "agentId": agent["agentId"], "overlapPolicy": "Skip", "configuration": cfg})

def big_file_job(t, agent, name, dest, **extra):
    cfg = flows.file_job_config("e2efiles", dest, paths=["bin"] if agent["platform"] == "Linux" else ["."], **extra)
    return t.call("POST", "/backup-jobs", {"name": f"{name} {uuid.uuid4().hex[:6]}", "agentId": agent["agentId"], "overlapPolicy": "Skip", "configuration": cfg})

def start(t, job):
    r = t.call("POST", f"/backup-jobs/{job['id']}/runs", {}); return r.get("id", r.get("run", {}).get("id"))

def artifacts_of(run):
    return [a for at in run["attempts"] for a in at["artifacts"]]

def lc(t, rid, timeout=900, stop_on=lib.TERMINAL):
    return t.wait(f"/backup-runs/{rid}", timeout=timeout, stop_on=stop_on)

@scenario("ops/schedule-cron-fires-and-skip-overlap")
def schedule(t, fa):
    job = small_file_job(t, fa, "sched")
    try:
        t.call("PUT", f"/backup-jobs/{job['id']}/schedule", {"enabled": True, "cronExpression": "* * * * *", "timeZone": "UTC", "overlapPolicy": "Skip"})
        end, runs = time.time() + 200, []
        while time.time() < end and len(runs) < 2:
            time.sleep(10); runs = [r for r in t.call("GET", f"/backup-jobs/{job['id']}/runs") if True] if isinstance(t.call("GET", f"/backup-jobs/{job['id']}/runs"), list) else t.call("GET", f"/backup-jobs/{job['id']}/runs")["items"]
        assert len(runs) >= 2, f"expected >=2 scheduled runs within 200s, got {len(runs)}"
        occ = t.call("GET", f"/backup-jobs/{job['id']}/schedule/occurrences")
        occ = occ["items"] if isinstance(occ, dict) else occ
        assert occ and all(o["Outcome" if "Outcome" in o else "outcome"] for o in occ)
        return {"runs": len(runs), "occurrences": len(occ)}
    finally:
        t.call("DELETE", f"/backup-jobs/{job['id']}/schedule", expect=(200, 204, 404)); t.call("POST", f"/backup-jobs/{job['id']}/disable", expect=(200, 204, 404))

@scenario("ops/pause-resume-and-cancel")
def pause_resume_cancel(t, fa):
    dests = [{"storageProvider": "sftp", "storageConnectionId": ST["connections"]["sftp"], "storageExecutionLocation": "Backend", "isPrimary": True}]
    job = big_file_job(t, fa, "pause", dests)
    t.call("PUT", f"/upload-bandwidth/jobs/{job['id']}", {"bytesPerSecond": 2 * 1024 * 1024})
    rid = start(t, job)
    for _ in range(60):
        d = t.call("GET", f"/backup-runs/{rid}")
        if d["status"] == "Running" and d["attempts"][-1].get("progressStage") in ("Uploading", "Transferring"): break
        time.sleep(1)
    t.call("POST", f"/backup-runs/{rid}/pause", {}); time.sleep(8)
    paused = t.call("GET", f"/backup-runs/{rid}")
    assert paused["status"] == "Paused", f"expected Paused, got {paused['status']}"
    pr = paused.get("pauseReason")
    t.call("POST", f"/backup-runs/{rid}/resume", {})
    done = lc(t, rid, 600)
    assert done["status"] == "Succeeded", f"after resume: {done['status']} {json.dumps(done)[:500]}"
    # cancel must be final, never a resumable pause
    rid2 = start(t, job); time.sleep(6)
    t.call("POST", f"/backup-runs/{rid2}/cancel", {})
    c = lc(t, rid2, 300, stop_on=lib.TERMINAL + ("Paused",))
    assert c["status"] == "Cancelled", f"cancel ended as {c['status']}"
    r = t.raw("POST", f"/backup-runs/{rid2}/resume", {})
    assert r.status_code == 409, f"resume after cancel returned {r.status_code}"
    return {"pauseReason": pr, "resumed": done["status"], "cancel": c["status"]}

@scenario("ops/bandwidth-limit-enforced")
def bandwidth(t, fa):
    dest = [{"storageProvider": "sftp", "storageConnectionId": ST["connections"]["sftp"], "storageExecutionLocation": "Backend", "isPrimary": True}]
    job = big_file_job(t, fa, "bw", dest)
    limit = 1024 * 1024
    t.call("PUT", f"/upload-bandwidth/jobs/{job['id']}", {"bytesPerSecond": limit})
    t0 = time.time(); rid = start(t, job); d = lc(t, rid, 900); took = time.time() - t0
    assert d["status"] == "Succeeded", d["status"]
    size = artifacts_of(d)[0]["sizeBytes"]; rate = size / took
    t.call("PUT", f"/upload-bandwidth/jobs/{job['id']}", {"bytesPerSecond": None})
    assert rate < limit * 1.6, f"observed {rate/1024:.0f} KiB/s for a {limit/1024:.0f} KiB/s limit"
    return {"sizeBytes": size, "seconds": round(took, 1), "kibPerSec": round(rate / 1024)}

@scenario("ops/retention-platform-minimum-enforced")
def retention_minimum(t, fa):
    job = small_file_job(t, fa, "retmin")
    r = t.raw("PUT", f"/backup-jobs/{job['id']}/retention", {"enabled": True, "keepForSeconds": 60, "deleteGracePeriodSeconds": 10})
    assert r.status_code == 400, f"a 60 s keep period was accepted ({r.status_code}) although the default minimum is 1 h"
    ok = t.raw("PUT", f"/backup-jobs/{job['id']}/retention", {"enabled": True, "keepForSeconds": 3600, "deleteGracePeriodSeconds": 3600})
    assert ok.status_code == 200, f"1 h/1 h rejected: {ok.status_code} {ok.text[:200]}"
    return {"rejected": 400}

@scenario("ops/retention-expires-and-deletes")
def retention(t, fa):
    job = small_file_job(t, fa, "ret")
    t.call("PUT", f"/backup-jobs/{job['id']}/retention", {"enabled": True, "keepForSeconds": 60, "deleteGracePeriodSeconds": 10})
    d = lc(t, start(t, job), 200); art = artifacts_of(d)[0]
    end = time.time() + 420; state = None
    while time.time() < end:
        a = t.call("GET", f"/backup-artifacts/{art['id']}"); state = a.get("state") or a.get("status")
        if state in ("Deleted",): break
        time.sleep(15)
    assert state == "Deleted", f"artifact still {state} after retention period"
    return {"artifactId": art["id"], "state": state}

@scenario("ops/artifact-delete-and-cancel-delete")
def delete_cancel(t, fa):
    job = small_file_job(t, fa, "del")
    art = artifacts_of(lc(t, start(t, job), 200))[0]
    t.call("DELETE", f"/backup-artifacts/{art['id']}")
    a = t.call("GET", f"/backup-artifacts/{art['id']}")
    t.call("POST", f"/backup-artifacts/{art['id']}/cancel-delete", {}, expect=(200, 202, 204, 409))
    a2 = t.call("GET", f"/backup-artifacts/{art['id']}")
    return {"afterDelete": a.get("state") or a.get("status"), "afterCancel": a2.get("state") or a2.get("status")}

def main(only=None):
    t = lib.Tenant("a"); fa = t.agents()["fedora-e2e"]
    for f in (schedule, pause_resume_cancel, bandwidth, retention, delete_cancel):
        if only and only not in f.scenario: continue
        f(t, fa)

if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else None)
