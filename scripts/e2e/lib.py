"""E2E scenario harness: API helpers, remote command runners and PASS/FAIL recording (results.jsonl in docs/test-environment)."""
import api, base64, json, subprocess, time, uuid, yaml, pathlib
ST = json.loads((api.ROOT / "docs/test-environment/state.json").read_text())
CRED = yaml.safe_load((api.ROOT / "docs/test-cred.yaml").read_text())
RESULTS = api.ROOT / "docs/test-environment/results.jsonl"
NOISE = ("quantum", "vulnerable", "upgraded", "**", "CLIXML", "<Objs")
TERMINAL = ("Succeeded", "Failed", "Cancelled", "AgentLost", "Skipped")

class Tenant:
    def __init__(self, key="a"):
        t = ST[key]; self.id = t["tenantId"]; self.s = api.login(t["login"], t["password"]); self.base = f"{api.BASE}/api/tenants/{self.id}"
    def call(self, method, path, body=None, expect=(200, 201, 202, 204), key=None):
        r = self.s.request(method, self.base + path, json=body, headers={"Idempotency-Key": key or uuid.uuid4().hex})
        if r.status_code not in expect:
            raise AssertionError(f"{method} {path} -> {r.status_code} {r.text[:400]}")
        return r.json() if r.content else None
    def raw(self, method, path, body=None):
        return self.s.request(method, self.base + path, json=body, headers={"Idempotency-Key": uuid.uuid4().hex})
    def agents(self):
        return {a["hostName"]: a for a in self.call("GET", "/agents")}
    def wait(self, path, timeout=600, interval=2, stop_on=TERMINAL + ("Paused",)):
        """Polls until the status is one of stop_on; Paused counts as stopped so a pause cannot hang a scenario."""
        end = time.time() + timeout
        while time.time() < end:
            d = self.call("GET", path)
            if d["status"] in stop_on:
                return d
            time.sleep(interval)
        raise TimeoutError(path)

def ssh(host, cmd, inp=None, timeout=600):
    r = subprocess.run(["ssh", host, cmd], input=inp, text=True, capture_output=True, timeout=timeout)
    out = "\n".join(l for l in (r.stdout + r.stderr).splitlines() if not any(x in l for x in NOISE))
    return r.returncode, out

def fedora(cmd, inp=None):
    """Runs a script on the Fedora VM (login shell there is fish, so always feed bash through stdin)."""
    return ssh("virtual-fedora", "bash -s", cmd if inp is None else inp + "\n" + cmd)

def win(ps, timeout=600):
    return ssh("virtual-win11", "powershell -NoProfile -EncodedCommand " + base64.b64encode(ps.encode("utf-16le")).decode(), timeout=timeout)

def record(name, ok, **detail):
    row = {"scenario": name, "result": "PASS" if ok else "FAIL", "at": time.strftime("%FT%T"), **detail}
    print(json.dumps(row, default=str), flush=True)
    with RESULTS.open("a") as f: f.write(json.dumps(row, default=str) + "\n")
    return ok

def scenario(name):
    def deco(fn):
        def run(*a, **k):
            try:
                detail = fn(*a, **k) or {}
                return record(name, True, **detail)
            except Exception as e:  # a scenario failure is a finding, not a crash
                return record(name, False, error=f"{type(e).__name__}: {e}"[:1500])
        run.__name__ = fn.__name__; run.scenario = name
        return run
    return deco
