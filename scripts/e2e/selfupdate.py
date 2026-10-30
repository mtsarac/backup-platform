"""Agent self-update through a signed AgentUpdateManifest, on a throwaway update-capable Linux agent (the Fedora lab agent `fedora-e2e` uses the flat installer layout and is never touched).
The throwaway agent is installed with the product's own versioned installer (deploy/linux/install-agent.sh: releases/<version>, `current` link, supervisor) at 1.0.0 from the package the
api serves, pinned to the e2e release key (scripts/e2e/lab-selfupdate.py). The 1.0.1 release is built from this tree (`dotnet publish -p:Version=1.0.1`, cached under
docs/test-environment/agent-builds) and signed by the e2e key. Checks, in order, one pass, no retries:
  1. the api refuses to publish a manifest whose payload was altered after signing, and one signed with an untrusted key id;
  2. a signed 1.0.1 release is published; its stored manifest is then tampered with on the api's volume (the agent must not trust whoever serves it): the restarted agent rejects it,
     downloads and stages nothing, and stays on 1.0.0 online;
  3. the original manifest is restored: the agent updates itself, restarts from releases/1.0.1, reports version 1.0.1 online with the same AgentId, and acknowledges the new release.
Run: python3 selfupdate.py"""
import base64, datetime, hashlib, importlib, io, json, os, pathlib, shutil, subprocess, tarfile, tempfile, time, uuid
from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.asymmetric import ec, utils
import api, lib
from lib import scenario

lab = importlib.import_module("lab-selfupdate")
KEY_ID, RID, OLD, NEW = "e2e", "linux-x64", "1.0.0", "1.0.1"
SERVER = api.BASE
ROOT, STATE, ETC, UNIT = "/opt/backup-platform-agent", "/var/lib/backup-platform-agent", "/etc/backup-platform-agent", "backup-platform-agent"
STORE = "/app/agent-updates"  # the api container's release store (AgentUpdates:PackagesDirectory default)
RELEASES = f"{STORE}/{RID}"
BUILDS = api.ROOT / "docs/test-environment/agent-builds"
DEPLOY = api.ROOT / "backup-backend/deploy/linux"


def check(cond, msg):
    if not cond: raise AssertionError(msg)


def build(version):
    """dotnet publish of the Linux agent at `version`, same switches as the api Dockerfile; cached (delete the directory to rebuild)."""
    out = BUILDS / f"{RID}-{version}"
    if not (out / "BackupPlatform.Agent.Host").exists():
        r = subprocess.run(["dotnet", "publish", "src/hosts/agent/BackupPlatform.Agent.Host.csproj", "-c", "Release", "-r", RID, "--self-contained", "true", "-p:AgentPlatform=Linux",
                            f"-p:Version={version}", "-o", str(out)], cwd=api.ROOT / "backup-backend", capture_output=True, text=True)
        check(r.returncode == 0, f"dotnet publish failed: {r.stdout[-600:]}")
    return out


def package(directory):
    """The release archive the agent accepts: flat ustar tar.gz of the publish output, no runtime configuration (the agent refuses packages that carry appsettings*), no owners."""
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w:gz", format=tarfile.USTAR_FORMAT) as tar:
        for path in sorted(directory.rglob("*")):
            if path.name.startswith("appsettings"): continue
            info = tar.gettarinfo(str(path), arcname=path.relative_to(directory).as_posix())
            info.uid = info.gid = 0; info.uname = info.gname = ""
            info.mode = 0o755 if path.is_dir() or os.access(path, os.X_OK) else 0o644
            tar.addfile(info, path.open("rb") if path.is_file() else None)
    return buf.getvalue()


def sign(version, pkg, key, key_id=KEY_ID):
    """A manifest exactly as the platform defines it: ECDSA P-256/SHA-256 over the canonical payload string, signature as fixed-size r||s."""
    now = int(time.time()); url = f"{SERVER}/api/agent/updates/packages/{RID}/{version}"; sha = hashlib.sha256(pkg).hexdigest()
    canonical = f"{RID}:{version}:{OLD}:{sha}:{len(pkg)}:{url}:{now}".encode()
    r, s = utils.decode_dss_signature(key.sign(canonical, ec.ECDSA(hashes.SHA256())))
    return {"payload": {"version": version, "platform": RID, "minimumSupportedVersion": OLD, "packageSha256": sha, "packageSize": len(pkg), "downloadUrl": url,
                        "publishedAt": datetime.datetime.fromtimestamp(now, datetime.timezone.utc).isoformat()},
            "keyId": key_id, "signatureBase64": base64.b64encode(r.to_bytes(32, "big") + s.to_bytes(32, "big")).decode()}


def publish(admin, manifest, pkg):
    return admin.post(f"{api.BASE}/api/platform/agent-updates", json={"manifest": manifest, "packageBase64": base64.b64encode(pkg).decode()},
                      headers={"Idempotency-Key": uuid.uuid4().hex}, timeout=300)


def api_sh(cmd):
    r = subprocess.run(["docker", "exec", "bp-api", "sh", "-c", cmd], capture_output=True, text=True); return r.returncode, (r.stdout + r.stderr).strip()


def vm(cmd):
    rc, out = lib.fedora(cmd); return out.strip()


def agent_now(t, name):
    return t.agents().get(name)


def wait_agent(t, name, version, timeout=240):
    """Polls the platform's view until the agent is online at `version`."""
    end = time.time() + timeout; seen = None
    while time.time() < end:
        a = agent_now(t, name); seen = a and (a["agentVersion"], a["connectivity"])
        if seen == (version, "online"): return a
        time.sleep(3)
    raise AssertionError(f"{name} never reached version {version} online; last seen {seen}")


def install(t, name, pem):
    """The product's versioned installer at 1.0.0 (payload = the package the api serves), the e2e key pinned, enrolled as `name`, service started."""
    tok = subprocess.run(["python3", "enroll.py", "a"], capture_output=True, text=True, check=True).stdout.strip()
    vm("sudo rm -rf /tmp/bpupd; mkdir -p /tmp/bpupd")
    subprocess.run(["scp", "-q", *map(str, [DEPLOY / "install-agent.sh", DEPLOY / "backup-platform-agent.service", DEPLOY / "agent-environment.example"]), "virtual-fedora:/tmp/bpupd/"], check=True)
    out = vm(f"""set -e
mkdir -p /tmp/bpupd/base && curl -sf {SERVER}/api/agent-dist/packages/{RID} | tar xz -C /tmp/bpupd/base
sudo bash /tmp/bpupd/install-agent.sh "$(echo /tmp/bpupd/base/*/bin)" {OLD} 2>&1 | tail -2
sudo restorecon -RF {ROOT} {STATE} {ETC}  # the payload was unpacked under /tmp and cp -a keeps its SELinux label, which systemd may not execute""")
    settings = json.dumps({"Root": ROOT, "Staging": f"{STATE}/updates", "Store": f"{STATE}/identity", "RuntimeConfiguration": f"{ETC}/appsettings.json", "TrustedKeys": {KEY_ID: pem}})
    out += vm(f"""set -e
cat > /tmp/bpupd/update-settings.json <<'JSON'
{settings}
JSON
sudo install -m 0640 -o root -g backup-agent /tmp/bpupd/update-settings.json {ETC}/update-settings.json
cd /tmp && sudo -u backup-agent {ROOT}/current/BackupPlatform.Agent.Host enroll --server {SERVER} --token '{tok}' --store-directory {STATE}/identity --name {name} --non-interactive 2>&1 | tail -2
sudo systemctl start {UNIT}""")
    return out


def uninstall(t, name):
    a = agent_now(t, name)
    if a: t.raw("POST", f"/agents/{a['agentId']}/revoke", {})
    vm(f"sudo systemctl disable --now {UNIT} 2>&1; sudo rm -rf {ROOT} {STATE} {ETC} /etc/systemd/system/{UNIT}.service /tmp/bpupd; sudo systemctl daemon-reload")
    api_sh(f"rm -rf {RELEASES}")


@scenario("agents/self-update-signed-manifest-version-changes-tampered-rejected")
def self_update(t):
    key, pem = lab.signing_key(), lab.public_pem()
    admin = api.login(); name = f"selfupdate-e2e-{uuid.uuid4().hex[:4]}"
    pkg = package(build(NEW))
    api_sh(f"rm -rf {RELEASES}")  # a previous run's release would be published over (and an agent would update at once)
    tmp = pathlib.Path(tempfile.mkdtemp(prefix="e2e-manifest-"))

    # 1. the api refuses a manifest altered after signing and one signed by a key it does not trust (the signature is checked before the package, so a stub package is enough)
    forged = sign(NEW, pkg, key); forged["payload"]["packageSha256"] = hashlib.sha256(b"another package").hexdigest()
    r = publish(admin, forged, b"stub"); check((r.status_code, r.json().get("error")) == (400, "invalid_manifest_signature"), f"altered manifest published: {r.status_code} {r.text[:150]}")
    r = publish(admin, sign(NEW, pkg, key, key_id="unknownkey"), b"stub"); check((r.status_code, r.json().get("error")) == (400, "untrusted_signing_key"), f"untrusted key id: {r.status_code} {r.text[:150]}")
    r = publish(admin, sign(NEW, pkg, ec.generate_private_key(ec.SECP256R1())), pkg); check((r.status_code, r.json().get("error")) == (400, "invalid_manifest_signature"), f"signature by another key: {r.status_code} {r.text[:150]}")
    check(api_sh(f"ls {RELEASES} 2>&1")[1].endswith("No such file or directory"), "a refused manifest left a release behind")

    try:
        install(t, name, pem)
        before = wait_agent(t, name, OLD)
        check(vm(f"readlink {ROOT}/current") == f"releases/{OLD}", "the new agent is not on the installed 1.0.0 release")

        # 2. a signed release is published, then its stored manifest is tampered with on the api's volume: the agent must reject it on its own
        r = publish(admin, sign(NEW, pkg, key), pkg); check(r.status_code == 201, f"publish of the signed release: {r.status_code} {r.text[:200]}")
        manifest_path = f"{RELEASES}/{NEW}/manifest.json"
        subprocess.run(["docker", "cp", f"bp-api:{manifest_path}", str(tmp / "good.json")], check=True, capture_output=True)
        bad = json.loads((tmp / "good.json").read_text()); bad["Payload"]["PublishedAt"] = "2027-01-01T00:00:00+00:00"  # nothing but the signature covers this field, so only a signature check can refuse the manifest
        (tmp / "bad.json").write_text(json.dumps(bad))
        subprocess.run(["docker", "cp", str(tmp / "bad.json"), f"bp-api:{manifest_path}"], check=True, capture_output=True)
        since = vm("date +%s")
        vm(f"sudo systemctl restart {UNIT}")
        deferred, end = "", time.time() + 120
        while time.time() < end and "Agent update deferred (InvalidDataException)" not in deferred:
            time.sleep(3); deferred = vm(f"sudo journalctl -u {UNIT} --since @{since} --no-pager")
        check("Agent update deferred (InvalidDataException)" in deferred, f"the agent never reported rejecting the tampered manifest; journal: {deferred[-500:]}")
        wait_agent(t, name, OLD, timeout=90)
        state = vm(f"readlink {ROOT}/current; ls -A {ROOT}/releases; sudo ls -A {STATE}/updates; test ! -e {ROOT}/.pending && echo no-pending")
        check(state.splitlines() == [f"releases/{OLD}", OLD, "no-pending"], f"after the rejection the agent has staged or activated something: {state!r}")

        # 3. the genuine manifest is back: the agent updates itself and comes back online with a new version and the same identity
        subprocess.run(["docker", "cp", str(tmp / "good.json"), f"bp-api:{manifest_path}"], check=True, capture_output=True)
        vm(f"sudo systemctl restart {UNIT}")
        after = wait_agent(t, name, NEW)
        check(after["agentId"] == before["agentId"], f"the agent identity changed: {before['agentId']} -> {after['agentId']}")
        end = time.time() + 60
        while time.time() < end and vm(f"test -e {ROOT}/.pending && echo pending") == "pending": time.sleep(2)
        state = vm(f"readlink {ROOT}/current; readlink {ROOT}/.previous; ls -A {ROOT}/releases; test ! -e {ROOT}/.pending && echo acknowledged; systemctl is-active {UNIT}; pgrep -af {ROOT}/releases/{NEW}/BackupPlatform.Agent.Host | wc -l")
        check(state.splitlines() == [f"releases/{NEW}", f"releases/{OLD}", OLD, NEW, "acknowledged", "active", "1"], f"after the update: {state!r}")
        # The WebRTC download helper ships in the release; an update that drops its execute bit silently ends agent-direct downloads on that agent.
        helper = f"{ROOT}/releases/{NEW}/backup-direct-download"
        check(vm(f"sudo test -x {helper} && echo yes") == "yes", "the updated release's backup-direct-download is not executable: " + vm(f"sudo ls -l {helper} {ROOT}/releases/{OLD}/backup-direct-download"))
        return {"agent": name, "before": before["agentVersion"], "after": after["agentVersion"], "online": after["connectivity"], "rejected": "Agent update deferred (InvalidDataException)", "releases": [OLD, NEW]}
    finally:
        uninstall(t, name)
        shutil.rmtree(tmp, ignore_errors=True)


if __name__ == "__main__":
    self_update(lib.Tenant("a"))
