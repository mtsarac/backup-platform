"""Create e2e tenants/users and enrollment tokens. State (incl. secrets) goes to docs/test-environment/state.json (gitignored)."""
import api, json, secrets, pathlib, sys
STATE = api.ROOT / "docs/test-environment/state.json"
st = json.loads(STATE.read_text()) if STATE.exists() else {}
s = api.login()
def ok(r):
    if not r.ok: sys.exit(f"{r.request.method} {r.url} -> {r.status_code} {r.text[:300]}")
    return r.json()
me = next(u for u in ok(s.get("/api/platform/users".join([api.BASE, ""])))["items"] if u["loginIdentifier"] == "admin")
for key in ("a", "b"):
    if key in st: continue
    t = ok(s.post(f"{api.BASE}/api/platform/tenants", json={"name": f"E2E Tenant {key.upper()}"}))
    pw = secrets.token_urlsafe(18) + "Aa1!"
    u = ok(s.post(f"{api.BASE}/api/platform/users", json={"loginIdentifier": f"owner-{key}", "displayName": f"Owner {key.upper()}", "password": pw}))
    tid = t.get("tenantId") or t.get("id"); uid = u.get("userId") or u.get("id")
    ok(s.post(f"{api.BASE}/api/platform/tenants/{tid}/memberships", json={"userId": uid, "role": "Owner"}))
    ok(s.put(f"{api.BASE}/api/platform/tenants/{tid}/agent-enrollment", json={"enabled": True}))
    st[key] = {"tenantId": tid, "login": f"owner-{key}", "password": pw, "userId": uid}
STATE.write_text(json.dumps(st, indent=2)); STATE.chmod(0o600)
print({k: {"tenantId": v["tenantId"], "login": v["login"]} for k, v in st.items()})
