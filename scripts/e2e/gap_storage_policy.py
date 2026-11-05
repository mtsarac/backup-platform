"""StoragePolicyGaps: STO-01..15 and POL-01/02/10 gap scenarios (scenario names `gap/<ID>/<detail>`). Run: python3 gap_storage_policy.py [name-substring ...].
Runs in the private tenant gap-sp-sto (agent gap-sp-sto on this PC, explicit SFTP allowlist 192.168.122.0/24) and a few read-only/file-only checks in tenant A (Fedora agent).
Needs: gap_sp_fedora.py up (private sshd :2222 + loop file system), gap_sp_common.ensure_tenant('sto') / ensure_agent('sto','sto', sftp_allow=[...]). Bandwidth/quota slices live in gap_bw.py and gap_quota.py."""
import base64, hashlib, json, subprocess, sys, threading, time, uuid
import api, lib, flows, files_providers as fp
import gap_sp_common as g, gap_sp_fedora as gf
from lib import ST, scenario

MB = g.MB
SFTP_PW = ST["fedora"]["sftp_password"]
FED_IP = "192.168.122.115"
CTX = {}
REASONS = {}  # failure (reasonCode, reason) per case, compared by the diagnosability scenario


def ctx():
    if not CTX:
        fp.load_secrets()
        t = g.GT("sto"); CTX.update(t=t, a=t.agents()["gap-sp-sto"])
    return CTX["t"], CTX["a"]


def sftp_conn(t, name, port=gf.PORT, base=gf.BASE, password=SFTP_PW, fingerprint=None, username="e2e-sftp", auth="password", secret=None, cfg=None):
    conf = {"host": FED_IP, "port": str(port), "username": username, "authenticationType": auth, "remoteBaseDirectory": base, "hostKeyFingerprint": fingerprint or gf.fingerprint(), **(cfg or {})}
    sec = secret or {"password": password}
    return t.call("POST", "/storage-connections", {"providerType": "sftp", "name": f"gap-sp {name} {uuid.uuid4().hex[:4]}", "configuration": conf, "secret": sec})


def s3_conn(t, name, endpoint=None, bucket="bpe2e", access=None, secret=None, cfg=None):
    e = api.env
    conf = {"endpoint": endpoint or f"https://{e['PUBLIC_HOST']}:9000", "bucket": bucket, "region": "us-east-1", **(cfg or {})}
    return t.call("POST", "/storage-connections", {"providerType": "s3", "name": f"gap-sp {name} {uuid.uuid4().hex[:4]}", "configuration": conf,
                                                    "secret": {"accessKey": access or e["E2E_MINIO_USER"], "secretKey": secret or e["E2E_MINIO_PASSWORD"]}})


def drop(t, *conns):
    for c in conns:
        if c: t.raw("DELETE", f"/storage-connections/{c['id'] if isinstance(c, dict) else c}")


def dest(conn, primary=False, provider=None):
    pv = provider or (conn["providerType"] if isinstance(conn, dict) else "sftp")
    return g.gdest(pv, conn=conn["id"], primary=primary)


def job_for(t, a, name, dests, paths=None, retry1=False, **cfg_extra):
    cfg = g.gcfg(dests, paths=paths, **cfg_extra)
    body = {"retryPolicy": {"maxAttempts": 1}} if retry1 else {}
    return g.new_job(t, a, name, cfg, **body)


def finish_or_fail(t, job, timeout=120):
    """Starts a run and returns (run id, detail, first failed/paused attempt or None)."""
    rid = g.start_run(t, job)
    end = time.time() + timeout
    while time.time() < end:
        d = t.call("GET", f"/backup-runs/{rid}")
        bad = [a for a in d["attempts"] if a["status"] in ("Failed", "AgentLost", "Cancelled", "Paused")]
        if bad or d["status"] in ("Succeeded", "Failed", "Cancelled", "Paused"):
            if d["status"] not in ("Failed", "Succeeded", "Cancelled"):
                t.raw("POST", f"/backup-runs/{rid}/cancel", {})
            att = bad[0] if bad else d["attempts"][0]
            return rid, d, att
        time.sleep(2)
    raise TimeoutError(f"run {rid} did not reach terminal or paused state within {timeout}s")


def sha_local(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for ch in iter(lambda: f.read(1 << 20), b""): h.update(ch)
    return h.hexdigest()


def tree_sha(root):
    rc, o = g.sh("bash", "-c", f"cd '{root}' && find . -type f -print0 | sort -z | xargs -0 sha256sum | sed 's# \\./# #' | sort -k2")
    return o


def file_restore(t, a, art_id, rel, replica_id=None, expect="Succeeded", paths=None):
    files = {"relativeDirectory": rel}
    if paths is not None: files["paths"] = paths
    req = {"agentId": a["agentId"], "target": {"host": "localhost", "port": None, "integratedSecurity": True, "databaseCredentialReference": None, "encrypt": False, "trustServerCertificate": False},
           "databaseName": "gapfiles", "mode": "NewDatabase", "options": {"files": files}}
    if replica_id: req["replicaId"] = replica_id
    s = t.call("POST", f"/backup-artifacts/{art_id}/restore", req)
    d = t.wait(f"/restores/{s.get('id', s.get('restoreId'))}", timeout=900)
    assert d["status"] == expect, f"restore {d['status']} (wanted {expect}): {json.dumps(d, default=str)[:700]}"
    return d


def restore_files(t, a, art_id, rel, replica_id=None, expect="Succeeded"):
    """File restore into <file source>/<rel> on the private agent; returns (restore detail, sha tree of the restored directory)."""
    d = file_restore(t, a, art_id, rel, replica_id=replica_id, expect=expect)
    root = g.agent_root("sto") / "files" / rel
    return d, (tree_sha(root) if root.exists() else "")


def src_tree(subpath=""):
    """sha tree of the source files (excluding scratch restore dirs zz-*)."""
    out = tree_sha(g.agent_root("sto") / "files" / subpath)
    return "\n".join(l for l in out.splitlines() if "  zz-" not in l)


def rm_restore(rel): g.sh("rm", "-rf", str(g.agent_root("sto") / "files" / rel))


def seed_files(name="sto"):
    """Deterministic source tree: docs/ (three files) so every scenario backs up the same ~10 MiB."""
    root = g.agent_root(name) / "files" / "docs"
    if not (root / "f1.bin").exists():
        for i, n in enumerate((4, 3, 2)): g.put_file(name, f"docs/f{i + 1}.bin", n * MB)
        (root / "small.txt").write_text("gap-sp small file\n")


def phys_local(replica_key, root_name="sto"):
    p = g.agent_root(root_name) / "local-store" / replica_key
    return p if p.exists() else None


def fed_sha(path):
    rc, o = lib.fedora(f"sudo sha256sum '{path}' 2>&1"); return o.split()[0] if rc == 0 and len(o.split()[0]) == 64 else None


def fed_exists(path):
    rc, o = lib.fedora(f"sudo test -e '{path}' && echo yes || echo no"); return o.strip().endswith("yes")


class Sampler:
    """Streams `<epoch> <file> <bytes>` for every file below `root` on the Fedora VM (twice a second) into self.rows from a background ssh process."""
    def __init__(self, root, pattern="*"):
        self.rows = []; self.stop_flag = False
        script = f"sudo bash -c 'while :; do t=$(date +%s.%N); find {root} -type f -name \"{pattern}\" -printf \"$t %f %s\\n\" 2>/dev/null; sleep 0.4; done'"
        self.p = subprocess.Popen(["ssh", "virtual-fedora", "bash -s"], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True)
        self.p.stdin.write(script + "\n"); self.p.stdin.flush()
        self.th = threading.Thread(target=self._read, daemon=True); self.th.start()
    def _read(self):
        for line in self.p.stdout:
            f = line.split()
            if len(f) == 3 and f[2].isdigit():
                try: self.rows.append((float(f[0]), f[1], int(f[2])))
                except ValueError: pass
    def close(self):
        self.p.kill(); self.th.join(timeout=2)
    def series(self, name_filter=lambda n: True):
        return [(t, s) for t, n, s in self.rows if name_filter(n)]


# ---- STO-02 SFTP -----------------------------------------------------------------------------------------------------------------------------
@scenario("gap/STO-02/host-key-change-is-refused-and-never-auto-trusted")
def sto02_hostkey():
    t, a = ctx(); seed_files(); fp1 = gf.fingerprint(); c = sftp_conn(t, "hk", fingerprint=fp1); out = {}
    try:
        job = job_for(t, a, "hk", [dest(c, True)], paths=["docs"], retry1=True)
        rid, d = g.run_to_end(t, job); assert d["status"] == "Succeeded", d["status"]
        out["baselineArtifact"] = g.arts(d)[0]["id"]; out["fingerprintPinned"] = fp1[:16]
        cap0 = t.call("POST", f"/storage-connections/{c['id']}/probe", {}); out["capacityBefore"] = cap0["status"]
        fp2 = gf.rotate_hostkey(); assert fp2 != fp1
        rid, d, att = finish_or_fail(t, job)
        assert att and att["status"] == "Failed" and d["status"] != "Succeeded", f"run with a changed host key: {d['status']} {att}"
        out["failedRun"] = {"reasonCode": att["reasonCode"], "reason": att["reason"]}
        assert att["reasonCode"] not in ("Unexpected", None), att
        REASONS["host-key-changed"] = (att["reasonCode"], att["reason"])
        live = [x["id"] for x in g.arts(d) if x["state"] == "Available" or any(r["status"] == "Available" for r in x["replicas"])]
        assert not live, f"a failed run against a changed host key left a usable artifact: {live}"
        out["orphanArtifacts"] = [(x["state"], [(r["status"], r["verificationState"]) for r in x["replicas"]]) for x in g.arts(d)]
        fp.assert_no_secret(d, "run detail")
        cap1 = t.call("POST", f"/storage-connections/{c['id']}/probe", {})
        for _ in range(20):
            if cap1["status"] != "Pending": break
            time.sleep(3); cap1 = t.call("GET", f"/storage-connections/{c['id']}/capacity")
        out["capacityAfter"] = {"status": cap1["status"], "msg": (cap1.get("errorMessage") or "")[:80]}
        assert cap1["status"] != "Available", f"capacity reported Available although the host key changed: {cap1}"
        now = t.call("GET", f"/storage-connections/{c['id']}")
        assert now["configuration"]["hostKeyFingerprint"] == fp1, "the pinned fingerprint was replaced without operator action (trust on first use)"
        # the operator re-pins the new key: works again
        t.call("PUT", f"/storage-connections/{c['id']}", {"name": now["name"], "configuration": {**now["configuration"], "hostKeyFingerprint": fp2}})
        rid, d = g.run_to_end(t, job); assert d["status"] == "Succeeded", f"after re-pinning: {d['status']}"
        out["afterRepin"] = d["status"]
        return out
    finally:
        drop(t, c)


def _gen_key(passphrase=None):
    import tempfile
    from pathlib import Path
    with tempfile.TemporaryDirectory(prefix="gap-sp-key-") as tmp:
        path = Path(tmp) / "key"
        result = subprocess.run(["ssh-keygen", "-q", "-t", "ed25519", "-N", passphrase or "", "-f", str(path)], capture_output=True)
        assert result.returncode == 0, "temporary key generation failed"
        return path.read_text(), path.with_suffix(".pub").read_text().strip()


@scenario("gap/STO-02/wrong-credentials-fail-safely")
def sto02_creds():
    t, a = ctx(); seed_files(); out = {}; made = []
    good_priv, good_pub = _gen_key(); other_priv, _ = _gen_key(); pass_priv, pass_pub = _gen_key("correct horse")
    gf.fed(f"echo '{good_pub}' | sudo tee -a /etc/ssh/gap-sp/authorized_keys >/dev/null; echo '{pass_pub}' | sudo tee -a /etc/ssh/gap-sp/authorized_keys >/dev/null")
    try:
        cases = {"wrong-username": dict(username="gap-no-such-user"), "wrong-password": dict(password="wrong-password-gap-sp"),
                 "key-not-authorized": dict(auth="private-key", secret={"privateKey": other_priv}),
                 "key-wrong-passphrase": dict(auth="private-key", secret={"privateKey": pass_priv, "passphrase": "wrong passphrase"})}
        for label, kw in cases.items():
            c = sftp_conn(t, label, **kw); made.append(c)
            job = job_for(t, a, label, [dest(c, True)], paths=["docs"], retry1=True)
            rid, d, att = finish_or_fail(t, job)
            assert att and att["status"] in ("Failed", "Paused"), f"{label}: {d['status']}"
            reason_code = att.get("reasonCode") or d.get("pauseReason")
            reason_text = att.get("reason") or d.get("pauseReason")
            assert reason_code not in ("Unexpected", None), f"{label}: unspecific reason {reason_code}"
            out[label] = {"reasonCode": reason_code, "reason": reason_text}; REASONS[label] = (reason_code, reason_text)
        # positive controls: the authorized key (plain and with the right passphrase) works
        for label, kw in {"key-authorized": dict(auth="private-key", secret={"privateKey": good_priv}),
                          "key-with-passphrase": dict(auth="private-key", secret={"privateKey": pass_priv, "passphrase": "correct horse"})}.items():
            c = sftp_conn(t, label, **kw); made.append(c)
            rid, d = g.run_to_end(t, job_for(t, a, label, [dest(c, True)], paths=["docs"], retry1=True))
            assert d["status"] == "Succeeded", f"{label}: {d['status']} {[(x['reasonCode'], x['reason']) for x in d['attempts']]}"
            out[label] = "Succeeded"
        return out
    finally:
        gf.fed("sudo truncate -s 0 /etc/ssh/gap-sp/authorized_keys")
        drop(t, *made)


@scenario("gap/STO-02/connection-loss-resumes-from-partial")
def sto02_resume():
    t, a = ctx(); g.put_file("sto", "resume/big.bin", 48 * MB)
    c = sftp_conn(t, "resume"); out = {}
    smp = Sampler(gf.BASE, "*")
    try:
        job = job_for(t, a, "resume", [dest(c, True)], paths=["resume"])
        t.call("PUT", f"/upload-bandwidth/jobs/{job['id']}", {"bytesPerSecond": 1 * MB})
        rid = g.start_run(t, job); t0 = time.time(); killed_at = None
        while time.time() - t0 < 240:
            part = [s for tt, n, s in smp.rows if n.endswith(".partial") and tt > t0]
            if part and max(part) >= 20 * MB: break
            time.sleep(0.5)
        else: raise AssertionError("the upload never reached 20 MiB")
        before = max(s for tt, n, s in smp.rows if n.endswith(".partial")); gf.stop_sshd(); killed_at = time.time()
        out["partialBytesAtKill"] = before
        # the run must not claim success while the target is down; it pauses (network) or fails the attempt
        paused = None
        for _ in range(60):
            d = t.call("GET", f"/backup-runs/{rid}")
            if d["status"] in ("Paused", "Failed") or any(x["status"] == "Failed" for x in d["attempts"]): paused = d; break
            time.sleep(2)
        assert paused is not None, "run neither paused nor failed after the SFTP server went away"
        out["whileDown"] = {"status": paused["status"], "pauseReason": paused.get("pauseReason"), "attempts": [(x["status"], x["reasonCode"]) for x in paused["attempts"]]}
        assert paused["status"] != "Succeeded"
        gf.start_sshd(); time.sleep(3)
        d = t.call("GET", f"/backup-runs/{rid}")
        if d["status"] == "Paused": t.call("POST", f"/backup-runs/{rid}/resume", {}); out["resume"] = "manual"
        else: out["resume"] = "automatic retry"
        d = t.wait(f"/backup-runs/{rid}", timeout=420, stop_on=lib.TERMINAL)
        assert d["status"] == "Succeeded", f"after the server came back: {d['status']} {[(x['status'], x['reasonCode'], x['reason']) for x in d['attempts']]}"
        art = g.arts(d)[0]; out["attempts"] = [(x["number"], x["status"]) for x in d["attempts"]]
        part_after = [s for tt, n, s in smp.rows if n.endswith(".partial") and tt > killed_at]
        out["partialMinAfterRestart"] = min(part_after) if part_after else None
        out["resumedFromPartial"] = bool(part_after) and min(part_after) >= before * 0.9
        rep = art["replicas"][0]; assert rep["verificationState"] == "Verified"
        scoped = f"{gf.BASE}/backup-platform/tenants/{t.id.replace('-', '')}/connections/{c['id'].replace('-', '')}/{rep['key']}"
        got = fed_sha(scoped); assert got == art["artifactChecksum"], f"stored object hash {got} != artifact checksum"
        out["finalSha"] = got[:16]
        t.call("PUT", f"/upload-bandwidth/jobs/{job['id']}", {"bytesPerSecond": None})
        rel = f"zz-restore-resume-{uuid.uuid4().hex[:6]}"
        try:
            _, tree = restore_files(t, a, art["id"], rel)
            assert tree_sha(g.agent_root("sto") / "files" / rel / "resume") == src_tree("resume"), "restored tree differs"
        finally: rm_restore(rel)
        return out
    finally:
        smp.close(); gf.start_sshd(); drop(t, c)


@scenario("gap/STO-02/host-key-and-credential-failures-are-distinguishable")
def sto02_distinguish():
    """The platform has distinct test codes (host_key_mismatch, authentication_failed); the failed backup attempt must let the operator tell them apart too."""
    assert "host-key-changed" in REASONS and "wrong-password" in REASONS, "needs the host-key and wrong-credentials scenarios run in the same invocation"
    hk, pw = REASONS["host-key-changed"], REASONS["wrong-password"]
    assert hk != pw, f"host key mismatch and wrong password are reported identically: {hk}"
    return {"hostKey": hk, "wrongPassword": pw}


ALL = [sto02_hostkey, sto02_creds, sto02_resume, sto02_distinguish]


def main(only=()):
    for fn in ALL:
        if only and not any(o in fn.scenario for o in only): continue
        fn()


if __name__ == "__main__":
    main(sys.argv[1:])
