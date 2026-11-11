"""The acceptance scenario in one command: agent enrollment, a denied role, agent mTLS, Central storage, agent-direct WebRTC download bytes, agent self-update, the 27-cell matrix and the large cell.
Every check is one PASS/FAIL row in results.jsonl; nothing is retried. Run: python3 acceptance.py [filter]"""
import secrets, sys, uuid, requests
import api, lib, matrix, mtls, central, webrtc, selfupdate, large
from lib import scenario

BASE = api.BASE
guid = lambda: str(uuid.uuid4())


def check(cond, msg):
    if not cond: raise AssertionError(msg)


def wait_api_ready(timeout=120):
    if hasattr(api, "wait_ready"):
        return api.wait_ready(timeout=timeout)
    return True


@scenario("accept/enrollment: both lab agents joined by token; a new token enrolls once and is then refused")
def enrollment():
    t = lib.Tenant("a"); agents = t.agents()
    for name in ("fedora-e2e", "win11-e2e"): check(agents.get(name, {}).get("status") == "Active", f"{name} is not an Active enrolled agent")
    tok = t.call("POST", "/agent-enrollment-tokens"); host = f"e2e-synth-{uuid.uuid4().hex[:6]}"
    body = {"enrollmentToken": tok["token"], "installationId": guid(), "agentVersion": "1.0.0", "protocolVersion": "1.0", "platform": "Linux", "hostName": host, "capabilities": []}
    first = requests.post(BASE + "/api/agent/enroll", json=body, verify=False, timeout=30)
    check(first.status_code == 201 and first.json().get("credential", {}).get("secret"), f"enroll -> {first.status_code} {first.text[:150]}")
    aid = first.json()["agentId"]
    try:
        check(t.agents().get(host, {}).get("agentId") == aid, "enrolled agent missing from the agent list")
        again = requests.post(BASE + "/api/agent/enroll", json={**body, "installationId": guid()}, verify=False, timeout=30)
        check(again.status_code == 401, f"a consumed token enrolled again -> {again.status_code}")
    finally:
        t.raw("POST", f"/agents/{aid}/revoke", {})
    return {"agents": sorted(agents)}


@scenario("accept/role-denied: a Viewer reads but cannot create jobs, start runs or restore")
def role_denied():
    P = api.login(); login, pw = f"e2e-accept-viewer-{uuid.uuid4().hex[:6]}", secrets.token_urlsafe(18) + "Aa1!"
    tid = lib.ST["a"]["tenantId"]
    def plat(method, path, body=None): return P.request(method, BASE + path, json=body, headers={"Idempotency-Key": uuid.uuid4().hex})
    u = plat("POST", "/api/platform/users", {"loginIdentifier": login, "displayName": "E2E accept viewer", "password": pw}); check(u.status_code == 201, f"create user {u.status_code}")
    uid = u.json().get("userId") or u.json().get("id")
    m = plat("POST", f"/api/platform/tenants/{tid}/memberships", {"userId": uid, "role": "Viewer"}); check(m.status_code == 201, f"membership {m.status_code}")
    try:
        s = api.login(login, pw); base = f"{BASE}/api/tenants/{tid}"
        go = lambda method, path, body=None: s.request(method, base + path, json=body, headers={"Idempotency-Key": uuid.uuid4().hex}).status_code
        g = guid(); got = {"read jobs": go("GET", "/backup-jobs"), "create job": go("POST", "/backup-jobs", {}), "start run": go("POST", f"/backup-jobs/{g}/runs", {}),
                           "restore": go("POST", f"/backup-artifacts/{g}/restore", {})}
        want = {"read jobs": 200, "create job": 403, "start run": 403, "restore": 403}
        check(got == want, f"viewer outcomes {got}, expected {want}")
    finally:
        plat("DELETE", f"/api/platform/tenants/{tid}/memberships/{uid}")
    return got


if __name__ == "__main__":
    only = sys.argv[1] if len(sys.argv) > 1 else None
    wait_api_ready()
    t = lib.Tenant("a")
    for fn in (enrollment, role_denied):
        if not only or only in fn.scenario: fn()
    for fn in (mtls.mtls, central.central, webrtc.webrtc_direct, selfupdate.self_update):
        if not only or only in fn.scenario: fn(t)
    matrix.main(only)
    if not only or only in large.large_cell.scenario: large.large_cell()
