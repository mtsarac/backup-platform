"""Shared helpers for the gap_sp_* scripts (StoragePolicyGaps). Private throwaway tenants (gap-sp-<key>) with their own agent on this PC, so quota,
plan and bandwidth changes never touch tenant A. State (with generated secrets) lives in /tmp/gap-sp/state.json (0600), not in the repo.
Scenario names are `gap/<ID>[/detail]`; every scenario appends a PASS/FAIL row to results.jsonl through lib.scenario."""
import json, os, pathlib, secrets, shutil, subprocess, time, uuid
import api, lib, flows
from lib import ST, scenario

PFX = "gap-sp-"
DIR = pathlib.Path("/tmp/gap-sp"); STATE = DIR / "state.json"
PKG = os.path.expanduser("~/.local/share/bpe2e-host-agent/pkg/backup-agent-linux-x64-1.0.0/bin")
# the agent uploads to MinIO itself (TLS), so its trust file holds the app certificate AND the MinIO certificate
CERT = "/tmp/gap-sp/ca.pem"
MB = 1024 * 1024


def _ca():
    c = api.ROOT / "docs/test-environment/certs"; DIR.mkdir(parents=True, exist_ok=True); pathlib.Path(CERT).write_text((c / "cert.pem").read_text() + (c / "minio/public.crt").read_text())


def _load(): return json.loads(STATE.read_text()) if STATE.exists() else {}
def _save(st): DIR.mkdir(parents=True, exist_ok=True); STATE.write_text(json.dumps(st, indent=1)); STATE.chmod(0o600)


def sh(*cmd, inp=None, check=False, timeout=600, env=None):
    r = subprocess.run(list(cmd), input=inp, capture_output=True, text=True, timeout=timeout, env=env)
    if check and r.returncode: raise RuntimeError(f"{cmd[:3]} -> {r.returncode}: {(r.stderr or r.stdout)[:300]}")
    return r.returncode, (r.stdout + r.stderr).strip()


class GT(lib.Tenant):
    """Tenant client for a gap-sp tenant (same API as lib.Tenant, credentials from /tmp/gap-sp/state.json)."""
    def __init__(self, key):
        t = _load()[key]; self.id = t["tenantId"]; self.key = key
        self.s = api.login(t["login"], t["password"]); self.base = f"{api.BASE}/api/tenants/{self.id}"


def ensure_tenant(key, plan="unlimited"):
    """Creates tenant gap-sp-<key>, an Owner user, enables agent enrollment and assigns the plan. Idempotent."""
    st = _load()
    if key in st: return GT(key)
    adm = api.login()
    def ok(r):
        assert r.ok, f"{r.request.method} {r.url} -> {r.status_code} {r.text[:300]}"; return r.json()
    t = ok(adm.post(f"{api.BASE}/api/platform/tenants", json={"name": f"{PFX}{key}"}))
    pw = secrets.token_urlsafe(18) + "Aa1!"
    u = ok(adm.post(f"{api.BASE}/api/platform/users", json={"loginIdentifier": f"{PFX}{key}-owner", "displayName": f"gap-sp {key} owner", "password": pw}))
    tid = t.get("tenantId") or t.get("id"); uid = u.get("userId") or u.get("id")
    ok(adm.post(f"{api.BASE}/api/platform/tenants/{tid}/memberships", json={"userId": uid, "role": "Owner"}))
    ok(adm.put(f"{api.BASE}/api/platform/tenants/{tid}/agent-enrollment", json={"enabled": True}))
    st[key] = {"tenantId": tid, "login": f"{PFX}{key}-owner", "password": pw, "userId": uid}; _save(st)
    if plan: assign_plan(tid, ST["plans"][plan])
    return GT(key)


def assign_plan(tid, plan_id, **extra):
    r = api.login().put(f"{api.BASE}/api/platform/tenants/{tid}/subscription", json={"planId": plan_id, **extra})
    assert r.ok or (r.status_code == 409 and r.json().get("error") == "plan_already_assigned"), f"assign plan: {r.status_code} {r.text[:300]}"
    return r


def agent_root(name): return pathlib.Path(os.path.expanduser(f"~/.local/share/{PFX}{name}"))


def ensure_agent(key, name, sftp_allow=None, extra_env=None):
    """Enrolls (once) and starts a user-mode agent `<PFX><name>` on this PC in tenant gap-sp-<key>. Own store, staging, local store and file source `gapfiles`
    (<root>/files). `sftp_allow` (CIDR list) makes the SFTP allowlist explicit. Returns the agent record."""
    _ca(); t = GT(key); root = agent_root(name); unit = f"{PFX}{name}-agent"
    for d in ("identity", "staging", "local-store", "files"): (root / d).mkdir(parents=True, exist_ok=True)
    for d in (root, root / "identity"): d.chmod(0o700)
    if not (root / "identity/agent-identity.json").exists():
        tok = t.s.post(f"{t.base}/agent-enrollment-tokens", json={}); tok.raise_for_status()
        env = {**os.environ, "SSL_CERT_FILE": CERT}
        rc, out = sh(f"{PKG}/BackupPlatform.Agent.Host", "enroll", "--server", api.BASE, "--token", tok.json()["token"], "--store-directory", str(root / "identity"),
                     "--non-interactive", "--name", f"{PFX}{name}", env=env)
        assert rc == 0, "enroll failed: " + out.replace(tok.json()["token"], "***")[-400:]
    envs = {"Agent__Backend__BaseUri": api.BASE, "Agent__CredentialStoreDirectory": str(root / "identity"), "SSL_CERT_FILE": CERT,
            "Agent__Backup__Staging__Mapping": "SameHost", "Agent__Backup__Staging__AgentDirectory": str(root / "staging"),
            "Agent__Backup__LocalStorage__RootDirectory": str(root / "local-store"), "Agent__Backup__FileSources__gapfiles__RootDirectory": str(root / "files")}
    for i, c in enumerate(sftp_allow or []): envs[f"Agent__Backup__Sftp__AllowedPrivateNetworks__{i}"] = c
    envs.update(extra_env or {})
    if sh("systemctl", "--user", "is-active", unit)[1] != "active":
        args = ["systemd-run", "--user", f"--unit={unit}", "--collect", "-p", "UMask=0077", "-p", "Restart=on-failure", "-p", "RestartSec=10", f"--working-directory={PKG}"]
        for k, v in envs.items(): args += ["-E", f"{k}={v}"]
        sh(*args, f"{PKG}/BackupPlatform.Agent.Host", check=True)
    end = time.time() + 90
    while time.time() < end:
        a = t.agents().get(f"{PFX}{name}")
        if a and a.get("status") == "Active" and any(c["identifier"] == "backup.multi-destination" and c["support"] == "Supported" for c in a.get("capabilities") or []): return a
        time.sleep(3)
    raise TimeoutError(f"agent {PFX}{name} did not become Active with its capabilities reported in tenant {key}")


def stop_agent(name): return sh("systemctl", "--user", "stop", f"{PFX}{name}-agent")
def start_agent(name):  # a stopped transient unit is gone (--collect), so it is re-created by ensure_agent
    return None


def put_file(name, rel, size, seed=None):
    """Incompressible deterministic-size file in the agent's file source (random bytes)."""
    p = agent_root(name) / "files" / rel; p.parent.mkdir(parents=True, exist_ok=True)
    with open(p, "wb") as f:
        left = size
        while left: n = min(left, MB); f.write(os.urandom(n)); left -= n
    return p


def gdest(provider="local", **kw):
    """Destination dict: local (agent-direct) or a storage connection id."""
    if provider == "local": return flows.local_dest(kw.pop("target", "default"), kw.pop("primary", True))
    return {"storageProvider": provider, "storageConnectionId": kw.pop("conn"), "storageExecutionLocation": kw.pop("loc", "Backend"), "isPrimary": kw.pop("primary", True)}


def gcfg(dests, paths=None, **extra):
    cfg = flows.file_job_config("gapfiles", dests, paths=paths or ["."], **extra)
    cfg["files"].update(includes=["**"], excludes=[], symlinkPolicy="Skip"); return cfg


def new_job(t, agent, name, cfg, **extra):
    return t.call("POST", "/backup-jobs", {"name": f"{PFX}{name} {uuid.uuid4().hex[:5]}", "agentId": agent["agentId"], "overlapPolicy": "Skip", "configuration": cfg, **extra})


def start_run(t, job):
    r = t.call("POST", f"/backup-jobs/{job['id']}/runs", {}); return r.get("id", r.get("run", {}).get("id"))


def arts(detail): return [a for at in detail["attempts"] for a in at["artifacts"]]


def run_to_end(t, job, timeout=900, stop_on=lib.TERMINAL):
    rid = start_run(t, job); return rid, t.wait(f"/backup-runs/{rid}", timeout=timeout, stop_on=stop_on)


def cleanup_tenant_objects(t):
    """Disables and deletes every job and artifact of a gap-sp tenant (used between scenarios)."""
    for j in t.call("GET", "/backup-jobs?pageSize=100")["items"]:
        t.raw("POST", f"/backup-jobs/{j['id']}/disable", {})
