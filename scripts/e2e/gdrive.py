"""Google Drive scenarios: connect (human consent), connection test, capacity, backup, restore + tree hash compare, multi-destination, retention delete, disconnect.
Connection "E2E Google Drive", tenant A. Run: python3 gdrive.py [name-filter]. Exit 2 with "MISSING PREREQUISITE" (nothing recorded as PASS) when the OAuth client is not configured on the platform.
The OAuth client id/secret are platform config (GOOGLE_CLIENT_ID/GOOGLE_CLIENT_SECRET/GOOGLE_REDIRECT_URI in git-ignored .env.e2e, or docs/client_secret_*.json for the dev stack), never in the repo.
Env: E2E_GDRIVE_CONSENT_TIMEOUT (seconds to wait for the human to approve in the browser, default 600).
"""
import os, sys, time, urllib.parse
import api, lib, files_providers as fp
from files_providers import dest, fcfg, run_ok, artifact, roundtrip, Host, file_restore, restore_and_compare
from lib import scenario


NAME = "E2E Google Drive"


def find_conn(t):
    return next((x for x in t.call("GET", "/storage-connections")["items"] if x["providerType"] == "google-drive" and x["name"] == NAME), None)


def connected(c):
    return bool(c and c.get("hasSecret") and c.get("configuration", {}).get("reauthorizationRequired") != "true")


def drive_conn(t):
    c = find_conn(t)
    assert connected(c), f"'{NAME}' is not connected: run `python3 gdrive.py gdrive/connect` first (needs a human to approve in the browser)"
    return c["id"]


def preflight(t):
    """Returns a 'missing prerequisite' message, or None when the platform can start an OAuth consent."""
    c = find_conn(t) or t.call("POST", "/storage-connections", {"providerType": "google-drive", "name": NAME, "configuration": {}})
    r = t.raw("POST", f"/storage-connections/{c['id']}/google-drive/authorize-url")
    if r.status_code == 409:
        return "the platform has no Google OAuth client (set GOOGLE_CLIENT_ID, GOOGLE_CLIENT_SECRET, GOOGLE_REDIRECT_URI in .env.e2e and restart the API; see ticket 15)"
    assert r.status_code == 200, f"authorize-url -> {r.status_code} {r.text[:200]}"
    want = os.environ.get("GOOGLE_CLIENT_ID")
    got = urllib.parse.parse_qs(urllib.parse.urlparse(r.json()["authorizationUrl"]).query)["client_id"][0]
    return f"platform uses client {got}, not GOOGLE_CLIENT_ID from the environment" if want and want != got else None


def connect(t):
    """Human consent: prints the Google URL and waits until the callback marks the connection connected."""
    c = find_conn(t)
    if connected(c):
        return {"alreadyConnected": c["configuration"].get("accountEmail")}
    url = t.call("POST", f"/storage-connections/{c['id']}/google-drive/authorize-url", {})["authorizationUrl"]
    print(f"OPEN THIS URL AND APPROVE (Google account must be a test user):\n{url}", flush=True)
    end = time.time() + int(os.environ.get("E2E_GDRIVE_CONSENT_TIMEOUT", "600"))
    while time.time() < end:
        c = find_conn(t)
        if connected(c): return {"account": c["configuration"].get("accountEmail")}
        time.sleep(3)
    raise TimeoutError("consent was not completed in time")


def disconnect(t):
    cid = drive_conn(t)
    t.call("POST", f"/storage-connections/{cid}/google-drive/disconnect", {})
    c = find_conn(t)
    assert not connected(c), c
    r = t.call("POST", f"/storage-connections/{cid}/test", {}, expect=(200, 201, 202, 204, 409, 422))
    assert (r or {}).get("test", {}).get("status") != "Succeeded", f"a disconnected connection still passes its test: {r}"
    return {"disconnected": True, "reauthorizationRequired": c["configuration"].get("reauthorizationRequired")}


def drive_dest(t, primary=True):
    return {"storageProvider": "google-drive", "storageConnectionId": drive_conn(t), "storageExecutionLocation": "Backend", "isPrimary": primary}


def test_ok(t):
    r = t.call("POST", f"/storage-connections/{drive_conn(t)}/test", {})["test"]
    assert r["status"] == "Succeeded", r
    return {"test": r["code"]}


def capacity(t):
    cid = drive_conn(t); p = t.call("POST", f"/storage-connections/{cid}/probe", {})
    for _ in range(30):
        if p["status"] != "Pending": break
        time.sleep(3); p = t.call("GET", f"/storage-connections/{cid}/capacity")
    assert p["status"] in ("Available", "NotMeasurable"), p
    return {"status": p["status"], "total": p.get("totalBytes"), "free": p.get("freeBytes")}


def download(t, art_id):
    """Drive replicas are served by the agent directly (WebRTC), so the platform returns a session, not bytes."""
    a = artifact(t, art_id); rep = next(r for r in a["replicas"] if r["storageProvider"] == "google-drive")
    p = t.call("POST", f"/backup-artifacts/{art_id}/downloads", {"replicaId": rep["id"], "decrypt": False})
    assert p["direct"] and p["checksum"] == a["artifactChecksum"] and p["sizeBytes"] == a["sizeBytes"], p
    return {"direct": True, "sizeBytes": p["sizeBytes"]}


def delete_removes_copy(t, ag, host, agent):
    job, rid, art, d = run_ok(t, agent, f"{host.name}-gdrive-del", fcfg([drive_dest(t)], paths=["docs"]))
    t.call("PUT", f"/backup-jobs/{job['id']}/retention", {"enabled": True, "keepForSeconds": 30, "deleteGracePeriodSeconds": 10})
    run = t.call("POST", f"/backup-jobs/{job['id']}/runs", {}); rid = run.get("id", run.get("run", {}).get("id"))  # expiry is fixed when the artifact is created
    d = t.wait(f"/backup-runs/{rid}", timeout=600, stop_on=lib.TERMINAL); assert d["status"] == "Succeeded", d["status"]
    art = fp.artifacts_of(d)[0]
    end = time.time() + 420; a = None
    while time.time() < end:
        a = artifact(t, art["id"])
        if a.get("state") == "Deleted" or all(r["status"] == "Deleted" for r in a["replicas"]): break
        time.sleep(5)
    assert all(r["status"] == "Deleted" for r in a["replicas"]), [(r["storageProvider"], r["status"]) for r in a["replicas"]]
    return {"artifactId": art["id"], "replicas": [(r["storageProvider"], r["status"]) for r in a["replicas"]]}


def files_roundtrip(t, ag, host, agent):
    out = roundtrip(t, host, agent, "gdrive", [drive_dest(t)], tag="gd")
    out["download"] = download(t, out["artifactId"])
    return out


def multi(t, ag, host, agent):
    s3 = dest("s3", primary=False)
    job, rid, art, d = run_ok(t, agent, f"{host.name}-gdrive-s3", fcfg([drive_dest(t), s3], paths=["docs", "nested"]))
    a = artifact(t, art["id"]); live = {r["storageProvider"] for r in a["replicas"] if r["status"] == "Available" and r["verificationState"] == "Verified"}
    assert live == {"google-drive", "s3"}, a["replicas"]
    drive = next(r for r in a["replicas"] if r["storageProvider"] == "google-drive")
    return {"restore": restore_and_compare(t, host, agent, art["id"], "gdm", ["docs", "nested"], replica_id=drive["id"])}


def main(only=None):
    t = lib.Tenant("a"); ag = t.agents(); fed, win = Host("fedora"), Host("win")
    msg = preflight(t)
    if msg: sys.exit(f"MISSING PREREQUISITE: {msg}")
    items = [("gdrive/connect", lambda: connect(t)), ("gdrive/connection-test", lambda: test_ok(t)), ("gdrive/capacity", lambda: capacity(t)),
             ("gdrive/files/fedora/roundtrip-and-download", lambda: files_roundtrip(t, ag, fed, ag["fedora-e2e"])),
             ("gdrive/files/win/roundtrip-and-download", lambda: files_roundtrip(t, ag, win, ag["win11-e2e"])),
             ("gdrive/multi/fedora/drive+s3", lambda: multi(t, ag, fed, ag["fedora-e2e"])),
             ("gdrive/retention-delete/fedora/removes-copy", lambda: delete_removes_copy(t, ag, fed, ag["fedora-e2e"])),
             ("gdrive/disconnect", lambda: disconnect(t))]
    fp.run_all(only, items)


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else None)
