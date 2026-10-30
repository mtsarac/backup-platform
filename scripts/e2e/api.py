"""Minimal control-plane client for the e2e environment. Reads credentials from .env.e2e; never prints them."""
import os, pathlib, time, requests, urllib3
urllib3.disable_warnings()
ROOT = pathlib.Path(__file__).resolve().parents[2]
ENV_FILE = ROOT / ".env.e2e"
env = dict(l.split("=", 1) for l in ENV_FILE.read_text().splitlines() if "=" in l and not l.startswith("#")) if ENV_FILE.exists() else {}
BASE = f"https://{env.get('PUBLIC_HOST', '192.168.122.1')}:{env.get('API_PORT', '5252')}"


def wait_ready(timeout=120, interval=1.0, base_url=None):
    """Waits until the API answers the antiforgery probe with HTTP 200 and valid JSON.
    Bounded and startup-only: never retries or masks failures inside scenarios."""
    url = f"{base_url or BASE}/api/auth/antiforgery"
    end = time.time() + max(0, timeout)
    last_error = "no attempt made"
    while True:
        try:
            r = requests.get(url, verify=False, timeout=min(5.0, max(1.0, float(timeout))))
            if r.status_code == 200:
                try:
                    data = r.json()
                    if isinstance(data, dict) and "headerName" in data and "requestToken" in data:
                        return True
                    last_error = f"HTTP 200 but unexpected JSON structure: {data!r}"
                except Exception as e:
                    last_error = f"HTTP 200 but response was not JSON ({r.headers.get('content-type', 'unknown')}): {r.text[:200]!r}"
            else:
                last_error = f"HTTP {r.status_code} ({r.headers.get('content-type', 'unknown')}): {r.text[:200]!r}"
        except requests.RequestException as e:
            last_error = f"Request failed: {type(e).__name__}: {e}"

        if time.time() >= end:
            break
        time.sleep(interval)

    raise TimeoutError(f"API at {url} was not ready within {timeout}s (last error: {last_error})")


def _antiforgery(session, phase="login"):
    r = session.get(f"{BASE}/api/auth/antiforgery", timeout=30)
    if r.status_code != 200:
        raise RuntimeError(f"{phase} antiforgery endpoint returned HTTP {r.status_code} ({r.headers.get('content-type', 'unknown')}): {r.text[:200]}")
    try:
        data = r.json()
    except Exception as e:
        raise RuntimeError(f"{phase} antiforgery response ({r.status_code} {r.headers.get('content-type', 'unknown')}) was not JSON: {r.text[:200]}") from e
    if not isinstance(data, dict) or "headerName" not in data or "requestToken" not in data:
        raise RuntimeError(f"{phase} antiforgery response missing token fields: {data!r}")
    return data


def login(login=None, password=None):
    s = requests.Session(); s.verify = False
    t = _antiforgery(s, "pre-login")
    s.headers[t["headerName"]] = t["requestToken"]
    r = s.post(f"{BASE}/api/auth/login", json={"loginIdentifier": login or env.get("BOOTSTRAP_ADMIN_LOGIN", "admin"), "password": password or env.get("BOOTSTRAP_ADMIN_PASSWORD", "")}, timeout=30)
    r.raise_for_status()
    t = _antiforgery(s, "post-login")
    s.headers[t["headerName"]] = t["requestToken"]
    return s
