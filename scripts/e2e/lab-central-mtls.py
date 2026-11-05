"""Lab prerequisites for the Central-storage and mTLS scenarios; idempotent. Run once, then recreate the api (the runbook compose command) and run configure-agents.py.
Writes to .env.e2e (gitignored): E2E_TENANT_A (the Central tenant scope key) and E2E_MTLS_ISSUER_PASSWORD; creates the agent-certificate issuer CA
(docs/test-environment/certs/mtls/issuer.pfx, an e2e-only CA that signs nothing but agent client certificates). Tenant A already has a default Managed Storage (central) connection."""
import json, secrets, subprocess, api, lib

ENV = api.ROOT / ".env.e2e"
MTLS = api.ROOT / "docs/test-environment/certs/mtls"


def set_env(key, value):
    lines = [l for l in ENV.read_text().splitlines() if not l.startswith(key + "=")]
    ENV.write_text("\n".join(lines + [f"{key}={value}"]) + "\n")


def issuer():
    if (MTLS / "issuer.pfx").exists() and "E2E_MTLS_ISSUER_PASSWORD" in api.env: return "issuer exists"
    MTLS.mkdir(parents=True, exist_ok=True); pw = secrets.token_urlsafe(24)
    run = lambda *a: subprocess.run(a, check=True, capture_output=True, cwd=MTLS)
    run("openssl", "req", "-x509", "-newkey", "rsa:3072", "-nodes", "-keyout", "ca.key", "-out", "ca.pem", "-days", "3650", "-subj", "/CN=bpe2e agent issuer",
        "-addext", "basicConstraints=critical,CA:TRUE", "-addext", "keyUsage=critical,keyCertSign,digitalSignature")
    run("openssl", "pkcs12", "-export", "-inkey", "ca.key", "-in", "ca.pem", "-out", "issuer.pfx", "-passout", "pass:" + pw)
    (MTLS / "ca.key").chmod(0o600); (MTLS / "issuer.pfx").chmod(0o644)  # the api container reads it as root; the password is the secret
    set_env("E2E_MTLS_ISSUER_PASSWORD", pw)
    return "issuer created"


if __name__ == "__main__":
    print(issuer())
    st = json.loads((api.ROOT / "docs/test-environment/state.json").read_text())
    set_env("E2E_TENANT_A", st["a"]["tenantId"].replace("-", ""))  # 32 hex: the container entrypoint is sh, which drops env names containing "-"
    print("E2E_TENANT_A set; now recreate the api (runbook compose command) and run configure-agents.py")
