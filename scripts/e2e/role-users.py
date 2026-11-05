"""Create Admin/Operator/Viewer users in tenants a and b through the platform API (Owner already exists from bootstrap.py). Idempotent: logins and passwords are kept in state.json["roles"]."""
import api, json, secrets, sys
STATE = api.ROOT / "docs/test-environment/state.json"
st = json.loads(STATE.read_text())
s = api.login()
def ok(r):
    if not r.ok: sys.exit(f"{r.request.method} {r.url} -> {r.status_code} {r.text[:300]}")
    return r.json()
roles = st.setdefault("roles", {})
for key in ("a", "b"):
    tid = st[key]["tenantId"]
    for role in ("Admin", "Operator", "Viewer"):
        name = f"{role.lower()}-{key}"
        if name in roles: continue
        pw = secrets.token_urlsafe(18) + "Aa1!"
        u = ok(s.post(f"{api.BASE}/api/platform/users", json={"loginIdentifier": name, "displayName": f"{role} {key.upper()}", "password": pw}))
        uid = u.get("userId") or u.get("id")
        ok(s.post(f"{api.BASE}/api/platform/tenants/{tid}/memberships", json={"userId": uid, "role": role}))
        roles[name] = {"tenant": key, "role": role, "login": name, "password": pw, "userId": uid}
STATE.write_text(json.dumps(st, indent=2)); STATE.chmod(0o600)
print({n: r["role"] + "/" + r["tenant"] for n, r in roles.items()})
