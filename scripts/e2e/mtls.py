"""Agent mTLS: a throwaway Fedora agent gets a client certificate from the platform issuer and talks to the backend's own TLS endpoint (:5253, where Kestrel
sees the client certificate; the :5252 nginx endpoint cannot). Valid identity works; a foreign certificate and a forged-identity certificate are rejected;
the agent's own certificate stops working the moment the agent is revoked. Needs scripts/e2e/lab-central-mtls.py and the compose override. One pass, no retries."""
import json, secrets, subprocess, time, uuid, lib
from lib import scenario

MTLS = "https://192.168.122.1:5253"
DIR = "/var/lib/backup-agent/mtls"
HOST = "/opt/backup-agent/bin/BackupPlatform.Agent.Host"
IDENTITY_OID = "1.3.6.1.4.1.57264.1.1"  # AgentCertificateAuthority: the agent/tenant/credential/generation JSON the backend trusts only when the chain is its own


def me(pem=None):
    """HTTP status of GET /api/agent/me on the mTLS endpoint, presenting the PEM (cert+key) on the Fedora VM, or nothing."""
    arg = f"--cert {pem}" if pem else ""
    rc, out = lib.fedora(f"sudo curl -s -o /dev/null -w '%{{http_code}}' --max-time 20 {arg} {MTLS}/api/agent/me")
    return out.strip().splitlines()[-1] if out.strip() else f"rc={rc}"


def enroll_agent(t, name):
    tok = subprocess.run(["python3", "enroll.py", "a"], capture_output=True, text=True, check=True).stdout.strip()
    lib.fedora(f"sudo systemctl stop bp-agent-mtls 2>/dev/null; sudo rm -rf {DIR}; sudo -u backup-agent mkdir -m 700 {DIR}")
    rc, out = lib.fedora(f"sudo -u backup-agent {HOST} enroll --server https://192.168.122.1:5252 --token '{tok}' --store-directory {DIR} --name {name} --non-interactive 2>&1 | tail -3")
    assert rc == 0, out


@scenario("agents/mtls-valid-identity-works-foreign-and-revoked-rejected")
def mtls(t):
    name, pw = f"mtls-e2e-{uuid.uuid4().hex[:4]}", secrets.token_urlsafe(24)
    enroll_agent(t, name)
    try:
        rc, out = lib.fedora(f"sudo -u backup-agent env AGENT_MTLS_CERTIFICATE_PASSWORD='{pw}' {HOST} certificate enroll {DIR} 2>&1 | tail -3")
        assert "Client certificate saved" in out, f"certificate enroll: {out}"
        agent = t.agents()[name]
        lib.fedora(f"sudo openssl pkcs12 -in {DIR}/client-certificate.pfx -passin 'pass:{pw}' -nodes -out /tmp/mtls-valid.pem && sudo chmod 644 /tmp/mtls-valid.pem")
        identity = json.dumps({"Agent": agent["agentId"], "Tenant": t.id, "Credential": str(uuid.uuid4()), "Generation": 1}, separators=(",", ":"))
        rc, out = lib.fedora(f"""cd /tmp
openssl req -x509 -newkey rsa:2048 -nodes -keyout foreign.key -out foreign.crt -days 1 -subj /CN=backup-agent-foreign -addext extendedKeyUsage=clientAuth 2>/dev/null
cat foreign.crt foreign.key > mtls-foreign.pem
openssl req -x509 -newkey rsa:2048 -nodes -keyout forged.key -out forged.crt -days 1 -subj /CN=backup-agent-{agent['agentId'].replace('-', '')} -addext extendedKeyUsage=clientAuth -addext '{IDENTITY_OID}=DER:{identity.encode().hex()}' 2>/dev/null
cat forged.crt forged.key > mtls-forged.pem; ls mtls-foreign.pem mtls-forged.pem""")
        assert rc == 0, out
        before = {"no certificate": me(), "valid": me("/tmp/mtls-valid.pem"), "foreign": me("/tmp/mtls-foreign.pem"), "forged identity": me("/tmp/mtls-forged.pem")}
        assert before == {"no certificate": "401", "valid": "200", "foreign": "401", "forged identity": "401"}, f"mTLS outcomes {before}"
        # The real daemon, with the certificate configured, runs against the mTLS endpoint and comes online (the curl checks above are what prove certificate-only identity).
        rc, out = lib.fedora(f"sudo systemd-run --unit=bp-agent-mtls --uid=backup-agent --gid=backup-agent -p WorkingDirectory=/opt/backup-agent/bin -p Environment=Agent__Backend__BaseUri={MTLS} "
                             f"-p Environment=AGENT_MTLS_CERTIFICATE_PATH={DIR}/client-certificate.pfx -p Environment=AGENT_MTLS_CERTIFICATE_PASSWORD={pw} {HOST} --store-directory {DIR} 2>&1 | tail -2")
        ag = None
        for _ in range(30):
            ag = t.agents().get(name)
            if ag and ag["connectivity"] == "online": break
            time.sleep(3)
        assert ag and ag["connectivity"] == "online", f"the agent never came online over mTLS: {ag and ag['connectivity']} {out}"
        r = t.raw("POST", f"/agents/{agent['agentId']}/revoke", {}); assert r.status_code in (200, 204), (r.status_code, r.text[:200])
        after = me("/tmp/mtls-valid.pem")
        assert after == "401", f"the revoked agent's certificate still authenticates: HTTP {after}"
        return {"before": before, "afterRevoke": after}
    finally:
        lib.fedora(f"sudo systemctl stop bp-agent-mtls 2>/dev/null; sudo rm -rf {DIR} /tmp/mtls-*.pem /tmp/foreign.* /tmp/forged.*")


if __name__ == "__main__":
    mtls(lib.Tenant("a"))
