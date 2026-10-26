"""Google Drive scenarios against a connection authorized by a human (connection named "E2E Google Drive", tenant A). Run: python3 gdrive.py [name-filter]."""
import hashlib, sys, time
import api, lib, files_providers as fp
from files_providers import dest, fcfg, run_ok, artifact, roundtrip, Host, file_restore, restore_and_compare
from lib import scenario


def drive_conn(t):
    c = next((x for x in t.call("GET", "/storage-connections")["items"] if x["providerType"] == "google-drive" and x["name"] == "E2E Google Drive"), None)
    assert c, "authorize the 'E2E Google Drive' connection first"
    return c["id"]


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
    items = [("gdrive/connection-test", lambda: test_ok(t)), ("gdrive/capacity", lambda: capacity(t)),
             ("gdrive/files/fedora/roundtrip-and-download", lambda: files_roundtrip(t, ag, fed, ag["fedora-e2e"])),
             ("gdrive/files/win/roundtrip-and-download", lambda: files_roundtrip(t, ag, win, ag["win11-e2e"])),
             ("gdrive/multi/fedora/drive+s3", lambda: multi(t, ag, fed, ag["fedora-e2e"])),
             ("gdrive/retention-delete/fedora/removes-copy", lambda: delete_removes_copy(t, ag, fed, ag["fedora-e2e"]))]
    fp.run_all(only, items)


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else None)
