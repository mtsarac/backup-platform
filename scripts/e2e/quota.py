"""Hard quota: a limited override of used+150 MiB blocks runs once it is reached; deleting artifacts releases the space."""
import sys, time, json, lib, flows, ops, api
from lib import ST, scenario

def override(body):
    s = api.login(); r = s.put(f"{api.BASE}/api/platform/tenants/{ST['a']['tenantId']}/subscription/storage-limit-override", json=body); r.raise_for_status()

@scenario("quota/hard-limit-blocks-and-delete-releases")
def quota(t):
    fa = t.agents()["fedora-e2e"]
    job = ops.big_file_job(t, fa, "quota", [flows.local_dest()])
    override({"kind": "unlimited"})  # a job without size history is refused (quota_size_unknown) under a hard limit; bootstrap it once
    seed = ops.artifacts_of(ops.lc(t, ops.start(t, job), 300))[0]
    t.call("DELETE", f"/backup-artifacts/{seed['id']}"); time.sleep(45)
    used0 = t.call("GET", "/storage-quota")["usedBytes"]; limit = used0 + 150 * 1024 * 1024
    override({"kind": "limited", "bytes": limit}); arts, outcomes = [], []
    try:
        for i in range(5):
            r = t.raw("POST", f"/backup-jobs/{job['id']}/runs", {})
            if r.status_code >= 400:
                outcomes.append((i, "refused-at-admission", r.status_code, r.json().get("code") if r.content else None)); break
            rid = r.json().get("id", r.json().get("run", {}).get("id"))
            d = ops.lc(t, rid, 300); a = d["attempts"][-1]
            if d["status"] == "Succeeded": arts.append(ops.artifacts_of(d)[0]); outcomes.append((i, "Succeeded"))
            else: outcomes.append((i, d["status"], a["reasonCode"], a["reason"])); break
        used = t.call("GET", "/storage-quota")
        refused = [o for o in outcomes if o[1] != "Succeeded"]
        assert refused, f"no run was refused with a 150 MiB headroom and 64 MiB artifacts: {outcomes} {used}"
        assert used["usedBytes"] <= limit, f"usage {used['usedBytes']} exceeds the limit {limit}"
        for a in arts: t.call("DELETE", f"/backup-artifacts/{a['id']}")
        end = time.time() + 400; after = used
        while time.time() < end:
            after = t.call("GET", "/storage-quota")
            if after["usedBytes"] <= used0 + 1024: break
            time.sleep(15)
        released = used["usedBytes"] - after["usedBytes"]
        assert released >= sum(a["sizeBytes"] for a in arts) * 0.99, f"deleted {len(arts)} artifacts but only {released} bytes were released"
        rid = ops.start(t, job); d = ops.lc(t, rid, 300)
        assert d["status"] == "Succeeded", f"run after release: {d['status']}"
        return {"outcomes": outcomes, "used0": used0, "limit": limit, "usedAtBlock": used["usedBytes"], "released": released}
    finally:
        override({"kind": "none"})

if __name__ == "__main__":
    quota(lib.Tenant("a"))
