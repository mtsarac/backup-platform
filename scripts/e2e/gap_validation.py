"""Gap tests AUTH-01b / API ids (#56): NUL and control characters in identifiers and all-zero ids in routes give a controlled 4xx, never a 500, with nothing about the database in the body.
Runs against both API nodes. No secrets are printed."""
import json, requests, api, lib
from lib import scenario

BAD_TEXT = ["adm\u0000in", "\u0000", "a\u0000b" * 5, "admin\u0001", "admin\n", "ad\ud800min"]
# The lab API runs ASPNETCORE_ENVIRONMENT=Development, where the framework prints its own BadHttpRequestException text for ANY malformed JSON body; only database details are asserted here.
LEAKS = ("npgsql", "postgres", "sqlstate", "22021", "dbupdateexception", "identity must not be empty")
ZERO = "00000000-0000-0000-0000-000000000000"


def clean(r):
    low = r.text.lower()
    return not any(x in low for x in LEAKS)


def anon(base):
    s = requests.Session(); s.verify = False
    t = s.get(f"{base}/api/auth/antiforgery", timeout=30).json(); s.headers[t["headerName"]] = t["requestToken"]
    return s


def admin(base):
    old = api.BASE; api.BASE = base
    try: return api.login()
    finally: api.BASE = old


@scenario("gap/AUTH-01b/nul-and-control-in-login-and-free-text")
def nul():
    out = {}
    for name, base in (("node1", "https://192.168.122.1:5252"), ("node2", "https://192.168.122.1:5255")):
        s = anon(base); codes = []
        for ident in BAD_TEXT:
            r = s.post(f"{base}/api/auth/login", data=json.dumps({"loginIdentifier": ident, "password": "x"}, ensure_ascii=False).encode("utf-8", "surrogatepass") if "\ud800" not in ident else b'{"loginIdentifier":"ad\\ud800min","password":"x"}',
                       headers={"Content-Type": "application/json"}, timeout=30)
            codes.append(r.status_code); assert r.status_code in (400, 401) and clean(r), (name, repr(ident), r.status_code, r.text[:200])
        a = admin(base); t0 = lib.Tenant("a") if False else None
        r = a.post(f"{base}/api/platform/tenants", json={"name": "nul\u0000tenant"}); assert r.status_code == 400 and clean(r), (r.status_code, r.text[:200]); codes.append(r.status_code)
        for path in ("/api/platform/users", "/api/platform/plans", "/api/platform/tenants"):
            r = a.get(f"{base}{path}?search=a%00b"); assert r.status_code == 400 and clean(r), (path, r.status_code, r.text[:200]); codes.append(r.status_code)
        out[name] = codes
    return out


@scenario("gap/AGT/all-zero-ids-never-500")
def zero():
    out = {}
    for name, base in (("node1", "https://192.168.122.1:5252"), ("node2", "https://192.168.122.1:5255")):
        a = admin(base); res = {}
        tid = a.get(f"{base}/api/platform/tenants").json()
        tid = (tid["items"] if isinstance(tid, dict) else tid)[0]
        tid = tid.get("id") or tid.get("tenantId")
        for method, path in [("GET", f"/api/platform/tenants/{ZERO}/agents"), ("GET", f"/api/platform/tenants/{tid}/agents/{ZERO}"), ("POST", f"/api/platform/tenants/{tid}/agents/{ZERO}/revoke"),
                             ("PUT", f"/api/platform/plans/{ZERO}/enabled"), ("GET", f"/api/tenants/{ZERO}/agents"),
                             ("GET", f"/api/platform/storage-connections?tenantId={ZERO}"), ("GET", f"/api/platform/tenants/{tid}/quota?agentId={ZERO}")]:
            r = a.request(method, f"{base}{path}", json={} if method in ("POST", "PUT") else None)
            res[f"{method} {path.replace(str(tid), '<tenant>')}"] = r.status_code
            assert r.status_code in (400, 404) and clean(r), (method, path, r.status_code, r.text[:200])
        r = requests.get(f"{base}/api/platform/tenants/{ZERO}/agents", verify=False); res["anonymous zero id"] = r.status_code; assert r.status_code in (401, 403), r.status_code
        out[name] = res
    return out


if __name__ == "__main__":
    print(nul()); print(zero())
