"""Feature flags: every flagged feature answers 404 / is absent while its flag is off and works once the operator turns it on.
sms, smb, sizeAnomaly, replicaRepair (default ON) and externalSecrets are API flags: bp-api is recreated with Features__<key> flipped
(docker-compose.e2e.override.yml reads E2E_FEATURE_<KEY>) and always put back to the product defaults. windowsVss and linuxSnapshots are agent flags: the
agent host refuses to start with a file source configured for that snapshot kind. Run: python3 flags.py [filter]"""
import os, subprocess, sys, time, uuid, requests, api, lib
from lib import scenario

COMPOSE = ["docker", "compose", "-p", "bpe2e", "--env-file", ".env.e2e", "-f", "docker-compose.yml", "-f", "docker-compose.e2e.override.yml"]
GATEWAY = {"enabled": True, "endpoint": "https://sms.e2e.test/send", "authentication": "none"}
PHONE = "+905551230000"
DEFAULTS = {"sms": False, "smb": False, "sizeAnomaly": False, "externalSecrets": False, "replicaRepair": True}

def features():
    return requests.get(f"{api.BASE}/api/features", verify=False, timeout=5).json()

def recreate_api(env):
    """Recreates only the api container, then restarts the frontend because nginx resolves `api` once at start."""
    subprocess.run(COMPOSE + ["up", "-d", "--no-build", "api"], cwd=api.ROOT, env={**os.environ, **env}, check=True, capture_output=True)
    subprocess.run(["docker", "restart", "bp-frontend"], check=True, capture_output=True)

def set_flags(**flags):
    """Restarts the api with the given flags (the rest at their product defaults) and waits until /api/features reports them."""
    want = {**DEFAULTS, **flags}
    recreate_api({f"E2E_FEATURE_{key.upper()}": str(on).lower() for key, on in want.items()})
    end = time.time() + 180
    while time.time() < end:
        try:
            got = features()
            if all(got.get(key) is on for key, on in want.items()): return
        except (requests.RequestException, ValueError): pass
        time.sleep(3)
    raise TimeoutError(f"api did not come back with {want}")


def sms_requests(admin, t):
    """Every SMS-specific call and the status it must answer while the feature is off."""
    transports = f"{api.BASE}/api/platform/notification-transports"
    return {
        "PUT transport": admin.put(f"{transports}/sms", json=GATEWAY),
        "PUT secret": admin.put(f"{transports}/sms/secret", json={"value": "gateway-token"}),
        "DELETE secret": admin.delete(f"{transports}/sms/secret"),
        "POST channel": t.raw("POST", "/notifications/channels", {"kind": "sms", "name": "e2e-sms", "target": PHONE}),
    }

@scenario("flags/sms-off-rejects-on-works")
def sms_flag(t):
    admin = api.login()
    transports = f"{api.BASE}/api/platform/notification-transports"
    set_flags(sms=False)
    try:
        assert not features()["sms"]
        off = {name: r.status_code for name, r in sms_requests(admin, t).items()}
        assert all(c == 404 for c in off.values()), f"SMS API answered while off: {off}"
        assert "sms" not in admin.get(transports).json(), "transport list still shows SMS while off"
        assert t.raw("GET", "/notifications/channels").status_code == 200, "the rest of notifications must stay reachable"
        set_flags(sms=True)
        r = admin.put(f"{transports}/sms", json=GATEWAY); assert r.status_code == 200, f"enable gateway: {r.status_code} {r.text[:200]}"
        assert "sms" in admin.get(transports).json()
        r = t.raw("POST", "/notifications/channels", {"kind": "sms", "name": f"e2e-sms-{time.time_ns()}", "target": PHONE}); assert r.status_code == 201, f"create sms channel: {r.status_code} {r.text[:200]}"
        t.call("DELETE", f"/notifications/channels/{r.json()['id']}")
        return {"offStatus": off}
    finally:
        if features()["sms"]: admin.put(f"{transports}/sms", json={"enabled": False})
        set_flags()


@scenario("flags/smb-off-rejects-on-works")
def smb_flag(t):
    env = api.env
    body = {"providerType": "smb", "name": f"e2e-flag-smb-{time.time_ns()}", "configuration": {"host": env["PUBLIC_HOST"], "share": "Data"},
            "secret": {"username": env["E2E_SMB_USER"], "password": env["E2E_SMB_PASSWORD"]}}
    kinds = lambda: [p["providerType"] for p in t.call("GET", "/storage-providers")["items"]]
    created = None
    set_flags(smb=False)
    try:
        r = t.raw("POST", "/storage-connections", body); assert r.status_code == 404, f"smb connection created while off: {r.status_code} {r.text[:200]}"
        assert "smb" not in kinds(), "provider list still offers smb while off"
        assert {"sftp", "s3", "local"} <= set(kinds()), "the other providers must stay"
        set_flags(smb=True)
        assert "smb" in kinds()
        r = t.raw("POST", "/storage-connections", body); assert r.status_code == 201, f"create smb while on: {r.status_code} {r.text[:200]}"
        created = r.json()["id"]
        return {"offStatus": 404}
    finally:
        if created: t.call("DELETE", f"/storage-connections/{created}")
        set_flags()

@scenario("flags/size-anomaly-off-rejects-on-works")
def size_anomaly_flag(t):
    anomaly = "backup_size_anomaly"
    policy = lambda: t.call("GET", "/notifications/policy")
    keys = ("enabled", "eventTypes", "minimumSeverity", "channelKinds", "recoveryNotifications", "staleAfterHours", "agentOfflineAfterMinutes", "reminderIntervalHours", "maxNotificationsPerHour")
    set_flags(sizeAnomaly=False)
    original = None
    try:
        assert anomaly not in policy()["eventTypes"], "policy still lists the size anomaly event while off"
        r = t.raw("PUT", "/notifications/policy", {"eventTypes": [anomaly]}); assert r.status_code == 404, f"size anomaly event accepted while off: {r.status_code}"
        set_flags(sizeAnomaly=True)
        original = {k: policy()[k] for k in keys}
        r = t.raw("PUT", "/notifications/policy", {**original, "eventTypes": sorted({*original["eventTypes"], anomaly})})
        assert r.status_code == 200, f"size anomaly event rejected while on: {r.status_code} {r.text[:200]}"
        assert anomaly in policy()["eventTypes"], "policy hides the size anomaly event while on"
        return {"offStatus": 404}
    finally:
        if original: t.raw("PUT", "/notifications/policy", original)
        set_flags()

@scenario("flags/replica-repair-default-on-off-rejects")
def repair_flag(t):
    artifact = t.call("GET", "/backup-artifacts?pageSize=1")["items"][0]["id"]
    listing = lambda: t.raw("GET", f"/backup-artifacts/{artifact}/repairs")
    start = lambda: t.raw("POST", f"/backup-artifacts/{artifact}/repairs", {"storageConnectionId": str(uuid.uuid4())})  # a made-up connection: the handler refuses it, so no repair ever starts
    assert features()["replicaRepair"] is True, "replicaRepair must default to on"
    assert listing().status_code == 200, "repair routes are missing with the default flag"
    set_flags(replicaRepair=False)
    try:
        off = {"list": listing(), "start": start()}
        assert all(r.status_code == 404 and not r.content for r in off.values()), f"repair routes answered while off: { {k: (v.status_code, v.text[:80]) for k, v in off.items()} }"
        assert t.raw("GET", "/backup-artifacts").status_code == 200, "the rest of artifacts must stay reachable"
    finally:
        set_flags()
    assert listing().status_code == 200, "repair routes did not come back"
    return {"offStatus": 404}

@scenario("flags/external-secrets-off-refuses-provider")
def external_secrets_flag(t):
    """Secrets:Provider=Vault is the external-secrets feature; with the flag off the api must refuse to start rather than quietly keep secrets elsewhere."""
    def logs(): return subprocess.run(["docker", "logs", "bp-api"], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True).stdout
    recreate_api({"E2E_SECRETS_PROVIDER": "Vault", "E2E_FEATURE_EXTERNALSECRETS": "false"})
    try:
        end = time.time() + 90
        while time.time() < end and "externalSecrets" not in logs(): time.sleep(3)
        assert "Features:externalSecrets" in logs(), "the api did not report the missing externalSecrets flag"
        try: ok = features().get("externalSecrets") is False
        except (requests.RequestException, ValueError): ok = False
        assert not ok, "the api serves requests with an external secrets provider while the flag is off"
    finally:
        set_flags()
    return {"offStatus": "refused at startup"}

AGENT_SNAPSHOTS = {"windowsVss": "Vss", "linuxSnapshots": "Btrfs"}

def agent_start(host, kind):
    """Starts the agent host in the foreground with a snapshot file source configured; returns its console output (it must exit at startup validation)."""
    env = {"Agent__Backup__FileSources__flagtest__RootDirectory": "/srv/e2e/files" if host == "fedora" else r"C:\e2e\files", "Agent__Backup__FileSources__flagtest__SnapshotKind": kind}
    if host == "fedora":
        exports = " ".join(f"{k}='{v}'" for k, v in env.items())
        return lib.fedora(f"cd /opt/backup-agent/bin && sudo -u backup-agent env {exports} timeout 60 ./BackupPlatform.Agent.Host 2>&1 | head -40")[1]
    # The service account is a virtual one and the agent refuses to read its identity as anyone else, so the service itself is restarted with the extra
    # environment (put back in finally) and the refusal is read from the Application event log. The lab service has Features__windowsVss=true for the VSS tests, so that entry is left out while the flag-off case runs.
    extra = ",".join(f"'{k}={v}'" for k, v in env.items())
    return lib.win(f"""$k='HKLM:\\SYSTEM\\CurrentControlSet\\Services\\BackupAgent'; $orig=@((Get-ItemProperty $k).Environment); $since=Get-Date
try {{
  Set-ItemProperty $k -Name Environment -Type MultiString -Value (@($orig | ? {{ $_ -notmatch '^Features__windowsVss=' }}) + @({extra}))
  Restart-Service BackupAgent -ErrorAction SilentlyContinue; Start-Sleep 5
  Get-WinEvent -FilterHashtable @{{LogName='Application'; StartTime=$since}} -ErrorAction SilentlyContinue | ? {{ $_.Message -match 'Features:' }} | Select -First 1 -ExpandProperty Message
  'service after refusal: ' + (Get-Service BackupAgent).Status
}} finally {{
  Set-ItemProperty $k -Name Environment -Type MultiString -Value $orig
  Restart-Service BackupAgent; Start-Sleep 3; 'service restored: ' + (Get-Service BackupAgent).Status
}}""", timeout=300)[1]

@scenario("flags/linux-snapshots-off-refuses-btrfs-source")
def linux_snapshots_flag(t):
    out = agent_start("fedora", AGENT_SNAPSHOTS["linuxSnapshots"])
    assert "Features:linuxSnapshots" in out, f"the fedora agent did not refuse a Btrfs file source while linuxSnapshots is off: {out[-600:]}"
    return {"offStatus": "refused at startup"}

@scenario("flags/windows-vss-off-refuses-vss-source")
def windows_vss_flag(t):
    out = agent_start("win", AGENT_SNAPSHOTS["windowsVss"])
    assert "Features:windowsVss" in out and "service restored: Running" in out, f"the windows agent did not refuse a Vss file source while windowsVss is off (or was not restored): {out[-600:]}"
    return {"offStatus": "refused at startup"}

SCENARIOS = [sms_flag, support_flag, smb_flag, size_anomaly_flag, repair_flag, external_secrets_flag, linux_snapshots_flag, windows_vss_flag]

if __name__ == "__main__":
    t = lib.Tenant("a")
    for run in SCENARIOS:
        if len(sys.argv) < 2 or sys.argv[1] in run.scenario: run(t)
