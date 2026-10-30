"""Central (platform-managed) storage: Fedora PostgreSQL -> Central backup, the stored file checked on the backend's own copy of the share, a backend download hash-checked
against the artifact checksum, then a restore into a new database compared row for row with the source. Needs the compose override (Backups__Storage__Central__*), the
CIFS mount and CentralStorage agent settings from configure-agents.py. One pass, no retries."""
import hashlib, subprocess, lib, flows, matrix, api
from lib import ST, scenario

CENTRAL_SHARE = "/central-share/central"


def central_dest(t):
    conn = next(c for c in t.call("GET", "/storage-connections")["items"] if c["providerType"] == "central")
    assert conn["effectiveEnabled"], f"the platform-managed connection is not enabled: {conn['name']}"
    return {"storageProvider": "central", "storageConnectionId": conn["id"], "storageExecutionLocation": "Backend", "isPrimary": True}


def backend_file(key):
    """(size, sha256) of the stored object as the API container sees it on the shared tree, or None."""
    r = subprocess.run(["docker", "exec", "bp-api", "sh", "-c", f"f=$(find {CENTRAL_SHARE} -type f -path '*/{key}' | head -1); [ -n \"$f\" ] && stat -c %s \"$f\" && sha256sum \"$f\""],
                       capture_output=True, text=True)
    if r.returncode != 0: return None
    size, line = r.stdout.split("\n")[:2]
    return int(size), line.split()[0]


@scenario("storage/central-backup-hash-download-restore")
def central(t):
    tg = matrix.targets(t, t.agents())["fedora-pg"]
    res = matrix.db_roundtrip(t, tg, "central", [central_dest(t)], tag="central-")  # backup, restore into a new database, source vs restored row count + hash
    art = t.call("GET", f"/backup-artifacts/{res['artifactId']}")
    assert sorted({r["storageProvider"] for r in art["replicas"]}) == ["central"], f"replicas {[r['storageProvider'] for r in art['replicas']]}"
    rep = art["replicas"][0]
    assert rep["verificationState"] == "Verified", f"replica {rep['verificationState']}"
    stored = backend_file(rep["key"])
    assert stored, f"the backend does not see {rep['key']} under {CENTRAL_SHARE}"
    assert stored == (art["sizeBytes"], art["artifactChecksum"]), f"stored file {stored} != artifact ({art['sizeBytes']}, {art['artifactChecksum']})"
    p = t.call("POST", f"/backup-artifacts/{art['id']}/downloads", {"replicaId": rep["id"], "decrypt": False})
    r = t.s.get(api.BASE + p["downloadUrl"], stream=True); h, n = hashlib.sha256(), 0
    for chunk in r.iter_content(1 << 20): h.update(chunk); n += len(chunk)
    assert r.status_code == 200 and n == art["sizeBytes"] and h.hexdigest() == art["artifactChecksum"], f"download {r.status_code} {n} bytes sha256 {h.hexdigest()[:12]}"
    return {**res, "storedBytes": stored[0], "artifactSha256": art["artifactChecksum"][:16], "downloadedBytes": n}


if __name__ == "__main__":
    central(lib.Tenant("a"))
