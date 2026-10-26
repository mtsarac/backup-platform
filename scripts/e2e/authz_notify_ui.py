"""Test-plan sections 1.4 (enrollment tokens), 8 (authorization, tenancy) and 9.2 (audit). Run: python3 authz_notify_ui.py [filter]

Fixtures live in tenant A and are named e2e-authz*; mutations by tenant B / role users are probed only against throwaway objects
(synthetic agent, throwaway connection, own job/artifact), and ids that do not exist for role probes. Secrets are never printed.
"""
import json, re, secrets, statistics, sys, threading, time, uuid, requests
import api, lib, flows, matrix
from lib import ST, scenario

BASE = api.BASE
TA, TB = ST["a"]["tenantId"], ST["b"]["tenantId"]
TOKEN_LIFETIME_MIN = 5  # server minimum (MinimumEnrollmentTokenLifetime)
guid = lambda: str(uuid.uuid4())


def call(s, method, path, body=None, headers=None):
    h = {"Idempotency-Key": uuid.uuid4().hex}; h.update(headers or {})
    return s.request(method, BASE + path, json=body, headers=h, timeout=90)


def no_csrf(s):
    """Same auth/antiforgery cookies, but without the X-CSRF-TOKEN header."""
    n = requests.Session(); n.verify = False; n.cookies.update(s.cookies); return n


def is_empty(d):
    return d == [] or (isinstance(d, dict) and d.get("items") == [])


def check(cond, msg):
    if not cond: raise AssertionError(msg)


def enroll(token, host, installation=None, **over):
    body = {"enrollmentToken": token, "installationId": installation or guid(), "agentVersion": "1.0.0", "protocolVersion": "1.0",
            "platform": "Linux", "hostName": host, "capabilities": []}
    body.update(over)
    return requests.post(BASE + "/api/agent/enroll", json=body, verify=False, timeout=30)


def err(r):
    try: return r.json().get("error")
    except Exception: return f"HTTP{r.status_code}"


EMPTY_LIST = {"job.runs", "artifact.repairs"}  # list-by-parent routes: empty 200 for an unknown parent is not a leak
OBS = []
F = {}  # shared fixture of tenant A (filled by fixture())


def platform():
    if "P" not in F: F["P"] = api.login()
    return F["P"]


def fixture():
    """A job, a succeeded run + artifact, a restore, a throwaway storage connection, notification channel, token and synthetic agent in tenant A."""
    if F.get("ready"): return F
    t = lib.Tenant("a"); agents = t.agents(); fa = agents["fedora-e2e"]
    tg = matrix.targets(t, agents)["fedora-pg"]
    cfg = flows.db_job_config(tg["engine"], tg["cfg"], [flows.local_dest()], encrypt=tg["encrypt"])
    job, rid, art, _ = flows.run_backup(t, fa, "e2e-authz fixture", cfg, timeout=300)
    req = {"agentId": fa["agentId"], "target": tg["target"](), "databaseName": "authz_probe_db", "mode": "NewDatabase"}
    pre = t.call("POST", f"/backup-artifacts/{art['id']}/restore-precheck", req)
    conn = t.call("POST", "/storage-connections", {"providerType": "s3", "name": f"e2e-authz-throwaway {uuid.uuid4().hex[:6]}",
        "configuration": {"endpoint": f"https://{api.env['PUBLIC_HOST']}:9000", "bucket": "authz-none", "region": "us-east-1"},
        "secret": {"accessKey": "AUTHZACCESSKEY", "secretKey": secrets.token_urlsafe(24)}})
    chan = t.call("POST", "/notifications/channels", {"kind": "webhook", "name": f"e2e-authz-hook {uuid.uuid4().hex[:6]}", "target": f"https://example.invalid/e2e-authz-{uuid.uuid4().hex[:8]}"})
    tok = t.call("POST", "/agent-enrollment-tokens", {"lifetimeMinutes": 60})
    synth_tok = t.call("POST", "/agent-enrollment-tokens", {"lifetimeMinutes": 60})
    host = f"e2e-authz-synth-{uuid.uuid4().hex[:6]}"
    r = enroll(synth_tok["token"], host); check(r.status_code == 201, f"synthetic enroll {r.status_code} {r.text[:200]}")
    inst = t.call("GET", "/discovery/instances")["items"][0]["id"]
    F.update(t=t, tb=lib.Tenant("b"), job=job, run=rid, art=art["id"], restore=pre.get("id", pre.get("restoreId")), conn=conn["id"], conn_name=conn["name"],
             chan=chan["id"], chan_name=chan["name"], tok=tok["tokenId"], synth=r.json()["agentId"], synth_host=host, real_agent=fa["agentId"], inst=inst,
             cfg=cfg, restore_req=req, ready=True)
    F["markers"] = [job["id"], job["name"], rid, art["id"], F["restore"], conn["id"], conn["name"], chan["id"], chan["name"], F["synth"], host, "fedora-e2e", fa["agentId"]]
    return F


def probes():
    """(label, method, tenant-relative path, body) against tenant A objects; every one must be refused for tenant B."""
    j, r, a, rs, c, ch, ag, tk = F["job"]["id"], F["run"], F["art"], F["restore"], F["conn"], F["chan"], F["synth"], F["tok"]
    job_body = {"name": "authz-pwn", "agentId": F["real_agent"], "configuration": F["cfg"]}
    return [
        ("agent.get", "GET", f"/agents/{F['real_agent']}", None), ("agent.revoke", "POST", f"/agents/{ag}/revoke", {}),
        ("agent.dblogin", "POST", f"/agents/{ag}/database-logins", {"userName": "pwn", "password": "pwn-pwn-pwn"}),
        ("agent.bw", "PUT", f"/upload-bandwidth/agents/{ag}", {"bytesPerSecond": 1000000}),
        ("agent.discovery-settings", "PUT", f"/discovery/agents/{ag}/settings", {"enabled": False, "scanIntervalMinutes": 60, "includeDocker": False}),
        ("agent.rescan", "POST", f"/discovery/agents/{ag}/rescan", {}), ("discovery.instance", "GET", f"/discovery/instances/{F['inst']}", None),
        ("token.get", "GET", f"/agent-enrollment-tokens/{tk}", None), ("token.revoke", "DELETE", f"/agent-enrollment-tokens/{tk}", None),
        ("job.get", "GET", f"/backup-jobs/{j}", None), ("job.put", "PUT", f"/backup-jobs/{j}", job_body),
        ("job.schedule.put", "PUT", f"/backup-jobs/{j}/schedule", {"cron": "0 3 * * *"}), ("job.schedule.delete", "DELETE", f"/backup-jobs/{j}/schedule", None),
        ("job.occurrences", "GET", f"/backup-jobs/{j}/schedule/occurrences", None), ("job.retention", "PUT", f"/backup-jobs/{j}/retention", {"enabled": False}),
        ("job.disable", "POST", f"/backup-jobs/{j}/disable", {}), ("job.enable", "POST", f"/backup-jobs/{j}/enable", {}),
        ("job.run", "POST", f"/backup-jobs/{j}/runs", {}), ("job.runs", "GET", f"/backup-jobs/{j}/runs", None), ("job.health", "GET", f"/backup-jobs/{j}/health", None),
        ("job.bw", "PUT", f"/upload-bandwidth/jobs/{j}", {"bytesPerSecond": 1000000}),
        ("run.get", "GET", f"/backup-runs/{r}", None), ("run.cancel", "POST", f"/backup-runs/{r}/cancel", {}), ("run.retry", "POST", f"/backup-runs/{r}/retry", {}),
        ("run.pause", "POST", f"/backup-runs/{r}/pause", {}), ("run.resume", "POST", f"/backup-runs/{r}/resume", {}),
        ("artifact.get", "GET", f"/backup-artifacts/{a}", None), ("artifact.delete", "DELETE", f"/backup-artifacts/{a}", None),
        ("artifact.cancel-delete", "POST", f"/backup-artifacts/{a}/cancel-delete", {}), ("artifact.repair", "POST", f"/backup-artifacts/{a}/repairs", {}),
        ("artifact.repairs", "GET", f"/backup-artifacts/{a}/repairs", None), ("artifact.download-availability", "GET", f"/backup-artifacts/{a}/download-availability", None),
        ("artifact.download", "POST", f"/backup-artifacts/{a}/downloads", {}), ("artifact.restore-targets", "GET", f"/backup-artifacts/{a}/restore-targets", None),
        ("artifact.restore", "POST", f"/backup-artifacts/{a}/restore", F["restore_req"]), ("artifact.precheck", "POST", f"/backup-artifacts/{a}/restore-precheck", F["restore_req"]),
        ("artifact.verify", "POST", f"/backup-artifacts/{a}/restore-verifications", F["restore_req"]), ("artifact.discovery", "POST", f"/backup-artifacts/{a}/restore-discovery", F["restore_req"]),
        ("restore.get", "GET", f"/restores/{rs}", None), ("restore.cancel", "POST", f"/restores/{rs}/cancel", {}),
        ("restore.cleanup", "POST", f"/restores/{rs}/cleanup/retry", {}), ("restore.rollback", "POST", f"/restores/{rs}/rollback-removal", {}),
        ("conn.get", "GET", f"/storage-connections/{c}", None), ("conn.put", "PUT", f"/storage-connections/{c}", {"name": "pwn", "configuration": {}}),
        ("conn.secret.put", "PUT", f"/storage-connections/{c}/secret", {"secret": {"accessKey": "pwn", "secretKey": "pwnpwnpwn"}}),
        ("conn.secret.delete", "DELETE", f"/storage-connections/{c}/secret", None), ("conn.test", "POST", f"/storage-connections/{c}/test", {}),
        ("conn.capacity", "GET", f"/storage-connections/{c}/capacity", None), ("conn.probe", "POST", f"/storage-connections/{c}/probe", {}),
        ("conn.disable", "POST", f"/storage-connections/{c}/disable", {}), ("conn.enable", "POST", f"/storage-connections/{c}/enable", {}),
        ("conn.delete", "DELETE", f"/storage-connections/{c}", None),
        ("notif.channel.get", "GET", f"/notifications/channels/{ch}", None), ("notif.channel.put", "PUT", f"/notifications/channels/{ch}", {"name": "pwn", "target": "https://example.invalid/pwn"}),
        ("notif.channel.secret", "PUT", f"/notifications/channels/{ch}/secret", {"value": "pwnpwnpwn"}), ("notif.channel.disable", "POST", f"/notifications/channels/{ch}/disable", {}),
        ("notif.channel.enable", "POST", f"/notifications/channels/{ch}/enable", {}), ("notif.channel.test", "POST", f"/notifications/channels/{ch}/test", {}),
        ("notif.channel.owner", "POST", f"/notifications/channels/{ch}/owner-security-recipient", {"enabled": True}), ("notif.channel.delete", "DELETE", f"/notifications/channels/{ch}", None),
    ]


# paths that must return only the caller's own tenant data; none of tenant A's markers may appear for tenant B
LISTS = ["/agents", "/agents/connectivity-summary", "/backup-jobs", "/backup-runs", "/backup-artifacts", "/restores", "/storage-connections", "/storage-quota",
         "/notifications/channels", "/notifications/deliveries", "/notifications/policy", "/agent-enrollment-tokens", "/agent-enrollment", "/dashboard",
         "/observability/health-summary", "/reports/backups", "/discovery/instances", "/discovery/agents", "/bandwidth-profiles", "/upload-bandwidth",
         "/artifact-break-glass", "/encryption", "/context"]


def leaks(text):
    return [m for m in F["markers"] if m in text]


def intact():
    t = F["t"]; probs = []
    j = t.raw("GET", f"/backup-jobs/{F['job']['id']}")
    if j.status_code != 200 or j.json()["name"] != F["job"]["name"] or not j.json()["enabled"]: probs.append(f"job changed {j.status_code}")
    if t.raw("GET", f"/backup-runs/{F['run']}").json().get("status") != "Succeeded": probs.append("run changed")
    art = t.raw("GET", f"/backup-artifacts/{F['art']}").json()
    if art.get("state") != "Available": probs.append(f"artifact state {art.get('state')}")
    ag = [a for a in t.call("GET", "/agents") if a["agentId"] == F["synth"]]
    if not ag or ag[0]["status"] != "Active": probs.append(f"synthetic agent {ag and ag[0]['status']}")
    c = t.raw("GET", f"/storage-connections/{F['conn']}").json()
    if c.get("name") != F["conn_name"] or not c.get("enabled", True): probs.append("connection changed")
    ch = t.raw("GET", f"/notifications/channels/{F['chan']}").json()
    if ch.get("name") != F["chan_name"] or not ch.get("enabled"): probs.append("channel changed")
    if t.raw("GET", f"/agent-enrollment-tokens/{F['tok']}").json().get("state") != "Active": probs.append("token changed")
    if t.raw("GET", f"/restores/{F['restore']}").status_code != 200: probs.append("restore gone")
    return probs


@scenario("authz/8.1 tenant B cannot reach tenant A objects")
def tenancy():
    fixture(); tb = F["tb"]; bad = []; n = 0
    for label, m, p, body in probes():
        for pref, want in ((TB, (403, 404)), (TA, (403,))):
            r = call(tb.s, m, f"/api/tenants/{pref}{p}", body); n += 1
            if pref == TB and label in EMPTY_LIST and r.status_code == 200 and not leaks(r.text) and is_empty(r.json()):
                OBS.append(f"{label} answers 200 with an empty list for a foreign id (no data, but not 404)"); continue
            if r.status_code not in want or leaks(r.text):
                bad.append(f"{label} via {'B' if pref == TB else 'A'} prefix -> {r.status_code} leak={leaks(r.text)} {r.text[:120]}")
    # tenant-less routes that take an object id
    extra = [("GET", f"/api/v1/backups/jobs/{F['job']['id']}/health", None), ("GET", f"/api/v1/observability/health-summary?tenantId={TA}", None),
             ("GET", f"/api/v1/storage/connections/{F['conn']}/capacity", None), ("POST", f"/api/v1/storage/connections/{F['conn']}/probe", {}),
             ("POST", f"/api/backups/runs/{F['run']}/pause", {}), ("POST", f"/api/backups/runs/{F['run']}/resume", {})]
    for m, p, body in extra:
        r = call(tb.s, m, p, body); n += 1
        if r.status_code not in (403, 404) or leaks(r.text): bad.append(f"{m} {p.split('?')[0]} -> {r.status_code} {r.text[:120]}")
    # list endpoints: via B prefix only B's own (empty) data, via A prefix refused
    for p in LISTS:
        r = call(tb.s, "GET", f"/api/tenants/{TB}{p}"); n += 1
        if r.status_code != 200 or leaks(r.text): bad.append(f"list {p} via B -> {r.status_code} leak={leaks(r.text)}")
        r = call(tb.s, "GET", f"/api/tenants/{TA}{p}"); n += 1
        if r.status_code != 403: bad.append(f"list {p} via A prefix -> {r.status_code}")
    for p in ("/api/me/tenants",):
        r = call(tb.s, "GET", p)
        if TA in r.text: bad.append("/api/me/tenants lists tenant A")
    bad += [f"INTEGRITY {x}" for x in intact()]
    check(not bad, f"{len(bad)}/{n} violations: " + " | ".join(bad[:12]))
    return {"requests": n, "probes": len(probes()), "observations": OBS}


# ---- 8.2 roles -------------------------------------------------------------------------------------------------------------------------------------------
V, O, A, OWN, NONE = 1, 2, 3, 4, 99  # minimum role rank: Viewer < Operator < Admin < Owner; NONE = platform-only
RANK = {"Viewer": 1, "Operator": 2, "Admin": 3, "Owner": 4}


def role_matrix():
    g = guid()
    return [
        ("agents", "GET", "/agents", None, V), ("agents", "GET", f"/agents/{g}", None, V), ("agents", "POST", f"/agents/{g}/revoke", {}, A),
        ("agents", "POST", f"/agents/{g}/database-logins", {"userName": "u", "password": "p"}, O), ("agents", "GET", "/discovery/instances", None, V),
        ("agents", "PUT", f"/discovery/agents/{g}/settings", {}, A),
        ("enrollment", "GET", "/agent-enrollment", None, A), ("enrollment", "PUT", "/agent-enrollment", {"enabled": True}, A),
        ("enrollment", "POST", "/agent-enrollment-tokens", {"lifetimeMinutes": 5}, A), ("enrollment", "GET", "/agent-enrollment-tokens", None, A),
        ("enrollment", "DELETE", f"/agent-enrollment-tokens/{g}", None, A),
        ("connections", "GET", "/storage-connections", None, V), ("connections", "GET", f"/storage-connections/{g}", None, V), ("connections", "POST", "/storage-connections", {}, A),
        ("connections", "PUT", f"/storage-connections/{g}", {}, A), ("connections", "DELETE", f"/storage-connections/{g}", None, A),
        ("connections", "POST", f"/storage-connections/{g}/test", {}, A), ("connections", "GET", f"/storage-connections/{g}/capacity", None, V),
        ("jobs", "GET", "/backup-jobs", None, V), ("jobs", "POST", "/backup-jobs", {}, O), ("jobs", "PUT", f"/backup-jobs/{g}", {}, O),
        ("jobs", "PUT", f"/backup-jobs/{g}/retention", {}, A), ("jobs", "POST", f"/backup-jobs/{g}/enable", {}, O), ("jobs", "PUT", f"/backup-jobs/{g}/schedule", {}, O),
        ("jobs", "GET", f"/backup-jobs/{g}/health", None, V),
        ("runs", "GET", "/backup-runs", None, V), ("runs", "GET", f"/backup-runs/{g}", None, V), ("runs", "POST", f"/backup-jobs/{g}/runs", {}, O),
        ("runs", "POST", f"/backup-runs/{g}/cancel", {}, O), ("runs", "POST", f"/backup-runs/{g}/retry", {}, O),
        ("restores", "GET", "/restores", None, V), ("restores", "POST", f"/backup-artifacts/{g}/restore", {}, O), ("restores", "POST", f"/backup-artifacts/{g}/restore-precheck", {}, O),
        ("restores", "GET", f"/backup-artifacts/{g}/restore-targets", None, V), ("restores", "POST", f"/restores/{g}/cancel", {}, O),
        ("restores", "POST", f"/restores/{g}/rollback-removal", {}, A),
        ("artifacts", "GET", "/backup-artifacts", None, V), ("artifacts", "GET", f"/backup-artifacts/{g}", None, V), ("artifacts", "DELETE", f"/backup-artifacts/{g}", None, A),
        ("artifacts", "POST", f"/backup-artifacts/{g}/cancel-delete", {}, A), ("artifacts", "POST", f"/backup-artifacts/{g}/repairs", {}, O),
        ("artifacts", "GET", f"/backup-artifacts/{g}/download-availability", None, A), ("artifacts", "POST", f"/backup-artifacts/{g}/downloads", {}, A),
        ("notifications", "GET", "/notifications/policy", None, A), ("notifications", "PUT", "/notifications/policy", {}, A), ("notifications", "GET", "/notifications/channels", None, A),
        ("notifications", "POST", "/notifications/channels", {}, A), ("notifications", "DELETE", f"/notifications/channels/{g}", None, A),
        ("notifications", "POST", f"/notifications/channels/{g}/test", {}, A), ("notifications", "GET", "/notifications/deliveries", None, A),
        ("notifications", "POST", f"/notifications/channels/{g}/owner-security-recipient", {"enabled": False}, OWN),
        ("quota/bandwidth", "GET", "/storage-quota", None, V), ("quota/bandwidth", "GET", "/upload-bandwidth", None, V), ("quota/bandwidth", "PUT", "/upload-bandwidth/tenant", {}, A),
        ("quota/bandwidth", "POST", "/bandwidth-profiles", {}, A), ("quota/bandwidth", "GET", "/bandwidth-profiles", None, V),
        ("encryption", "GET", "/encryption", None, A), ("encryption", "PUT", "/encryption/recovery-key", {}, A), ("encryption", "GET", "/artifact-break-glass", None, A),
        ("reports", "GET", "/reports/backups", None, V), ("reports", "GET", "/dashboard", None, V), ("reports", "GET", "/context", None, V),
    ]


PLATFORM_ROUTES = [("GET", "/api/platform/users"), ("POST", "/api/platform/users"), ("GET", "/api/platform/tenants"), ("POST", f"/api/platform/tenants/{TA}/memberships"),
                   ("PUT", f"/api/platform/tenants/{TA}/memberships/{{uid}}"), ("DELETE", f"/api/platform/tenants/{TA}/memberships/{{uid}}"), ("GET", f"/api/platform/tenants/{TA}/memberships"),
                   ("GET", "/api/platform/audit"), ("GET", "/api/platform/audit/integrity"), ("GET", "/api/platform/summary"), ("GET", "/api/platform/agents"),
                   ("GET", "/api/platform/plans"), ("PUT", f"/api/platform/tenants/{TA}/agent-enrollment"), ("GET", "/api/platform/notification-transports"),
                   ("PUT", "/api/platform/notification-transports/email"), ("GET", "/api/platform/storage-connections"), ("GET", "/api/platform/quota-policies"),
                   ("GET", f"/api/platform/tenants/{TA}/quota"), ("GET", f"/api/platform/tenants/{TA}/wrap-keys"), ("GET", "/api/platform/artifact-break-glass"),
                   ("POST", "/api/platform/agent-updates"), ("PUT", f"/api/platform/tenants/{TA}/subscription")]


def make_user(prefix):
    P = platform(); login = f"e2e-role-{prefix}-{uuid.uuid4().hex[:6]}"; pw = secrets.token_urlsafe(18) + "Aa1!"
    r = call(P, "POST", "/api/platform/users", {"loginIdentifier": login, "displayName": f"E2E role {prefix}", "password": pw})
    check(r.status_code == 201, f"create user {r.status_code} {r.text[:200]}")
    return login, pw, r.json().get("userId") or r.json().get("id")


@scenario("authz/8.2 role matrix Owner/Admin/Operator/Viewer per endpoint group")
def roles():
    P = platform(); users = {}; bad = []; n = 0; created_tokens = []; summary = {}
    try:
        for role in RANK:
            login, pw, uid = make_user(role.lower())
            r = call(P, "POST", f"/api/platform/tenants/{TA}/memberships", {"userId": uid, "role": role})
            check(r.status_code == 201, f"membership {role} {r.status_code} {r.text[:200]}")
            users[role] = (uid, api.login(login, pw))
        for role, (uid, s) in users.items():
            ctx = call(s, "GET", f"/api/tenants/{TA}/context").json()
            summary[role] = {"context": ctx["membershipRole"], "allowed": 0, "denied": 0}
            for group, m, p, body, need in role_matrix():
                r = call(s, m, f"/api/tenants/{TA}{p}", body); n += 1
                allowed = RANK[role] >= need
                if m == "POST" and p == "/agent-enrollment-tokens" and r.status_code == 201: created_tokens.append(r.json()["tokenId"])
                if allowed and (r.status_code in (401, 403) or r.status_code >= 500): bad.append(f"{role} should be ALLOWED {group} {m} {p} -> {r.status_code} {r.text[:100]}")
                if not allowed and r.status_code != 403: bad.append(f"{role} should be DENIED {group} {m} {p} -> {r.status_code} {r.text[:100]}")
                summary[role]["allowed" if allowed else "denied"] += 1
            for m, p in PLATFORM_ROUTES:  # no tenant role grants platform administration
                r = call(s, m, p.format(uid=uid), {}); n += 1
                if r.status_code != 403: bad.append(f"{role} platform {m} {p} -> {r.status_code}")
        # live role change and membership removal must apply to an existing session without re-login
        uid, vs = users["Viewer"]
        check(call(vs, "POST", f"/api/tenants/{TA}/backup-jobs", {}).status_code == 403, "viewer could create job")
        call(P, "PUT", f"/api/platform/tenants/{TA}/memberships/{uid}", {"role": "Operator"})
        st = call(vs, "POST", f"/api/tenants/{TA}/backup-jobs", {}).status_code
        if st in (401, 403): bad.append(f"role change Viewer->Operator not live: {st}")
        call(P, "DELETE", f"/api/platform/tenants/{TA}/memberships/{uid}")
        st = call(vs, "GET", f"/api/tenants/{TA}/backup-jobs").status_code
        if st != 403: bad.append(f"removed membership still has access: {st}")
    finally:
        for role, (uid, _) in users.items(): call(P, "DELETE", f"/api/platform/tenants/{TA}/memberships/{uid}")
        for tid in created_tokens: call(F["t"].s if "t" in F else lib.Tenant("a").s, "DELETE", f"/api/tenants/{TA}/agent-enrollment-tokens/{tid}")
    check(not bad, f"{len(bad)}/{n} wrong: " + " | ".join(bad[:12]))
    return {"requests": n, "roles": summary}


# ---- 8.3 / 8.4 hardening -----------------------------------------------------------------------------------------------------------------------------
@scenario("authz/8.3 unauthenticated and CSRF-less requests rejected")
def unauth_csrf():
    bad = []; anon = requests.Session(); anon.verify = False
    tokens = anon.get(BASE + "/api/auth/antiforgery").json(); anon.headers[tokens["headerName"]] = tokens["requestToken"]
    for m, p in [("GET", f"/api/tenants/{TA}/agents"), ("GET", f"/api/tenants/{TA}/backup-jobs"), ("POST", f"/api/tenants/{TA}/backup-jobs"), ("GET", "/api/me"),
                 ("GET", "/api/me/tenants"), ("GET", "/api/platform/users"), ("GET", "/api/platform/audit"), ("POST", "/api/auth/logout"),
                 ("GET", f"/api/v1/backups/jobs/{guid()}/health"), ("POST", f"/api/backups/runs/{guid()}/pause"), ("GET", f"/api/tenants/{TA}/context")]:
        r = call(anon, m, p, {} if m == "POST" else None)
        if r.status_code != 401: bad.append(f"anonymous {m} {p} -> {r.status_code}")
    # login without antiforgery header/cookie
    bare = requests.Session(); bare.verify = False
    r = bare.post(BASE + "/api/auth/login", json={"loginIdentifier": "x", "password": "y"})
    if r.status_code != 400: bad.append(f"login without CSRF -> {r.status_code}")
    # authenticated session but state-changing request without / with a bad antiforgery header
    t = F["t"] if "t" in F else lib.Tenant("a"); s = t.s; target = f"/api/tenants/{TA}/backup-jobs/{guid()}/disable"
    r = call(s, "POST", target, {}); ok_status = r.status_code
    if ok_status != 404: bad.append(f"valid CSRF baseline -> {ok_status} (expected 404 for unknown job)")
    for label, sess_, hdr in (("missing header", no_csrf(s), None), ("garbage header", no_csrf(s), "garbage"),
                              ("token of another session", no_csrf(s), lib.Tenant("b").s.headers["X-CSRF-TOKEN"])):
        r = call(sess_, "POST", target, {}, {"X-CSRF-TOKEN": hdr} if hdr else None)
        if r.status_code != 400 or "antiforgery" not in r.text.lower(): bad.append(f"{label} -> {r.status_code} {r.text[:80]}")
    for m in ("PUT", "DELETE"):
        r = call(no_csrf(s), m, f"/api/tenants/{TA}/backup-jobs/{guid()}/schedule", {})
        if r.status_code != 400: bad.append(f"{m} without CSRF -> {r.status_code}")
    r = call(no_csrf(s), "POST", "/api/auth/logout")
    if r.status_code != 400: bad.append(f"logout without CSRF -> {r.status_code}")
    check(not bad, " | ".join(bad))
    return {"anonymous_routes": 11}


@scenario("authz/8.4 platform-admin routes refused to tenant users; admin has no implicit tenant role")
def platform_only():
    bad = []; owner = lib.Tenant("a").s
    for m, p in PLATFORM_ROUTES:
        r = call(owner, m, p.format(uid=guid()), {})
        if r.status_code != 403: bad.append(f"owner-a {m} {p} -> {r.status_code}")
    me = call(owner, "GET", "/api/me").json()
    check(me.get("isPlatformAdmin") is False, "owner-a reported as platform admin")
    P = platform()
    adm = call(P, "GET", "/api/me").json(); check(adm.get("isPlatformAdmin") is True, "admin not platform admin")
    for p in ("/agents", "/backup-jobs", "/notifications/channels", "/storage-connections"):
        r = call(P, "GET", f"/api/tenants/{TA}{p}")
        if r.status_code != 403: bad.append(f"platform admin w/o membership GET {p} -> {r.status_code} (tenant data readable through platform role)")
    check(not bad, " | ".join(bad))
    return {"platform_routes": len(PLATFORM_ROUTES)}


@scenario("authz/8.3 logout invalidates the session server-side")
def logout_invalidates():
    login, pw, uid = make_user("logout"); s = api.login(login, pw)
    check(call(s, "GET", "/api/me").status_code == 200, "fresh session rejected")
    cookies = s.cookies.copy(); hdrs = dict(s.headers)
    check(call(s, "POST", "/api/auth/logout").status_code == 204, "logout failed")
    check(call(s, "GET", "/api/me").status_code == 401, "same session still valid after logout")
    replay = requests.Session(); replay.verify = False; replay.cookies.update(cookies); replay.headers.update(hdrs)
    r = call(replay, "GET", "/api/me"); check(r.status_code == 401, f"replayed cookie accepted after logout: {r.status_code}")
    r = call(replay, "GET", f"/api/tenants/{TA}/agents"); check(r.status_code == 401, f"replayed cookie on tenant route: {r.status_code}")
    return {"user": login}


@scenario("authz/8.3 wrong-password handling, no user enumeration, cookie flags")
def login_hardening():
    login, pw, uid = make_user("lockout"); notes = {}
    anon = requests.Session(); anon.verify = False
    def attempt(l, p):
        t = anon.get(BASE + "/api/auth/antiforgery").json(); h = {t["headerName"]: t["requestToken"]}
        t0 = time.perf_counter(); r = anon.post(BASE + "/api/auth/login", json={"loginIdentifier": l, "password": p}, headers=h); return r, time.perf_counter() - t0
    known, unknown = [], []
    ref = None
    for i in range(4):
        r1, d1 = attempt(login, "Wrong-" + secrets.token_urlsafe(8) + "1!"); r2, d2 = attempt(f"e2e-nouser-{uuid.uuid4().hex[:8]}", "Wrong-" + secrets.token_urlsafe(8) + "1!")
        known.append(d1); unknown.append(d2)
        sig1 = (r1.status_code, r1.text, r1.headers.get("content-type")); sig2 = (r2.status_code, r2.text, r2.headers.get("content-type"))
        check(sig1 == sig2, f"known vs unknown user responses differ: {sig1} vs {sig2}")
        check("Set-Cookie" not in r1.headers or "Auth" not in r1.headers.get("Set-Cookie", ""), "auth cookie set on failed login")
    notes["status_wrong_password"] = r1.status_code; notes["error_text"] = r1.json().get("error")
    notes["median_ms_known_user"] = round(statistics.median(known) * 1000); notes["median_ms_unknown_user"] = round(statistics.median(unknown) * 1000)
    statuses = []
    t0 = time.time()
    for i in range(10):
        r, _ = attempt(login, "Wrong-" + secrets.token_urlsafe(8) + "1!"); statuses.append(r.status_code)
    notes["10_more_failures"] = {str(c): statuses.count(c) for c in set(statuses)}; notes["seconds"] = round(time.time() - t0, 1)
    check(statuses.count(401) == 1 and statuses.count(429) == 9, f"throttle: {notes['10_more_failures']}")
    check("Retry-After" in r.headers, "429 without Retry-After")
    locked, _ = attempt(login, pw)
    check(locked.status_code == 429, f"correct password accepted while locked: {locked.status_code}")
    login2, pw2, _ = make_user("cookie")
    ok, _ = attempt(login2, pw2); check(ok.status_code in (200, 204), f"login of a healthy account: {ok.status_code}")
    notes["correct_password_status"] = ok.status_code
    cookie = ok.headers.get("set-cookie", "")
    notes["cookie_flags"] = {k: (k.lower() in cookie.lower()) for k in ("HttpOnly", "Secure", "SameSite=", "__Host-")}
    notes["auth_cookie_samesite"] = (re.search(r"samesite=(\w+)", cookie, re.I) or [None, None])[1]
    check(all(notes["cookie_flags"].values()), f"auth cookie flags: {notes['cookie_flags']}")
    # malformed input must not be a 500 or reveal anything
    for body in ({}, {"loginIdentifier": None, "password": None}, {"loginIdentifier": ["a"], "password": 1}, {"loginIdentifier": "a" * 5000, "password": "b" * 5000}):
        r, _ = attempt(None, None) if False else (anon.post(BASE + "/api/auth/login", json=body, headers={"X-CSRF-TOKEN": anon.get(BASE + "/api/auth/antiforgery").json()["requestToken"]}), 0)
        check(r.status_code in (400, 401), f"malformed login body -> {r.status_code}")
    locked = statuses.count(429) + statuses.count(423)
    if ok.status_code == 200 and not locked:
        notes["finding"] = "no lockout or throttling: 46 consecutive wrong passwords for one account were all answered 401 and the account still accepted the right password"
    check(ok.status_code in (200, 204), f"login after failures: {ok.status_code}")
    return notes


# ---- 1.4 enrollment tokens -----------------------------------------------------------------------------------------------------------------------------
def token_state(t, tid):
    r = t.raw("GET", f"/agent-enrollment-tokens/{tid}"); return r.status_code, (r.json().get("state") if r.status_code == 200 else None)


SYNTH_AGENTS = []


@scenario("enroll/1.4 token is single use; invalid request does not burn it")
def enroll_single_use():
    t = F["t"]; tok = t.call("POST", "/agent-enrollment-tokens", {"lifetimeMinutes": 30}); host = f"e2e-synth-{uuid.uuid4().hex[:6]}"
    bad = enroll(tok["token"], host, platform="Plan9"); check(bad.status_code == 400 and err(bad) == "invalid_request", f"bad platform -> {bad.status_code} {bad.text[:100]}")
    check(token_state(t, tok["tokenId"])[1] == "Active", "token consumed by an invalid request")
    r = enroll(tok["token"], host); check(r.status_code == 201, f"first enroll {r.status_code} {r.text[:150]}"); aid = r.json()["agentId"]; SYNTH_AGENTS.append(aid)
    check("credential" in r.json() and r.json()["credential"].get("secret"), "no credential returned")
    r2 = enroll(tok["token"], host + "-again"); check(r2.status_code == 401 and err(r2) == "enrollment_token_consumed", f"second enroll {r2.status_code} {r2.text[:150]}")
    r3 = enroll(tok["token"], host, installation=guid()); check(r3.status_code == 401, f"third enroll {r3.status_code}")
    st = t.call("GET", f"/agent-enrollment-tokens/{tok['tokenId']}")
    check(st["state"] == "Consumed" and st["agentId"] == aid, f"token status {st}")
    check(tok["tokenId"] not in [x["tokenId"] for x in t.call("GET", "/agent-enrollment-tokens")], "consumed token still listed as active")
    rr = t.raw("DELETE", f"/agent-enrollment-tokens/{tok['tokenId']}")
    return {"consumed_error": err(r2), "revoke_consumed_status": rr.status_code}


@scenario("enroll/1.4 revoked token cannot enroll; malformed and unknown tokens indistinguishable")
def enroll_revocation():
    t = F["t"]; tok = t.call("POST", "/agent-enrollment-tokens", {"lifetimeMinutes": 30})
    check(t.raw("DELETE", f"/agent-enrollment-tokens/{tok['tokenId']}").status_code == 204, "revoke failed")
    r = enroll(tok["token"], "e2e-synth-revoked"); check(r.status_code == 401, f"revoked token enrolled: {r.status_code} {r.text[:150]}")
    revoked_err = err(r)
    st = token_state(t, tok["tokenId"])
    check(tok["tokenId"] not in [x["tokenId"] for x in t.call("GET", "/agent-enrollment-tokens")], "revoked token still listed")
    forged = tok["token"][:-4] + ("AAAA" if not tok["token"].endswith("AAAA") else "BBBB")
    errs = {"revoked": revoked_err, "unknown": err(enroll(f"{guid()}.{'A' * 43}", "x")), "malformed": err(enroll("not-a-token", "x")), "empty": err(enroll("", "x")),
            "wrong-secret": err(enroll(forged, "x"))}
    check(len(set(errs.values())) == 1 and revoked_err == "invalid_enrollment_token", f"error codes distinguish token states: {errs}")
    again = t.raw("DELETE", f"/agent-enrollment-tokens/{tok['tokenId']}")
    return {"errors": errs, "status_after_revoke": st, "second_revoke_status": again.status_code}


@scenario("enroll/1.4 token is bound to its tenant; other tenant cannot see/revoke it")
def enroll_tenant_binding():
    t, tb = F["t"], F["tb"]; tok = t.call("POST", "/agent-enrollment-tokens", {"lifetimeMinutes": 30}); host = f"e2e-synth-bind-{uuid.uuid4().hex[:6]}"
    check(tb.raw("DELETE", f"/agent-enrollment-tokens/{tok['tokenId']}").status_code == 404, "tenant B could revoke A's token")
    check(tb.raw("GET", f"/agent-enrollment-tokens/{tok['tokenId']}").status_code == 404, "tenant B could read A's token")
    check(tok["tokenId"] not in json.dumps(tb.call("GET", "/agent-enrollment-tokens")), "A's token listed for B")
    check(token_state(t, tok["tokenId"])[1] == "Active", "A's token changed by B")
    r = enroll(tok["token"], host); check(r.status_code == 201, f"enroll {r.status_code} {r.text[:150]}"); aid = r.json()["agentId"]; SYNTH_AGENTS.append(aid)
    check(aid in [a["agentId"] for a in t.call("GET", "/agents")], "agent not in tenant A")
    check(aid not in json.dumps(tb.call("GET", "/agents")), "agent visible to tenant B")
    check(tb.raw("GET", f"/agents/{aid}").status_code == 404, "tenant B can read the agent")
    return {"agent_tenant": "A"}


@scenario("enroll/1.4 disabled enrollment policy: no tokens, enrollment_disabled")
def enroll_disabled():
    tb = F["tb"]; tok = None
    try:
        tok = tb.call("POST", "/agent-enrollment-tokens", {"lifetimeMinutes": 30})
        tb.call("PUT", "/agent-enrollment", {"enabled": False})
        r = tb.raw("POST", "/agent-enrollment-tokens", {}); check(r.status_code == 403 and r.json().get("error") == "enrollment_disabled", f"token create while disabled {r.status_code} {r.text[:120]}")
        e = enroll(tok["token"], "e2e-synth-disabled"); check(e.status_code == 403 and err(e) == "enrollment_disabled", f"enroll while disabled {e.status_code} {e.text[:150]}")
        check(token_state(tb, tok["tokenId"])[1] == "Active", "token consumed although enrollment was disabled")
        tb.call("PUT", "/agent-enrollment", {"enabled": True})
    finally:
        tb.raw("PUT", "/agent-enrollment", {"enabled": True})
        if tok: tb.raw("DELETE", f"/agent-enrollment-tokens/{tok['tokenId']}")
    check(tb.call("GET", "/agents") == [], "an agent was enrolled into tenant B")
    return {"tenant": "B"}


@scenario("enroll/1.4 lifetime bounds enforced (min 5 min, max 7 days)")
def enroll_lifetime_bounds():
    t = F["t"]; res = {}
    for m in (-5, 0, 1, 4, 10081, 100000):
        r = t.raw("POST", "/agent-enrollment-tokens", {"lifetimeMinutes": m}); res[m] = r.status_code
        if r.status_code == 201: t.raw("DELETE", f"/agent-enrollment-tokens/{r.json()['tokenId']}")
    check(all(res[m] == 400 for m in (-5, 0, 1, 4, 10081, 100000)), f"out-of-range lifetimes accepted: {res}")
    for m in (5, 10080):
        r = t.raw("POST", "/agent-enrollment-tokens", {"lifetimeMinutes": m}); res[m] = r.status_code
        if r.status_code == 201: t.raw("DELETE", f"/agent-enrollment-tokens/{r.json()['tokenId']}")
    check(res[5] == 201 and res[10080] == 201, f"boundary lifetimes rejected: {res}")
    return {"status_by_minutes": res}


EXPIRY = {}


def expiry_worker():
    """Started in the background at the beginning of the run; a 5 minute token needs 5+ minutes to expire."""
    @scenario("enroll/1.4 expired token is refused (5 min lifetime)")
    def body():
        t = F["t"]; tok = t.call("POST", "/agent-enrollment-tokens", {"lifetimeMinutes": TOKEN_LIFETIME_MIN})
        early = enroll(tok["token"], "x", platform="Plan9"); check(early.status_code == 400, f"token not accepted before expiry: {early.status_code}")
        left = (TOKEN_LIFETIME_MIN * 60) + 8
        time.sleep(left)
        r = enroll(tok["token"], "e2e-synth-expired"); check(r.status_code == 401 and err(r) == "enrollment_token_expired", f"after expiry: {r.status_code} {r.text[:150]}")
        st = token_state(t, tok["tokenId"])
        check(tok["tokenId"] not in [x["tokenId"] for x in t.call("GET", "/agent-enrollment-tokens")], "expired token still listed active")
        return {"token_state_after_expiry": st}
    body()


def cleanup_synthetic():
    t = F["t"]; done = {}
    for aid in [F["synth"]] + SYNTH_AGENTS:
        r = t.raw("POST", f"/agents/{aid}/revoke", {}); done[aid[:8]] = r.status_code
    return done


def teardown():
    """Removes everything this module created in tenant A (names start with e2e-authz): synthetic agents revoked, channels/connections deleted, jobs disabled, artifacts deleted."""
    t = lib.Tenant("a"); out = {}
    for a in t.call("GET", "/agents"):
        if a["hostName"].startswith("e2e-authz-synth") or a["hostName"].startswith("e2e-synth"):
            if a["status"] == "Active": t.raw("POST", f"/agents/{a['agentId']}/revoke", {})
    for c in t.call("GET", "/notifications/channels")["items"]:
        if c["name"].startswith(("e2e-authz", "e2e-mail", "e2e-hook", "e2e-ghost")): t.raw("DELETE", f"/notifications/channels/{c['id']}")
    for c in t.call("GET", "/storage-connections")["items"]:
        if c["name"].startswith("e2e-authz"): out["conn " + c["name"]] = t.raw("DELETE", f"/storage-connections/{c['id']}").status_code
    for j in t.call("GET", "/backup-jobs"):
        if j["name"].startswith("e2e-authz") and j["enabled"]: t.raw("POST", f"/backup-jobs/{j['id']}/disable", {})
    for a in t.call("GET", "/backup-artifacts?pageSize=100")["items"]:
        if (a.get("jobName") or "").startswith("e2e-authz") and a.get("state") == "Available": out["artifact " + a["id"][:8]] = t.raw("DELETE", f"/backup-artifacts/{a['id']}").status_code
    return out


# ---- 9.2 audit -------------------------------------------------------------------------------------------------------------------------------------------
def audit_all(P, tenant=None, pages=6):
    out = []
    for page in range(1, pages + 1):
        r = call(P, "GET", f"/api/platform/audit?pageSize=100&page={page}" + (f"&tenantId={tenant}" if tenant else ""))
        check(r.status_code == 200, f"audit {r.status_code}")
        items = r.json()["items"]; out += items
        if len(items) < 100: break
    return out


@scenario("audit/9.2 platform audit has entries for performed actions; tenants isolated")
def audit():
    P = platform(); t = F["t"]; tb = F["tb"]; bad = []
    ev_a = audit_all(P, TA); ev_b = audit_all(P, TB); ev_all = audit_all(P, None, 10)
    actions_a = sorted({e["action"] for e in ev_a})
    check(all(e["tenantId"] == TA for e in ev_a), "tenant filter A returned other tenants' rows")
    check(all(e["tenantId"] == TB for e in ev_b), "tenant filter B returned other tenants' rows")
    ids_a = {e["id"] for e in ev_a}; check(not (ids_a & {e["id"] for e in ev_b}), "same audit row under both tenants")
    blob_b = json.dumps(ev_b)
    for m in F["markers"]:
        if m in blob_b: bad.append(f"tenant B audit contains A marker {m[:12]}")
    owner_a = ST["a"]["userId"]; admin_id = call(P, "GET", "/api/me").json()["userId"]
    def has(substr, actor=None, target=None, evs=ev_a):
        return [e for e in evs if substr in e["action"].lower() and (actor is None or e["actorUserId"] == actor) and (target is None or target in json.dumps(e))]
    checks = {
        "backup job created by owner-a": has("job", owner_a), "enrollment token created by owner-a": has("token", owner_a),
        "enrollment token revoked by owner-a": has("revoke", owner_a), "storage connection created by owner-a": has("connection", owner_a),
        "notification channel created by owner-a": has("channel", owner_a), "agent enrolled (synthetic)": has("enroll"),
        "membership added by platform admin": has("membership", admin_id), "role user created by admin": [e for e in ev_all if "user" in e["action"].lower() and e["actorUserId"] == admin_id],
        "login succeeded of owner-a": [e for e in ev_all if "login" in e["action"].lower() and "succeeded" in e["action"].lower() and e["actorUserId"] == owner_a],
        "login failed recorded (anonymous)": [e for e in ev_all if "loginfailed" in e["action"].lower().replace("_", "").replace(".", "").replace("-", "")],
        "logout recorded": [e for e in ev_all if "logout" in e["action"].lower()],
        "tenant B enrollment policy change by owner-b": has("enrollment", ST["b"]["userId"], evs=ev_b),
    }
    for k, v in checks.items():
        if not v: bad.append(f"missing: {k}")
    # tenant side: no tenant audit listing exists; owner may not read platform audit and may not see another tenant's
    r = call(tb.s, "GET", "/api/platform/audit"); check(r.status_code == 403, f"owner-b platform audit {r.status_code}")
    r = call(tb.s, "GET", f"/api/platform/audit?tenantId={TA}"); check(r.status_code == 403, f"owner-b platform audit tenant A {r.status_code}")
    for p in ("/audit", "/audit-events", "/audit-log"):
        r = call(tb.s, "GET", f"/api/tenants/{TB}{p}"); notes_status = r.status_code
    integ = call(P, "GET", "/api/platform/audit/integrity?limit=50").json()
    statuses = {}
    for e in integ["events"]: statuses[e["status"]] = statuses.get(e["status"], 0) + 1
    check(not bad, " | ".join(bad))
    return {"rows_a": len(ev_a), "rows_b": len(ev_b), "actions_a": actions_a, "integrity": {"signingEnabled": integ["signingEnabled"], **statuses}}


def main(only=None):
    fixture()
    th = None
    if not only or "expiry" in only or "enroll" in only:
        th = threading.Thread(target=expiry_worker, daemon=True); th.start()
    run = lambda name, fn: (not only or only in name) and fn()
    run("tenancy", tenancy); run("roles", roles); run("unauth", unauth_csrf); run("platform", platform_only); run("logout", logout_invalidates); run("login", login_hardening)
    for fn in (enroll_single_use, enroll_revocation, enroll_tenant_binding, enroll_disabled, enroll_lifetime_bounds):
        run("enroll", fn)
    if th: th.join()
    run("audit", audit)
    print("cleanup:", cleanup_synthetic(), teardown())


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else None)
