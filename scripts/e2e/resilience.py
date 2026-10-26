"""Failure injection and quota scenarios: agent kill, API restart, storage outage, clock skew, concurrency, quota, agent revoke."""
import sys, time, json, subprocess, uuid, lib, flows, ops
from lib import ST, scenario

def sftp_dest(): return [{"storageProvider": "sftp", "storageConnectionId": ST["connections"]["sftp"], "storageExecutionLocation": "Backend", "isPrimary": True}]
def s3_dest(): return [{"storageProvider": "s3", "storageConnectionId": ST["connections"]["s3"], "storageExecutionLocation": "Backend", "isPrimary": True}]

def throttled_big_job(t, agent, dest, bps=2 * 1024 * 1024):
    job = ops.big_file_job(t, agent, "res", dest)
    t.call("PUT", f"/upload-bandwidth/jobs/{job['id']}", {"bytesPerSecond": bps}); return job

def wait_uploading(t, rid, timeout=90):
    end = time.time() + timeout
    while time.time() < end:
        d = t.call("GET", f"/backup-runs/{rid}")
        if d["status"] == "Running" and d["attempts"][-1].get("progressStage") == "Uploading": return d
        if d["status"] in lib.TERMINAL: raise AssertionError(f"run ended before upload phase: {d['status']}")
        time.sleep(1)
    raise TimeoutError("never reached Uploading")

def final(t, rid, timeout=900):
    return t.wait(f"/backup-runs/{rid}", timeout=timeout, stop_on=lib.TERMINAL)

def staging_leftovers():
    return lib.fedora("sudo find /var/lib/backup-agent/staging -type f | head -5; sudo find /tmp -maxdepth 1 -name 'bp-smb-*' | head")[1].strip()

@scenario("res/agent-killed-mid-upload-recovers")
def agent_kill(t):
    fa = t.agents()["fedora-e2e"]; job = throttled_big_job(t, fa, sftp_dest())
    rid = ops.start(t, job); wait_uploading(t, rid)
    lib.fedora("sudo systemctl kill -s KILL backup-agent")
    d = t.wait(f"/backup-runs/{rid}", timeout=600, stop_on=lib.TERMINAL + ("Paused",))
    info = {"afterKill": d["status"], "pauseReason": d.get("pauseReason")}
    resumes = 0
    while d["status"] == "Paused" and resumes < 3:
        t.call("POST", f"/backup-runs/{rid}/resume", {}); resumes += 1
        d = t.wait(f"/backup-runs/{rid}", timeout=300, stop_on=lib.TERMINAL + ("Paused",))
    info.update(resumes=resumes, final=d["status"], attempts=[(a["number"], a["status"], a["reasonCode"]) for a in d["attempts"]])
    assert d["status"] == "Succeeded", info
    mine = [a["id"].replace("-", "") for a in d["attempts"]]
    left = [l for l in staging_leftovers().splitlines() if any(m in l for m in mine)]
    assert not left, f"files of this run left in staging after completion: {left}"
    return info

@scenario("res/api-restart-mid-upload")
def api_restart(t):
    fa = t.agents()["fedora-e2e"]; job = throttled_big_job(t, fa, sftp_dest())
    rid = ops.start(t, job); wait_uploading(t, rid)
    subprocess.run(["docker", "restart", "bp-api"], check=True, capture_output=True)
    time.sleep(5)
    for _ in range(60):
        try: t.call("GET", f"/backup-runs/{rid}"); break
        except Exception: time.sleep(3)
    d = final(t, rid, 900); statuses = [(a["number"], a["status"], a["reasonCode"]) for a in d["attempts"]]
    assert d["status"] == "Succeeded", f"run ended {d['status']}: {statuses}"
    return {"attempts": statuses}

@scenario("res/storage-refusing-pauses-then-resume-completes")
def storage_outage(t):
    fa = t.agents()["fedora-e2e"]; job = throttled_big_job(t, fa, s3_dest(), 4 * 1024 * 1024)
    rid = ops.start(t, job); wait_uploading(t, rid)
    subprocess.run(["docker", "stop", "bpe2e-minio"], check=True, capture_output=True)
    try:
        d = t.wait(f"/backup-runs/{rid}", timeout=300, stop_on=lib.TERMINAL + ("Paused",))
    finally:
        subprocess.run(["docker", "start", "bpe2e-minio"], capture_output=True); time.sleep(8)
    assert d["status"] == "Paused" and d.get("pauseReason") == "PausedByNetworkError", (d["status"], d.get("pauseReason"))
    t.call("POST", f"/backup-runs/{rid}/resume", {})
    d2 = t.wait(f"/backup-runs/{rid}", timeout=400, stop_on=lib.TERMINAL + ("Paused",))
    if d2["status"] == "Paused":  # a first resume can stall (see ops/pause-resume-and-cancel)
        t.call("POST", f"/backup-runs/{rid}/resume", {}); d2 = t.wait(f"/backup-runs/{rid}", timeout=400, stop_on=lib.TERMINAL + ("Paused",))
    assert d2["status"] == "Succeeded", f"after resume: {d2['status']}"
    return {"pauseReason": d["pauseReason"], "afterResume": d2["status"]}

@scenario("res/storage-hanging-detected-within-5min")
def storage_hang(t):
    fa = t.agents()["fedora-e2e"]; job = throttled_big_job(t, fa, s3_dest(), 4 * 1024 * 1024)
    rid = ops.start(t, job); wait_uploading(t, rid)
    subprocess.run(["docker", "pause", "bpe2e-minio"], check=True, capture_output=True)
    try:
        d = t.wait(f"/backup-runs/{rid}", timeout=300, stop_on=lib.TERMINAL + ("Paused",))
    except TimeoutError:
        d = t.call("GET", f"/backup-runs/{rid}")
    finally:
        subprocess.run(["docker", "unpause", "bpe2e-minio"], capture_output=True)
    state = d["status"]
    for _ in range(3):  # leave nothing half-done behind
        d = t.wait(f"/backup-runs/{rid}", timeout=300, stop_on=lib.TERMINAL + ("Paused",))
        if d["status"] == "Paused": t.call("POST", f"/backup-runs/{rid}/resume", {})
        else: break
    assert state in ("Paused",) or state in lib.TERMINAL, f"transfer to a hanging storage was still '{state}' after 300 s (no stall detection)"
    return {"stateAfter300s": state}

@scenario("res/agent-service-restart-keeps-identity")
def agent_restart(t):
    before = t.agents()["fedora-e2e"]["agentId"]
    lib.fedora("sudo systemctl restart backup-agent"); time.sleep(20)
    a = t.agents()["fedora-e2e"]
    assert a["agentId"] == before and a["connectivity"] == "online", (a["agentId"], a["connectivity"])
    return {"agentId": before}

@scenario("res/api-restart-agents-reconnect")
def api_reconnect(t):
    subprocess.run(["docker", "restart", "bp-api"], check=True, capture_output=True); time.sleep(60)
    ag = {}
    for _ in range(30):
        try: ag = {k: v for k, v in t.agents().items() if k in ("fedora-e2e", "win11-e2e")}
        except Exception: pass
        if ag and all(a["connectivity"] == "online" for a in ag.values()): break
        time.sleep(5)
    assert ag and all(a["connectivity"] == "online" for a in ag.values()), {k: v["connectivity"] for k, v in ag.items()}
    return {"agents": list(ag)}

@scenario("res/concurrent-runs-both-agents")
def concurrent(t):
    ag = t.agents(); rids = []
    for name in ("fedora-e2e", "win11-e2e"):
        for i in range(3):
            job = ops.small_file_job(t, ag[name], f"conc-{name[:3]}")
            rids.append(ops.start(t, job))
    res = [final(t, r, 900)["status"] for r in rids]
    assert all(s == "Succeeded" for s in res), res
    return {"runs": len(res)}

@scenario("res/clock-skew-agent-ahead-15min")
def clock_skew(t):
    fa = t.agents()["fedora-e2e"]; job = ops.small_file_job(t, fa, "skew")
    lib.fedora("sudo timedatectl set-ntp false; sudo date -s '+15 minutes' >/dev/null; sudo systemctl restart backup-agent")
    try:
        time.sleep(25); a = t.agents()["fedora-e2e"]
        d = final(t, ops.start(t, job), 300); st = d["status"]
        info = {"connectivity": a["connectivity"], "run": st, "reason": d["attempts"][-1]["reason"]}
    finally:
        lib.fedora("sudo date -s '-15 minutes' >/dev/null; sudo timedatectl set-ntp true; sudo systemctl restart backup-agent"); time.sleep(20)
    return info

def main(only=None):
    t = lib.Tenant("a")
    for f in (agent_kill, storage_outage, storage_hang, api_reconnect):
        if only and only not in f.scenario: continue
        f(t)

if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else None)
