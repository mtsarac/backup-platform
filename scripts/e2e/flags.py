"""Feature flags: SMS is off by default (its API answers 404, GET /api/features says false) and works once the operator turns it on.
Restarts bp-api with Features__sms flipped (docker-compose.e2e.override.yml reads E2E_FEATURE_SMS) and leaves it off. Run: python3 flags.py"""
import os, subprocess, time, requests, api, lib
from lib import scenario

COMPOSE = ["docker", "compose", "-p", "bpe2e", "--env-file", ".env.e2e", "-f", "docker-compose.yml", "-f", "docker-compose.e2e.override.yml"]
GATEWAY = {"enabled": True, "endpoint": "https://sms.e2e.test/send", "authentication": "none"}
PHONE = "+905551230000"

def set_sms(on):
    """Recreates only the api container, then restarts the frontend because nginx resolves `api` once at start."""
    subprocess.run(COMPOSE + ["up", "-d", "--no-build", "api"], cwd=api.ROOT, env={**os.environ, "E2E_FEATURE_SMS": str(on).lower()}, check=True, capture_output=True)
    subprocess.run(["docker", "restart", "bp-frontend"], check=True, capture_output=True)
    end = time.time() + 180
    while time.time() < end:
        try:
            if requests.get(f"{api.BASE}/api/features", verify=False, timeout=5).json().get("sms") is on: return
        except (requests.RequestException, ValueError): pass
        time.sleep(3)
    raise TimeoutError(f"api did not come back with sms={on}")

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
    set_sms(False)
    try:
        assert not requests.get(f"{api.BASE}/api/features", verify=False).json()["sms"]
        off = {name: r.status_code for name, r in sms_requests(admin, t).items()}
        assert all(code == 404 for code in off.values()), f"SMS API answered while off: {off}"
        assert "sms" not in admin.get(transports).json(), "transport list still shows SMS while off"
        assert t.raw("GET", "/notifications/channels").status_code == 200, "the rest of notifications must stay reachable"
        set_sms(True)
        r = admin.put(f"{transports}/sms", json=GATEWAY); assert r.status_code == 200, f"enable gateway: {r.status_code} {r.text[:200]}"
        assert "sms" in admin.get(transports).json()
        r = t.raw("POST", "/notifications/channels", {"kind": "sms", "name": f"e2e-sms-{time.time_ns()}", "target": PHONE}); assert r.status_code == 201, f"create sms channel: {r.status_code} {r.text[:200]}"
        t.call("DELETE", f"/notifications/channels/{r.json()['id']}")
        return {"offStatus": off}
    finally:
        if requests.get(f"{api.BASE}/api/features", verify=False).json()["sms"]: admin.put(f"{transports}/sms", json={"enabled": False})
        set_sms(False)

if __name__ == "__main__":
    sms_flag(lib.Tenant("a"))
