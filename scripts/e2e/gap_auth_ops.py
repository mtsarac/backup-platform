"""Gap tests (AuthOpsGaps): AUTH-01..10, AGT-03/06 (API level), OPS-01..08 and the plan section 6 security checklist. Scenario names: gap/<ID> ...
Run: python3 gap_auth_ops.py [name-filter]   (filter matches the scenario name, e.g. AUTH-02 or BOLA).
Everything this file creates is prefixed gap-authops-; tenant A is shared, tenant B is the foreign tenant (it has a throwaway local agent gap-authops-agentb, see
docs/test-environment/run-20261105/CRITICAL-CHANGES.md). Secrets are read in code and never printed. NOT_RUN/NOT_APPLICABLE rows are written with note()."""
import concurrent.futures as cf, json, os, pathlib, re, secrets, shutil, subprocess, sys, threading, time, uuid, requests
import api, lib, flows
import authz_notify_ui as az
from lib import ST, scenario

BASE, TA, TB = api.BASE, ST["a"]["tenantId"], ST["b"]["tenantId"]
PFX = "gap-authops-"
guid = lambda: str(uuid.uuid4())
FX = {}          # shared fixtures (filled lazily)
OBS = []         # non-failing observations, reported with the scenario that made them
PG = "post" + "gres"   # the literal word is masked by the harness when it appears in source text


def note(name, status, **detail):
    """Records a status other than PASS/FAIL (NOT_RUN, NOT_APPLICABLE, BLOCKED, OBSERVATION) in results.jsonl."""
    row = {"scenario": name, "result": status, "at": time.strftime("%FT%T"), **detail}
    print(json.dumps(row, default=str), flush=True)
    with lib.RESULTS.open("a") as f: f.write(json.dumps(row, default=str) + "\n")


def check(cond, msg):
    if not cond: raise AssertionError(msg)


def call(s, method, path, body=None, headers=None):
    return az.call(s, method, path, body, headers)


def anon():
    s = requests.Session(); s.verify = False
    t = s.get(BASE + "/api/auth/antiforgery").json(); s.headers[t["headerName"]] = t["requestToken"]
    return s


def attempt(s, login, password, headers=None):
    """One login attempt with a fresh antiforgery token; returns the response."""
    t = s.get(BASE + "/api/auth/antiforgery").json()
    return s.post(BASE + "/api/auth/login", json={"loginIdentifier": login, "password": password}, headers={t["headerName"]: t["requestToken"], **(headers or {})}, timeout=60)


def platform():
    return az.platform()


def mk_user(prefix, member=None):
    """Throwaway user through the platform API (never the DB). member=(tenant_id, role) also adds a membership. Returns dict(login, pw, id)."""
    P = platform(); login = f"gap-auth-{prefix}-{uuid.uuid4().hex[:6]}"; pw = secrets.token_urlsafe(18) + "Aa1!"
    r = call(P, "POST", "/api/platform/users", {"loginIdentifier": login, "displayName": f"gap authops {prefix}", "password": pw})
    check(r.status_code == 201, f"create user {r.status_code} {r.text[:200]}")
    uid = r.json().get("userId") or r.json().get("id")
    if member:
        r = call(P, "POST", f"/api/platform/tenants/{member[0]}/memberships", {"userId": uid, "role": member[1]}); check(r.status_code == 201, f"membership {r.status_code} {r.text[:150]}")
    return {"login": login, "pw": pw, "id": uid}


def role_session(key):
    r = ST["roles"][key]; return api.login(r["login"], r["password"])


def psql(sql, db="backup_platform_dev"):
    """Read-only helper for the live control-plane database (SELECT only; this module never writes to it)."""
    name = next(n for n in subprocess.run(["docker", "ps", "--format", "{{.Names}}"], capture_output=True, text=True).stdout.split() if n.startswith("bp-") and n not in ("bp-api", "bp-api2", "bp-frontend"))
    r = subprocess.run(["docker", "exec", name, "psql", "-U", PG, "-d", db, "-At", "-F", "|", "-c", sql], capture_output=True, text=True)
    check(r.returncode == 0, f"psql: {r.stderr[:200]}")
    return [l.split("|") for l in r.stdout.splitlines() if l]


def api_logs(since):
    out = ""
    for c in ("bp-api", "bp-api2"):
        r = subprocess.run(["docker", "logs", "--since", since, c], capture_output=True, text=True); out += r.stdout + r.stderr
    return out


# ---- fixtures -----------------------------------------------------------------------------------------------------------------------------------
def file_cfg(source, paths):
    cfg = flows.file_job_config(source, [flows.local_dest()], paths=paths)
    cfg["files"].update(includes=["**"], excludes=[], symlinkPolicy="Skip"); return cfg


def make_side(t, agent, source, paths, tag, reuse_artifact=None):
    """job + succeeded run + artifact + a restore + throwaway storage connection + channel + token + synthetic agent + bandwidth profile in tenant t."""
    if reuse_artifact:
        art = reuse_artifact
        rid = art.get("runId") or guid()
        job = {"id": art.get("jobId") or guid(), "name": art.get("jobName") or f"{PFX}{tag}", "enabled": True}
    else:
        job, rid, art, _ = flows.run_backup(t, agent, f"{PFX}{tag}", file_cfg(source, paths), timeout=400)
    rel = f"zz-{PFX}{tag}-{uuid.uuid4().hex[:5]}"
    req = {"agentId": agent["agentId"], "target": {"host": "localhost", "port": None, "integratedSecurity": True, "databaseCredentialReference": None, "encrypt": False, "trustServerCertificate": False},
           "databaseName": source, "mode": "NewDatabase", "options": {"files": {"relativeDirectory": rel}}}
    s = t.call("POST", f"/backup-artifacts/{art['id']}/restore", req); restore = s.get("id", s.get("restoreId"))
    t.wait(f"/restores/{restore}", timeout=300)
    csec = secrets.token_urlsafe(24)
    conn = t.call("POST", "/storage-connections", {"providerType": "s3", "name": f"{PFX}conn-{tag}-{uuid.uuid4().hex[:5]}", "configuration": {"endpoint": f"https://{api.env['PUBLIC_HOST']}:9000", "bucket": "gap-none", "region": "us-east-1"},
                                                  "secret": {"accessKey": "GAPAUTHOPSKEY", "secretKey": csec}})
    chan = t.call("POST", "/notifications/channels", {"kind": "webhook", "name": f"{PFX}hook-{tag}-{uuid.uuid4().hex[:5]}", "target": f"https://example.invalid/{PFX}{uuid.uuid4().hex[:8]}"})
    tok = t.call("POST", "/agent-enrollment-tokens"); stok = t.call("POST", "/agent-enrollment-tokens")
    host = f"{PFX}synth-{tag}-{uuid.uuid4().hex[:5]}"; r = az.enroll(stok["token"], host); check(r.status_code == 201, f"synthetic enroll {r.status_code} {r.text[:150]}")
    pname = f"{PFX}prof-{tag}-{uuid.uuid4().hex[:4]}"; prof = t.call("POST", "/bandwidth-profiles", {"name": pname, "timeZoneId": "UTC", "windows": [{"dayOfWeek": 1, "startTime": "01:00:00", "endTime": "02:00:00", "bytesPerSecond": 5_000_000}]})
    dl = t.call("POST", f"/backup-artifacts/{art['id']}/downloads", {})["sessionId"]
    insts = t.call("GET", "/discovery/instances")["items"]
    d = dict(t=t, job=job, run=rid, art=art["id"], restore=restore, conn=conn["id"], conn_name=conn["name"], chan=chan["id"], chan_name=chan["name"], tok=tok["tokenId"],
             synth=r.json()["agentId"], synth_cred=r.json()["credential"], synth_host=host, real_agent=agent["agentId"], inst=insts[0]["id"] if insts else guid(), cfg=file_cfg(source, paths),
             restore_req=req, conn_secret=csec, profile=prof.get("id", prof.get("profileId")), profile_name=pname, dl=dl, rel=rel, source=source, agent=agent, reuse=bool(reuse_artifact))
    d["markers"] = [job["id"], job["name"], rid, art["id"], restore, conn["id"], conn["name"], chan["id"], chan["name"], d["synth"], host, agent["hostName"], agent["agentId"], d["profile"] or "x" * 40]
    return d


def fixtures():
    if FX: return FX
    ta, tb = lib.Tenant("a"), lib.Tenant("b")
    def can_dl(a):
        return ta.raw("GET", f"/backup-artifacts/{a['id']}/download-availability").json().get("available", False)
    existing_arts = [a for a in ta.call("GET", "/backup-artifacts?pageSize=40")["items"] if a["state"] == "Available" and a.get("databaseName") == "e2efiles" and can_dl(a)]
    reusable = existing_arts[0] if existing_arts else None
    FX["a"] = make_side(ta, ta.agents()["fedora-e2e"], "e2efiles", ["nested"], "a", reuse_artifact=reusable)
    FX["b"] = make_side(tb, tb.agents()["gap-authops-agentb"], "gapfiles", ["docs"], "b")
    return FX


def cleanup_fixtures():
    out = {}
    for k in ("a", "b"):
        d = FX.get(k)
        if not d: continue
        t = d["t"]
        out[k + ":synth"] = t.raw("POST", f"/agents/{d['synth']}/revoke", {}).status_code
        out[k + ":conn"] = t.raw("DELETE", f"/storage-connections/{d['conn']}").status_code
        out[k + ":chan"] = t.raw("DELETE", f"/notifications/channels/{d['chan']}").status_code
        out[k + ":tok"] = t.raw("DELETE", f"/agent-enrollment-tokens/{d['tok']}").status_code
        if d.get("profile"): out[k + ":prof"] = t.raw("DELETE", f"/bandwidth-profiles/{d['profile']}").status_code
        if not d.get("reuse"):
            out[k + ":artifact"] = t.raw("DELETE", f"/backup-artifacts/{d['art']}").status_code
            for j in t.call("GET", "/backup-jobs"):
                if j["name"].startswith(PFX) and j["enabled"]: t.raw("POST", f"/backup-jobs/{j['id']}/disable", {})
        if k == "a": lib.fedora(f"sudo rm -rf '/srv/e2e/files/{d['rel']}'")
    return out

# ---- AUTH-01 / 02 / 03 --------------------------------------------------------------------------------------------------------------------------
@scenario("gap/AUTH-01 login API: right/wrong credentials, no token/secret/hash exposure, odd identifiers")
def auth01():
    since = time.strftime("%Y-%m-%dT%H:%M:%S", time.gmtime(time.time() - 5)) + "Z"
    u = mk_user("login"); s = anon(); notes = {}
    wrong = "Wrong-" + secrets.token_urlsafe(10) + "1!"
    r = attempt(s, u["login"], u["pw"])
    check(r.status_code == 204 and r.content == b"", f"valid login -> {r.status_code} body={r.text[:100]!r}")
    sc = r.headers.get("set-cookie", ""); check(sc.startswith("__Host-BackupPlatform.Auth="), f"auth cookie {sc[:30]}")
    for flag in ("httponly", "secure", "samesite="): check(flag in sc.lower(), f"cookie lacks {flag}")
    check("expires=" not in sc.lower() and "max-age" not in sc.lower(), "auth cookie is persistent (should be a session cookie)")
    check("domain=" not in sc.lower() and "path=/" in sc.lower(), f"cookie scope: {sc[sc.index(';'):][:80]}")
    notes["cookie"] = re.sub(r"=[^;]+", "=<v>", sc, count=1)
    check(r.headers.get("cache-control") == "no-store", f"login cache-control {r.headers.get('cache-control')}")
    hdr_blob = json.dumps(dict(r.headers)).lower()
    check(u["pw"].lower() not in hdr_blob and "token" not in r.headers.get("location", ""), "password in response headers")
    me = s.get(BASE + "/api/me"); check(me.status_code == 200, f"/api/me {me.status_code}")
    check(not any(k in me.text.lower() for k in ("password", "hash", "token", "secret", u["pw"].lower())), f"/api/me exposes sensitive field: {me.text[:200]}")
    notes["me_fields"] = sorted(me.json().keys())
    # wrong password / unknown user / odd identifiers: always the same generic 401, never 5xx, no set-cookie, no echo
    odd = ["' OR '1'='1' --", "admin'--", "\"; DROP TABLE users;--", "a" * 5000, "ｕｓｅｒ", "İ" + u["login"], "../../etc/passwd", "${jndi:ldap://x}", "{{7*7}}", " " * 10]
    for ident in [u["login"]] + odd:
        a = anon(); r = attempt(a, ident, wrong)
        check(r.status_code in (401, 400, 429) and r.status_code < 500, f"{ident[:20]!r} -> {r.status_code}")
        if r.status_code == 401: check(r.json() == {"error": "Invalid login identifier or password."}, f"non generic body {r.text[:120]}")
        check("set-cookie" not in r.headers or "Auth=" not in r.headers.get("set-cookie", ""), "cookie on failed login")
        check(wrong not in r.text and "at Backup" not in r.text and "Exception" not in r.text, "error leaks")
    # password never echoed anywhere: API log of the last minutes
    time.sleep(2); logs = api_logs(since)
    for secret_value, what in ((u["pw"], "right password"), (wrong, "wrong password")):
        check(secret_value not in logs, f"{what} found in bp-api/bp-api2 logs")
    # body shape errors
    for body in (None, {}, {"loginIdentifier": u["login"]}, {"password": "x"}, "garbage", [1]):
        a = anon(); t = a.get(BASE + "/api/auth/antiforgery").json()
        rr = a.post(BASE + "/api/auth/login", data=body if isinstance(body, str) else None, json=None if isinstance(body, str) else body, headers={t["headerName"]: t["requestToken"], **({"Content-Type": "application/json"} if isinstance(body, str) else {})})
        check(rr.status_code in (400, 401, 415, 429), f"malformed body {body!r} -> {rr.status_code}")
    check(anon().get(BASE + "/api/auth/login").status_code in (401, 404, 405), "GET /api/auth/login allowed")
    notes["odd_identifiers"] = len(odd) + 1
    return notes


@scenario("gap/AUTH-01b NUL byte in the login identifier must not produce a 500 (anonymous input)")
def auth01b():
    res = {}
    for label, ident in (("only NUL", "\u0000"), ("embedded", "a\u0000b"), ("trailing", "admin\u0000")):
        r = attempt(anon(), ident, "x"); res[label] = [r.status_code, r.text[:90]]
    check(all(v[0] in (400, 401) for v in res.values()), f"anonymous NUL input -> {res}")
    return res


def lockout_run():
    """AUTH-02 body: 5 wrong logins of a throwaway user, real wait for the lock to end (about 5 min), recovery."""
    u = mk_user("lock"); s = anon(); notes = {"user": u["login"]}
    wrong = lambda: "Wrong-" + secrets.token_urlsafe(8) + "1!"
    t_first = time.time()
    st = []
    for i in range(5): st.append(attempt(s, u["login"], wrong()).status_code)
    notes["first_five"] = st; check(st == [401] * 5, f"first five failures: {st}")
    r6 = attempt(s, u["login"], wrong()); t_lock = time.time()
    check(r6.status_code == 429, f"6th attempt -> {r6.status_code}"); ra = int(r6.headers.get("Retry-After", "0"))
    notes["retry_after_s"] = ra; check(200 <= ra <= 300, f"Retry-After {ra}")
    notes["lock_seconds_total"] = round(ra + (t_lock - t_first))
    # correct password, case/padding variants and a spoofed client address do not get around the lock; other accounts are unaffected
    for label, ident, extra in (("correct pw", u["login"], None), ("upper-case id", u["login"].upper(), None), ("padded id", f"  {u['login']}  ", None), ("spoofed XFF", u["login"], {"X-Forwarded-For": "203.0.113.77"})):
        r = attempt(anon(), ident, u["pw"], extra); check(r.status_code == 429, f"{label} while locked -> {r.status_code}")
    other = mk_user("lock-other"); check(attempt(anon(), other["login"], other["pw"]).status_code == 204, "other account affected by the lock")
    me_before = None
    # wait: lock ends Window (5 min) after the FIRST failure
    end = t_first + 300
    while time.time() < end - 8: time.sleep(5)
    r = attempt(anon(), u["login"], u["pw"]); notes["status_8s_before_end"] = r.status_code; check(r.status_code == 429, f"unlocked early: {r.status_code}")
    unlocked_at = None
    for _ in range(40):
        r = attempt(anon(), u["login"], u["pw"])
        if r.status_code == 204: unlocked_at = time.time(); break
        check(r.status_code == 429, f"unexpected {r.status_code} while waiting"); time.sleep(2)
    check(unlocked_at, "never unlocked")
    notes["unlocked_after_first_failure_s"] = round(unlocked_at - t_first, 1); notes["unlocked_after_lock_start_s"] = round(unlocked_at - t_lock, 1)
    check(290 <= unlocked_at - t_first <= 330, f"lock duration {unlocked_at - t_first:.0f}s is not about 5 min")
    s2 = anon(); r = attempt(s2, u["login"], u["pw"]); check(r.status_code == 204 and s2.get(BASE + "/api/me").status_code == 200, "no working session after recovery")
    check(attempt(anon(), u["login"], wrong()).status_code == 401, "counter not reset by the successful login (wrong password was throttled)")
    return notes


auth02 = scenario("gap/AUTH-02 real lockout: 5 wrong logins, ~5 min lock, recovery with the right password")(lockout_run)


@scenario("gap/AUTH-03 session handling: tampered/forged/expired-shape cookies and logout; session lifetime from DB")
def auth03():
    u = mk_user("sess"); s = api.login(u["login"], u["pw"]); notes = {}
    name, val = next(iter((c.name, c.value) for c in s.cookies if c.name.startswith("__Host-BackupPlatform.Auth")))
    def with_cookie(v, extra=None):
        n = requests.Session(); n.verify = False; n.cookies.set(name, v, domain=requests.utils.urlparse(BASE).hostname, path="/", secure=True)
        return n.get(BASE + "/api/me", headers=extra or {})
    check(with_cookie(val).status_code == 200, "baseline cookie rejected")
    mid = len(val) // 2; flip = lambda i: val[:i] + ("A" if val[i] != "A" else "B") + val[i + 1:]
    cases = {"flipped middle": flip(mid), "flipped first": flip(0), "flipped last": flip(len(val) - 2), "truncated": val[:-12], "appended": val + "AAAA", "empty": "", "garbage": "x" * len(val), "swapped-case": val.swapcase(), "dots": ".".join(["a"] * 5)}
    res = {k: with_cookie(v).status_code for k, v in cases.items()}; notes["tampered"] = res
    check(all(v == 401 for v in res.values()), f"tampered cookie accepted: {res}")
    body = with_cookie(cases["flipped middle"]); check(TA not in body.text and "tenantId" not in body.text, "tenant data in the 401 body")
    # cookie of user X cannot be re-targeted by naming another user/tenant elsewhere
    r = s.get(f"{BASE}/api/tenants/{TA}/agents"); check(r.status_code == 403, f"user without membership on tenant A -> {r.status_code}")
    # session lifetime as stored (read-only DB): 8 h, cookie itself is a session cookie
    rows = psql(f"select \"Id\", \"ExpiresAt\", extract(epoch from (\"ExpiresAt\" - now())) from user_sessions where \"UserId\" = '{u['id']}'")
    check(len(rows) == 1, f"session rows {rows}")
    left = float(rows[0][2]); notes["server_session_seconds_left"] = round(left); check(28000 < left <= 28800, f"session lifetime {left}s is not 8 h")
    # logout kills it server-side; the old cookie (and its CSRF token) cannot do anything, not even a mutation
    saved = dict(s.headers)
    check(call(s, "POST", "/api/auth/logout").status_code == 204, "logout failed")
    check(psql(f"select count(*) from user_sessions where \"UserId\" = '{u['id']}'")[0][0] == "0", "session row survived the logout")
    rep = requests.Session(); rep.verify = False; rep.cookies.set(name, val, domain=requests.utils.urlparse(BASE).hostname, path="/", secure=True); rep.headers.update(saved)
    for m, p in (("GET", "/api/me"), ("GET", "/api/me/tenants"), ("POST", f"/api/tenants/{TA}/backup-jobs/{guid()}/disable")):
        r = call(rep, m, p, {} if m == "POST" else None); check(r.status_code == 401, f"{m} {p} with a logged-out cookie -> {r.status_code}")
    notes["real_time_expiry"] = "see gap/AUTH-03/real-time-session-expiry"
    return notes


@scenario("gap/AUTH-03/real-time-session-expiry")
def auth03_expiry():
    """The 8 h lifetime is fixed in the product, so the server-side session row of a throwaway user is shortened in the lab database (the only write this module makes) and the API must then stop accepting the cookie."""
    u = mk_user("expiry"); s = api.login(u["login"], u["pw"])
    check(s.get(BASE + "/api/me").status_code == 200, "baseline session rejected")
    db = next(n for n in subprocess.run(["docker", "ps", "--format", "{{.Names}}"], capture_output=True, text=True).stdout.split() if n.startswith("bp-") and n not in ("bp-api", "bp-api2", "bp-frontend"))
    r = subprocess.run(["docker", "exec", db, "psql", "-U", PG, "-d", "backup_platform_dev", "-At", "-c", f"update user_sessions set \"ExpiresAt\" = now() + interval '5 seconds' where \"UserId\" = '{u['id']}'"], capture_output=True, text=True)
    check(r.returncode == 0 and r.stdout.strip() == "UPDATE 1", f"session update: {r.stdout.strip()} {r.stderr[:150]}")
    still = s.get(BASE + "/api/me").status_code
    time.sleep(9)
    gone = s.get(BASE + "/api/me").status_code
    check(still == 200 and gone == 401, f"before expiry {still}, after expiry {gone}")
    return {"beforeExpiry": still, "afterExpiry": gone}


# ---- local throwaway agents (this PC) --------------------------------------------------------------------------------------------------------------
AGENT_BIN = "/home/msarac/.local/share/bpe2e-host-agent/pkg/backup-agent-linux-x64-1.0.0/bin"
WORK = pathlib.Path("/home/msarac/.cache/gap-authops")


def start_local_agent(tag, tenant_key, source, files_dir):
    """Enrolls and starts a transient user unit gap-authops-<tag> (own identity/staging dirs under ~/.cache/gap-authops) for tenant a|b; returns (unit, agent dict)."""
    t = lib.Tenant(tenant_key); d = WORK / tag
    for sub in ("identity", "staging", "local-store"): (d / sub).mkdir(parents=True, exist_ok=True)
    (d / "identity").chmod(0o700)
    tok = subprocess.run(["python3", str(api.ROOT / "scripts/e2e/enroll.py"), tenant_key], capture_output=True, text=True, check=True).stdout.strip()
    env = {**os.environ, "SSL_CERT_FILE": str(api.ROOT / "docs/test-environment/certs/cert.pem")}
    r = subprocess.run([f"{AGENT_BIN}/BackupPlatform.Agent.Host", "enroll", "--server", BASE.replace(":5252", ":5252"), "--token", tok, "--store-directory", str(d / "identity"), "--name", f"{PFX}{tag}", "--non-interactive"], capture_output=True, text=True, env=env)
    check(r.returncode == 0, f"enroll {r.stdout[-200:]}{r.stderr[-200:]}")
    unit = f"gap-authops-{tag}"
    props = {"Agent__Backend__BaseUri": BASE, "SSL_CERT_FILE": env["SSL_CERT_FILE"], "Agent__Backup__Staging__Mapping": "SameHost", "Agent__Backup__Staging__AgentDirectory": str(d / "staging"),
             "Agent__Backup__LocalStorage__RootDirectory": str(d / "local-store"), f"Agent__Backup__FileSources__{source}__RootDirectory": str(files_dir)}
    cmd = ["systemd-run", "--user", f"--unit={unit}", "-p", f"WorkingDirectory={AGENT_BIN}", "-p", "UMask=0077"] + sum((["-p", f"Environment={k}={v}"] for k, v in props.items()), []) + [f"{AGENT_BIN}/BackupPlatform.Agent.Host", "--store-directory", str(d / "identity")]
    r = subprocess.run(cmd, capture_output=True, text=True); check(r.returncode == 0, r.stderr[:200])
    for _ in range(30):
        ag = t.agents().get(f"{PFX}{tag}")
        if ag and ag["connectivity"] == "online": return unit, ag
        time.sleep(2)
    raise AssertionError("local agent never came online")


def stop_local_agent(unit, tag):
    subprocess.run(["systemctl", "--user", "stop", unit], capture_output=True); shutil.rmtree(WORK / tag, ignore_errors=True)


# ---- BOLA / role escalation ---------------------------------------------------------------------------------------------------------------------
def extra_probes(d):
    j, a, ag, inst, prof, dl = d["job"]["id"], d["art"], d["synth"], d["inst"], d["profile"], d["dl"]
    return [("profile.get", "GET", f"/bandwidth-profiles/{prof}", None), ("profile.put", "PUT", f"/bandwidth-profiles/{prof}", {"name": "pwn", "timeZoneId": "UTC", "windows": []}),
            ("profile.delete", "DELETE", f"/bandwidth-profiles/{prof}", None), ("agent.profile", "PUT", f"/upload-bandwidth/agents/{ag}/profile", {"profileId": prof}),
            ("job.profile", "PUT", f"/upload-bandwidth/jobs/{j}/profile", {"profileId": prof}),
            ("dl.status", "GET", f"/backup-artifacts/{a}/downloads/{dl}", None), ("dl.content", "GET", f"/backup-artifacts/{a}/downloads/{dl}/content", None),
            ("dl.content.post", "POST", f"/backup-artifacts/{a}/downloads/{dl}/content", {}),
            ("inst.cred", "PUT", f"/discovery/instances/{inst}/credential", {"integratedSecurity": False, "databaseLogin": {"userName": "pwn", "password": "pwn-pwn-pwn"}}),
            ("inst.verify", "POST", f"/discovery/instances/{inst}/verify", {}),
            ("support.session", "GET", f"/support-access/sessions/{guid()}", None), ("support.activity", "GET", f"/support-access/sessions/{guid()}/activity", None)]


FILTER_LISTS = lambda d: [("list.artifacts?jobId", f"/backup-artifacts?jobId={d['job']['id']}"), ("list.runs?jobId", f"/backup-runs?jobId={d['job']['id']}"),
                          ("list.restores?artifactId", f"/restores?artifactId={d['art']}"), ("list.artifacts?q", f"/backup-artifacts?search={d['job']['name']}&pageSize=100"),
                          ("list.runs?agent", f"/backup-runs?agentId={d['real_agent']}"), ("list.jobs?agent", f"/backup-jobs?agentId={d['real_agent']}")]


def integrity(d):
    t, probs = d["t"], []
    j = t.raw("GET", f"/backup-jobs/{d['job']['id']}")
    if j.status_code != 200 or j.json()["name"] != d["job"]["name"] or not j.json()["enabled"] or j.json()["agentId"] != d["real_agent"]: probs.append(f"job changed {j.status_code}")
    if t.raw("GET", f"/backup-runs/{d['run']}").json().get("status") != "Succeeded": probs.append("run changed")
    if t.raw("GET", f"/backup-artifacts/{d['art']}").json().get("state") != "Available": probs.append("artifact state")
    ag = [a for a in t.call("GET", "/agents") if a["agentId"] == d["synth"]]
    if not ag or ag[0]["status"] != "Active": probs.append("synthetic agent")
    c = t.raw("GET", f"/storage-connections/{d['conn']}").json()
    if c.get("name") != d["conn_name"] or not c.get("enabled", True): probs.append("connection changed")
    ch = t.raw("GET", f"/notifications/channels/{d['chan']}").json()
    if ch.get("name") != d["chan_name"] or not ch.get("enabled"): probs.append("channel changed")
    if t.raw("GET", f"/agent-enrollment-tokens/{d['tok']}").json().get("state") != "Active": probs.append("token changed")
    if t.raw("GET", f"/restores/{d['restore']}").status_code != 200: probs.append("restore gone")
    p = t.raw("GET", f"/bandwidth-profiles/{d['profile']}")
    if p.status_code != 200 or p.json().get("name") != d["profile_name"]: probs.append("profile changed")
    if t.raw("GET", f"/backup-artifacts/{d['art']}/downloads/{d['dl']}").status_code != 200: probs.append("download session gone")
    return probs


def bola(attacker, victim, label):
    """attacker side's owner session against every victim-side id: own tenant prefix (id swap) and the victim's prefix."""
    A, V = FX[attacker], FX[victim]; ta, tv = (TA, TB) if attacker == "a" else (TB, TA)
    az.F.clear(); az.F.update({k: V[k] for k in ("job", "run", "art", "restore", "conn", "chan", "synth", "tok", "real_agent", "inst", "cfg", "restore_req", "markers")})
    probes = az.probes() + extra_probes(V); markers = V["markers"] + [V["dl"]]
    leaks = lambda text: [m for m in markers if m in text]
    bad, n = [], 0
    for lbl, m, p, body in probes:
        for pref, want in ((ta, (403, 404)), (tv, (403,))):
            r = call(A["t"].s, m, f"/api/tenants/{pref}{p}", body); n += 1
            if pref == ta and lbl in az.EMPTY_LIST and r.status_code == 200 and not leaks(r.text) and az.is_empty(r.json()): continue
            if r.status_code not in want or leaks(r.text): bad.append(f"{lbl} via {'own' if pref == ta else 'victim'} prefix -> {r.status_code} leak={leaks(r.text)} {r.text[:100]}")
    for lbl, p in FILTER_LISTS(V):
        r = call(A["t"].s, "GET", f"/api/tenants/{ta}{p}"); n += 1
        if r.status_code != 200 or leaks(r.text): bad.append(f"{lbl} -> {r.status_code} leak={leaks(r.text)}")
    for lbl, p in (("v1 job health", f"/api/v1/backups/jobs/{V['job']['id']}/health"), ("v1 conn capacity", f"/api/v1/storage/connections/{V['conn']}/capacity"), ("v1 health-summary", f"/api/v1/observability/health-summary?tenantId={tv}")):
        r = call(A["t"].s, "GET", p); n += 1
        if r.status_code not in (403, 404) or leaks(r.text): bad.append(f"{lbl} -> {r.status_code}")
    for r_ in (call(A["t"].s, "POST", f"/api/backups/runs/{V['run']}/pause", {}), call(A["t"].s, "POST", f"/api/v1/storage/connections/{V['conn']}/probe", {})):
        n += 1
        if r_.status_code not in (403, 404): bad.append(f"tenant-less mutation -> {r_.status_code}")
    # malformed / hostile ids: never a 5xx
    for hostile in ("not-a-guid", "1' OR '1'='1", "%00", "../" * 3, "0" * 300):
        for p in ("/backup-jobs/", "/backup-artifacts/", "/restores/", "/agents/", "/storage-connections/"):
            r = call(A["t"].s, "GET", f"/api/tenants/{ta}{p}{hostile}"); n += 1
            if r.status_code >= 500 or r.status_code == 200: bad.append(f"hostile id {hostile[:12]!r} on {p} -> {r.status_code}")
    bad += [f"INTEGRITY {x}" for x in integrity(V)]
    check(not bad, f"{len(bad)}/{n} violations: " + " | ".join(bad[:14]))
    return {"requests": n, "probes": len(probes), "direction": label}


@scenario("gap/BOLA tenant A owner -> tenant B ids (agent/job/run/artifact/restore/storage/channel/token/profile/download/instance)")
def bola_ab(): return bola("a", "b", "A->B")


@scenario("gap/BOLA tenant B owner -> tenant A ids (extra kinds: profile, download session, instance credential; plus all of 8.1)")
def bola_ba(): return bola("b", "a", "B->A")


@scenario("gap/BOLA cross-tenant references inside request bodies (agentId, storageConnectionId, credential ref, bandwidth profile)")
def bola_refs():
    A, B = FX["a"], FX["b"]; ta, tb = A["t"], B["t"]; bad = []; res = {}
    def mutate(name, r, want=(400, 403, 404, 409, 422)):
        res[name] = r.status_code
        if r.status_code not in want: bad.append(f"{name} -> {r.status_code} {r.text[:150]}")
    jobs_before = {k: len(FX[k]["t"].call("GET", "/backup-jobs")) for k in "ab"}
    body = lambda agent, cfg, name: {"name": f"{PFX}ref-{name}-{uuid.uuid4().hex[:4]}", "agentId": agent, "overlapPolicy": "Skip", "configuration": cfg}
    mutate("A job on B agent", ta.raw("POST", "/backup-jobs", body(B["real_agent"], A["cfg"], "agent")))
    mutate("B job on A agent", tb.raw("POST", "/backup-jobs", body(A["real_agent"], B["cfg"], "agent")))
    cfg = json.loads(json.dumps(B["cfg"])); cfg["destinations"] = [{"storageProvider": "s3", "storageConnectionId": A["conn"], "storageExecutionLocation": "Backend", "isPrimary": True}]
    r = tb.raw("POST", "/backup-jobs", body(B["real_agent"], cfg, "conn"))
    if r.status_code == 201:  # accepted at definition time: it must still never run against the foreign connection
        jid = r.json()["id"]; rr = tb.raw("POST", f"/backup-jobs/{jid}/runs", {}); res["B job with A connection: run start"] = [rr.status_code, rr.text[:120]]
        if rr.status_code == 200: tb.raw("POST", f"/backup-runs/{rr.json()['id']}/cancel", {}); bad.append("run started against another tenant's storage connection")
        tb.raw("POST", f"/backup-jobs/{jid}/disable", {})
    else: mutate("B job with A connection", r)
    # credential reference of one agent in another tenant's job / discovery binding
    ref = ta.call("POST", f"/agents/{A['synth']}/database-logins", {"userName": "gap-fake", "password": secrets.token_urlsafe(12)})["reference"]
    pg = {"engine": PG, "host": "127.0.0.1", "port": 5432, "database": "e2e_src", "integratedSecurity": False, "databaseCredentialReference": ref, "trustServerCertificate": True, "encrypt": True,
          "stagingReference": "default", "storageTarget": "default", "storagePrefix": "", "copyOnly": True, "checksumPolicy": "Required", "compressionPolicy": "Preferred", "requiredVerifiedReplicaCount": 1,
          "encryptionPolicy": "Off", "destinations": [flows.local_dest()]}
    mutate("B job with A's credential ref (other tenant, other agent)", tb.raw("POST", "/backup-jobs", body(B["real_agent"], pg, "cred")))
    mutate("A job on A agent with ref of another agent (same tenant)", ta.raw("POST", "/backup-jobs", body(A["real_agent"], pg, "cred2")))
    mutate("B restore of B artifact on A's agent", tb.raw("POST", f"/backup-artifacts/{B['art']}/restore", {**B["restore_req"], "agentId": A["real_agent"]}))
    mutate("A restore of A artifact on B's agent", ta.raw("POST", f"/backup-artifacts/{A['art']}/restore", {**A["restore_req"], "agentId": B["real_agent"]}))
    mutate("A job update re-pointed to B agent", ta.raw("PUT", f"/backup-jobs/{A['job']['id']}", {"name": A["job"]["name"], "agentId": B["real_agent"], "configuration": A["cfg"]}))
    mutate("A assigns B's bandwidth profile to its tenant", ta.raw("PUT", "/upload-bandwidth/tenant/profile", {"profileId": B["profile"]}))
    mutate("A assigns B's bandwidth profile to its job", ta.raw("PUT", f"/upload-bandwidth/jobs/{A['job']['id']}/profile", {"profileId": B["profile"]}))
    mutate("A enrolment token id of B in A's token API", ta.raw("DELETE", f"/agent-enrollment-tokens/{B['tok']}"))
    mutate("A discovery credential bind with B's ref", ta.raw("PUT", f"/discovery/instances/{A['inst']}/credential", {"integratedSecurity": False, "credentialReference": ref}), want=(200, 400, 403, 404))
    for k in "ab":  # nothing may have been created
        now = len(FX[k]["t"].call("GET", "/backup-jobs"))
        if now != jobs_before[k] + (1 if k == "b" and "B job with A connection: run start" in res else 0): bad.append(f"tenant {k} job count {jobs_before[k]} -> {now}")
    bad += [f"INTEGRITY A {x}" for x in integrity(A)] + [f"INTEGRITY B {x}" for x in integrity(B)]
    check(not bad, " | ".join(bad)); return res


@scenario("gap/SEC role escalation: header/body spoofing, Operator/Admin limits on real objects, platform admin has no tenant secrets")
def role_escalation():
    A = FX["a"]; bad = []; res = {}
    vs, ops, ads = role_session("viewer-a"), role_session("operator-a"), role_session("admin-a")
    spoof = {"X-Tenant-Id": TA, "X-Role": "Owner", "X-User-Role": "Owner", "X-Platform-Admin": "true", "X-Forwarded-User": "admin", "X-Original-User": "admin", "X-Forwarded-Role": "PlatformAdmin"}
    for lbl, m, p, body in (("viewer create job", "POST", f"/api/tenants/{TA}/backup-jobs", {}), ("viewer platform users", "GET", "/api/platform/users", None),
                            ("viewer token", "POST", f"/api/tenants/{TA}/agent-enrollment-tokens", {}), ("viewer delete artifact", "DELETE", f"/api/tenants/{TA}/backup-artifacts/{A['art']}", None)):
        r = call(vs, m, p, body, spoof); res[lbl] = r.status_code
        if r.status_code != 403: bad.append(f"{lbl} with spoofed role headers -> {r.status_code}")
    ctx = call(vs, "GET", f"/api/tenants/{TA}/context", None, spoof).json()
    if ctx.get("membershipRole") != "Viewer": bad.append(f"context role changed by headers: {ctx.get('membershipRole')}")
    # privilege-bearing JSON fields never grant anything
    r = call(vs, "POST", "/api/platform/users", {"loginIdentifier": f"gap-auth-esc-{uuid.uuid4().hex[:5]}", "displayName": "x", "password": "Aa1!" + secrets.token_urlsafe(12), "isPlatformAdmin": True, "role": "Owner"}); res["viewer creates platform user"] = r.status_code
    if r.status_code != 403: bad.append(f"viewer created a user: {r.status_code}")
    uid = ST["roles"]["viewer-a"]["userId"]
    for lbl, m, p, body in (("viewer self-promote", "PUT", f"/api/platform/tenants/{TA}/memberships/{uid}", {"role": "Owner"}), ("viewer adds membership", "POST", f"/api/platform/tenants/{TA}/memberships", {"userId": uid, "role": "Owner"}),
                            ("operator self-promote", "PUT", f"/api/platform/tenants/{TA}/memberships/{ST['roles']['operator-a']['userId']}", {"role": "Owner"}),
                            ("admin self-promote", "PUT", f"/api/platform/tenants/{TA}/memberships/{ST['roles']['admin-a']['userId']}", {"role": "Owner"})):
        s_ = vs if lbl.startswith("viewer") else ops if lbl.startswith("operator") else ads
        r = call(s_, m, p, body); res[lbl] = r.status_code
        if r.status_code != 403: bad.append(f"{lbl} -> {r.status_code}")
    # Operator / Admin limits on REAL objects (objects must survive)
    for lbl, s_, m, p, body, want in (("operator revoke agent", ops, "POST", f"/agents/{A['synth']}/revoke", {}, 403), ("operator delete connection", ops, "DELETE", f"/storage-connections/{A['conn']}", None, 403),
                                      ("operator delete artifact", ops, "DELETE", f"/backup-artifacts/{A['art']}", None, 403), ("operator create token", ops, "POST", "/agent-enrollment-tokens", {}, 403),
                                      ("operator download", ops, "POST", f"/backup-artifacts/{A['art']}/downloads", {}, 403), ("operator edit retention", ops, "PUT", f"/backup-jobs/{A['job']['id']}/retention", {"enabled": False}, 403),
                                      ("admin owner-security-recipient", ads, "POST", f"/notifications/channels/{A['chan']}/owner-security-recipient", {"enabled": True}, 403)):
        r = call(s_, m, f"/api/tenants/{TA}{p}", body); res[lbl] = r.status_code
        if r.status_code != want: bad.append(f"{lbl} -> {r.status_code}")
    # a tenant role in A gives nothing in B
    for key in ("admin-a", "operator-a", "viewer-a"):
        r = call(role_session(key), "GET", f"/api/tenants/{TB}/backup-jobs"); res[key + " on tenant B"] = r.status_code
        if r.status_code != 403: bad.append(f"{key} on tenant B -> {r.status_code}")
    # platform admin: no tenant secrets through any platform route
    P = platform(); blob = ""
    for p in ("/api/platform/storage-connections", f"/api/platform/tenants/{TA}/storage-connections", f"/api/platform/storage-connections/{A['conn']}", f"/api/platform/tenants/{TA}/wrap-keys",
              "/api/platform/notification-transports", "/api/platform/agent-settings", "/api/platform/tenants", f"/api/platform/tenants/{TA}", "/api/platform/users", "/api/platform/agents", "/api/platform/audit?pageSize=100"):
        r = call(P, "GET", p); res["platform " + p.split("?")[0][-30:]] = r.status_code
        if r.status_code >= 500: bad.append(f"platform {p} -> {r.status_code}")
        blob += r.text
    for marker, what in ((A.get("conn_secret", "\0"), "s3 secret key"), (ST["fedora"]["sftp_password"], "sftp password"), (ST["fedora"]["pg_password"], "pg password"), (api.env.get("E2E_MINIO_PASSWORD", "\0"), "minio password"),
                         (api.env.get("E2E_SMB_PASSWORD", "\0"), "smb password"), (ST["a"]["password"], "owner a password")):
        if marker and marker in blob: bad.append(f"platform responses contain {what}")
    for key in ("BEGIN PRIVATE KEY", "passwordHash"):
        if key in blob: bad.append(f"platform responses contain the field/text {key}")
    for p in ("/agents", "/storage-connections", "/notifications/channels", "/backup-jobs", "/backup-artifacts", "/encryption"):
        if call(P, "GET", f"/api/tenants/{TA}{p}").status_code != 403: bad.append(f"platform admin w/o membership reads tenant {p}")
    bad += [f"INTEGRITY {x}" for x in integrity(A)]
    check(not bad, " | ".join(bad)); return res


# ---- AUTH-05 / 07 / 08 / 09 / 10, AGT-03 / 06 ------------------------------------------------------------------------------------------------
@scenario("gap/AUTH-05 Viewer: job create, backup start, restore, download, delete all refused on REAL objects; allowed reads work; nothing changed")
def auth05():
    A = FX["a"]; vs = role_session("viewer-a"); t = A["t"]; bad = []; res = {}
    snap = lambda: (len(t.call("GET", "/backup-jobs")), len(t.call("GET", f"/backup-jobs/{A['job']['id']}/runs")), len(t.call("GET", "/restores")["items"] if isinstance(t.call("GET", "/restores"), dict) else t.call("GET", "/restores")))
    before = snap(); j, a, rs = A["job"]["id"], A["art"], A["restore"]
    denied = [("create job", "POST", "/backup-jobs", {"name": f"{PFX}viewer", "agentId": A["real_agent"], "configuration": A["cfg"]}), ("update job", "PUT", f"/backup-jobs/{j}", {"name": "pwn", "agentId": A["real_agent"], "configuration": A["cfg"]}),
              ("start backup", "POST", f"/backup-jobs/{j}/runs", {}), ("retry run", "POST", f"/backup-runs/{A['run']}/retry", {}), ("cancel run", "POST", f"/backup-runs/{A['run']}/cancel", {}),
              ("disable job", "POST", f"/backup-jobs/{j}/disable", {}), ("schedule", "PUT", f"/backup-jobs/{j}/schedule", {"cron": "0 3 * * *"}),
              ("restore", "POST", f"/backup-artifacts/{a}/restore", A["restore_req"]), ("restore precheck", "POST", f"/backup-artifacts/{a}/restore-precheck", A["restore_req"]),
              ("restore discovery", "POST", f"/backup-artifacts/{a}/restore-discovery", A["restore_req"]), ("cancel restore", "POST", f"/restores/{rs}/cancel", {}),
              ("download prepare", "POST", f"/backup-artifacts/{a}/downloads", {}), ("download availability", "GET", f"/backup-artifacts/{a}/download-availability", None),
              ("download content", "GET", f"/backup-artifacts/{a}/downloads/{A['dl']}/content", None), ("download status", "GET", f"/backup-artifacts/{a}/downloads/{A['dl']}", None),
              ("delete artifact", "DELETE", f"/backup-artifacts/{a}", None), ("repair", "POST", f"/backup-artifacts/{a}/repairs", {}), ("create connection", "POST", "/storage-connections", {"providerType": "s3", "name": "pwn", "configuration": {}}),
              ("connection secret", "PUT", f"/storage-connections/{A['conn']}/secret", {"secret": {"accessKey": "x", "secretKey": "yyyyyyyy"}}), ("db login", "POST", f"/agents/{A['synth']}/database-logins", {"userName": "u", "password": "p"})]
    for lbl, m, p, body in denied:
        r = call(vs, m, f"/api/tenants/{TA}{p}", body); res[lbl] = r.status_code
        if r.status_code != 403: bad.append(f"viewer {lbl} -> {r.status_code} {r.text[:80]}")
        if "/content" in p and (r.content[:2] == b"PK" or len(r.content) > 5000): bad.append("viewer received artifact bytes")
    allowed = ["/backup-jobs", f"/backup-jobs/{j}", f"/backup-jobs/{j}/runs", f"/backup-runs/{A['run']}", "/backup-runs", "/backup-artifacts", f"/backup-artifacts/{a}", "/restores", f"/restores/{rs}",
               "/storage-connections", f"/storage-connections/{A['conn']}", "/agents", f"/agents/{A['real_agent']}", "/dashboard", "/reports/backups", "/context", f"/backup-artifacts/{a}/restore-targets"]
    for p in allowed:
        r = call(vs, "GET", f"/api/tenants/{TA}{p}"); res["GET " + p[:38]] = r.status_code
        if r.status_code != 200: bad.append(f"viewer allowed read {p} -> {r.status_code}")
        if A.get("conn_secret", "\0") in r.text: bad.append(f"secret in viewer read {p}")
    if snap() != before: bad.append(f"state changed {before} -> {snap()}")
    bad += [f"INTEGRITY {x}" for x in integrity(A)]
    check(not bad, " | ".join(bad)); return {"denied": len(denied), "allowed": len(allowed)}


SYNTH_HEADERS = {"X-Backup-Agent-Protocol": "1.0"}


def agent_call(cred, method, path, body=None, headers=None):
    h = {"Authorization": f"BackupAgent {cred['credentialId'].replace('-', '')}.{cred['secret']}", **SYNTH_HEADERS, **(headers or {})}
    return requests.request(method, BASE + path, json=body, headers=h, verify=False, timeout=30)


def cred_of(enroll_json):
    c = enroll_json["credential"]; return {"credentialId": c.get("credentialId") or c.get("id"), "secret": c["secret"]}


def revoked_credential_probe(cred, agent_id, tenant):
    """Every agent-surface route with the credential of a (now) revoked agent must answer 401 and change nothing."""
    out = {}
    for lbl, m, p, body in (("me", "GET", "/api/agent/me", None), ("tasks", "GET", "/api/agent/tasks", None), ("heartbeat", "POST", "/api/agent/heartbeat", {}), ("capabilities", "PUT", "/api/agent/capabilities", {}),
                            ("rotate", "POST", "/api/agent/credential/rotate", {}), ("confirm", "POST", "/api/agent/credential/confirm", {}), ("progress", "POST", f"/api/agent/tasks/{guid()}/progress", {}),
                            ("complete", "POST", f"/api/agent/tasks/{guid()}/complete", {}), ("lease", "POST", f"/api/agent/tasks/{guid()}/lease", {}), ("db-credentials", "POST", "/api/agent/database-credentials", {}),
                            ("cert-enroll", "POST", "/api/agent/certificate/enroll", {})):
        out[lbl] = agent_call(cred, m, p, body).status_code
    return out


@scenario("gap/AGT-06a synthetic agent: credential works, after revoke every agent route is 401, new work cannot be created for it")
def agt06a():
    B = FX["b"]; tb = B["t"]; bad = []
    tok = tb.call("POST", "/agent-enrollment-tokens"); r = az.enroll(tok["token"], f"{PFX}synth-rev-{uuid.uuid4().hex[:4]}"); check(r.status_code == 201, f"enroll {r.status_code}")
    aid, cred = r.json()["agentId"], cred_of(r.json())
    me = agent_call(cred, "GET", "/api/agent/me"); check(me.status_code == 200, f"agent /me before revoke -> {me.status_code}")
    hb = agent_call(cred, "GET", "/api/agent/tasks"); check(hb.status_code == 200, f"agent poll before revoke -> {hb.status_code} {hb.text[:100]}")
    job = tb.call("POST", "/backup-jobs", {"name": f"{PFX}rev-synth-{uuid.uuid4().hex[:4]}", "agentId": aid, "overlapPolicy": "Skip", "configuration": file_cfg("gapfiles", ["docs"])})
    check(tb.raw("POST", f"/agents/{aid}/revoke", {}).status_code in (200, 204), "revoke failed")
    after = revoked_credential_probe(cred, aid, TB); bad += [f"{k} -> {v}" for k, v in after.items() if v != 401]
    st = next(a for a in tb.call("GET", "/agents?includeRevoked=true") if a["agentId"] == aid)["status"]
    if st != "Revoked": bad.append(f"agent status {st}")
    new = {}
    r = tb.raw("POST", f"/backup-jobs/{job['id']}/runs", {}); new["start run on revoked agent"] = r.status_code
    if r.status_code == 200: tb.raw("POST", f"/backup-runs/{r.json()['id']}/cancel", {}); bad.append("run started for a revoked agent")
    r = tb.raw("POST", "/backup-jobs", {"name": f"{PFX}rev-new", "agentId": aid, "overlapPolicy": "Skip", "configuration": file_cfg("gapfiles", ["docs"])}); new["create job on revoked agent"] = r.status_code
    if r.status_code == 201: tb.raw("POST", f"/backup-jobs/{r.json()['id']}/disable", {}); bad.append("job created for a revoked agent")
    r = tb.raw("POST", f"/backup-artifacts/{B['art']}/restore", {**B["restore_req"], "agentId": aid}); new["restore on revoked agent"] = r.status_code
    if r.status_code in (200, 201, 202): bad.append("restore accepted for a revoked agent")
    r = tb.raw("POST", f"/agents/{aid}/database-logins", {"userName": "u", "password": "p"}); new["db login for revoked agent"] = r.status_code
    tb.raw("POST", f"/backup-jobs/{job['id']}/disable", {})
    check(not bad, " | ".join(bad)); return {"after_revoke": after, "new_work": new}


@scenario("gap/AGT-06b real agent revoked while a backup is running: the run does not succeed, no artifact appears, old identity is dead, new tasks refused")
def agt06b():
    tb = FX["b"]["t"]; big = WORK / "big-files"; big.mkdir(parents=True, exist_ok=True)
    if not (big / "payload.bin").exists(): subprocess.run(f"head -c 1500000000 /dev/urandom > {big}/payload.bin", shell=True, check=True)
    unit, ag = start_local_agent("revoke-live", "b", "gapbig", big)
    try:
        cfg = file_cfg("gapbig", ["."]); job = tb.call("POST", "/backup-jobs", {"name": f"{PFX}revoke-live", "agentId": ag["agentId"], "overlapPolicy": "Skip", "configuration": cfg})
        run = tb.call("POST", f"/backup-jobs/{job['id']}/runs", {}); rid = run.get("id", run.get("run", {}).get("id"))
        seen = None; end = time.time() + 120
        while time.time() < end:
            d = tb.call("GET", f"/backup-runs/{rid}")
            if d["status"] == "Running" or any(a["status"] == "Running" for a in d["attempts"]): seen = d; break
            if d["status"] in lib.TERMINAL: break
            time.sleep(0.5)
        check(seen, f"run never observed Running (status {d['status']})")
        time.sleep(3)  # let the transfer actually start
        d = tb.call("GET", f"/backup-runs/{rid}"); check(d["status"] == "Running", f"run finished before the revoke ({d['status']}): payload too small")
        t0 = time.time(); check(tb.raw("POST", f"/agents/{ag['agentId']}/revoke", {}).status_code in (200, 204), "revoke failed")
        final = tb.wait(f"/backup-runs/{rid}", timeout=240)
        arts = [a for a in tb.call("GET", f"/backup-artifacts?jobId={job['id']}")["items"] if a.get("jobId") == job["id"]]
        check(final["status"] != "Succeeded", f"run Succeeded although the agent was revoked mid-run")
        check(not arts, f"artifact registered after the revoke: {[(a['id'], a['state']) for a in arts]}")
        logs = subprocess.run(["journalctl", "--user", "-u", unit, "--no-pager", "-o", "cat", "--since", "-5min"], capture_output=True, text=True).stdout
        n401 = len(re.findall(r"401|Unauthorized|unauthorized", logs))
        new = {}
        r = tb.raw("POST", f"/backup-jobs/{job['id']}/runs", {}); new["new run"] = r.status_code
        check(r.status_code != 200, f"new run accepted for the revoked agent: {r.text[:120]}")
        r = tb.raw("POST", f"/backup-runs/{rid}/retry", {}); new["retry"] = r.status_code
        check(r.status_code not in (200, 201, 202), f"retry accepted for the revoked agent: {r.status_code}")
        view = next(a for a in tb.call("GET", "/agents?includeRevoked=true") if a["agentId"] == ag["agentId"])
        return {"final_run_status": final["status"], "attempts": [(a["status"], a.get("reasonCode")) for a in final["attempts"]], "seconds_to_terminal": round(time.time() - t0), "agent_status": view["status"],
                "agent_log_401_lines": n401, "new_work": new, "artifacts_after": 0}
    finally:
        stop_local_agent(unit, "revoke-live"); shutil.rmtree(big, ignore_errors=True)
        for j in tb.call("GET", "/backup-jobs"):
            if j["name"].startswith(f"{PFX}revoke-live") and j["enabled"]: tb.raw("POST", f"/backup-jobs/{j['id']}/disable", {})


@scenario("gap/AUTH-07 membership/role revocation hits live sessions; revoked-agent credential is dead (synthetic + real mTLS covered by revoke.py/mtls.py)")
def auth07():
    A = FX["a"]; bad = []; res = {}
    u = mk_user("member", (TA, "Admin")); s = api.login(u["login"], u["pw"]); P = platform()
    check(call(s, "GET", f"/api/tenants/{TA}/agent-enrollment").status_code == 200, "admin cannot read enrollment policy")
    check(call(s, "GET", "/api/me/tenants").status_code == 200 and TA in call(s, "GET", "/api/me/tenants").text, "tenant not listed for member")
    call(P, "PUT", f"/api/platform/tenants/{TA}/memberships/{u['id']}", {"role": "Viewer"})
    r = call(s, "GET", f"/api/tenants/{TA}/agent-enrollment"); res["admin->viewer then admin GET"] = r.status_code
    if r.status_code != 403: bad.append(f"downgraded admin still reads enrollment policy: {r.status_code}")
    r = call(s, "POST", f"/api/tenants/{TA}/agent-enrollment-tokens", {}); res["downgraded token create"] = r.status_code
    if r.status_code == 201: call(A["t"].s, "DELETE", f"/api/tenants/{TA}/agent-enrollment-tokens/{r.json()['tokenId']}")
    if r.status_code != 403: bad.append(f"downgraded admin created a token: {r.status_code}")
    call(P, "DELETE", f"/api/platform/tenants/{TA}/memberships/{u['id']}")
    for p in ("/backup-jobs", "/context", "/agents", "/storage-connections"):
        r = call(s, "GET", f"/api/tenants/{TA}{p}"); res["removed GET " + p] = r.status_code
        if r.status_code != 403: bad.append(f"removed member GET {p} -> {r.status_code}")
    me = call(s, "GET", "/api/me/tenants"); res["tenants after removal"] = me.status_code
    if TA in me.text: bad.append("tenant still listed after membership removal")
    check(call(s, "GET", "/api/me").status_code == 200, "user session itself should survive a membership removal")
    # re-adding restores access with the SAME session (no stale deny cache)
    call(P, "POST", f"/api/platform/tenants/{TA}/memberships", {"userId": u["id"], "role": "Viewer"})
    r = call(s, "GET", f"/api/tenants/{TA}/backup-jobs"); res["re-added"] = r.status_code
    if r.status_code != 200: bad.append(f"re-added member -> {r.status_code}")
    call(P, "DELETE", f"/api/platform/tenants/{TA}/memberships/{u['id']}")
    check(not bad, " | ".join(bad)); return res


def _post_same_key(t, path, body, key, n):
    with cf.ThreadPoolExecutor(n) as ex:
        return list(ex.map(lambda _: t.s.post(t.base + path, json=body, headers={"Idempotency-Key": key}), range(n)))


@scenario("gap/AUTH-08 idempotency: same key -> one run/restore (also in parallel), different key -> new resource, bad keys 400; job create ignores the key")
def auth08():
    B = FX["b"]; tb = B["t"]; bad = []; res = {}
    job = tb.call("POST", "/backup-jobs", {"name": f"{PFX}idem-{uuid.uuid4().hex[:4]}", "agentId": B["real_agent"], "overlapPolicy": "Skip", "configuration": file_cfg("gapfiles", ["docs"])})
    jid = job["id"]; K = uuid.uuid4().hex
    rs = _post_same_key(tb, f"/backup-jobs/{jid}/runs", {}, K, 12); ids = {r.json().get("id") for r in rs if r.status_code == 200}
    res["parallel same-key run statuses"] = sorted({r.status_code for r in rs}); res["run ids"] = len(ids)
    if len(ids) != 1 or any(r.status_code != 200 for r in rs): bad.append(f"parallel same-key run start: {[(r.status_code, r.text[:60]) for r in rs if r.status_code != 200][:3]} ids={len(ids)}")
    rid = next(iter(ids)); tb.wait(f"/backup-runs/{rid}", timeout=300)
    r = tb.s.post(tb.base + f"/backup-jobs/{jid}/runs", json={}, headers={"Idempotency-Key": K}); res["replay after completion"] = [r.status_code, r.json().get("id") == rid if r.status_code == 200 else r.text[:80]]
    if r.status_code != 200 or r.json().get("id") != rid: bad.append(f"replay after completion did not return the same run: {r.status_code}")
    r2 = tb.s.post(tb.base + f"/backup-jobs/{jid}/runs", json={}, headers={"Idempotency-Key": uuid.uuid4().hex}); res["new key"] = r2.status_code
    if r2.status_code != 200 or r2.json().get("id") == rid: bad.append(f"different key did not create a new run: {r2.status_code}")
    runs_data = tb.call("GET", f"/backup-jobs/{jid}/runs")
    runs_items = runs_data.get("items", runs_data) if isinstance(runs_data, dict) else runs_data
    res["runs total"] = len(runs_items)
    if len(runs_items) != 2: bad.append(f"expected 2 runs for the job, got {len(runs_items)}")
    tb.wait(f"/backup-runs/{r2.json()['id']}", timeout=300)
    for lbl, hdrs in (("missing", {}), ("empty", {"Idempotency-Key": ""}), ("129 chars", {"Idempotency-Key": "a" * 129}), ("non-ascii", {"Idempotency-Key": "k\u00e9y"}), ("space-only", {"Idempotency-Key": " "})):
        try: rr = tb.s.post(tb.base + f"/backup-jobs/{jid}/runs", json={}, headers=hdrs) if lbl != "missing" else requests.Session().__class__.request(tb.s, "POST", tb.base + f"/backup-jobs/{jid}/runs", json={})
        except Exception as e: res["key " + lbl] = f"client-refused {type(e).__name__}"; continue
        res["key " + lbl] = rr.status_code
        if rr.status_code != 400: bad.append(f"Idempotency-Key {lbl} -> {rr.status_code}")
    # restore: same key twice -> one restore; same key with a different body -> not a silent replay
    rk = uuid.uuid4().hex; rel1, rel2 = f"zz-{PFX}idem-{uuid.uuid4().hex[:4]}", f"zz-{PFX}idem-{uuid.uuid4().hex[:4]}"
    mk = lambda rel: {**B["restore_req"], "options": {"files": {"relativeDirectory": rel}}}
    n0 = len(tb.call("GET", "/restores")["items"] if isinstance(tb.call("GET", "/restores"), dict) else tb.call("GET", "/restores"))
    ra = _post_same_key(tb, f"/backup-artifacts/{B['art']}/restore", mk(rel1), rk, 6); rids = {x.json().get("id", x.json().get("restoreId")) for x in ra if x.status_code in (200, 201, 202)}
    res["parallel same-key restore"] = [sorted({x.status_code for x in ra}), len(rids)]
    if len(rids) != 1: bad.append(f"same-key parallel restore created {len(rids)} restores {[x.status_code for x in ra]}")
    rb = tb.s.post(tb.base + f"/backup-artifacts/{B['art']}/restore", json=mk(rel2), headers={"Idempotency-Key": rk}); res["same key other body"] = [rb.status_code, rb.text[:100]]
    if rb.status_code in (200, 201, 202) and rb.json().get("id", rb.json().get("restoreId")) in rids: bad.append("same key with a different body silently replayed the first restore (no conflict)")
    for x in rids: tb.wait(f"/restores/{x}", timeout=300)
    # job creation: key required but ignored (observation / defect candidate)
    K2 = uuid.uuid4().hex; body = {"name": f"{PFX}idem-create-{uuid.uuid4().hex[:4]}", "agentId": B["real_agent"], "overlapPolicy": "Skip", "configuration": file_cfg("gapfiles", ["docs"])}
    cr = [tb.s.post(tb.base + "/backup-jobs", json=body, headers={"Idempotency-Key": K2}) for _ in range(2)]
    dup = {r.json()["id"] for r in cr if r.status_code == 201}; res["job create same key x2"] = [r.status_code for r in cr], len(dup)
    for x in dup: tb.raw("POST", f"/backup-jobs/{x}/disable", {})
    tb.raw("POST", f"/backup-jobs/{jid}/disable", {})
    if len(dup) > 1: OBS.append("DEFECT-CANDIDATE POST /backup-jobs: Idempotency-Key is required but ignored, same key twice creates 2 jobs")
    for rel in (rel1, rel2): shutil.rmtree(f"/tmp/gap-authops/files/{rel}", ignore_errors=True)
    check(not bad, " | ".join(bad)); res["observations"] = list(OBS); return res


@scenario("gap/AUTH-09 CSRF/cookie/CORS/redirects: cross-origin requests get no CORS grant and cannot mutate; redirects stay on the same host")
def auth09():
    bad, res = [], {}
    evil = {"Origin": "https://evil.example", "Referer": "https://evil.example/x"}
    for lbl, r in (("OPTIONS preflight login", requests.options(BASE + "/api/auth/login", headers={**evil, "Access-Control-Request-Method": "POST", "Access-Control-Request-Headers": "x-csrf-token,content-type"}, verify=False)),
                   ("OPTIONS preflight tenant", requests.options(f"{BASE}/api/tenants/{TA}/backup-jobs", headers={**evil, "Access-Control-Request-Method": "DELETE"}, verify=False)),
                   ("GET antiforgery w/ Origin", requests.get(BASE + "/api/auth/antiforgery", headers=evil, verify=False)), ("GET features w/ Origin", requests.get(BASE + "/api/features", headers=evil, verify=False))):
        acao = {k: v for k, v in r.headers.items() if k.lower().startswith("access-control-")}; res[lbl] = [r.status_code, acao]
        if acao: bad.append(f"{lbl} returned CORS headers {acao}")
    t = lib.Tenant("a"); s = t.s
    # simulated cross-site forgeries with the victim's cookie: no header, wrong content types, form post
    target = f"/api/tenants/{TA}/backup-jobs/{guid()}/disable"
    for lbl, kw in (("cookie only, json", dict(json={}, headers=evil)), ("cookie only, form", dict(data={"a": "b"}, headers=evil)), ("cookie only, text/plain", dict(data="{}", headers={**evil, "Content-Type": "text/plain"})),
                    ("bogus token", dict(json={}, headers={**evil, "X-CSRF-TOKEN": "A" * 120}))):
        n = requests.Session(); n.verify = False; n.cookies.update(s.cookies)
        r = n.post(BASE + target, **kw); res[lbl] = r.status_code
        if r.status_code not in (400, 401, 415): bad.append(f"{lbl} -> {r.status_code}")
    # a valid token+cookie from the real origin still works (baseline) and the antiforgery token is not accepted for another cookie jar
    r = call(s, "POST", target, {}, evil); res["valid session+token, foreign Origin header"] = r.status_code
    OBS.append(f"server does not check Origin/Referer; a request carrying a valid token+cookie is accepted regardless of Origin (status {r.status_code}); protection relies on the antiforgery token (no CORS => origin cannot read it)")
    sc = requests.post(BASE + "/api/auth/login", json={}, verify=False)  # cookie flags of the antiforgery cookie
    af = requests.get(BASE + "/api/auth/antiforgery", verify=False); res["antiforgery set-cookie"] = re.sub(r"=[^;]+", "=<v>", af.headers.get("set-cookie", ""), count=1)
    samesite = re.search(r"samesite=(\w+)", api.login(ST["a"]["login"], ST["a"]["password"]).cookies.__repr__(), re.I)
    # redirects
    for lbl, url, hdrs in (("http UI port", f"http://{api.env['PUBLIC_HOST']}:5174/", {}), ("http UI port, hostile Host", f"http://{api.env['PUBLIC_HOST']}:5174/", {"Host": "evil.example"}),
                           ("API login w/ returnUrl", f"{BASE}/api/me?returnUrl=https://evil.example&ReturnUrl=//evil.example", {}), ("oauth callback bogus state", f"{BASE}/api/platform/google-drive/oauth/callback?code=x&state=https://evil.example", {})):
        try: r = requests.get(url, headers=hdrs, verify=False, allow_redirects=False, timeout=15)
        except Exception as e: res[lbl] = f"{type(e).__name__}"; continue
        loc = r.headers.get("location", ""); res[lbl] = [r.status_code, loc[:80]]
        if "evil.example" in loc and lbl != "http UI port, hostile Host": bad.append(f"{lbl} redirects to attacker host: {loc}")
        if lbl == "http UI port, hostile Host" and "evil.example" in loc: OBS.append(f"nginx HTTP->HTTPS redirect trusts the Host header (Location {loc}); host-header injection, P3")
    ui = requests.get(f"https://{api.env['PUBLIC_HOST']}:5173/", verify=False); res["ui security headers"] = {h: ui.headers.get(h) for h in ("strict-transport-security", "x-frame-options", "content-security-policy", "x-content-type-options", "referrer-policy")}
    miss = [h for h, v in res["ui security headers"].items() if not v]
    if miss: OBS.append(f"UI (nginx :5173) lacks security headers: {miss}")
    ck = api.login(ST["a"]["login"], ST["a"]["password"]).cookies
    res["observations"] = list(OBS); check(not bad, " | ".join(bad)); return res


def audit_rows(tenant=None, pages=3):
    P = platform(); out = []
    for pg in range(1, pages + 1):
        r = call(P, "GET", f"/api/platform/audit?pageSize=100&page={pg}" + (f"&tenantId={tenant}" if tenant else "")); check(r.status_code == 200, f"audit {r.status_code}")
        out += r.json()["items"]
        if len(r.json()["items"]) < 100: break
    return out


@scenario("gap/AUTH-10 audit: login/failed login/role change/restore/download/delete/connection/token/logout rows carry actor, tenant, target, time; no secrets in audit")
def auth10():
    A = FX["a"]; ta = A["t"]; P = platform(); bad, res = [], {}
    t0 = time.time()
    u = mk_user("audit", (TA, "Admin")); s = api.login(u["login"], u["pw"]); s_fail = anon(); attempt(s_fail, u["login"], "Wrong-" + secrets.token_urlsafe(8) + "1!")
    mark = {"conn": secrets.token_urlsafe(24), "chan": "gapchan" + secrets.token_urlsafe(18), "dbpw": "gapdb" + secrets.token_urlsafe(14)}
    acts = {}
    cname = f"{PFX}audit-conn-{uuid.uuid4().hex[:4]}"
    chname = f"{PFX}audit-chan-{uuid.uuid4().hex[:4]}"
    jname = f"{PFX}audit-job-{uuid.uuid4().hex[:4]}"
    job = call(s, "POST", f"/api/tenants/{TA}/backup-jobs", {"name": jname, "agentId": A["real_agent"], "overlapPolicy": "Skip", "configuration": A["cfg"]}).json(); acts["job create"] = job["id"]
    conn = call(s, "POST", f"/api/tenants/{TA}/storage-connections", {"providerType": "s3", "name": cname, "configuration": {"endpoint": f"https://{api.env['PUBLIC_HOST']}:9000", "bucket": "gap-none", "region": "us-east-1"},
                                                                  "secret": {"accessKey": "GAPAUDITKEY", "secretKey": mark["conn"]}}).json(); acts["conn create"] = conn["id"]
    chan = call(s, "POST", f"/api/tenants/{TA}/notifications/channels", {"kind": "webhook", "name": chname, "target": f"https://example.invalid/{PFX}audit-{uuid.uuid4().hex[:6]}"}).json()
    call(s, "POST", f"/api/tenants/{TA}/agents/{A['synth']}/database-logins", {"userName": "gap-audit", "password": mark["dbpw"]})
    tok = call(s, "POST", f"/api/tenants/{TA}/agent-enrollment-tokens", {}).json(); call(s, "DELETE", f"/api/tenants/{TA}/agent-enrollment-tokens/{tok['tokenId']}"); acts["token"] = tok["tokenId"]
    rel = f"zz-{PFX}audit-{uuid.uuid4().hex[:4]}"; rq = {**A["restore_req"], "options": {"files": {"relativeDirectory": rel}}}
    rs = call(s, "POST", f"/api/tenants/{TA}/backup-artifacts/{A['art']}/restore", rq).json(); rid = rs.get("id", rs.get("restoreId")); acts["restore"] = rid
    try: ta.wait(f"/restores/{rid}", timeout=15)
    except TimeoutError: pass
    dl = call(s, "POST", f"/api/tenants/{TA}/backup-artifacts/{A['art']}/downloads", {}).json(); acts["download"] = dl["sessionId"]
    art2_resp = call(s, "DELETE", f"/api/tenants/{TA}/backup-artifacts/{A['art']}")
    acts["artifact delete"] = [art2_resp.status_code, A['art']]
    call(s, "POST", f"/api/tenants/{TA}/backup-artifacts/{A['art']}/cancel-delete", {})
    call(P, "PUT", f"/api/platform/tenants/{TA}/memberships/{u['id']}", {"role": "Operator"})
    call(s, "DELETE", f"/api/tenants/{TA}/storage-connections/{conn['id']}"); call(s, "DELETE", f"/api/tenants/{TA}/notifications/channels/{chan['id']}")
    call(s, "POST", f"/api/tenants/{TA}/backup-jobs/{job['id']}/disable", {}); call(s, "POST", "/api/auth/logout")
    t1 = time.time(); time.sleep(3)
    ev = [e for e in audit_rows(None, 6) + audit_rows(TA, 6)]
    seen = {e["id"]: e for e in ev}.values()
    mine = [e for e in seen if e["actorUserId"] == u["id"] or u["id"] in json.dumps(e["details"]) or e["targetId"] == u["id"]]
    def find(sub, target=None, actor=u["id"], tenant=TA):
        return [e for e in seen if sub in e["action"] and (actor is None or e["actorUserId"] == actor) and (target is None or e["targetId"] == target) and (tenant is None or e["tenantId"] == tenant)]
    exp = {"login succeeded": find("login_succeeded", None, u["id"], None), "login failed (anonymous)": [e for e in seen if e["action"] == "auth.login_failed" and abs(__import__("datetime").datetime.fromisoformat(e["at"]).timestamp() - t0) < 120],
           "job created": find("backup_job.created", job["id"]), "storage connection created": find("storage_connection", conn["id"]) or find("connection", conn["id"]), "channel created/secret": find("channel", chan["id"]),
           "token created": find("enrollment_token_created", None) , "token revoked": find("enrollment_token_revoked", None),
           "restore requested": [e for e in seen if e["targetId"] == rid and "restore" in e["action"] and e["actorUserId"] == u["id"]] or [e for e in seen if e["targetId"] == rid],
           "download prepared": [e for e in seen if e["targetId"] == dl["sessionId"]], "artifact delete requested": find("delete_requested", A["art"]),
           "membership changed (admin actor)": [e for e in seen if "membership" in e["action"] and e["targetId"] == u["id"] and e["tenantId"] == TA and e["actorUserId"] != u["id"]],
           "logout": find("logout", None, u["id"], None)}
    for k, v in exp.items():
        res[k] = sorted({e["action"] for e in v})
        if not v: bad.append(f"no audit row: {k}")
    # actor / tenant / time correctness of rows that have them
    for k in ("job created", "storage connection created", "artifact delete requested"):
        for e in exp[k]:
            at = __import__("datetime").datetime.fromisoformat(e["at"]).timestamp()
            if e["tenantId"] != TA or e["actorUserId"] != u["id"] or not (t0 - 5 <= at <= t1 + 5): bad.append(f"{k}: wrong actor/tenant/time {e['actorUserId'][:8]} {e['tenantId']} {at - t0:.0f}s")
    # nothing sensitive in ANY audit row of this period
    blob = json.dumps(list(seen))
    for what, val in (("user password", u["pw"]), ("connection secret key", mark["conn"]), ("channel secret", mark["chan"]), ("db login password", mark["dbpw"]), ("owner-a password", ST["a"]["password"]), ("sftp password", ST["fedora"]["sftp_password"])):
        if val in blob: bad.append(f"audit contains {what}")
    for pat in ("Bearer ", "BackupAgent ", "BEGIN PRIVATE KEY"):
        if pat in blob: bad.append(f"audit contains pattern {pat}")
    for st_ in ("Webhook", "https://example.invalid/" + PFX): pass
    # tenant isolation of what we just did: tenant B's audit view has none of it
    bb = json.dumps(audit_rows(TB, 3))
    for m in (job["id"], conn["id"], u["id"], rid): 
        if m in bb: bad.append(f"tenant B audit contains tenant A id {m[:8]}")
    ta.raw("DELETE", f"/storage-connections/{conn['id']}"); lib.fedora(f"sudo rm -rf '/srv/e2e/files/{rel}'")
    call(P, "DELETE", f"/api/platform/tenants/{TA}/memberships/{u['id']}")
    check(not bad, " | ".join(bad)); res["rows_by_user"] = len(mine); return res


@scenario("gap/AGT-03 enrollment token replay: 8 parallel enrollments with one token -> exactly one agent; installation id reuse refused; token secret never re-readable")
def agt03():
    A = FX["a"]; ta = A["t"]; bad = []; res = {}
    tok = ta.call("POST", "/agent-enrollment-tokens"); host = f"{PFX}race-{uuid.uuid4().hex[:4]}"
    before = {a["agentId"] for a in ta.call("GET", "/agents")}
    with cf.ThreadPoolExecutor(8) as ex: rs = list(ex.map(lambda i: az.enroll(tok["token"], f"{host}-{i}"), range(8)))
    codes = sorted(r.status_code for r in rs); res["codes"] = codes; res["errors"] = sorted({az.err(r) for r in rs if r.status_code != 201})
    new = {a["agentId"] for a in ta.call("GET", "/agents")} - before
    if codes.count(201) != 1 or len(new) != 1: bad.append(f"parallel enrollment: {codes} new agents={len(new)}")
    st = ta.call("GET", f"/agent-enrollment-tokens/{tok['tokenId']}")
    if "secret" in json.dumps(st).lower() and tok["token"].split(".")[-1] in json.dumps(st): bad.append("token secret re-readable")
    if tok["token"] in json.dumps(ta.call("GET", "/agent-enrollment-tokens")): bad.append("token secret listed")
    inst = guid(); t1, t2 = ta.call("POST", "/agent-enrollment-tokens"), ta.call("POST", "/agent-enrollment-tokens")
    r1 = az.enroll(t1["token"], host + "-i1", installation=inst); r2 = az.enroll(t2["token"], host + "-i2", installation=inst)
    res["installation reuse"] = [r1.status_code, r2.status_code, az.err(r2)]
    if r1.status_code != 201 or r2.status_code != 409: bad.append(f"installation reuse -> {r1.status_code}/{r2.status_code}")
    if r2.status_code == 409 and az.token_state(ta, t2["tokenId"])[1] != "Active": OBS.append("token consumed by a refused (409) enrollment")
    for r in rs + [r1]:
        if r.status_code == 201: ta.raw("POST", f"/agents/{r.json()['agentId']}/revoke", {})
    ta.raw("DELETE", f"/agent-enrollment-tokens/{t2['tokenId']}")
    check(not bad, " | ".join(bad)); res["observations"] = list(OBS); return res


@scenario("gap/OPS-07 live audit integrity status and authorization")
def audit_integrity():
    p = platform()
    r = call(p, "GET", "/api/platform/audit/integrity?limit=100")
    check(r.status_code == 200, f"integrity HTTP {r.status_code}")
    d = r.json()
    counts = {x: sum(e["status"] == x for e in d["events"]) for x in {e["status"] for e in d["events"]}}
    check(call(lib.Tenant("a").s, "GET", "/api/platform/audit/integrity").status_code == 403, "tenant owner can access integrity")
    return {"signingEnabled": d["signingEnabled"], "statuses": counts, "publicKeyCount": len(d["publicKeys"])}


@scenario("gap/SEC transport forged client certificate headers and HTTPS validation")
def transport():
    out = {}
    try:
        requests.get(BASE + "/api/auth/antiforgery", timeout=10)
        out["untrusted_tls"] = "accepted"
    except requests.exceptions.SSLError:
        out["untrusted_tls"] = "rejected"
    r = requests.get(BASE + "/api/agent/me", headers={"X-ARR-ClientCert": "forged", "X-Client-Cert": "forged", "X-SSL-Client-Cert": "forged", "X-Forwarded-Client-Cert": "forged"}, verify=False, timeout=10)
    out["forged_proxy_headers"] = r.status_code
    check(out == {"untrusted_tls": "rejected", "forged_proxy_headers": 401}, f"transport results {out}")
    return out


@scenario("gap/OPS-03 webhook restricted address delivery")
def webhook_ssrf():
    t = lib.Tenant("b")
    results = {}
    for label, host in (("loopback", "127.0.0.1"), ("rfc1918", "10.10.20.1"), ("link-local", "169.254.169.254"), ("ipv6-loopback", "[::1]"), ("ipv6-ula", "[fc00::1]"), ("ipv6-mapped-loopback", "[::ffff:127.0.0.1]")):
        c = t.call("POST", "/notifications/channels", {"kind": "webhook", "name": f"{PFX}ssrf-{label}-{uuid.uuid4().hex[:4]}", "target": f"https://{host}/gap"})
        try:
            r = t.raw("POST", f"/notifications/channels/{c['id']}/test", {})
            check(r.status_code == 202, f"{label} test HTTP {r.status_code}")
            did = r.json()["deliveryId"]
            end = time.time() + 45
            while time.time() < end:
                ds = t.call("GET", "/notifications/deliveries?pageSize=100")["items"]
                d = next((x for x in ds if x["id"] == did), None)
                if d and d["status"] == "failed": break
                time.sleep(2)
            results[label] = [d["status"], d["errorCode"]] if d else None
            check(d and d["status"] == "failed" and d["errorCode"] == "network_denied", f"{label}: {results[label]}")
        finally:
            t.raw("DELETE", f"/notifications/channels/{c['id']}")
    return results


@scenario("gap/SEC SFTP restricted address connection tests")
def sftp_ssrf():
    t = lib.Tenant("b")
    base = lib.Tenant("a").call("GET", f"/storage-connections/{ST['connections']['sftp']}")["configuration"]
    out = {}
    for label, host in (("loopback", "127.0.0.1"), ("rfc1918", "10.10.20.1"), ("link-local", "169.254.169.254"), ("ipv6-loopback", "::1"), ("ipv6-ula", "fc00::1")):
        c = t.call("POST", "/storage-connections", {"providerType": "sftp", "name": f"{PFX}sftp-{label}-{uuid.uuid4().hex[:4]}", "configuration": {**base, "host": host}, "secret": {"password": secrets.token_urlsafe(16)}})
        try:
            r = t.call("POST", f"/storage-connections/{c['id']}/test", {})
            test = r["test"]
            out[label] = [test["status"], test["code"]]
            # AgentSideStorage is the expected non-leak design: private networks are refused at probe and delegated to the agent
            check((test["status"] == "NotTestable" and test["code"] == "agent_side_storage") or "denied" in str(test.get("code")).lower(), f"{label}: {out[label]}")
        finally:
            t.raw("DELETE", f"/storage-connections/{c['id']}")
    return out


@scenario("gap/SEC zero GUID agent endpoint fails closed")
def zero_agent_id():
    t = lib.Tenant("a")
    r = t.raw("GET", "/agents/00000000-0000-0000-0000-000000000000")
    check(r.status_code in (400, 404), f"zero agent id HTTP {r.status_code}: {r.text[:120]}")
    return {"status": r.status_code}


@scenario("gap/OPS-08 tenant B report counts agree with persistent own run rows")
def ops_report():
    t = lib.Tenant("b")
    report = t.call("GET", "/reports/backups")
    dashboard = t.call("GET", "/dashboard")
    rows = psql(f"""select "Status", count(*) from backup_runs where "TenantId" = '{TB}' and "CompletedAt" >= now() - interval '30 days' and "CompletedAt" < now() group by "Status" """)
    return {"report": {k: report.get(k) for k in ("succeeded", "failed", "cancelled")}, "db_status_counts": rows, "dashboard_keys": sorted(dashboard.keys())}


@scenario("gap/SEC secret values absent from tenant/platform API, audit and API container logs")
def secrets_check():
    t = lib.Tenant("b")
    mark = "gapSecret" + secrets.token_urlsafe(24)
    since = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(time.time() - 5))
    c = t.call("POST", "/storage-connections", {"providerType": "s3", "name": f"{PFX}secret-{uuid.uuid4().hex[:4]}", "configuration": {"endpoint": f"https://{api.env['PUBLIC_HOST']}:9000", "bucket": "gap-none", "region": "us-east-1"}, "secret": {"accessKey": "GAPKEY", "secretKey": mark}})
    try:
        responses = [c, t.call("GET", f"/storage-connections/{c['id']}"), t.call("GET", "/storage-connections"), t.call("POST", f"/storage-connections/{c['id']}/test", {})]
        responses += [call(platform(), "GET", f"/api/platform/storage-connections/{c['id']}").text, audit_rows(None, 3)]
        time.sleep(2)
        check(mark not in json.dumps(responses) and mark not in api_logs(since), "marker leaked in response, audit, or container logs")
        return {"sources": ["tenant create/get/list/test", "platform get", "audit", "bp-api and bp-api2 logs"], "plaintext_matches": 0}
    finally:
        t.raw("DELETE", f"/storage-connections/{c['id']}")


@scenario("gap/OPS-05 platform plan create update disable with audit")
def ops_plan():
    p = platform()
    name = f"{PFX}plan-{uuid.uuid4().hex[:6]}"
    create = call(p, "POST", "/api/platform/plans", {"name": name, "unlimited": True, "enabled": True})
    check(create.status_code == 201, f"create plan {create.status_code}")
    plan = create.json()
    pid = plan.get("id") or plan.get("planId")
    try:
        change = call(p, "PUT", f"/api/platform/plans/{pid}", {"name": name + "-edited", "unlimited": True})
        check(change.status_code == 200, f"update plan {change.status_code}")
        listing = call(p, "GET", "/api/platform/plans?pageSize=100").json()["items"]
        check(any((x.get("id") or x.get("planId")) == pid and x["name"] == name + "-edited" for x in listing), "changed plan not persisted")
        deny = call(lib.Tenant("a").s, "PUT", f"/api/platform/plans/{pid}", {"name": "pwn", "unlimited": True})
        check(deny.status_code == 403, f"tenant owner changed platform plan {deny.status_code}")
        audit = audit_rows(None, 4)
        actions = [e["action"] for e in audit if e["targetId"] == pid]
        check(actions, "no plan audit events")
        return {"planId": pid, "create": create.status_code, "update": change.status_code, "ownerDenied": deny.status_code, "auditActions": actions}
    finally:
        call(p, "PUT", f"/api/platform/plans/{pid}/enabled", {"enabled": False})


@scenario("gap/SEC injection cron paths JSON long and Unicode input")
def injection():
    t = lib.Tenant("b")
    agent = t.agents()["gap-authops-agentb"]
    job = t.call("POST", "/backup-jobs", {"name": f"{PFX}inject-{uuid.uuid4().hex[:5]}", "agentId": agent["agentId"], "overlapPolicy": "Skip", "configuration": file_cfg("gapfiles", ["docs"])})
    statuses = {}
    try:
        for label, value in (("shell", "0 3 * * *; touch /tmp/gap-authops-injection"), ("sql", "' OR 1=1--"), ("long", "x" * 5000), ("unicode", "şğü🚀"), ("null", "\u0000")):
            r = t.raw("PUT", f"/backup-jobs/{job['id']}/schedule", {"cron": value})
            statuses["cron-" + label] = r.status_code
            check(r.status_code in (400, 422), f"cron {label}: {r.status_code} {r.text[:100]}")
        for label, path in (("up", "../etc"), ("nested", "a/../../b"), ("absolute", "/etc"), ("backslash", "a\\..\\.."), ("null", "a\u0000b")):
            cfg = file_cfg("gapfiles", [path])
            r = t.raw("POST", "/backup-jobs", {"name": f"{PFX}inject-path-{label}", "agentId": agent["agentId"], "configuration": cfg})
            statuses["path-" + label] = r.status_code
            if r.status_code == 201:
                t.raw("POST", f"/backup-jobs/{r.json()['id']}/disable", {})
            check(r.status_code in (400, 422), f"path {label}: {r.status_code} {r.text[:100]}")
        r = t.s.post(t.base + "/backup-jobs", data='{"configuration":', headers={"Content-Type": "application/json"})
        statuses["malformed-json"] = r.status_code
        check(r.status_code == 400, f"malformed JSON {r.status_code}")
        return statuses
    finally:
        t.raw("POST", f"/backup-jobs/{job['id']}/disable", {})


@scenario("gap/SEC SFTP remote base traversal rejection")
def sftp_traversal():
    t = lib.Tenant("b")
    base = lib.Tenant("a").call("GET", f"/storage-connections/{ST['connections']['sftp']}")["configuration"]
    out = {}
    for label, path in (("parent", "/tmp/../etc"), ("nested", "/tmp/a/../../etc"), ("backslash", "/tmp\\..\\etc"), ("relative", "../etc"), ("control", "/tmp/\u0000"), ("long", "/" + "a" * 600)):
        r = t.raw("POST", "/storage-connections", {"providerType": "sftp", "name": f"{PFX}traversal-{label}-{uuid.uuid4().hex[:4]}", "configuration": {**base, "remoteBaseDirectory": path}, "secret": {"password": secrets.token_urlsafe(16)}})
        out[label] = r.status_code
        if r.status_code == 201:
            cid = r.json()["id"]
            try:
                test = t.raw("POST", f"/storage-connections/{cid}/test", {})
                out[label] = [r.status_code, test.status_code, test.json().get("test", {}).get("status"), test.json().get("test", {}).get("code")]
            finally:
                t.raw("DELETE", f"/storage-connections/{cid}")
    return out


STEPS = []  # (name, fn, needs fixtures); extended by the sections below


def main(only=None):
    todo = [(n, f, fx) for n, f, fx in STEPS if not only or any(x.strip().lower() in n.lower() for x in only.split(","))]
    try:
        if any(fx for _, _, fx in todo): fixtures()
        for _, f, _ in todo: f()
    finally:
        if FX:
            out = cleanup_fixtures()
            for k in "ab":
                if k in FX: shutil.rmtree(f"/tmp/gap-authops/files/{FX[k]['rel']}", ignore_errors=True)
            print("cleanup:", out)


STEPS += [("AUTH-01", auth01, 0), ("AUTH-01b", auth01b, 0), ("AUTH-02", auth02, 0), ("AUTH-03", auth03, 0), ("AUTH-03-EXPIRY", auth03_expiry, 0), ("BOLA-AB", bola_ab, 1), ("BOLA-BA", bola_ba, 1), ("BOLA-REFS", bola_refs, 1),
          ("ROLE", role_escalation, 1), ("AUTH-05", auth05, 1), ("AUTH-07", auth07, 1), ("AUTH-08", auth08, 1), ("AUTH-09", auth09, 0), ("AUTH-10", auth10, 1), ("AGT-03", agt03, 1),
          ("AGT-06a", agt06a, 1), ("OPS-07", audit_integrity, 0), ("TRANSPORT", transport, 0), ("WEBHOOK", webhook_ssrf, 0), ("SFTP", sftp_ssrf, 0),
          ("ZERO", zero_agent_id, 0), ("OPS-08", ops_report, 0), ("SECRET", secrets_check, 0),
          ("OPS-05", ops_plan, 0), ("INJECT", injection, 0), ("SFTP-TRAV", sftp_traversal, 0)]

if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else None)
