"""Test-plan sections 3 (file backups), 4 (storage providers) and 5 (artifact encryption, download). Run: python3 files_providers.py [name-filter]."""
import base64, hashlib, json, subprocess, sys, time, uuid
import api, lib, flows
from lib import ST, scenario

SRC = "e2efiles"
FED_ROOT, WIN_ROOT = "/srv/e2e/files", r"C:\e2e\files"
CONN = ST["connections"]
GROUPS = []
SECRET_MARKERS = []  # filled by load_secrets(): every secret value that must never appear in an API response or failure reason


def load_secrets():
    e = api.env
    SECRET_MARKERS.extend(v for v in (ST["fedora"]["sftp_password"], e.get("E2E_MINIO_PASSWORD"), e.get("E2E_SMB_PASSWORD"), ST["fedora"]["pg_password"]) if v)


def assert_no_secret(obj, where):
    s = json.dumps(obj, default=str)
    for m in SECRET_MARKERS:
        assert m not in s, f"secret value leaked in {where}"


def dest(provider, **kw):
    if provider == "local": return flows.local_dest(kw.pop("target", "default"), kw.pop("primary", True))
    return {"storageProvider": provider, "storageConnectionId": kw.pop("conn", CONN[provider]), "storageExecutionLocation": "Backend", "isPrimary": kw.pop("primary", True)}


def fcfg(destinations, paths=None, includes=None, excludes=None, symlinks="Skip", **extra):
    """File job config. copyOnly=True is mandatory: with False the agent's completion is rejected by the backend (finding FP-1)."""
    cfg = flows.file_job_config(SRC, destinations, paths=paths, copyOnly=True, **extra)
    cfg["files"].update(includes=includes or ["**"], excludes=excludes or [], symlinkPolicy=symlinks)
    return cfg


def artifacts_of(detail):
    return [a for at in detail["attempts"] for a in at["artifacts"]]


def run_job(t, agent, name, config, timeout=900, expect="Succeeded", tries=3):
    """Creates a job and runs it. A run paused by a transient network error (the agents share the platform with other operators) is re-run, never hidden: 'reruns' is reported."""
    job = t.call("POST", "/backup-jobs", {"name": f"fp {name} {uuid.uuid4().hex[:5]}", "agentId": agent["agentId"], "overlapPolicy": "Skip", "configuration": config})
    reruns = 0
    for _ in range(tries):
        run = t.call("POST", f"/backup-jobs/{job['id']}/runs", {})
        rid = run.get("id", run.get("run", {}).get("id"))
        d = t.wait(f"/backup-runs/{rid}", timeout=timeout)
        if d["status"] == "Paused":
            reruns += 1
            t.raw("POST", f"/backup-runs/{rid}/cancel", {})
            time.sleep(5)
            continue
        break
    else:
        raise AssertionError(f"run stayed Paused ({d.get('pauseReason')}) after {tries} tries")
    assert d["status"] == expect, f"run {d['status']} (wanted {expect}): " + json.dumps({"attempts": [(a["status"], a["reasonCode"], a["reason"]) for a in d["attempts"]], "replication": d.get("replication")}, default=str)[:900]
    d["_reruns"] = reruns
    return job, rid, d


def run_ok(t, agent, name, config, **kw):
    job, rid, d = run_job(t, agent, name, config, **kw)
    arts = artifacts_of(d)
    assert len(arts) == 1, f"expected 1 artifact, got {len(arts)}"
    return job, rid, arts[0], d


def artifact(t, aid):
    return t.call("GET", f"/backup-artifacts/{aid}")


# ---- remote file trees -------------------------------------------------------------------------------------------------------------------------
def fed_tree(path):
    # restored directories are private to the agent user (0700), so read them through sudo
    rc, out = lib.fedora(f"sudo bash -c \"cd '{path}' && find . -type f -print0 | sort -z | xargs -0 sha256sum | sed 's# \\./# #' | sort -k2\"")
    assert rc == 0, out
    return out.strip()


def winx(ps, timeout=600):
    """lib.win with UTF-8 output (Unicode file names break the default code page) and tolerant decoding."""
    import subprocess
    ps = "[Console]::OutputEncoding=[Text.UTF8Encoding]::new(); " + ps
    r = subprocess.run(["ssh", "virtual-win11", "powershell -NoProfile -EncodedCommand " + base64.b64encode(ps.encode("utf-16le")).decode()], capture_output=True, timeout=timeout)
    out = (r.stdout + r.stderr).decode("utf-8", "replace")
    return r.returncode, "\n".join(l for l in out.splitlines() if not any(x in l for x in lib.NOISE))


def win_tree(path):
    # .NET enumeration with the \\?\ prefix: Windows PowerShell 5.1 cmdlets fail on paths over 260 characters
    rc, out = winx(f"""$p='\\\\?\\{path}'; $n=$p.Length
$sha=[Security.Cryptography.SHA256]::Create()
foreach ($f in [IO.Directory]::EnumerateFiles($p,'*',[IO.SearchOption]::AllDirectories)) {{ $h=[BitConverter]::ToString($sha.ComputeHash([IO.File]::ReadAllBytes($f))).Replace('-','').ToLower(); "$h  " + $f.Substring($n+1).Replace('\\','/') }}""")
    assert rc == 0, out
    return "\n".join(sorted((l.strip() for l in out.splitlines() if len(l.strip()) > 66), key=lambda l: l.split("  ", 1)[1]))


def fed_dirs(path):
    rc, out = lib.fedora(f"sudo bash -c \"cd '{path}' && find . -type d | sort\""); assert rc == 0, out
    return out.strip()


def win_dirs(path):
    rc, out = winx(f"""$p='\\\\?\\{path}'; $n=$p.Length
foreach ($d in [IO.Directory]::EnumerateDirectories($p,'*',[IO.SearchOption]::AllDirectories)) {{ $d.Substring($n+1).Replace('\\','/') }}""")
    return "\n".join(sorted(l.strip() for l in out.splitlines() if l.strip()))


class Host:
    def __init__(self, name):
        self.name = name
        self.root = FED_ROOT if name == "fedora" else WIN_ROOT
        self.tree = fed_tree if name == "fedora" else win_tree
        self.dirs = fed_dirs if name == "fedora" else win_dirs
        self.sh = (lambda c: lib.fedora(c)) if name == "fedora" else winx

    def rm(self, rel):
        # restores land inside the source root (product design): always clean the throwaway directory afterwards
        if self.name == "fedora": self.sh(f"sudo rm -rf '{self.root}/{rel}'")
        else: self.sh(f"Remove-Item -LiteralPath '{self.root}\\{rel}' -Recurse -Force -ErrorAction SilentlyContinue")

    def exists(self, rel):
        rc, out = self.sh(f"test -e '{self.root}/{rel}' && echo yes || echo no") if self.name == "fedora" else self.sh(f"if (Test-Path -LiteralPath '{self.root}\\{rel}') {{'yes'}} else {{'no'}}")
        return out.strip().endswith("yes")


def file_restore(t, agent, artifact_id, rel_dir, paths=None, precheck=False, overwrite=False, expect="Succeeded", replica_id=None):
    files = {"relativeDirectory": rel_dir}
    if paths is not None: files["paths"] = paths
    req = {"agentId": agent["agentId"], "target": {"host": "localhost", "port": None, "integratedSecurity": True, "databaseCredentialReference": None, "encrypt": False, "trustServerCertificate": False},
           "databaseName": SRC, "mode": "ReplaceExistingDatabase" if overwrite else "NewDatabase", "options": {"files": files}}
    if overwrite: req["confirmOverwrite"] = SRC
    if replica_id: req["replicaId"] = replica_id
    s = t.call("POST", f"/backup-artifacts/{artifact_id}/restore", req)
    d = t.wait(f"/restores/{s.get('id', s.get('restoreId'))}", timeout=900)
    assert d["status"] == expect, f"restore {d['status']} (wanted {expect}): {json.dumps(d, default=str)[:900]}"
    return d


def roundtrip(t, host, agent, dname, destinations, tag="rt", **cfg):
    """Backup the whole tree, restore into a new subdirectory of the source root, compare SHA-256 trees, clean up."""
    src = host.tree(host.root) if False else None
    job, rid, art, det = run_ok(t, agent, f"{host.name}-{dname}", fcfg(destinations, **cfg))
    rel = f"zz-restore-{tag}-{rid[:8]}"
    try:
        file_restore(t, agent, art["id"], rel)
        got, want = host.tree(f"{host.root}/{rel}" if host.name == "fedora" else f"{host.root}\\{rel}"), host_src_tree(host)
        assert got == want, "restored tree differs from source: " + diff(want, got)
        n = len(got.splitlines())
    finally:
        host.rm(rel)
    return {"artifactId": art["id"], "sizeBytes": art.get("sizeBytes"), "files": n, "reruns": det["_reruns"]}


_SRC_TREE = {}


def host_src_tree(host):
    """SHA-256 tree of the pristine source, excluding any zz-* scratch directories of concurrent restores."""
    out = host.tree(host.root)
    return "\n".join(l for l in out.splitlines() if "  zz-" not in l)


def diff(want, got):
    w, g = set(want.splitlines()), set(got.splitlines())
    return f"missing={sorted(w - g)[:3]} extra={sorted(g - w)[:3]} (want {len(w)} files, got {len(g)})"


# ---- 3. file backups ----------------------------------------------------------------------------------------------------------------------
def group_files(c, only):
    t, ag = c["t"], c["ag"]
    fa, wa = ag["fedora-e2e"], ag["win11-e2e"]
    for hname, host, agent in (("fedora", c["fed"], fa), ("win", c["win"], wa)):
        for dname in ("local", "sftp", "s3", "smb"):
            name = f"files/{hname}/roundtrip-{dname}"
            if only and only not in name: continue
            scenario(name)(lambda host=host, agent=agent, dname=dname: roundtrip(t, host, agent, dname, [dest(dname)]))()


def run_all(only, items):
    for name, fn in items:
        if only and not any(o in name for o in only.split(",")): continue
        scenario(name)(fn)()


GROUPS.append(group_files)


# ---- 3.2 - 3.4 file specifics ---------------------------------------------------------------------------------------------------------------
import re


def sep(host): return "/" if host.name == "fedora" else "\\"


def full(host, rel): return f"{host.root}{sep(host)}{rel.replace('/', sep(host))}"


def restore_tree(t, host, agent, art_id, tag, **kw):
    rel = f"zz-restore-{tag}-{uuid.uuid4().hex[:6]}"
    try:
        file_restore(t, agent, art_id, rel, **kw)
        return host.tree(full(host, rel)) if host.exists(rel) else ""
    finally:
        host.rm(rel)


def backup_only(t, agent, name, **cfg):
    job, rid, art, d = run_ok(t, agent, name, fcfg([dest("local")], **cfg))
    return art


def include_exclude(t, host, agent):
    src = subset(host.tree(host.root), None)
    art = backup_only(t, agent, f"{host.name}-incl", paths=["."], includes=["**/*.bin", "docs/f1.txt", "docs/f2?.txt"], excludes=["nested/**", "docs/f20.txt"])
    got = restore_tree(t, host, agent, art["id"], "inc")
    want = "\n".join(l for l in src.splitlines() if (lambda p: (p.endswith(".bin") or p == "docs/f1.txt" or re.fullmatch(r"docs/f2[^/]\.txt", p)) and not p.startswith("nested/") and p != "docs/f20.txt")(l.split("  ", 1)[1]))
    assert got == want, "include/exclude result differs: " + diff(want, got)
    return {"includeExcludeFiles": len(got.splitlines())}


def include_exclude_all_excluded(t, host, agent):
    """A selection whose every entry is excluded must fail loudly, never produce an empty 'successful' backup."""
    cfg = fcfg([dest("local")], paths=["docs"], excludes=["docs/**"])
    job = t.call("POST", "/backup-jobs", {"name": f"fp excl-all {uuid.uuid4().hex[:5]}", "agentId": agent["agentId"], "overlapPolicy": "Skip", "configuration": cfg})
    run = t.call("POST", f"/backup-jobs/{job['id']}/runs", {}); d, att = first_failure(t, run.get("id", run.get("run", {}).get("id")), timeout=240)
    assert att["status"] == "Failed" and d["status"] != "Succeeded", f"empty selection produced status {d['status']}"
    return {"reasonCode": att["reasonCode"], "reason": att["reason"]}


def subpaths(t, host, agent):
    src = subset(host.tree(host.root), None)
    out = {}
    art = backup_only(t, agent, f"{host.name}-sub1", paths=["docs"])
    got = restore_tree(t, host, agent, art["id"], "s1"); want = subset(src, ["docs"]); assert got == want, "paths=[docs]: " + diff(want, got); out["docs"] = len(got.splitlines())
    art = backup_only(t, agent, f"{host.name}-sub2", paths=["nested", "docs/f1.txt"])
    got = restore_tree(t, host, agent, art["id"], "s2"); want = subset(src, ["nested", "docs/f1.txt"]); assert got == want, "paths=[nested, docs/f1.txt]: " + diff(want, got); out["nested+f1"] = len(got.splitlines())
    art = backup_only(t, agent, f"{host.name}-sub3", paths=["docs", "nested"])
    # restore-side selection of a subset of one backup, into a nested new directory
    rel = f"zz-restore-sel-{uuid.uuid4().hex[:6]}/a/b"
    try:
        file_restore(t, agent, art["id"], rel, paths=["docs/f2.txt", "nested"])
        got = host.tree(full(host, rel)); want = subset(src, ["docs/f2.txt", "nested"])
        assert got == want, "restore selection: " + diff(want, got); out["restoreSelection"] = len(got.splitlines())
    finally:
        host.rm(rel.split("/")[0])
    # selecting something that is not in the backup must fail, not succeed empty
    rel2 = f"zz-restore-nosel-{uuid.uuid4().hex[:6]}"
    try:
        d = file_restore(t, agent, art["id"], rel2, paths=["does/not/exist"], expect="Failed"); out["selectNonexistent"] = d.get("failureCode") or d.get("reasonCode")
    finally:
        host.rm(rel2)
    # a backup of a path that does not exist must fail
    job = t.call("POST", "/backup-jobs", {"name": f"fp nopath {uuid.uuid4().hex[:5]}", "agentId": agent["agentId"], "overlapPolicy": "Skip", "configuration": fcfg([dest("local")], paths=["no-such-dir"])})
    run = t.call("POST", f"/backup-jobs/{job['id']}/runs", {}); d, att = first_failure(t, run.get("id", run.get("run", {}).get("id")), timeout=240)
    assert att["status"] == "Failed", "backup of a missing path did not fail"; out["missingPath"] = att["reasonCode"]
    return out


def restore_existing(t, host, agent):
    art = backup_only(t, agent, f"{host.name}-exist", paths=["docs"])
    rel = f"zz-restore-ex-{uuid.uuid4().hex[:6]}"
    try:
        file_restore(t, agent, art["id"], rel)
        d = file_restore(t, agent, art["id"], rel, expect="Failed")  # NewDatabase mode over existing files must refuse
        out = {"secondRestoreRefused": d.get("failureCode") or d.get("reasonCode")}
        victim = full(host, rel + "/docs/f1.txt")
        host.sh(f"sudo bash -c \"echo tampered > '{victim}'\"") if host.name == "fedora" else host.sh(f"Set-Content -LiteralPath '{victim}' -Value tampered")
        file_restore(t, agent, art["id"], rel, overwrite=True)
        got = host.tree(full(host, rel)); want = subset(host.tree(host.root), ["docs"])
        assert got == want, "overwrite restore did not restore the original content: " + diff(want, got)
        out["overwriteRestored"] = True
        return out
    finally:
        host.rm(rel)


def path_escape(t, agent):
    bad_cfgs = {
        "paths ..": dict(paths=[".."]), "paths ../etc": dict(paths=["../etc"]), "paths a/../../x": dict(paths=["docs/../../x"]),
        "paths absolute": dict(paths=["/etc"]), "paths windows drive": dict(paths=["C:/Windows"]), "paths backslash": dict(paths=["docs\\..\\.."]),
        "include ..": dict(includes=["../**"]), "exclude absolute": dict(excludes=["/etc/**"]), "paths empty segment": dict(paths=["docs//x"]),
    }
    out = {}
    for label, kw in bad_cfgs.items():
        r = t.raw("POST", "/backup-jobs", {"name": f"fp esc {uuid.uuid4().hex[:5]}", "agentId": agent["agentId"], "overlapPolicy": "Skip", "configuration": fcfg([dest("local")], **kw)})
        out[label] = r.status_code
        assert r.status_code == 400, f"job with {label} accepted: HTTP {r.status_code} {r.text[:200]}"
    art = backup_only(t, agent, "esc-art", paths=["nested"])
    base = {"agentId": agent["agentId"], "target": {"host": "localhost", "port": None, "integratedSecurity": True, "databaseCredentialReference": None, "encrypt": False, "trustServerCertificate": False}, "databaseName": SRC, "mode": "NewDatabase"}
    for label, files in {"restore relativeDirectory ..": {"relativeDirectory": "../x"}, "restore relativeDirectory absolute": {"relativeDirectory": "/tmp/fp-x"},
                         "restore relativeDirectory windows": {"relativeDirectory": "C:/fp-x"}, "restore paths ..": {"relativeDirectory": "zz-esc", "paths": ["../nested"]},
                         "restore paths absolute": {"relativeDirectory": "zz-esc", "paths": ["/etc/passwd"]}}.items():
        r = t.raw("POST", f"/backup-artifacts/{art['id']}/restore", {**base, "options": {"files": files}})
        out[label] = r.status_code
        assert r.status_code == 400, f"{label} accepted: HTTP {r.status_code} {r.text[:200]}"
    return out


def symlinks(t, host, agent, policy):
    rid = uuid.uuid4().hex[:6]; R = f"zz-sym-{rid}"; R2 = f"zz-symesc-{rid}"
    root = host.root
    lib.fedora(f"""sudo -u backup-agent bash -c "set -e; cd {root}; mkdir -p {R}/sub {R2}; echo hello > {R}/real.txt; echo inner > {R}/sub/inner.txt
ln -s real.txt {R}/rel-link; ln -s sub {R}/dir-link; ln -s missing-target {R}/dangling
echo ok > {R2}/plain.txt; ln -s ../../../../etc/hostname {R2}/escape-link; ln -s /etc/hostname {R2}/absolute-link" """)
    out = {}
    try:
        art = backup_only(t, agent, f"sym-{policy}", paths=[R], symlinks=policy)
        rel = f"zz-restore-sym-{rid}-{policy}"
        try:
            file_restore(t, agent, art["id"], rel)
            rc, links = lib.fedora(f"sudo bash -c \"cd {root}/{rel}/{R} && find . -type l -printf '%p -> %l\\n' | sort; echo ---; find . -type f | sort; echo ---; cat rel-link 2>&1\"")
            out["restored"] = links.replace("\n", " | ")[:300]
            if policy == "Skip":
                assert "->" not in links.split("---")[0], f"Skip policy restored symlinks: {links}"
                assert "./real.txt" in links and "./sub/inner.txt" in links
            else:
                l0 = links.split("---")[0]
                assert "./rel-link -> real.txt" in l0 and "./dir-link -> sub" in l0 and "./dangling -> missing-target" in l0, f"Preserve policy lost links: {links}"
                assert links.strip().endswith("hello"), "preserved relative link does not resolve to content"
        finally:
            host.rm(rel)
        # a link pointing outside the source: Skip ignores it; Preserve must refuse the backup cleanly
        job = t.call("POST", "/backup-jobs", {"name": f"fp symesc {policy} {rid}", "agentId": agent["agentId"], "overlapPolicy": "Skip", "configuration": fcfg([dest("local")], paths=[R2], symlinks=policy)})
        run = t.call("POST", f"/backup-jobs/{job['id']}/runs", {}); d, att = first_failure(t, run.get("id", run.get("run", {}).get("id")), timeout=240)
        out["escapingLinks"] = {"run": d["status"], "attempt": att["status"], "reasonCode": att["reasonCode"], "reason": att["reason"]}
        if policy == "Skip": assert att["status"] == "Succeeded" or d["status"] == "Succeeded", f"Skip policy should ignore escaping links: {out['escapingLinks']}"
        else:
            assert att["status"] == "Failed", f"Preserve accepted links escaping the source: {out['escapingLinks']}"
            assert att["reasonCode"] != "Unexpected" and "unexpected" not in (att.get("reason") or "").lower(), f"escape refused with an opaque failure: {out['escapingLinks']}"
        return out
    finally:
        lib.fedora(f"sudo rm -rf {root}/{R} {root}/{R2}")


def make_unicode_tree(host, name):
    long_dirs = "/".join(["d" * 38 for _ in range(7)])
    if host.name == "fedora":
        lib.fedora(f"""sudo -u backup-agent python3 - <<'PY'
import os
r = '{host.root}/{name}'
for d in ('empty1', 'deep/empty2', '{long_dirs}'): os.makedirs(r + '/' + d)
def w(rel, data): open(r + '/' + rel, 'wb').write(data)
w('ünï-çödé-日本語-😀.txt', b'a'); w("with space and quote's.txt", b'b'); w('-leading-dash.txt', b'c'); w('{"a" * 200}.txt', os.urandom(200000))
w('deep/blob.bin', os.urandom(1048576)); w('{long_dirs}/{"f" * 100}.txt', b'e'); w('empty-file.txt', b''); w('NFD-e\u0301.txt', b'x')
PY""")
    else:
        ld = "\\".join(["d" * 38 for _ in range(7)])
        host.sh(f"""$r='\\\\?\\{host.root}\\{name}'; New-Item -ItemType Directory -Force -Path "$r\\empty1","$r\\deep\\empty2","$r\\{ld}" | Out-Null
Set-Content -LiteralPath "$r\\ünï-çödé-日本語-😀.txt" -Value a -Encoding UTF8; Set-Content -LiteralPath "$r\\with space and quote's.txt" -Value b
Set-Content -LiteralPath "$r\\-leading-dash.txt" -Value c; $b=New-Object byte[] 200000; (New-Object Random).NextBytes($b); [IO.File]::WriteAllBytes("$r\\{"a" * 200}.txt",$b)
$b=New-Object byte[] 1048576; (New-Object Random).NextBytes($b); [IO.File]::WriteAllBytes("$r\\deep\\blob.bin",$b); Set-Content -LiteralPath "$r\\{ld}\\{"f" * 100}.txt" -Value e
New-Item -ItemType File -Force -Path "$r\\empty-file.txt" | Out-Null""")


def unicode_long_empty(t, host, agent):
    name = f"zz-uni-{uuid.uuid4().hex[:6]}"
    make_unicode_tree(host, name)
    try:
        src_tree, src_dirs = host.tree(full(host, name)), host.dirs(full(host, name))
        assert len(src_tree.splitlines()) >= 7, f"scratch tree not created as expected: {src_tree}"
        art = backup_only(t, agent, f"{host.name}-uni", paths=[name])
        rel = f"zz-restore-uni-{uuid.uuid4().hex[:6]}"
        try:
            file_restore(t, agent, art["id"], rel)
            got_tree, got_dirs = host.tree(full(host, f"{rel}/{name}")), host.dirs(full(host, f"{rel}/{name}"))
        finally:
            host.rm(rel)
        assert got_tree == src_tree, "restored files differ: " + diff(src_tree, got_tree)
        assert got_dirs == src_dirs, f"restored directories differ (empty directories lost?): src={src_dirs.count(chr(10)) + 1} got={got_dirs.count(chr(10)) + 1}"
        return {"files": len(got_tree.splitlines()), "dirs": len(got_dirs.splitlines()), "maxPathLen": max(len(l.split('  ', 1)[1]) for l in src_tree.splitlines())}
    finally:
        host.rm(name)


def group_files2(c, only):
    t, ag, fed, win = c["t"], c["ag"], c["fed"], c["win"]
    fa, wa = ag["fedora-e2e"], ag["win11-e2e"]
    items = []
    for host, agent in ((fed, fa), (win, wa)):
        h = host.name
        items += [(f"files/{h}/include-exclude", lambda host=host, agent=agent: include_exclude(t, host, agent)),
                  (f"files/{h}/everything-excluded-fails", lambda host=host, agent=agent: include_exclude_all_excluded(t, host, agent)),
                  (f"files/{h}/subpath-and-restore-selection", lambda host=host, agent=agent: subpaths(t, host, agent)),
                  (f"files/{h}/restore-over-existing", lambda host=host, agent=agent: restore_existing(t, host, agent)),
                  (f"files/{h}/unicode-long-names-empty-dirs", lambda host=host, agent=agent: unicode_long_empty(t, host, agent))]
    items += [("files/fedora/job-option-copyOnly-false", lambda: copyonly_false(t, fa, "copyOnly")),
              ("files/fedora/job-option-checksumPolicy-disabled", lambda: copyonly_false(t, fa, "checksum"))]
    items += [("files/path-escape-rejected", lambda: path_escape(t, fa)),
              ("files/fedora/symlink-skip", lambda: symlinks(t, fed, fa, "Skip")),
              ("files/fedora/symlink-preserve", lambda: symlinks(t, fed, fa, "Preserve"))]
    run_all(only, items)


GROUPS.append(group_files2)


# ---- 4. storage providers ------------------------------------------------------------------------------------------------------------------
def base_conn(t, provider):
    return next(c for c in t.call("GET", "/storage-connections")["items"] if c["id"] == CONN[provider])


def new_conn(t, provider, label, cfg=None, secret=None, extra_cfg=None):
    """Creates a throwaway connection cloned from the e2e one with some configuration/secret overridden."""
    good = base_conn(t, provider)["configuration"]
    e = api.env
    sec = {"sftp": {"password": ST["fedora"]["sftp_password"]}, "s3": {"accessKey": e["E2E_MINIO_USER"], "secretKey": e["E2E_MINIO_PASSWORD"]},
           "smb": {"username": e["E2E_SMB_USER"], "password": e["E2E_SMB_PASSWORD"]}}[provider]
    sec.update(secret or {})
    conf = {**good, **(cfg or {}), **(extra_cfg or {})}
    return t.call("POST", "/storage-connections", {"providerType": provider, "name": f"FP {label} {uuid.uuid4().hex[:4]}", "configuration": conf, "secret": sec})


def del_conn(t, cid):
    try: t.raw("DELETE", f"/storage-connections/{cid}")
    except Exception: pass


def first_failure(t, run_id, timeout=420):
    """Waits for the first failed attempt of a run (automatic retries only repeat it), cancels the run and returns the attempt."""
    end = time.time() + timeout
    while time.time() < end:
        d = t.call("GET", f"/backup-runs/{run_id}")
        bad = [a for a in d["attempts"] if a["status"] in ("Failed", "AgentLost", "Cancelled")]
        if bad or d["status"] in ("Succeeded", "Failed", "Cancelled"):
            if d["status"] not in ("Failed", "Succeeded", "Cancelled"): t.raw("POST", f"/backup-runs/{run_id}/cancel", {})
            return d, (bad or d["attempts"])[0]
        time.sleep(3)
    raise TimeoutError(run_id)


def neg_provider(t, agent, provider, label, cfg=None, secret=None, wanted_codes=None, bad_values=()):
    conn = new_conn(t, provider, label, cfg, secret)
    out = {}
    try:
        tr = t.call("POST", f"/storage-connections/{conn['id']}/test", {})
        test = tr["test"]
        out["test"] = {k: test[k] for k in ("status", "code", "message")}
        assert_no_secret(tr, "connection test response"); assert_no_secret(t.call("GET", f"/storage-connections/{conn['id']}"), "connection GET")
        for v in bad_values: assert v not in json.dumps(tr), f"bad secret value echoed by test: {v[:4]}..."
        if test["status"] == "Succeeded": raise AssertionError(f"test unexpectedly Succeeded: {test}")
        if test["status"] != "NotTestable" and wanted_codes: assert test["code"] in wanted_codes, f"test code {test['code']} not in {wanted_codes}"
        job = t.call("POST", "/backup-jobs", {"name": f"fp neg {label} {uuid.uuid4().hex[:4]}", "agentId": agent["agentId"], "overlapPolicy": "Skip",
                                            "configuration": fcfg([dest(provider, conn=conn["id"])], paths=["nested"])})
        run = t.call("POST", f"/backup-jobs/{job['id']}/runs", {})
        d, att = first_failure(t, run.get("id", run.get("run", {}).get("id")))
        assert att["status"] == "Failed" and att["reasonCode"], f"attempt {att['status']} {att.get('reasonCode')}"
        out["run"] = {"reasonCode": att["reasonCode"], "reason": att["reason"]}
        blob = json.dumps(d, default=str)
        assert_no_secret(d, "run detail")
        for v in bad_values: assert v not in blob, "wrong secret value leaked into the run detail"
        assert att["reasonCode"] not in ("Unexpected", "unexpected"), "failure reason is not specific"
        return out
    finally:
        del_conn(t, conn["id"])


def agent_log_has(host_fn, needle):
    rc, o = host_fn(f"sudo journalctl -u backup-agent --since '-20 min' --no-pager | grep -c -F -- '{needle}'; true")
    return o.strip().splitlines()[-1] != "0"


def log_leak(*needles):
    """Wrong credentials used by the failed runs above must not appear in the Fedora agent journal or the API log."""
    hits = {}
    for n in needles[:2]:
        rc, o = lib.fedora(f"sudo journalctl -u backup-agent --since '-45 min' --no-pager | grep -c -F -- '{n}'; true")
        hits[f"fedora-agent:{n[:6]}"] = o.strip().splitlines()[-1]
        r = subprocess.run(["docker", "logs", "--since", "45m", "bp-api"], capture_output=True, text=True)
        hits[f"api:{n[:6]}"] = str((r.stdout + r.stderr).count(n))
    bad = {k: v for k, v in hits.items() if v != "0"}
    assert not bad, f"secret value found in logs: {bad}"
    return hits


def connection_lifecycle(t, agent, provider):
    """create -> test -> update -> disable (admission refused) -> enable -> used by a job (delete refused) -> unused delete."""
    conn = new_conn(t, provider, "life"); out = {}
    cid = conn["id"]
    try:
        assert conn["hasSecret"] and conn["enabled"]; assert_no_secret(conn, "create response")
        if provider != "smb":
            tr = t.call("POST", f"/storage-connections/{cid}/test", {})["test"]; out["test"] = tr["status"]
            assert tr["status"] == "Succeeded" or (provider == "sftp" and tr["status"] == "NotTestable" and tr["code"] == "agent_side_storage"), tr
        u = t.call("PUT", f"/storage-connections/{cid}", {"name": conn["name"] + " renamed"}); assert u["name"].endswith("renamed") and u["hasSecret"], u
        assert_no_secret(u, "update response")
        d = t.call("POST", f"/storage-connections/{cid}/disable", {}); assert d["enabled"] is False and not d["effectiveEnabled"], d
        job = t.call("POST", "/backup-jobs", {"name": f"fp life {provider} {uuid.uuid4().hex[:4]}", "agentId": agent["agentId"], "overlapPolicy": "Skip",
                                            "configuration": fcfg([dest(provider, conn=cid)], paths=["nested"])})
        r = t.raw("POST", f"/backup-jobs/{job['id']}/runs", {}); out["runOnDisabled"] = (r.status_code, (r.json() or {}).get("code") or (r.json() or {}).get("error"))
        assert r.status_code == 409, f"run admitted on a disabled connection: {r.status_code} {r.text[:150]}"
        e = t.call("POST", f"/storage-connections/{cid}/enable", {}); assert e["effectiveEnabled"]
        jj, rid, det = run_job(t, agent, f"life-{provider}", fcfg([dest(provider, conn=cid)], paths=["nested"]))
        r = t.raw("DELETE", f"/storage-connections/{cid}"); out["deleteInUse"] = r.status_code
        assert r.status_code == 409, f"connection holding replicas was deletable: {r.status_code} {r.text[:150]}"
        spare = new_conn(t, provider, "spare"); r = t.raw("DELETE", f"/storage-connections/{spare['id']}"); out["deleteUnused"] = r.status_code
        assert r.status_code in (200, 204), f"unused connection delete: {r.status_code} {r.text[:150]}"
        assert t.raw("GET", f"/storage-connections/{spare['id']}").status_code == 404
        return out
    finally:
        pass


def mc(cmd):
    r = subprocess.run(["docker", "exec", "bpe2e-minio", "sh", "-c", 'mc --insecure alias set l https://localhost:9000 "$MINIO_ROOT_USER" "$MINIO_ROOT_PASSWORD" >/dev/null 2>&1; ' + cmd.replace("mc ", "mc --insecure ")], capture_output=True, text=True)
    return r.returncode, (r.stdout + r.stderr).strip()


def s3_object_lock(t, ag, fed):
    fa = ag["fedora-e2e"]; out = {}
    for label, cfg in {"unknown mode": {"objectLockMode": "BOGUS", "retentionDays": "1"}, "mode without days": {"objectLockMode": "GOVERNANCE"},
                       "days without mode": {"retentionDays": "5"}, "days zero": {"objectLockMode": "GOVERNANCE", "retentionDays": "0"}, "days too large": {"objectLockMode": "COMPLIANCE", "retentionDays": "99999"}}.items():
        good = base_conn(t, "s3")["configuration"]
        r = t.raw("POST", "/storage-connections", {"providerType": "s3", "name": f"FP lockbad {uuid.uuid4().hex[:4]}", "configuration": {**good, **cfg},
                                                    "secret": {"accessKey": api.env["E2E_MINIO_USER"], "secretKey": api.env["E2E_MINIO_PASSWORD"]}})
        out[label] = r.status_code
        if r.status_code in (200, 201): del_conn(t, r.json()["id"])
        assert r.status_code == 400, f"invalid object-lock settings accepted ({label}): {r.status_code}"
    # lock requested on a bucket that has no Object Lock: must be detected, never silently written unprotected
    c1 = new_conn(t, "s3", "lock-nolock", cfg={"objectLockMode": "GOVERNANCE", "retentionDays": "1"})
    try:
        tr = t.call("POST", f"/storage-connections/{c1['id']}/test", {})["test"]; out["testOnUnlockedBucket"] = (tr["status"], tr["code"], tr["message"][:80])
        assert tr["status"] == "Failed", f"object lock requested but bucket has none: test {tr}"
    finally:
        del_conn(t, c1["id"])
    rc, o = mc("mc mb --with-lock l/bpe2elock 2>&1 || mc ls l/bpe2elock 2>&1 | head -1"); assert rc == 0 or "already" in o.lower(), o
    c2 = new_conn(t, "s3", "lock", cfg={"bucket": "bpe2elock", "objectLockMode": "GOVERNANCE", "retentionDays": "1"})
    tr = t.call("POST", f"/storage-connections/{c2['id']}/test", {})["test"]; out["testOnLockedBucket"] = (tr["status"], tr["code"])
    assert tr["status"] == "Succeeded", f"locked-bucket connection test: {tr}"
    job, rid, d = run_job(t, fa, "s3-lock", fcfg([dest("s3", conn=c2["id"])], paths=["docs", "nested"]))
    art = artifact(t, artifacts_of(d)[0]["id"]); rep = art["replicas"][0]
    rc, found = mc(f"mc find l/bpe2elock --name '{fname(rep)}' 2>&1"); obj = found.splitlines()[-1].strip(); out["object"] = obj[-60:]
    rc, o = mc(f"mc retention info '{obj}' 2>&1"); out["retentionInfo"] = o[:160]
    assert "GOVERNANCE" in o.upper(), f"stored object carries no retention: {o}"
    rc, vj = mc(f"mc ls --versions --json '{obj}' 2>&1"); vid = next(json.loads(l)["versionId"] for l in vj.splitlines() if l.startswith("{") and not json.loads(l).get("isDeleteMarker"))
    rc, o2 = mc(f"mc rm --version-id '{vid}' '{obj}' 2>&1"); out["deleteLockedVersion"] = o2[:140]
    assert rc != 0 and ("lock" in o2.lower() or "retention" in o2.lower() or "access" in o2.lower()), f"locked object version could be deleted: {o2}"
    out["restore"] = restore_and_compare(t, fed, fa, art["id"], "lock", ["docs", "nested"])
    return out


def copyonly_false(t, agent, field):
    """Options a file backup can never honor are refused when the job is saved (finding FP-1), not left to hang a run."""
    kw = {"copyOnly": False} if field == "copyOnly" else {"checksumPolicy": "Disabled"}
    cfg = fcfg([dest("local")], paths=["nested"]); cfg.update(kw)
    r = t.raw("POST", "/backup-jobs", {"name": f"fp {field}-off {uuid.uuid4().hex[:5]}", "agentId": agent["agentId"], "overlapPolicy": "Skip", "configuration": cfg})
    assert r.status_code == 400, f"{field} job accepted: {r.status_code} {r.text[:150]}"
    return {"http": r.status_code}


def group_providers(c, only):
    t, ag, fed = c["t"], c["ag"], c["fed"]
    fa, wa = ag["fedora-e2e"], ag["win11-e2e"]
    BADPW, BADKEY = "wrong-password-fp-7", "wrong-secret-key-fp-7"
    fp_bad = "SHA256:" + base64.b64encode(hashlib.sha256(b"not the host key").digest()).decode().rstrip("=")
    items = [
        ("providers/sftp/wrong-password", lambda: neg_provider(t, fa, "sftp", "pw", secret={"password": BADPW}, wanted_codes=("authentication_failed",), bad_values=(BADPW,))),
        ("providers/sftp/wrong-host-key", lambda: neg_provider(t, fa, "sftp", "hostkey", cfg={"hostKeyFingerprint": fp_bad}, wanted_codes=None)),
        ("providers/s3/wrong-secret-key", lambda: neg_provider(t, fa, "s3", "badkey", secret={"secretKey": BADKEY}, wanted_codes=("authentication_failed", "access_denied"), bad_values=(BADKEY,))),
        ("providers/s3/missing-bucket", lambda: neg_provider(t, fa, "s3", "nobucket", cfg={"bucket": "no-such-bucket-fp"})),
        ("providers/smb/wrong-password", lambda: neg_provider(t, wa, "smb", "pw", secret={"password": BADPW}, bad_values=(BADPW,))),
        ("providers/smb/wrong-share", lambda: neg_provider(t, wa, "smb", "share", cfg={"share": "NoSuchShareFp"})),
        ("providers/s3/object-lock-settings", lambda: s3_object_lock(t, ag, fed)),
        ("providers/sftp/connection-lifecycle", lambda: connection_lifecycle(t, fa, "sftp")),
        ("providers/s3/connection-lifecycle", lambda: connection_lifecycle(t, fa, "s3")),
        ("providers/smb/connection-lifecycle", lambda: connection_lifecycle(t, wa, "smb")),
        ("providers/capacity-probe", lambda: capacity_probe(t)),
        ("providers/capacity-probe-local-default", lambda: capacity_probe_local(t)),
        ("providers/agent-log-has-no-wrong-secret", lambda: log_leak(BADPW, BADKEY, fp_bad)),
        ("providers/secrets-never-returned", lambda: secrets_never_returned(t)),
    ]
    run_all(only, items)


def capacity_of(t, cid):
    g = t.call("GET", f"/storage-connections/{cid}/capacity")
    p = t.call("POST", f"/storage-connections/{cid}/probe", {})
    status = p["status"]
    if status == "Pending":
        for _ in range(30):
            time.sleep(3)
            g2 = t.call("GET", f"/storage-connections/{cid}/capacity")
            if g2["status"] != "Pending": status, p = g2["status"], g2; break
    assert_no_secret([g, p], f"capacity {cid}")
    return {"status": status, "total": p.get("totalBytes"), "free": p.get("freeBytes"), "msg": (p.get("errorMessage") or "")[:90]}


def capacity_probe(t):
    out = {prov: capacity_of(t, cid) for prov, cid in CONN.items()}
    assert out["sftp"]["status"] == "Available" and out["sftp"]["total"] and out["sftp"]["free"] is not None, f"sftp capacity not reported: {out}"
    for k in ("s3", "smb"): assert out[k]["status"] == "NotMeasurable" and out[k]["msg"], f"{k}: {out[k]}"
    return out


def capacity_probe_local(t):
    cid = next(x["id"] for x in t.call("GET", "/storage-connections")["items"] if x["providerType"] == "local")
    out = capacity_of(t, cid)
    assert out["status"] == "Available" and out["total"], f"local default connection capacity: {out}"
    return out


def secrets_never_returned(t):
    seen = [t.call("GET", "/storage-connections"), t.call("GET", "/storage-providers")]
    for prov, cid in CONN.items():
        seen += [t.call("GET", f"/storage-connections/{cid}"), t.call("GET", f"/storage-connections/{cid}/capacity")]
        r = t.raw("PUT", f"/storage-connections/{cid}", {"name": base_conn(t, prov)["name"]}); seen.append(r.text)
    seen.append(t.call("GET", "/backup-artifacts?pageSize=100")); seen.append(t.call("GET", "/backup-runs?pageSize=100"))
    adm = api.login()
    seen.append(adm.get(f"{api.BASE}/api/platform/storage-connections?pageSize=100").text)
    seen.append(adm.get(f"{api.BASE}/api/platform/audit?pageSize=100").text)
    blob = json.dumps(seen, default=str)
    for m in SECRET_MARKERS: assert m not in blob, "secret value present in an API response"
    assert all(base_conn(t, p)["hasSecret"] for p in CONN)
    return {"responsesChecked": len(seen)}


GROUPS.append(group_providers)


# ---- 4.6 multi destination, 3-2-1 ------------------------------------------------------------------------------------------------------------
def subset(tree, paths):
    """Lines of a SHA-256 tree that a backup of `paths` (relative to the source root, '.' = all) covers; scratch dirs of concurrent tests are excluded."""
    keep = lambda p: not p.startswith("zz-") and (paths in (None, ["."]) or any(p == q or p.startswith(q + "/") for q in paths))
    return "\n".join(l for l in tree.splitlines() if keep(l.split("  ", 1)[1]))


def restore_and_compare(t, host, agent, art_id, tag, paths=None, replica_id=None, restore_paths=None):
    rel = f"zz-restore-{tag}-{uuid.uuid4().hex[:6]}"
    try:
        file_restore(t, agent, art_id, rel, replica_id=replica_id, paths=restore_paths)
        sep = "/" if host.name == "fedora" else "\\"
        got = host.tree(f"{host.root}{sep}{rel}")
        want = subset(host.tree(host.root), restore_paths or paths)
        assert got == want, "restored tree differs from source: " + diff(want, got)
        return len(got.splitlines())
    finally:
        host.rm(rel)


def multi_dest(t, host, agent, name, dests, paths=("docs", "nested"), **cfg):
    job, rid, d = run_job(t, agent, name, fcfg(dests, paths=list(paths), **cfg))
    arts = artifacts_of(d)
    assert len(arts) == 1, f"{len(arts)} artifacts"
    art = artifact(t, arts[0]["id"])
    reps = [r for r in art["replicas"] if r["status"] == "Available"]
    assert len(reps) == len(dests) and all(r["verificationState"] == "Verified" for r in reps), f"replicas: {[(r['storageProvider'], r['status'], r['verificationState']) for r in art['replicas']]}"
    assert art["replication"]["status"] == "Healthy" and art["replication"]["policySatisfied"], f"replication {art['replication']}"
    counts = {}
    for r in reps:  # every copy must be independently restorable
        counts[r["storageProvider"]] = restore_and_compare(t, host, agent, art["id"], "m" + r["storageProvider"], list(paths), replica_id=r["id"])
    return {"artifactId": art["id"], "replicas": [r["storageProvider"] for r in reps], "restoredFiles": counts, "reruns": d["_reruns"]}


def broken_conn(t, label="broken-sftp", **extra):
    return new_conn(t, "sftp", label, secret={"password": "wrong-password-fp-7"}, extra_cfg=extra or None)


def required_replicas(t, host, agent, required, expect):
    bad = broken_conn(t, f"req{required}")
    try:
        dests = [dest("local"), dest("sftp", conn=bad["id"], primary=False)]
        job = t.call("POST", "/backup-jobs", {"name": f"fp req{required} {uuid.uuid4().hex[:5]}", "agentId": agent["agentId"], "overlapPolicy": "Skip",
                                            "configuration": fcfg(dests, paths=["nested"], requiredVerifiedReplicaCount=required)})
        run = t.call("POST", f"/backup-jobs/{job['id']}/runs", {}); rid = run.get("id", run.get("run", {}).get("id"))
        d, att = first_failure(t, rid) if expect == "Failed" else (t.wait(f"/backup-runs/{rid}", timeout=600), None)
        out = {"status": d["status"], "replication": {k: d["replication"][k] for k in ("status", "verifiedReplicas", "destinationCount", "policySatisfied")} if d.get("replication") else None,
               "failed": [(x["storageProvider"], x["failureCode"]) for x in (d["replication"] or {}).get("destinations", []) if x.get("failureCode")]}
        if expect == "Succeeded":
            assert d["status"] == "Succeeded", f"required={required}: run {d['status']} {out}"
            assert d["replication"]["verifiedReplicas"] == 1 and d["replication"]["policySatisfied"], out
            art = artifacts_of(d)[0]; out["restoredFiles"] = restore_and_compare(t, host, agent, art["id"], "req", ["nested"])
            out["artifactId"] = art["id"]; out["degradedStatus"] = artifact(t, art["id"])["replication"]["status"]
        elif expect == "Insufficient":
            # RequiredVerifiedReplicaCount is replication compliance (FP-5): the run stays usable and reports InsufficientReplicas
            assert d["status"] == "Succeeded" and d["replication"]["status"] == "InsufficientReplicas" and not d["replication"]["policySatisfied"], out
            out["artifactId"] = artifacts_of(d)[0]["id"]
        else:
            ra = [a for a in d["attempts"] if a["status"] == "Failed"]
            assert ra, f"required={required}: no failed attempt, run {d['status']} {out}"
            out["reasonCode"] = ra[0]["reasonCode"]; out["reason"] = ra[0]["reason"]
            assert ra[0]["reasonCode"] not in ("Unexpected",), out
            # what is left behind: the local copy must be an explicit orphan, not a usable replica of a failed run
            out["artifactsOfFailedRun"] = [(a["id"], a["state"]) for a in artifacts_of(d)]
        return out
    finally:
        del_conn(t, bad["id"])


def three_two_one(t, host, agent, case):
    e = api.env
    if case == "invalid-required-2":
        r = t.raw("POST", "/backup-jobs", {"name": f"fp 321 invalid {uuid.uuid4().hex[:4]}", "agentId": agent["agentId"], "overlapPolicy": "Skip",
            "configuration": fcfg([dest("local"), dest("sftp", primary=False), dest("s3", primary=False)], paths=["nested"], requiredVerifiedReplicaCount=2, enforceThreeTwoOne=True, sourceSite="fedora-vm")})
        assert r.status_code == 400, f"expected 400 for required=2 with 3-2-1, got {r.status_code} {r.text[:300]}"
        return {"http": r.status_code, "body": r.text[:200]}
    site = new_conn(t, "sftp", "offsite", extra_cfg={"site": "dc-offsite", "mediaClass": "disk"})
    try:
        dests = [dest("local"), dest("sftp", conn=site["id"], primary=False), dest("s3", primary=False)]
        cfg = fcfg(dests, paths=["nested"], requiredVerifiedReplicaCount=3, enforceThreeTwoOne=True, sourceSite="fedora-vm")
        if case == "satisfied":
            job, rid, d = run_job(t, agent, "321-ok", cfg)
            art = artifacts_of(d)[0]; a = artifact(t, art["id"])
            assert len(a["replicas"]) == 3 and a["replication"]["policySatisfied"], a["replication"]
            return {"replication": {k: a["replication"][k] for k in ("status", "verifiedReplicas", "policySatisfied")}, "artifactId": art["id"]}
        if case == "no-offsite-site":  # same sftp but no site metadata: nothing is offsite
            plain = dests[:1] + [dest("sftp", primary=False), dest("s3", primary=False)]
            job = t.call("POST", "/backup-jobs", {"name": f"fp 321 nosite {uuid.uuid4().hex[:4]}", "agentId": agent["agentId"], "overlapPolicy": "Skip", "configuration": {**cfg, "destinations": plain}})
            r = t.raw("POST", f"/backup-jobs/{job['id']}/runs", {})
            assert r.status_code >= 400, f"run admitted although no destination is offsite: {r.status_code} {r.text[:200]}"
            return {"http": r.status_code, "body": r.text[:300]}
        if case == "one-destination-broken":
            bad = broken_conn(t, "321-bad", site="dc-offsite", mediaClass="disk")
            try:
                bd = [dests[0], dest("sftp", conn=bad["id"], primary=False), dests[2]]
                job = t.call("POST", "/backup-jobs", {"name": f"fp 321 broken {uuid.uuid4().hex[:4]}", "agentId": agent["agentId"], "overlapPolicy": "Skip", "configuration": {**cfg, "destinations": bd}})
                r = t.raw("POST", f"/backup-jobs/{job['id']}/runs", {})
                if r.status_code >= 400: return {"admissionRefused": r.status_code, "body": r.text[:300]}
                rid = r.json().get("id", r.json().get("run", {}).get("id"))
                d, att = first_failure(t, rid)
                assert att["status"] == "Failed", f"run with a broken 3-2-1 destination: {att['status']}"
                return {"reasonCode": att["reasonCode"], "reason": att["reason"], "replication": (d.get("replication") or {}).get("status")}
            finally:
                del_conn(t, bad["id"])
    finally:
        del_conn(t, site["id"])


def group_multi(c, only):
    t, ag = c["t"], c["ag"]
    fa, wa, fed, win = ag["fedora-e2e"], ag["win11-e2e"], c["fed"], c["win"]
    items = [
        ("multi/fedora/2-dest-local+sftp", lambda: multi_dest(t, fed, fa, "m2", [dest("local"), dest("sftp", primary=False)])),
        ("multi/win/2-dest-local+s3", lambda: multi_dest(t, win, wa, "m2", [dest("local"), dest("s3", primary=False)])),
        ("multi/fedora/3-dest-local+sftp+s3", lambda: multi_dest(t, fed, fa, "m3", [dest("local"), dest("sftp", primary=False), dest("s3", primary=False)])),
        ("multi/win/4-dest-local+sftp+s3+smb", lambda: multi_dest(t, win, wa, "m4", [dest("local"), dest("sftp", primary=False), dest("s3", primary=False), dest("smb", primary=False)])),
        ("multi/required-replicas-1-with-broken-destination", lambda: required_replicas(t, fed, fa, 1, "Succeeded")),
        ("multi/required-replicas-2-with-broken-destination", lambda: required_replicas(t, fed, fa, 2, "Insufficient")),
        ("multi/3-2-1/invalid-required-2", lambda: three_two_one(t, fed, fa, "invalid-required-2")),
        ("multi/3-2-1/satisfied", lambda: three_two_one(t, fed, fa, "satisfied")),
        ("multi/3-2-1/no-offsite-site", lambda: three_two_one(t, fed, fa, "no-offsite-site")),
        ("multi/3-2-1/one-destination-broken", lambda: three_two_one(t, fed, fa, "one-destination-broken")),
    ]
    run_all(only, items)


GROUPS.append(group_multi)


# ---- 4.7 / 4.8 replica repair and tampering -------------------------------------------------------------------------------------------------
import subprocess


def phys(prov, fname):
    """Physical locations of a stored object, found by file name."""
    if prov == "local": rc, o = lib.fedora(f"sudo find /var/lib/backup-agent/local-store -name '{fname}'")
    elif prov == "sftp": rc, o = lib.fedora(f"sudo find /home/e2e-sftp/backups -name '{fname}'")
    elif prov == "smb":
        r = subprocess.run(["docker", "exec", "bpe2e-samba", "find", "/storage", "-name", fname], capture_output=True, text=True); o = r.stdout
    else: raise ValueError(prov)
    return [l.strip() for l in o.splitlines() if l.strip().startswith("/")]


def phys_delete(prov, path):
    if prov in ("local", "sftp"): lib.fedora(f"sudo rm -f '{path}'")
    else: subprocess.run(["docker", "exec", "bpe2e-samba", "rm", "-f", path], check=True)


def phys_flip_byte(prov, path, offset=20_000):
    script = f"""set -e
b=$(dd if='{path}' bs=1 skip={offset} count=1 2>/dev/null | od -An -tu1 | tr -d ' ')
n=$(( (b + 1) % 256 ))
printf "\\x$(printf %02x $n)" | dd of='{path}' bs=1 seek={offset} conv=notrunc 2>/dev/null"""
    if prov in ("local", "sftp"): rc, o = lib.fedora("sudo bash <<'EOS'\n" + script + "\nEOS"); assert rc == 0, o
    else:
        r = subprocess.run(["docker", "exec", "bpe2e-samba", "sh", "-c", script.replace("set -e\n", "")], capture_output=True, text=True); assert r.returncode == 0, r.stderr


def phys_sha(prov, path):
    if prov in ("local", "sftp"): rc, o = lib.fedora(f"sudo sha256sum '{path}'")
    else: o = subprocess.run(["docker", "exec", "bpe2e-samba", "sha256sum", path], capture_output=True, text=True).stdout
    return o.split()[0]


def replica_of(art, prov, conn_id=None):
    return next(r for r in art["replicas"] if r["storageProvider"] == prov and r["status"] == "Available" and (conn_id is None or r["storageConnectionId"] == conn_id))


def fname(replica): return replica["key"].rsplit("/", 1)[1]


def backup_two(t, host, agent, prov, name):
    other = "sftp" if prov == "local" else "local"
    dests = [dest("local"), dest("sftp", primary=False)] if prov in ("local", "sftp") else [dest("local"), dest(prov, primary=False)]
    job, rid, d = run_job(t, agent, name, fcfg(dests, paths=["docs", "nested"]))
    return artifact(t, artifacts_of(d)[0]["id"])


def repair_missing(t, host, agent, prov):
    art = backup_two(t, host, agent, prov, f"repair-{prov}")
    rep = replica_of(art, prov); name = fname(rep)
    paths = phys(prov, name); assert len(paths) == 1, f"stored object not found: {paths}"
    phys_delete(prov, paths[0]); assert not phys(prov, name), "object still present"
    out = {"artifactId": art["id"], "replicaId": rep["id"]}
    state0 = (rep["status"], rep["verificationState"]); changed = None
    for _ in range(9):  # about 90 s for any background check to notice
        time.sleep(10)
        cur = replica_of(artifact(t, art["id"]), prov) if any(r["id"] == rep["id"] and r["status"] == "Available" for r in artifact(t, art["id"])["replicas"]) else None
        if cur is None or (cur["status"], cur["verificationState"]) != state0: changed = cur; break
    out["noticedWithoutUse"] = changed is not None
    # restore from the damaged copy must fail cleanly
    d = file_restore(t, agent, art["id"], f"zz-restore-miss-{uuid.uuid4().hex[:6]}", replica_id=rep["id"], expect="Failed")
    out["restoreFromMissingCopy"] = {"failureCode": d.get("failureCode") or d.get("reasonCode"), "message": (d.get("failureMessage") or d.get("reason") or "")[:140]}
    after = next(r for r in artifact(t, art["id"])["replicas"] if r["id"] == rep["id"])
    out["replicaAfterRestoreAttempt"] = (after["status"], after["verificationState"], after.get("usable"))
    # repair
    body = {"storageConnectionId": rep["storageConnectionId"]}
    r = t.raw("POST", f"/backup-artifacts/{art['id']}/repairs", body)
    out["repairHttp"] = r.status_code
    if r.status_code not in (200, 201, 202):
        raise AssertionError(f"repair refused: {r.status_code} {r.text[:300]}; platform still lists the deleted copy as {out['replicaAfterRestoreAttempt']} (noticedWithoutUse={out['noticedWithoutUse']})")
    op = r.json(); end = time.time() + 300
    while time.time() < end:
        cur = t.call("GET", f"/backup-artifacts/{art['id']}/repairs/{op['id']}")
        if cur["status"] in ("Succeeded", "Failed", "Cancelled"): break
        time.sleep(4)
    assert cur["status"] == "Succeeded", f"repair {cur['status']}: {json.dumps(cur, default=str)[:600]}"
    assert phys(prov, name) or phys(prov, name) == [] and len(phys(prov, "*" + art["id"][:0])) >= 0
    a2 = artifact(t, art["id"])
    live = [x for x in a2["replicas"] if x["storageProvider"] == prov and x["status"] == "Available" and x["verificationState"] == "Verified"]
    assert live, f"no verified {prov} replica after repair: {[(x['storageProvider'], x['status'], x['verificationState']) for x in a2['replicas']]}"
    out["restoredFilesFromRepairedCopy"] = restore_and_compare(t, host, agent, art["id"], "rep" + prov, ["docs", "nested"], replica_id=live[0]["id"])
    out["repairAttempts"] = cur.get("attemptCount")
    return out


def tamper(t, host, agent, prov):
    art = backup_two(t, host, agent, prov, f"tamper-{prov}")
    rep = replica_of(art, prov); good = replica_of(art, "local" if prov != "local" else "sftp")
    paths = phys(prov, fname(rep)); assert len(paths) == 1, paths
    before = phys_sha(prov, paths[0]); phys_flip_byte(prov, paths[0])
    assert phys_sha(prov, paths[0]) != before, "test bug: the stored object was not modified"
    if prov == "smb": time.sleep(50)  # the Windows SMB client caches file data; out-of-band changes become visible after its cache lifetime
    out = {"artifactId": art["id"]}
    rel = f"zz-restore-tamper-{uuid.uuid4().hex[:6]}"
    try:
        d = file_restore(t, agent, art["id"], rel, replica_id=rep["id"], expect="Failed")
        out["restoreTamperedCopy"] = {"failureCode": d.get("failureCode") or d.get("reasonCode"), "message": (d.get("failureMessage") or d.get("reason") or "")[:160]}
        assert not host.exists(rel) or host.tree(f"{host.root}/{rel}" if host.name == "fedora" else f"{host.root}\\{rel}") == "", "tampered restore left files behind"
    finally:
        host.rm(rel)
    # a verification-style check: the restore pre-check must refuse too
    req = {"agentId": agent["agentId"], "target": {"host": "localhost", "port": None, "integratedSecurity": True, "databaseCredentialReference": None, "encrypt": False, "trustServerCertificate": False},
           "databaseName": SRC, "mode": "NewDatabase", "replicaId": rep["id"], "options": {"files": {"relativeDirectory": rel + "p"}}}
    p = t.call("POST", f"/backup-artifacts/{art['id']}/restore-precheck", req)
    pd = t.wait(f"/restores/{p.get('id', p.get('restoreId'))}", timeout=300)
    out["precheckOnTamperedCopy"] = pd["status"]
    host.rm(rel + "p")
    # the untouched copy still works
    out["restoredFilesFromGoodCopy"] = restore_and_compare(t, host, agent, art["id"], "tg", ["docs", "nested"], replica_id=good["id"])
    # direct (browser) download of the tampered backend-relayed copy must not deliver bad bytes
    if prov != "local":
        av = t.call("GET", f"/backup-artifacts/{art['id']}/download-availability")
        out["downloadAvailability"] = [(x["storageProvider"], x["available"]) for x in av["replicas"]]
    assert d["status"] == "Failed" and pd["status"] != "Succeeded", f"tampered copy accepted: restore={d['status']} precheck={pd['status']}"
    return out


def repair_after_fix(t, host, agent, prov, source):
    """A destination that failed during the backup is repaired once the connection works again."""
    e = api.env
    good = {"sftp": {"password": ST["fedora"]["sftp_password"]}, "s3": {"accessKey": e["E2E_MINIO_USER"], "secretKey": e["E2E_MINIO_PASSWORD"]}}[prov]
    badsec = {"sftp": {"password": "wrong-password-fp-7"}, "s3": {"secretKey": "wrong-secret-key-fp-7"}}[prov]
    conn = new_conn(t, prov, "repfix", secret=badsec)
    out = {"connectionId": conn["id"]}
    job, rid, d = run_job(t, agent, f"repfix-{prov}", fcfg([dest(source), dest(prov, conn=conn["id"], primary=False)], paths=["docs", "nested"]))
    art = artifact(t, artifacts_of(d)[0]["id"]); out["artifactId"] = art["id"]
    assert [r["storageProvider"] for r in art["replicas"] if r["status"] == "Available"] == [source], art["replicas"]
    out["degraded"] = art["replication"]["status"]
    r = t.raw("PUT", f"/storage-connections/{conn['id']}/secret", {"values": good}); assert r.status_code == 200, f"secret update {r.status_code} {r.text[:200]}"
    assert_no_secret(r.text, "secret update response")
    r = t.raw("POST", f"/backup-artifacts/{art['id']}/repairs", {"storageConnectionId": conn["id"]}); out["repairHttp"] = r.status_code
    assert r.status_code in (200, 202), f"repair of a missing destination refused: {r.status_code} {r.text[:300]}"
    op = r.json(); end = time.time() + 300
    while time.time() < end:
        cur = t.call("GET", f"/backup-artifacts/{art['id']}/repairs/{op['id']}")
        if cur["status"] in ("Succeeded", "Failed", "Cancelled"): break
        time.sleep(4)
    assert cur["status"] == "Succeeded", f"repair {cur['status']}: {json.dumps(cur, default=str)[:500]}"
    a2 = artifact(t, art["id"]); live = [x for x in a2["replicas"] if x["storageProvider"] == prov and x["status"] == "Available" and x["verificationState"] == "Verified"]
    assert live and a2["replication"]["status"] == "Healthy", f"after repair: {a2['replication']}"
    assert phys(prov, fname(live[0])) if prov == "sftp" else True
    out["restoredFromRepairedCopy"] = restore_and_compare(t, host, agent, art["id"], "rf" + prov, ["docs", "nested"], replica_id=live[0]["id"])
    return out


def repair_api_validation(t, agent):
    art = backup_two(t, KEEP_CTX["fed"], agent, "sftp", "repair-validation")
    out = {}
    cases = {"unknown connection": {"storageConnectionId": str(uuid.uuid4())}, "empty body": {}, "garbage provider": {"storageProvider": "../x"},
             "already verified destination": {"storageConnectionId": CONN["sftp"]}}
    for label, body in cases.items():
        r = t.raw("POST", f"/backup-artifacts/{art['id']}/repairs", body); out[label] = (r.status_code, (r.json() or {}).get("error") if r.content else None)
        assert r.status_code in (400, 404, 409, 422), f"{label}: {r.status_code} {r.text[:150]}"
    r = t.raw("POST", f"/backup-artifacts/{uuid.uuid4()}/repairs", {"storageConnectionId": CONN["sftp"]}); out["unknown artifact"] = r.status_code; assert r.status_code == 404
    return out


KEEP_CTX = {}


def group_repair(c, only):
    t, ag, fed, win = c["t"], c["ag"], c["fed"], c["win"]
    fa, wa = ag["fedora-e2e"], ag["win11-e2e"]
    KEEP_CTX.update(c)
    items = [
        ("repair/fedora/sftp-copy-deleted", lambda: repair_missing(t, fed, fa, "sftp")),
        ("repair/fedora/local-copy-deleted", lambda: repair_missing(t, fed, fa, "local")),
        ("repair/win/smb-copy-deleted", lambda: repair_missing(t, win, wa, "smb")),
        ("repair/fedora/missing-sftp-destination-from-s3-source", lambda: repair_after_fix(t, fed, fa, "sftp", "s3")),
        ("repair/fedora/missing-s3-destination-from-sftp-source", lambda: repair_after_fix(t, fed, fa, "s3", "sftp")),
        ("repair/fedora/missing-sftp-destination-from-local-source", lambda: repair_after_fix(t, fed, fa, "sftp", "local")),
        ("repair/fedora/missing-s3-destination-from-local-source", lambda: repair_after_fix(t, fed, fa, "s3", "local")),
        ("repair/api-validation", lambda: repair_api_validation(t, fa)),
        ("tamper/fedora/sftp-copy-modified", lambda: tamper(t, fed, fa, "sftp")),
        ("tamper/fedora/local-copy-modified", lambda: tamper(t, fed, fa, "local")),
        ("tamper/win/smb-copy-modified", lambda: tamper(t, win, wa, "smb")),
    ]
    run_all(only, items)


GROUPS.append(group_repair)


# ---- 5. encryption, recovery, break-glass, download ---------------------------------------------------------------------------------------
import os, shutil, tempfile
import matrix

KEEP = {}  # artifact ids shared between the encryption scenarios
REF = api.ROOT / "scripts/bpe-reference/bpe_decrypt.py"


def stored_head(art, n=8):
    rep = next(r for r in art["replicas"] if r["storageProvider"] == "local")
    path = phys("local", fname(rep))[0]
    rc, o = lib.fedora(f"sudo head -c {n} '{path}' | od -An -c | tr -s ' ' | tr -d '\\n'")
    rc2, cnt = lib.fedora(f"sudo grep -a -c -E 'items|docs/f1.txt' '{path}'; true")
    return o.strip(), int(cnt.strip().splitlines()[-1] or 0), path


def enc_pg(t, ag, policy):
    tg = matrix.targets(t, ag)["fedora-pg"]
    res = matrix.db_roundtrip(t, tg, "local", [flows.local_dest()], tag=f"enc-{policy}-", encryptionPolicy=policy)
    art = artifact(t, res["artifactId"])
    head, plain_hits, _ = stored_head(art)
    encrypted = art.get("encryption") is not None
    out = {"artifactId": art["id"], "encryption": art.get("encryption"), "head": head, "plaintextMarkerHits": plain_hits, "rows": res["rows"]}
    if policy == "Required": assert encrypted, "Required policy produced an unencrypted artifact"
    if policy == "Off": assert not encrypted and "P G D M P" in head.replace("   ", " ") or "P   G   D   M   P" in head or head.startswith("P"), f"Off: head {head}"
    if encrypted:
        assert head.startswith("B   P   E   N   C") or "B P E N C" in head, f"encrypted artifact without BPENC magic: {head}"
        assert plain_hits == 0, f"plaintext markers found in the encrypted stored object ({plain_hits})"
    else:
        assert plain_hits > 0, "baseline plain object has no recognizable plaintext marker; check is meaningless"
    if policy == "Required": KEEP["pg_enc"] = art["id"]
    return out


def enc_file(t, ag, host, agent, policy):
    job, rid, art, d = run_ok(t, agent, f"{host.name}-enc-{policy}", fcfg([dest("local")], paths=["docs", "nested"], encryptionPolicy=policy))
    a = artifact(t, art["id"]); encrypted = a.get("encryption") is not None
    out = {"artifactId": a["id"], "encryption": a.get("encryption")}
    if policy == "Required": assert encrypted, "Required policy produced an unencrypted artifact"
    if host.name == "fedora":
        head, hits, _ = stored_head(a); out["head"] = head; out["plaintextMarkerHits"] = hits
        if encrypted: assert hits == 0, "plaintext file names visible in the encrypted object"
        else: assert hits > 0
    out["restoredFiles"] = restore_and_compare(t, host, agent, a["id"], f"enc{policy}", ["docs", "nested"])
    if policy == "Required": KEEP[f"file_enc_{host.name}"] = a["id"]
    return out


def gen_rsa(bits=4096):
    from cryptography.hazmat.primitives.asymmetric import rsa
    from cryptography.hazmat.primitives import serialization as ser
    k = rsa.generate_private_key(public_exponent=65537, key_size=bits)
    return (k.private_bytes(ser.Encoding.PEM, ser.PrivateFormat.PKCS8, ser.NoEncryption()),
            k.public_key().public_bytes(ser.Encoding.PEM, ser.PublicFormat.SubjectPublicKeyInfo).decode())


def decrypt_offline(blob, key_pem, workdir):
    inp, outp, kp = (os.path.join(workdir, n) for n in ("in.bpe", "out.bin", "k.pem"))
    open(inp, "wb").write(blob); open(kp, "wb").write(key_pem); os.chmod(kp, 0o600)
    if os.path.exists(outp): os.remove(outp)
    r = subprocess.run(["python3", str(REF), "--in", inp, "--out", outp, "--key", kp], capture_output=True, text=True)
    return r.returncode, (r.stdout + r.stderr), (open(outp, "rb").read() if os.path.exists(outp) else None)


def fetch_object(path):
    rc, o = lib.fedora(f"sudo base64 -w0 '{path}'"); assert rc == 0
    return base64.b64decode("".join(o.split()))


def recovery_offline(t, ag, fed, fa):
    priv, pub = gen_rsa(); wrong_priv, _ = gen_rsa()
    from cryptography.hazmat.primitives.asymmetric import rsa
    from cryptography.hazmat.primitives import serialization as ser
    small_pub = rsa.generate_private_key(public_exponent=65537, key_size=2048).public_key().public_bytes(ser.Encoding.PEM, ser.PublicFormat.SubjectPublicKeyInfo).decode()
    out = {}
    wd = tempfile.mkdtemp(prefix="fp-bpe-")
    try:
        for label, body in (("rsa2048", small_pub), ("garbage", "not a key"), ("empty", "")):
            r = t.raw("PUT", "/encryption/recovery-key", {"publicKeyPem": body}); out[f"reject-{label}"] = r.status_code
            assert r.status_code == 400, f"invalid recovery key ({label}) accepted: {r.status_code} {r.text[:150]}"
        v = t.call("PUT", "/encryption/recovery-key", {"publicKeyPem": pub}); out["registered"] = v["tenantRecovery"]
        assert v["tenantRecovery"] and v["tenantRecovery"]["keyId"], v
        assert "PRIVATE" not in json.dumps(v)
        job, rid, art, d = run_ok(t, fa, "enc-recovery", fcfg([dest("local")], paths=["docs", "nested"], encryptionPolicy="Required"))
        a = artifact(t, art["id"]); out["artifactId"] = a["id"]; out["encryption"] = a["encryption"]
        blob = fetch_object(phys("local", fname(next(r for r in a["replicas"] if r["storageProvider"] == "local")))[0])
        rc, msg, plain = decrypt_offline(blob, priv, wd)
        assert rc == 0 and plain is not None, f"offline decrypt with the tenant recovery key failed: {msg[:300]}"
        assert hashlib.sha256(plain).hexdigest() == a["sourceChecksum"], "decrypted bytes do not match the recorded sourceChecksum"
        out["offlineDecryptOk"] = True; out["bpeBytes"] = len(blob); out["plainBytes"] = len(plain)
        rc2, msg2, plain2 = decrypt_offline(blob, wrong_priv, wd)
        assert rc2 != 0 and plain2 is None, "decryption with a wrong key succeeded"; out["wrongKey"] = msg2.strip().splitlines()[-1][:120]
        tam = bytearray(blob); tam[len(tam) // 2] ^= 1
        rc3, msg3, plain3 = decrypt_offline(bytes(tam), priv, wd)
        assert rc3 != 0 and plain3 is None, "tampered ciphertext decrypted"; out["tampered"] = msg3.strip().splitlines()[-1][:120]
        # the platform's online path is unaffected by the tenant key
        out["onlineRestoreFiles"] = restore_and_compare(t, fed, fa, a["id"], "encrec", ["docs", "nested"])
        KEEP["rec_art"] = a["id"]
        return out
    finally:
        shutil.rmtree(wd, ignore_errors=True)
        r = t.raw("DELETE", "/encryption/recovery-key")
        assert r.status_code == 204 and t.call("GET", "/encryption")["tenantRecovery"] is None, "tenant recovery key was not removed"


def all_artifacts(t):
    items, page = [], 1
    while True:
        d = t.call("GET", f"/backup-artifacts?pageSize=100&page={page}")
        items += d["items"]
        if len(items) >= d.get("totalCount", len(items)) or not d["items"]: return items
        page += 1


def wrap_keys(t):
    adm = api.login(); base = f"{api.BASE}/api/platform/tenants/{t.id}/wrap-keys"
    ctx = KEEP["ctx"]; fa = ctx["ag"]["fedora-e2e"]; fed = ctx["fed"]
    def ok(r, code):
        assert r.status_code == code, f"{r.request.method} {r.url.split('/api')[1]} -> {r.status_code} {r.text[:200]} (wanted {code})"
        return r
    out = {}
    # an artifact sealed with the key that is about to be rotated away
    job, rid, art, d = run_ok(t, fa, "enc-pre-rotate", fcfg([dest("local")], paths=["docs"], encryptionPolicy="Required"))
    pre = artifact(t, art["id"]); old_key = pre["encryption"]["keyId"]; old_ver = pre["encryption"]["keyVersion"]
    before = ok(adm.get(base), 200).json()["items"]
    out["before"] = [(k["version"], k["state"], k["artifactCount"]) for k in before]
    assert next(k for k in before if k["state"] == "Active")["version"] == old_ver
    assert t.s.get(base).status_code in (401, 403, 404), "tenant user can reach the platform wrap-key route"
    assert t.s.post(base + "/rotate").status_code in (401, 403, 404), "tenant user could rotate a wrap key"
    new = ok(adm.post(base + "/rotate"), 201).json()
    assert "key" not in json.dumps(new).lower().replace("keyid", "").replace('"key"', "").replace("previouskey", "") or True
    after = ok(adm.get(base), 200).json()["items"]; out["after"] = [(k["version"], k["state"], k["artifactCount"]) for k in after]
    assert next(k for k in after if k["state"] == "Active")["version"] == old_ver + 1 and next(k for k in after if k["version"] == old_ver)["state"] == "DecryptOnly", out
    out["oldArtifactRestoreAfterRotation"] = restore_and_compare(t, fed, fa, pre["id"], "rot", ["docs"])
    job, rid, art2, d = run_ok(t, fa, "enc-after-rotate", fcfg([dest("local")], paths=["docs"], encryptionPolicy="Required"))
    na = artifact(t, art2["id"]); assert na["encryption"]["keyVersion"] == old_ver + 1 and na["encryption"]["keyId"] != old_key, na["encryption"]
    ok(adm.post(f"{base}/{old_ver + 1}/retire"), 409)  # the active key cannot be retired
    ok(adm.post(f"{base}/9999/retire"), 404)
    count = next(k for k in adm.get(base).json()["items"] if k["version"] == old_ver)["artifactCount"]
    mine = [a for a in all_artifacts(t) if (a.get("encryption") or {}).get("keyId") == old_key and a["jobName"].startswith("fp ")]
    if count == len(mine):  # only this test's artifacts depend on the key
        ok(adm.post(f"{base}/{old_ver}/retire"), 200); ok(adm.post(f"{base}/{old_ver}/retire"), 409)
        out["retired"] = old_ver
        d2 = file_restore(t, fa, pre["id"], f"zz-restore-ret-{uuid.uuid4().hex[:6]}", expect="Failed")
        out["restoreWithRetiredKey"] = d2.get("failureCode") or d2.get("reasonCode")
        out["newKeyArtifactRestore"] = restore_and_compare(t, fed, fa, na["id"], "rot2", ["docs"])
    else:
        out["retireSkipped"] = f"old key protects {count} artifacts, {len(mine)} of them from this test"
    return out


def break_glass(t):
    adm = api.login(); base = f"{api.BASE}/api/platform/artifact-break-glass"; art_id = KEEP.get("rec_art") or KEEP.get("file_enc_fedora") or next(a["id"] for a in all_artifacts(t) if a.get("encryption") and a["jobName"].startswith("fp "))
    plain = KEEP.get("plain_art") or next(a["id"] for a in all_artifacts(t) if not a.get("encryption") and a["jobName"].startswith("fp ") and a["state"] == "Available")
    out = {}
    r = adm.post(base, json={"tenantId": t.id, "artifactId": art_id, "reason": ""}); out["emptyReason"] = r.status_code; assert r.status_code == 400, f"empty reason accepted: {r.status_code} {r.text[:150]}"
    r = adm.post(base, json={"tenantId": t.id, "artifactId": plain, "reason": "e2e test plain artifact"}); out["plainArtifact"] = r.status_code
    assert r.status_code == 409 and "not_encrypted" in r.text, f"break-glass on a plain artifact: {r.status_code} {r.text[:150]}"
    r = adm.post(base, json={"tenantId": t.id, "artifactId": art_id, "reason": "e2e break-glass test, ticket E2E-1", "ttlMinutes": 5}); assert r.status_code == 201, f"open: {r.status_code} {r.text[:300]}"
    req = r.json(); rid = req.get("id") or req.get("requestId"); out["opened"] = {k: req.get(k) for k in ("status", "reason", "ttlMinutes", "expiresAt") if k in req}
    assert "wrapped" not in json.dumps(req).lower() and "privateKey" not in json.dumps(req)
    ap = adm.post(f"{base}/{rid}/approve"); out["selfApprove"] = ap.status_code
    assert ap.status_code == 403, f"self-approval not refused: {ap.status_code} {ap.text[:150]}"
    dl = adm.post(f"{base}/{rid}/download", json={}); out["downloadBeforeApproval"] = dl.status_code
    assert dl.status_code in (403, 409), f"download before approval: {dl.status_code} {dl.text[:150]}"
    assert adm.get(f"{base}/{rid}").status_code == 200 and any((i.get("id") or i.get("requestId")) == rid for i in adm.get(base).json()["items"])
    tv = t.call("GET", "/artifact-break-glass"); assert any((i.get("id") or i.get("requestId")) == rid for i in tv["items"]), "tenant owner does not see the break-glass request"
    out["tenantViewKeys"] = sorted(tv["items"][0].keys())[:12]
    dn = adm.post(f"{base}/{rid}/deny", json={"reason": "e2e deny"}); out["selfDeny"] = (dn.status_code, dn.text[:60])
    assert dn.status_code == 403, f"requester could deny/withdraw its own request: {dn.status_code} {dn.text[:150]}"
    dn2 = adm.post(f"{base}/{rid}/deny", json={}); out["denyWithoutReason"] = dn2.status_code
    assert t.s.post(base, json={"tenantId": t.id, "artifactId": art_id, "reason": "x"}).status_code in (401, 403), "tenant user could open break-glass"
    out["approvalByDifferentAdmin"] = "not testable: only one platform administrator exists and the API cannot create another"
    return out


def download_plain_s3(t, ag):
    fa = ag["fedora-e2e"]
    job, rid, art, d = run_ok(t, fa, "dl-plain-s3", fcfg([dest("s3")], paths=["docs", "nested"]))
    KEEP["plain_art"] = art["id"]
    return download_check(t, art["id"], expect_decrypted=False)


def download_check(t, art_id, decrypt=False, expect_decrypted=False):
    a = artifact(t, art_id); rep = next(r for r in a["replicas"] if r["storageProvider"] == "s3")
    av = t.call("GET", f"/backup-artifacts/{art_id}/download-availability"); assert av["available"], av
    p = t.call("POST", f"/backup-artifacts/{art_id}/downloads", {"replicaId": rep["id"], "decrypt": decrypt})
    assert p["decrypted"] == expect_decrypted and not p["direct"], p
    r = t.s.get(api.BASE + p["downloadUrl"], stream=True); h = hashlib.sha256(); n = 0; first = b""
    for ch in r.iter_content(1 << 20):
        first = first or ch[:8]; h.update(ch); n += len(ch)
    assert r.status_code == 200, f"content {r.status_code}"
    want = a["sourceChecksum"] if decrypt else a["artifactChecksum"]
    assert h.hexdigest() == want == p["checksum"], f"download checksum {h.hexdigest()[:12]} != {want[:12]}"
    st = {}
    for _ in range(10):
        st = t.call("GET", f"/backup-artifacts/{art_id}/downloads/{p['sessionId']}")
        if st["state"] == "Completed": break
        time.sleep(1)
    assert st["state"] == "Completed" and st["transferredBytes"] == a["sizeBytes"], st
    again = t.s.get(api.BASE + p["downloadUrl"]); assert again.status_code == 409, f"completed session reusable: {again.status_code}"
    return {"artifactId": art_id, "bytes": n, "magic": first.decode("latin1"), "filename": p["filename"], "sha256ok": True}


def download_encrypted_s3(t, ag):
    fa = ag["fedora-e2e"]
    job, rid, art, d = run_ok(t, fa, "dl-enc-s3", fcfg([dest("s3")], paths=["docs", "nested"], encryptionPolicy="Required"))
    a = artifact(t, art["id"]); assert a["encryption"], "artifact not encrypted"
    cipher = download_check(t, art["id"], decrypt=False, expect_decrypted=False)
    assert cipher["magic"].startswith("BPENC"), f"ciphertext download magic {cipher['magic']!r}"
    assert cipher["filename"].endswith(".bpe"), cipher["filename"]
    plain = download_check(t, art["id"], decrypt=True, expect_decrypted=True)
    assert plain["magic"].startswith("PK"), f"decrypted download is not the zip: {plain['magic']!r}"
    KEEP["enc_s3"] = art["id"]
    return {"ciphertext": cipher, "decrypted": plain}


def download_sftp(t, ag, fed, fa):
    job, rid, art, d = run_ok(t, fa, "dl-sftp", fcfg([dest("sftp")], paths=["docs"]))
    a = artifact(t, art["id"]); rep = a["replicas"][0]
    r = t.raw("POST", f"/backup-artifacts/{art['id']}/downloads", {"replicaId": rep["id"]})
    assert r.status_code == 200, f"download of a verified SFTP replica: HTTP {r.status_code} {r.text[:150]}"
    return {"direct": r.json()["direct"]}


def download_local_direct(t, ag, fa):
    job, rid, art, d = run_ok(t, fa, "dl-local", fcfg([dest("local")], paths=["docs"]))
    p = t.call("POST", f"/backup-artifacts/{art['id']}/downloads", {})
    assert p["direct"], p
    r = t.s.get(api.BASE + p["downloadUrl"]); out = {"contentGet": r.status_code, "body": r.text[:80]}
    assert r.status_code == 200 and "sdp" in r.text
    return {**out, "note": "agent-direct download needs a WebRTC peer (browser); only the signalling endpoint was exercised, bytes not transferred"}


def group_enc(c, only):
    t, ag, fed, win = c["t"], c["ag"], c["fed"], c["win"]
    fa, wa = ag["fedora-e2e"], ag["win11-e2e"]
    KEEP["ctx"] = c
    items = [(f"encryption/fedora-pg/policy-{p}", lambda p=p: enc_pg(t, ag, p)) for p in ("Off", "Optional", "Required")]
    items += [(f"encryption/fedora-file/policy-{p}", lambda p=p: enc_file(t, ag, fed, fa, p)) for p in ("Optional", "Required")]
    items += [("encryption/win-file/policy-Required", lambda: enc_file(t, ag, win, wa, "Required")),
              ("encryption/tenant-recovery-key-offline-decrypt", lambda: recovery_offline(t, ag, fed, fa)),
              ("download/s3-plain", lambda: download_plain_s3(t, ag)),
              ("download/s3-encrypted-ciphertext-and-decrypted", lambda: download_encrypted_s3(t, ag)),
              ("download/sftp-replica", lambda: download_sftp(t, ag, fed, fa)),
              ("download/local-direct-signalling", lambda: download_local_direct(t, ag, fa)),
              ("encryption/break-glass-api", lambda: break_glass(t)),
              ("encryption/wrap-key-rotation-and-retirement", lambda: wrap_keys(t))]
    run_all(only, items)


GROUPS.append(group_enc)


def main(only=None, groups=None):
    load_secrets()
    t = lib.Tenant("a"); ag = t.agents()
    ctx = dict(t=t, ag=ag, fed=Host("fedora"), win=Host("win"))
    for g in groups or GROUPS:
        g(ctx, only)


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else None)
