"""FedoraSpecial gap scenarios (plan section 9 PG Physical/Streaming + RST-14, LVM snapshot, AGT-01/08/10/12). Names: gap/<id>...
Run: python3 gap_fedora.py <group> [mode]   groups: pg-physical <Physical|Streaming>, recovery, lvm, agent, ...
Throwaway objects are prefixed `gap-fedora-`; everything created is deleted at the end of each group. Secrets are only read in code, never printed."""
import hashlib, json, re, subprocess, sys, time, uuid
import api, lib, flows
from lib import ST, scenario

PFX = "gap-fedora-"
FED = "virtual-fedora"
LIVE = ("Pending", "Leased", "Running", "Paused")  # anything not terminal


def t_a():
    return lib.Tenant("a")


def fedora_id(t):
    return t.agents()["fedora-e2e"]["agentId"]


def busy(t):
    """Runs/restores of the Fedora agent that are not terminal (a restart of the agent would hit them)."""
    fid, out = fedora_id(t), []
    page = 1
    while True:
        d = t.call("GET", f"/backup-runs?pageSize=100&page={page}")
        for r in d["items"]:
            if r["agentId"] == fid and r["status"] not in lib.TERMINAL:
                out.append(("run", r["id"], r["status"]))
        if page * d["pageSize"] >= d["totalCount"]:
            break
        page += 1
    try:
        for r in t.call("GET", "/restores?pageSize=100").get("items", []):
            if r.get("agentId") == fid and r["status"] not in lib.TERMINAL + ("Cancelled",):
                out.append(("restore", r["id"], r["status"]))
    except AssertionError:
        pass
    return out


def wait_idle(t, timeout=900):
    end = time.time() + timeout
    while time.time() < end:
        b = busy(t)
        if not b:
            return
        print("fedora agent busy:", b, flush=True)
        time.sleep(15)
    raise TimeoutError("fedora agent never became idle")


def pg_mode(t, mode):
    """Switches the agent-wide PostgreSQL BackupMode (restarts the Fedora agent); only when no run of that agent is live."""
    wait_idle(t)
    r = subprocess.run([sys.executable, str(api.ROOT / "scripts/e2e/pg-mode.py"), mode], capture_output=True, text=True)
    print(r.stdout[-400:], r.stderr[-400:], flush=True)
    assert r.returncode == 0, "pg-mode.py failed"
    t0 = time.time()
    while time.time() - t0 < 90:  # agent has to come back online and re-heartbeat
        a = t.agents()["fedora-e2e"]
        if a.get("connectivity") == "online":
            return
        time.sleep(3)
    raise AssertionError("fedora agent did not come back online")


def db(sql):
    r = subprocess.run(["/tmp/gap-db.sh", sql], capture_output=True, text=True)
    assert r.returncode == 0, r.stderr[:300]
    return r.stdout.strip()


def sha256_file_remote(path):
    rc, out = lib.fedora(f"sudo -n sha256sum {path}")
    return out.split()[0]
