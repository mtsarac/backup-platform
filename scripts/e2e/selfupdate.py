"""Agent self-update through a signed AgentUpdateManifest, on a throwaway update-capable Linux agent (the Fedora lab agent `fedora-e2e` uses the flat installer layout and is never touched).
The throwaway agent is installed with the product's own versioned installer (deploy/linux/install-agent.sh: releases/<version>, `current` link, supervisor) at 1.0.0 from the package the
api serves, pinned to the e2e release key (scripts/e2e/lab-selfupdate.py). The 1.0.1 release is built from this tree (`dotnet publish -p:Version=1.0.1`, cached under
docs/test-environment/agent-builds) and signed by the e2e key. Checks, in order, one pass, no retries:
  1. the api refuses to publish a manifest whose payload was altered after signing, and one signed with an untrusted key id;
  2. a signed 1.0.1 release is published; its stored manifest is then tampered with on the api's volume (the agent must not trust whoever serves it): the restarted agent rejects it,
     downloads and stages nothing, and stays on 1.0.0 online;
  3. the original manifest is restored: the agent updates itself, restarts from releases/1.0.1, reports version 1.0.1 online with the same AgentId, and acknowledges the new release.
Run: python3 selfupdate.py"""
import base64, datetime, hashlib, importlib, io, json, os, pathlib, shutil, subprocess, sys, tarfile, tempfile, time, uuid, zipfile
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


def sign(version, pkg, key, key_id=KEY_ID, rid=RID):
    """A manifest exactly as the platform defines it: ECDSA P-256/SHA-256 over the canonical payload string, signature as fixed-size r||s."""
    now = int(time.time()); url = f"{SERVER}/api/agent/updates/packages/{rid}/{version}"; sha = hashlib.sha256(pkg).hexdigest()
    canonical = f"{rid}:{version}:{OLD}:{sha}:{len(pkg)}:{url}:{now}".encode()
    r, s = utils.decode_dss_signature(key.sign(canonical, ec.ECDSA(hashes.SHA256())))
    return {"payload": {"version": version, "platform": rid, "minimumSupportedVersion": OLD, "packageSha256": sha, "packageSize": len(pkg), "downloadUrl": url,
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


def windows_package(directory, include_settings=False):
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(directory.rglob("*")):
            if path.is_file() and (include_settings or not path.name.lower().startswith("appsettings")):
                archive.write(path, path.relative_to(directory).as_posix())
    return buf.getvalue()


def windows_build(version):
    out = BUILDS / f"win-x64-{version}"
    if not (out / "BackupPlatform.Agent.Host.exe").exists():
        r = subprocess.run(["dotnet", "publish", "src/hosts/agent/BackupPlatform.Agent.Host.csproj", "-c", "Release", "-r", "win-x64",
                            "--self-contained", "true", "-p:AgentPlatform=Windows", f"-p:Version={version}", "-o", str(out)],
                           cwd=api.ROOT / "backup-backend", capture_output=True, text=True)
        check(r.returncode == 0, f"Windows dotnet publish failed: {r.stdout[-600:]} {r.stderr[-600:]}")
    return out


def windows_ps(host, script, secrets=()):
    r = subprocess.run(["ssh", host, "powershell -NoProfile -NonInteractive -Command -"],
                       input="$ErrorActionPreference='Stop'\ntry {\n" + script + "\n} catch { Write-Error $_; exit 1 }\n",
                       capture_output=True, text=True, timeout=300)
    error = r.stderr[-1000:]
    for secret in secrets: error = error.replace(secret, "[redacted]")
    check(r.returncode == 0, f"Windows command failed: {error}")
    return r.stdout.strip()


@scenario("agents/windows-self-update-signed-manifest-version-changes")
def windows_self_update(t):
    """Run only against a disposable Windows host and an isolated update server."""
    host = os.environ.get("E2E_WINDOWS_SELFUPDATE_HOST", "")
    check(host and not host.startswith("-") and all(c.isalnum() or c in ".-_@" for c in host),
          "Set E2E_WINDOWS_SELFUPDATE_HOST to a disposable Windows SSH host.")
    check(os.environ.get("E2E_WINDOWS_SELFUPDATE_ISOLATED") == "yes",
          "Set E2E_WINDOWS_SELFUPDATE_ISOLATED=yes only for a dedicated backend and Windows host.")
    check(lab.KEY.exists(), "Missing e2e signing key; configure the backend to trust it before running.")
    key = lab.signing_key(); pem = lab.public_pem()
    admin = api.login()
    rc, output = api_sh("test ! -e /app/agent-updates/win-x64")
    check(rc == 0, f"Windows release store must be absent on the isolated backend: {output}")
    windows_ps(host, """
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'SSH PowerShell must run elevated.' }
if (Get-Service BackupAgent -ErrorAction SilentlyContinue) { throw 'BackupAgent already exists; use a disposable host.' }
if (Get-ScheduledTask -TaskName BackupAgentUpdate -ErrorAction SilentlyContinue) { throw 'BackupAgentUpdate already exists; use a disposable host.' }
""")
    name = f"selfupdate-win-e2e-{uuid.uuid4().hex[:8]}"
    marker = uuid.uuid4().hex
    remote_zip = f"bp-selfupdate-{marker}.zip"
    root = f"C:\\Program Files\\bp-selfupdate-{marker}"
    store = f"C:\\ProgramData\\bp-selfupdate-{marker}"
    installer = api.ROOT / "backup-backend/deploy/windows"
    old = windows_build(OLD)
    pkg = windows_package(windows_build(NEW))
    token = subprocess.run(["python3", "enroll.py", "a"], cwd=api.ROOT / "scripts/e2e", capture_output=True, text=True, check=True).stdout.strip()
    with tempfile.TemporaryDirectory(prefix="e2e-win-update-") as tmp:
        bundle = pathlib.Path(tmp) / remote_zip
        with zipfile.ZipFile(bundle, "w", compression=zipfile.ZIP_DEFLATED) as archive:
            for path in old.rglob("*"):
                if path.is_file(): archive.write(path, "bin/" + path.relative_to(old).as_posix())
            for script in ("Install-BackupAgentService.ps1", "Uninstall-BackupAgentService.ps1"):
                archive.write(installer / script, script)
        subprocess.run(["scp", "-q", str(bundle), f"{host}:{remote_zip}"], check=True)
        created = False
        try:
            created = True
            windows_ps(host, f"""
$root = '{root}'; $store = '{store}'; $bundle = Join-Path $HOME '{remote_zip}'
if ((Test-Path $root) -or (Test-Path $store) -or (Test-Path "$store.updates")) {{ throw 'Scenario paths already exist.' }}
New-Item -ItemType Directory -Path $root | Out-Null
New-Item -ItemType File -Path "$root\\.e2e-owned" | Out-Null
& icacls.exe $root /inheritance:r /grant '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
if ($LASTEXITCODE -ne 0) {{ throw 'Could not protect the scenario binary directory.' }}
Expand-Archive -LiteralPath $bundle -DestinationPath $root
& "$root\\Install-BackupAgentService.ps1" -BinaryDirectory "$root\\bin" -StoreDirectory $store -BackendUri '{SERVER}' -EnrollmentToken '{token}' -AgentName '{name}' -TrustedUpdateKeys @{{ e2e = @'
{pem}
'@
}}
""", secrets=(token,))
            before = wait_agent(t, name, OLD)
            r = publish(admin, sign(NEW, pkg, key, rid="win-x64"), pkg)
            check(r.status_code == 201, f"Windows release publish failed: {r.status_code} {r.text[:200]}")
            windows_ps(host, "Restart-Service BackupAgent")
            after = wait_agent(t, name, NEW, timeout=300)
            check(after["agentId"] == before["agentId"], "Windows update changed the agent identity")
            end = time.time() + 90
            state = None
            while time.time() < end:
                output = windows_ps(host, f"""
$control = '{root}\\bin\\update-control'
$active = Get-Content "$control\\active.json" -Raw | ConvertFrom-Json
$service = Get-CimInstance Win32_Service -Filter "Name='BackupAgent'"
@{{ version = $active.Version; executable = $active.Executable; path = $service.PathName; status = $service.State;
   pending = (Test-Path "$control\\pending.json"); activation = (Test-Path "$control\\activation");
   healthy = $(if (Test-Path '{store}.updates\\healthy') {{ Get-Content '{store}.updates\\healthy' -Raw }} else {{ $null }}) }} | ConvertTo-Json -Compress
""")
                state = json.loads(output)
                expected = f"{root}\\bin\\releases\\{NEW}\\BackupPlatform.Agent.Host.exe"
                if (state["version"] == NEW and state["executable"].lower() == expected.lower()
                    and state["path"].strip('"').lower() == expected.lower() and state["status"] == "Running"
                    and not state["pending"] and not state["activation"] and state["healthy"] == NEW):
                    break
                time.sleep(2)
            else:
                raise AssertionError(f"Windows update was not acknowledged: {state}")
            return {"agent": name, "before": OLD, "after": NEW, "online": after["connectivity"], "identityPreserved": True}
        finally:
            try:
                a = agent_now(t, name)
                if a:
                    response = t.raw("POST", f"/agents/{a['agentId']}/revoke", {})
                    check(response.status_code in (200, 201, 202, 204), f"Agent revocation failed: {response.status_code}")
            finally:
                if created:
                    windows_ps(host, f"""
$root = '{root}'; $store = '{store}'
if (Test-Path "$root\\.e2e-owned") {{
    $service = Get-CimInstance Win32_Service -Filter "Name='BackupAgent'"
    if ($service -and $service.PathName -like "*$root\\bin\\*") {{
        & "$root\\Uninstall-BackupAgentService.ps1" -RemoveIdentity -StoreDirectory $store
    }} elseif ($service) {{ throw 'Another BackupAgent service exists; refusing cleanup.' }}
    Remove-Item -LiteralPath $root,"$store.updates" -Recurse -Force -ErrorAction SilentlyContinue
}}
Remove-Item -LiteralPath (Join-Path $HOME '{remote_zip}') -Force -ErrorAction SilentlyContinue
""")


if __name__ == "__main__":
    (windows_self_update if sys.argv[1:] == ["--windows"] else self_update)(lib.Tenant("a"))
