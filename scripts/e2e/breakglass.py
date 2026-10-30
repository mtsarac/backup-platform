"""Break-glass approve + decrypt with the support-sessions feature OFF (its default): an encrypted S3 backup is requested by one PlatformAdmin, refused until a DIFFERENT
PlatformAdmin approves, then downloaded once as plaintext whose SHA-256 equals the artifact's source checksum; a denied request stays unusable. The API cannot promote a
user, so the second PlatformAdmin is created through the API and added to platform_admins in the lab database (stored in state.json, gitignored). One pass, no retries."""
import hashlib, json, secrets, subprocess, uuid, requests, api, lib, flows
from lib import ST, scenario

ROOT = f"{api.BASE}/api/platform/artifact-break-glass"
DB = ["docker", "exec", "bp-postgres", "psql", "-U", "postgres", "-d", "backup_platform_dev", "-tAc"]


def second_admin():
    """Reuse credentials only while their platform administrator exists in this database."""
    p = api.ROOT / "docs/test-environment/state.json"; st = json.loads(p.read_text())
    admin = st.get("admin2")
    if admin:
        uid = uuid.UUID(admin["userId"])
        out = subprocess.run(DB + [f"select exists(select 1 from platform_admins where \"UserId\" = '{uid}')"], capture_output=True, text=True)
        assert out.returncode == 0, out.stderr[:300]
    if not admin or out.stdout.strip() != "t":
        s = api.login(); pw = secrets.token_urlsafe(18) + "Aa1!"
        login = f"e2e-admin2-{uuid.uuid4().hex[:12]}"
        r = s.post(f"{api.BASE}/api/platform/users", json={"loginIdentifier": login, "displayName": "E2E second admin", "password": pw}, headers={"Idempotency-Key": uuid.uuid4().hex})
        assert r.status_code == 201, f"create second admin: {r.status_code} {r.text[:200]}"
        uid = r.json().get("userId") or r.json().get("id")
        out = subprocess.run(DB + [f"insert into platform_admins (\"UserId\") values ('{uid}') on conflict do nothing"], capture_output=True, text=True)
        assert out.returncode == 0, out.stderr[:300]
        st["admin2"] = {"login": login, "password": pw, "userId": uid}; p.write_text(json.dumps(st, indent=2))
    return api.login(st["admin2"]["login"], st["admin2"]["password"])


def post(s, path, body=None):
    return s.post(f"{ROOT}{path}", json=body if body is not None else {}, headers={"Idempotency-Key": uuid.uuid4().hex})


def encrypted_s3_artifact(t):
    fa = t.agents()["fedora-e2e"]
    dest = [{"storageProvider": "s3", "storageConnectionId": ST["connections"]["s3"], "storageExecutionLocation": "Backend", "isPrimary": True}]
    cfg = flows.file_job_config("e2efiles", dest, paths=["docs"], copyOnly=True, encryptionPolicy="Required")
    cfg["files"].update(includes=["**"], excludes=[], symlinkPolicy="Skip")
    _, _, art, _ = flows.run_backup(t, fa, "break-glass", cfg)
    full = t.call("GET", f"/backup-artifacts/{art['id']}")
    assert full["encryption"], "the artifact is not encrypted"
    return full


def stream(admin, path):
    r = admin.get(api.BASE + path, stream=True); h, first, n = hashlib.sha256(), b"", 0
    for chunk in r.iter_content(1 << 20): first = first or chunk[:4]; h.update(chunk); n += len(chunk)
    return r.status_code, h.hexdigest(), first, n


@scenario("encryption/break-glass-approve-decrypt-support-sessions-off")
def break_glass(t):
    assert requests.get(f"{api.BASE}/api/features", verify=False).json()["supportSessions"] is False, "supportSessions must be off for this scenario"
    a1, a2 = api.login(), second_admin()
    assert a1.get(f"{api.BASE}/api/platform/support-sessions").status_code == 404, "the support-sessions API answers while its flag is off"
    art = encrypted_s3_artifact(t)
    # The picker the request dialog uses: encrypted backups only, no support session involved.
    listing = a1.get(f"{ROOT}/artifacts", params={"tenantId": t.id, "pageSize": 100}); assert listing.status_code == 200, f"picker list {listing.status_code} {listing.text[:200]}"
    items = listing.json()["items"]; ids = {i["id"] for i in items}
    assert art["id"] in ids, "the new encrypted backup is missing from the break-glass picker"
    assert all(t.call("GET", f"/backup-artifacts/{i}")["encryption"] for i in ids), "the picker listed a backup that is not encrypted"
    assert set(items[0]) == {"id", "databaseName", "jobName", "createdAt"}, f"picker leaks fields: {sorted(items[0])}"
    assert requests.get(f"{ROOT}/artifacts", verify=False).status_code in (401, 403), "picker is reachable without a login"
    assert t.s.get(f"{ROOT}/artifacts", params={"tenantId": t.id}).status_code == 403, "a tenant owner reached the platform picker"
    # request -> refused while unapproved
    opened = post(a1, "", {"tenantId": t.id, "artifactId": art["id"], "reason": "e2e break-glass: keys lost drill", "ttlMinutes": 30}); assert opened.status_code == 201, f"open {opened.status_code} {opened.text[:200]}"
    rid = opened.json()["id"]
    unapproved = post(a1, f"/{rid}/download"); assert (unapproved.status_code, unapproved.json().get("error")) == (409, "request_not_approved"), f"unapproved download: {unapproved.status_code} {unapproved.text[:150]}"
    assert post(a1, f"/{rid}/approve").status_code == 403, "self-approval was not refused"
    assert t.s.post(f"{ROOT}/{rid}/approve", headers={"Idempotency-Key": uuid.uuid4().hex}).status_code == 403, "a tenant owner approved a break-glass request"
    # a DIFFERENT PlatformAdmin approves; only the requester may download
    approved = post(a2, f"/{rid}/approve"); assert approved.status_code == 200 and approved.json()["status"] == "Approved", f"approve {approved.status_code} {approved.text[:150]}"
    assert post(a2, f"/{rid}/download").status_code == 403, "the approver (not the requester) could download"
    prepared = post(a1, f"/{rid}/download"); assert prepared.status_code == 200, f"approved download {prepared.status_code} {prepared.text[:200]}"
    status, sha, magic, size = stream(a1, prepared.json()["downloadUrl"])
    assert status == 200, f"content {status}"
    assert sha == art["sourceChecksum"], f"decrypted sha256 {sha[:12]} != source checksum {art['sourceChecksum'][:12]}"
    assert sha != art["artifactChecksum"] and magic.startswith(b"PK"), f"not plaintext: {magic!r}"
    replay = post(a1, f"/{rid}/download"); assert (replay.status_code, replay.json().get("error")) == (409, "request_consumed"), f"single use: {replay.status_code} {replay.text[:150]}"
    seen = {i["id"]: i["status"] for i in t.call("GET", "/artifact-break-glass")["items"]}
    assert seen.get(rid) == "Consumed", f"the tenant sees {seen.get(rid)}"
    # a denied request never decrypts
    d = post(a1, "", {"tenantId": t.id, "artifactId": art["id"], "reason": "e2e break-glass: to be denied", "ttlMinutes": 30}).json()["id"]
    assert post(a2, f"/{d}/deny", {"reason": "e2e deny"}).status_code == 200
    refused = post(a1, f"/{d}/download"); assert refused.status_code == 409, f"denied request download: {refused.status_code} {refused.text[:150]}"
    return {"artifactId": art["id"], "decryptedBytes": size, "sha256": sha[:16], "unapproved": unapproved.json()["error"], "denied": refused.json().get("error")}


if __name__ == "__main__":
    break_glass(lib.Tenant("a"))
