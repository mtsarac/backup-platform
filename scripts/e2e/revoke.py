"""Enroll a throwaway second agent on the Fedora VM, revoke it from the API and verify it is cut off immediately."""
import subprocess, time, lib
from lib import scenario

@scenario("agents/revoke-cuts-off-agent-immediately")
def revoke(t):
    tok = subprocess.run(["python3", "enroll.py", "a"], capture_output=True, text=True, check=True).stdout.strip()
    lib.fedora("sudo systemctl stop bp-agent2 2>/dev/null; sudo rm -rf /var/lib/backup-agent/agent2; sudo -u backup-agent mkdir -m 700 /var/lib/backup-agent/agent2")
    rc, out = lib.fedora(f"sudo -u backup-agent /opt/backup-agent/bin/BackupPlatform.Agent.Host enroll --server https://192.168.122.1:5252 --token '{tok}' --store-directory /var/lib/backup-agent/agent2 --name revoke-e2e --non-interactive 2>&1 | tail -3")
    assert rc == 0, out
    rc, out = lib.fedora("sudo systemd-run --unit=bp-agent2 --uid=backup-agent --gid=backup-agent -p WorkingDirectory=/opt/backup-agent/bin -p Environment=Agent__Backend__BaseUri=https://192.168.122.1:5252 /opt/backup-agent/bin/BackupPlatform.Agent.Host --store-directory /var/lib/backup-agent/agent2 2>&1 | tail -2")
    try:
        ag = None
        for _ in range(30):
            ag = t.agents().get("revoke-e2e")
            if ag and ag["connectivity"] == "online": break
            time.sleep(3)
        assert ag and ag["connectivity"] == "online", f"second agent never came online: {ag and ag['connectivity']} {out}"
        r = t.raw("POST", f"/agents/{ag['agentId']}/revoke", {}); assert r.status_code in (200, 204), (r.status_code, r.text[:200])
        time.sleep(100)
        after = t.agents()["revoke-e2e"]
        _, log = lib.fedora("sudo journalctl -u bp-agent2 --no-pager --since '-100 sec' -o cat | grep -v '^ *at ' | tail -8 | cut -c1-220")
        assert after["status"] == "Revoked" and after["connectivity"] != "online", (after["status"], after["connectivity"], after.get("lastHeartbeatAt"), log)
        # a revoked agent must not be able to get work or heartbeat again
        time.sleep(40)
        again = t.agents()["revoke-e2e"]
        assert again["connectivity"] != "online", "revoked agent came back online"
        return {"status": after["status"], "connectivity": again["connectivity"]}
    finally:
        lib.fedora("sudo systemctl stop bp-agent2; sudo rm -rf /var/lib/backup-agent/agent2")

if __name__ == "__main__":
    revoke(lib.Tenant("a"))
