"""Gap tests POL-04 (#55): orphan .partial cleanup on the agent's local volumes, default and a second volume.
Private tenant gap-sp-part with its own agent (default local store + volume `vol2`). Scenarios gap/POL-04/partial-*. No secrets are printed."""
import sys, time, os, shutil, subprocess
import gap_sp_common as g, api, lib
from lib import scenario, TERMINAL

KEY = "part"; MB = g.MB
ROOT = lambda: g.agent_root(KEY)


def partials():
    out = {}
    for d in ("local-store", "vol2"):
        for f in (ROOT() / d).rglob(".*.partial"): out[f"{d}/{f.relative_to(ROOT() / d)}"] = f.stat().st_size
    return out


def setup():
    t = g.ensure_tenant(KEY, plan="unlimited")
    (ROOT() / "vol2").mkdir(parents=True, exist_ok=True)
    ag = g.ensure_agent(KEY, KEY, extra_env={"Agent__Backup__LocalStorage__Volumes__vol2": str(ROOT() / "vol2")})
    return t, ag


def volume_dest(t, target):
    dest = g.gdest("local")
    if target != "default":
        found = [c for c in t.call("GET", "/storage-connections")["items"] if c["name"] == f"gap-sp-part-{target}"]
        conn = found[0] if found else t.call("POST", "/storage-connections", {"providerType": "local", "name": f"gap-sp-part-{target}", "configuration": {"volume": target}})
        dest["storageConnectionId"] = conn["id"]
    return dest


def mk(t, ag, sub, mb, target):
    g.put_file(KEY, f"{sub}/f.bin", mb * MB)
    return g.new_job(t, ag, sub, g.gcfg([volume_dest(t, target)], paths=[sub]), retryPolicy={"maxAttempts": 1})


def midflight(t, rid, before, pause_after=1.5):
    """Waits until the run is Running and a new partial exists, then lets it grow a little."""
    end = time.time() + 120
    while time.time() < end:
        d = t.call("GET", f"/backup-runs/{rid}"); assert d["status"] not in TERMINAL, f"ended early: {d['status']}"
        new = {k: v for k, v in partials().items() if k not in before}
        if d["status"] == "Running" and new: time.sleep(pause_after); return {k: v for k, v in partials().items() if k not in before}
        time.sleep(0.2)
    raise TimeoutError("no partial appeared")


def run_cancel_beside_paused(target, store):
    t, ag = setup(); before = partials()
    jp = mk(t, ag, f"keep-{target}", 300, target); rp = g.start_run(t, jp)
    kept = midflight(t, rp, before, 0.3); assert t.raw("POST", f"/backup-runs/{rp}/pause", {}).status_code < 300
    d = t.wait(f"/backup-runs/{rp}", timeout=120); assert d["status"] == "Paused", d["status"]
    time.sleep(3); kept_size = {k: partials()[k] for k in kept}
    jc = mk(t, ag, f"cancel-{target}", 300, target); rc = g.start_run(t, jc)
    mid = midflight(t, rc, {**before, **kept}); assert mid, "the cancelled run has no partial"
    r = t.raw("POST", f"/backup-runs/{rc}/cancel", {}); assert r.status_code < 300, r.text[:200]
    dc = t.wait(f"/backup-runs/{rc}", timeout=300, stop_on=TERMINAL); time.sleep(3)
    left = {k: v for k, v in partials().items() if k not in before}
    assert dc["status"] == "Cancelled" and set(left) == set(kept) and all(left[k] == kept_size[k] for k in kept), {"status": dc["status"], "left": left, "kept": kept_size, "cancelPartial": mid}
    r = t.raw("POST", f"/backup-runs/{rp}/resume", {}); assert r.status_code < 300, r.text[:200]
    dp = t.wait(f"/backup-runs/{rp}", timeout=600, stop_on=TERMINAL)
    a = g.arts(dp)
    assert dp["status"] == "Succeeded" and a and not {k: v for k, v in partials().items() if k not in before}, {"status": dp["status"], "arts": len(a), "left": {k: v for k, v in partials().items() if k not in before}}
    return {"target": target, "cancelPartialBytesBeforeCancel": mid, "pausedPartialKept": kept_size, "cancelled": dc["status"], "resumedRun": dp["status"],
            "resumedArtifactState": a[0]["state"], "partialsAfter": {k: v for k, v in partials().items() if k not in before}}


@scenario("gap/POL-04/partial-cancel-removed-paused-kept-default-volume")
def default_volume(): return run_cancel_beside_paused("default", "local-store")


@scenario("gap/POL-04/partial-cancel-removed-paused-kept-second-volume")
def second_volume(): return run_cancel_beside_paused("vol2", "vol2")


@scenario("gap/POL-04/partial-killed-agent-swept")
def killed_agent():
    t, ag = setup(); before = partials()
    j = mk(t, ag, "killed", 300, "vol2"); rid = g.start_run(t, j)
    mid = midflight(t, rid, before)
    subprocess.run(["systemctl", "--user", "kill", "-s", "KILL", f"{g.PFX}{KEY}-agent"], check=True)
    subprocess.run(["systemctl", "--user", "stop", f"{g.PFX}{KEY}-agent"], check=False); time.sleep(1)  # no automatic restart: the sweep must run only after the backdating
    left = {k: v for k, v in partials().items() if k not in before}; assert left, "kill left no partial"
    r = t.raw("POST", f"/backup-runs/{rid}/cancel", {}); assert r.status_code < 300, r.text[:200]
    # the agent is down: the backend ends the attempt itself once the lease is gone
    d = t.wait(f"/backup-runs/{rid}", timeout=900, stop_on=TERMINAL)
    old = time.time() - 13 * 3600
    for k in left: os.utime(ROOT() / k.split("/", 1)[0] / k.split("/", 1)[1], (old, old))
    t0 = time.time(); g.ensure_agent(KEY, KEY, extra_env={"Agent__Backup__LocalStorage__Volumes__vol2": str(ROOT() / "vol2")})
    end = time.time() + 150
    while time.time() < end and any(k in partials() for k in left): time.sleep(3)
    gone = not any(k in partials() for k in left)
    assert gone and d["status"] in TERMINAL, {"run": d["status"], "left": {k: v for k, v in partials().items() if k in left}}
    return {"run": d["status"], "partialAtKill": left, "removedAfterRestartSeconds": round(time.time() - t0)}


if __name__ == "__main__":
    for fn in (default_volume, second_volume, killed_agent):
        if not sys.argv[1:] or any(a in fn.__name__ for a in sys.argv[1:]): print(fn())
