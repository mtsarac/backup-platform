"""Create the e2e storage connections for tenant A (sftp, s3, smb) and record their ids in state.json."""
import lib, json, pathlib, subprocess, base64, hashlib
p = lib.api.ROOT / "docs/test-environment/state.json"; st = json.loads(p.read_text()); env = lib.api.env
t = lib.Tenant("a")
rc, out = lib.fedora("ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub")
fp = out.split()[1]
fed_ip = next(l.split()[1] for l in subprocess.run(["ssh", "-G", "virtual-fedora"], capture_output=True, text=True).stdout.splitlines() if l.startswith("hostname "))
f = st["fedora"]
specs = {
  "sftp": {"providerType": "sftp", "name": "E2E SFTP (fedora)", "configuration": {"host": fed_ip, "port": "22", "username": f["sftp_user"], "authenticationType": "password",
           "remoteBaseDirectory": "/home/e2e-sftp/backups", "hostKeyFingerprint": fp}, "secret": {"password": f["sftp_password"]}},
  "s3": {"providerType": "s3", "name": "E2E S3 (minio)", "configuration": {"endpoint": f"https://{env['PUBLIC_HOST']}:9000", "bucket": "bpe2e", "region": "us-east-1"},
         "secret": {"accessKey": env["E2E_MINIO_USER"], "secretKey": env["E2E_MINIO_PASSWORD"]}},
  "smb": {"providerType": "smb", "name": "E2E SMB (samba)", "configuration": {"host": env["PUBLIC_HOST"], "share": "Data"},
          "secret": {"username": env["E2E_SMB_USER"], "password": env["E2E_SMB_PASSWORD"]}},
}
st.setdefault("connections", {})
for k, body in specs.items():
    if k in st["connections"]: continue
    try:
        v = t.call("POST", "/storage-connections", body)
        st["connections"][k] = v["id"]
        r = t.call("POST", f"/storage-connections/{v['id']}/test", {})
        print(k, "created; test:", json.dumps(r)[:400])
    except Exception as e:
        print(k, "ERR", str(e)[:500])
p.write_text(json.dumps(st, indent=2))
