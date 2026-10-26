"""Create the e2e plans and assign the unlimited one to both tenants."""
import api, json, sys
st = json.loads((api.ROOT / "docs/test-environment/state.json").read_text()); s = api.login()
def chk(r):
    if not r.ok: sys.exit(f"{r.request.method} {r.url} {r.status_code} {r.text[:300]}")
    return r.json()
plans = chk(s.get(f"{api.BASE}/api/platform/plans"))
items = plans["items"] if isinstance(plans, dict) else plans
def ensure(name, **body):
    p = next((x for x in items if x["name"] == name), None)
    return p or chk(s.post(f"{api.BASE}/api/platform/plans", json={"name": name, "enabled": True, **body}))
unl = ensure("E2E Unlimited", unlimited=True)
small = ensure("E2E Small 300MB", storageLimitBytes=300 * 1024 * 1024, unlimited=False)
pid = lambda p: p.get("id") or p.get("planId")
st["plans"] = {"unlimited": pid(unl), "small": pid(small)}
for k in ("a", "b"):
    chk(s.put(f"{api.BASE}/api/platform/tenants/{st[k]['tenantId']}/subscription", json={"planId": pid(unl)}))
(api.ROOT / "docs/test-environment/state.json").write_text(json.dumps(st, indent=2)); print(st["plans"])
