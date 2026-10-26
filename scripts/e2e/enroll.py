"""Print a one-time enrollment token for a tenant key (a|b) on stdout only."""
import api, json, sys
st = json.loads((api.ROOT / "docs/test-environment/state.json").read_text())[sys.argv[1]]
s = api.login(st["login"], st["password"])
r = s.post(f"{api.BASE}/api/tenants/{st['tenantId']}/agent-enrollment-tokens", json={})
r.raise_for_status(); print(r.json()["token"])
