"""Lab prerequisite for the self-update scenario; idempotent. Creates the e2e release signing key (docs/test-environment/certs/update/release-signing.key, 0600, never committed)
and writes its public half to .env.e2e as E2E_UPDATE_PUBLIC_KEY; the compose override hands that to the api as AgentUpdates__TrustedKeys__e2e (key id "e2e").
Run once, then recreate the api (runbook compose command). The scenario pins the same public key in the throwaway agent's update settings."""
import api
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric import ec

ENV = api.ROOT / ".env.e2e"
DIR = api.ROOT / "docs/test-environment/certs/update"
KEY = DIR / "release-signing.key"


def signing_key():
    if KEY.exists(): return serialization.load_pem_private_key(KEY.read_bytes(), None)
    DIR.mkdir(parents=True, exist_ok=True)
    key = ec.generate_private_key(ec.SECP256R1())
    KEY.write_bytes(key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption())); KEY.chmod(0o600)
    return key


def public_pem():
    return signing_key().public_key().public_bytes(serialization.Encoding.PEM, serialization.PublicFormat.SubjectPublicKeyInfo).decode()


def set_env(name, value):
    lines = [l for l in ENV.read_text().splitlines() if not l.startswith(name + "=")]
    ENV.write_text("\n".join(lines + [f'{name}="{value}"']) + "\n")


if __name__ == "__main__":
    set_env("E2E_UPDATE_PUBLIC_KEY", public_pem().replace("\n", "\\n"))  # compose expands \n inside double quotes
    print("E2E_UPDATE_PUBLIC_KEY set; now recreate the api (runbook compose command)")
