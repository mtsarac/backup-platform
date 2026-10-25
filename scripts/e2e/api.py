"""Minimal control-plane client for the e2e environment. Reads credentials from .env.e2e; never prints them."""
import os, pathlib, requests, urllib3
urllib3.disable_warnings()
ROOT = pathlib.Path(__file__).resolve().parents[2]
env = dict(l.split("=", 1) for l in (ROOT / ".env.e2e").read_text().splitlines() if "=" in l and not l.startswith("#"))
BASE = f"https://{env['PUBLIC_HOST']}:{env.get('API_PORT', '5252')}"

def login(login=None, password=None):
    s = requests.Session(); s.verify = False
    t = s.get(f"{BASE}/api/auth/antiforgery").json()
    s.headers[t["headerName"]] = t["requestToken"]
    r = s.post(f"{BASE}/api/auth/login", json={"loginIdentifier": login or env["BOOTSTRAP_ADMIN_LOGIN"], "password": password or env["BOOTSTRAP_ADMIN_PASSWORD"]})
    r.raise_for_status()
    t = s.get(f"{BASE}/api/auth/antiforgery").json()
    s.headers[t["headerName"]] = t["requestToken"]
    return s
