"""Test-plan 9.1: notification channels, recipient link flow, failure notifications, rate limits through GreenMail (SMTP 192.168.122.1:3025,
REST http://localhost:8181). Run: python3 notify.py. Uses the tenant A fixture from authz_notify_ui.py. Restores policy/channels at the end."""
import copy, email, json, re, sys, time, uuid, urllib.parse, requests
from email import policy as emailpolicy
import api, lib, flows
from lib import ST, scenario
from authz_notify_ui import cleanup_synthetic, teardown, F, TA, TB, BASE, call, check, fixture, platform

GM = "http://localhost:8181"
HOST = api.env["PUBLIC_HOST"]
RUN = uuid.uuid4().hex[:6]
MAIL = f"e2e-notify-{RUN}@e2e.test"
POLICY = dict(enabled=True, eventTypes=["backup_failed"], minimumSeverity="warning", channelKinds=["email", "webhook"], recoveryNotifications=True,
              staleAfterHours=24, agentOfflineAfterMinutes=60, reminderIntervalHours=0, maxNotificationsPerHour=50)
S = {}


def mails(addr):
    r = requests.get(f"{GM}/api/user/{urllib.parse.quote(addr)}/messages", timeout=10)
    return [email.message_from_string(m["mimeMessage"], policy=emailpolicy.default) for m in r.json()] if r.status_code == 200 else []


def body_of(m):
    part = m.get_body(preferencelist=("plain", "html")); return part.get_content() if part else ""


def wait_mail(addr, n, timeout=120, subject=None):
    end = time.time() + timeout
    while time.time() < end:
        got = [m for m in mails(addr) if subject is None or subject in (m["Subject"] or "")]
        if len(got) >= n: return got
        time.sleep(3)
    raise AssertionError(f"expected {n} mail(s) to {addr} (subject~{subject}), got {[m['Subject'] for m in mails(addr)]}")


def delivery(t, delivery_id, timeout=90, until=("sent", "failed", "suppressed", "cancelled")):
    end = time.time() + timeout
    while time.time() < end:
        items = t.call("GET", "/notifications/deliveries?pageSize=100")["items"]
        d = next((x for x in items if x["id"] == delivery_id), None)
        if d and d["status"] in until and (d["status"] != "pending" or d["errorCode"]): return d
        time.sleep(2)
    raise AssertionError(f"delivery {delivery_id} did not reach {until}: {d}")


def link(m, kind):
    text = body_of(m)
    urls = re.findall(r"https?://[^\s<>\"']+", text)
    found = [u for u in urls if f"/recipient/{kind}" in u]
    check(found, f"no {kind} link in mail: {urls[:5]}")
    return found[0].rstrip(").,")


def token_of(url):
    return urllib.parse.unquote(re.search(r"token=([^&\s]+)", url).group(1))


def chan(t, cid): return t.call("GET", f"/notifications/channels/{cid}")


@scenario("notify/9.1 platform email transport + tenant policy (GreenMail)")
def setup():
    t = fixture()["t"]; P = platform()
    r = call(t.s, "GET", "/api/platform/notification-transports"); check(r.status_code == 403, f"tenant owner read transports: {r.status_code}")
    r = call(P, "PUT", "/api/platform/notification-transports/email", {"enabled": True, "host": HOST, "port": 3025, "security": "none", "fromAddress": "backup-e2e@e2e.test", "fromName": "E2E Backup"})
    check(r.status_code == 200, f"transport PUT {r.status_code} {r.text[:200]}")
    tr = call(P, "GET", "/api/platform/notification-transports").json()["email"]
    check(tr["configured"] and tr["enabled"], f"transport state {tr}")
    bad = call(P, "PUT", "/api/platform/notification-transports/email", {"enabled": True, "host": HOST, "port": 3025, "security": "none", "username": "u", "fromAddress": "backup-e2e@e2e.test"})
    check(bad.status_code == 400, f"credentials over unencrypted connection accepted: {bad.status_code}")
    r = t.raw("PUT", "/notifications/policy", POLICY); check(r.status_code == 200, f"policy {r.status_code} {r.text[:200]}")
    check(r.json()["transports"]["email"]["available"], f"email not available: {r.json()['transports']}")
    for field, val in (("minimumSeverity", "loud"), ("eventTypes", ["nope"]), ("channelKinds", ["pager"]), ("maxNotificationsPerHour", 0), ("maxNotificationsPerHour", 10_000)):
        r = t.raw("PUT", "/notifications/policy", {**POLICY, field: val}); check(r.status_code == 400, f"policy {field}={val} -> {r.status_code}")
    return {"transport": {k: tr[k] for k in ("enabled", "configured", "host", "port", "security", "hasSecret")}}


@scenario("notify/9.1 email channel: verification mail, recipient link flow (confirm, tamper, unsubscribe, re-point)")
def email_channel():
    t = F["t"]
    c = t.call("POST", "/notifications/channels", {"kind": "email", "name": f"e2e-mail {RUN}", "target": MAIL}); S["mail"] = c["id"]
    check(not c["recipientVerified"], "new email channel already verified")
    dup = t.raw("POST", "/notifications/channels", {"kind": "email", "name": f"e2e-mail2 {RUN}", "target": MAIL}); check(dup.status_code == 409, f"duplicate target {dup.status_code}")
    d1 = t.call("POST", f"/notifications/channels/{c['id']}/test", {})["deliveryId"]
    dd = delivery(t, d1); check(dd["status"] == "sent", f"verification delivery {dd}")
    ms = wait_mail(MAIL, 1); m = ms[0]
    check(m["List-Unsubscribe"] and "List-Unsubscribe-Post" in m, f"missing one-click unsubscribe headers: {m['List-Unsubscribe']}")
    confirm = link(m, "confirm"); S["confirm"] = confirm; token = token_of(confirm)
    check("#token=" in confirm and "?token=" not in confirm, "confirm token not in URL fragment")
    # alert while unconfirmed must be suppressed, not sent (tested later with a real failing run)
    page = requests.get(confirm.split("#")[0], verify=False); check(page.status_code == 200 and "text/html" in page.headers["content-type"], f"confirm page {page.status_code}")
    check("script-src 'sha256-" in page.headers.get("content-security-policy", "") and page.headers.get("referrer-policy") == "no-referrer", "recipient page lacks CSP/referrer policy")
    post = lambda route, tok, **k: requests.post(f"{BASE}/api/notifications/recipient/{route}", data={"token": tok}, verify=False, **k)
    bad = {"tampered": token[:-3] + ("AAA" if not token.endswith("AAA") else "BBB"), "garbage": "x", "empty": ""}
    for k, v in bad.items():
        r = post("confirm", v); check(r.status_code == 400, f"{k} token -> {r.status_code}")
    check(post("unsubscribe", token).status_code == 400, "confirm token accepted on the unsubscribe route")
    check(not chan(t, c["id"])["recipientVerified"], "channel verified by invalid links")
    r = post("confirm", token); check(r.status_code == 200, f"confirm {r.status_code} {r.text[:150]}")
    ch = chan(t, c["id"]); check(ch["recipientVerified"], f"not verified after confirm: {ch}")
    check(post("confirm", token).status_code == 200, "repeating the link is not harmless")
    d2 = t.call("POST", f"/notifications/channels/{c['id']}/test", {})["deliveryId"]
    check(delivery(t, d2)["status"] == "sent", "test delivery after confirmation not sent")
    ms = wait_mail(MAIL, 2)
    S["one_click"] = re.search(r"<(https?://[^>]+)>", ms[-1]["List-Unsubscribe"]).group(1)
    return {"subjects": [x["Subject"] for x in ms], "headers": ms[-1]["List-Unsubscribe-Post"], "verify_delivery": dd["status"]}


@scenario("notify/9.1 unsubscribe (one-click + link), stale links after re-point, confirm link after unsubscribe")
def unsubscribe_flow():
    t = F["t"]; cid = S["mail"]; notes = {}
    post = lambda route, tok: requests.post(f"{BASE}/api/notifications/recipient/{route}", data={"token": tok}, verify=False)
    one = requests.post(S["one_click"], data={"List-Unsubscribe": "One-Click"}, verify=False)
    check(one.status_code == 200, f"one-click unsubscribe {one.status_code} {one.text[:150]}")
    ch = chan(t, cid); notes["after_unsubscribe"] = {k: ch.get(k) for k in ("enabled", "recipientVerified")}
    check(not ch["recipientVerified"] or not ch["enabled"], f"still deliverable after unsubscribe: {notes['after_unsubscribe']}")
    r = t.raw("POST", f"/notifications/channels/{cid}/test", {}); notes["test_after_unsubscribe"] = r.status_code
    # the old confirm link (still unexpired) is replayed after the recipient unsubscribed
    rc = post("confirm", token_of(S["confirm"])); notes["replay_confirm_after_unsubscribe"] = rc.status_code
    ch2 = chan(t, cid); notes["after_replayed_confirm"] = {k: ch2.get(k) for k in ("enabled", "recipientVerified")}
    notes["replayed_confirm_resubscribes"] = bool(ch2["recipientVerified"]) and rc.status_code == 200
    # re-point: links mailed to the previous address must stop working
    new = f"e2e-notify-moved-{RUN}@e2e.test"
    t.call("PUT", f"/notifications/channels/{cid}", {"name": f"e2e-mail {RUN}", "target": new})
    rc = post("confirm", token_of(S["confirm"])); notes["old_link_after_repoint"] = rc.status_code
    check(rc.status_code in (400, 404), f"old link still works after the channel was re-pointed: {rc.status_code}")
    check(not chan(t, cid)["recipientVerified"], "re-pointed channel kept its verification")
    t.call("PUT", f"/notifications/channels/{cid}", {"name": f"e2e-mail {RUN}", "target": MAIL})
    S["unsub_all"] = notes
    return notes


@scenario("notify/9.1 webhook channel to a private network is refused (network_denied) and test rate limit")
def webhook_guard():
    t = F["t"]
    c = t.call("POST", "/notifications/channels", {"kind": "webhook", "name": f"e2e-hook-denied {RUN}", "target": "https://10.10.20.1/e2e-hook"}); S["hook"] = c["id"]
    d = t.call("POST", f"/notifications/channels/{c['id']}/test", {})["deliveryId"]
    dd = delivery(t, d); check(dd["status"] == "failed" and dd["errorCode"] == "network_denied", f"webhook to 10.10.20.1: {dd}")
    check(c["targetDisplay"] == "https://10.10.20.1/…", f"webhook path not masked: {c['targetDisplay']}")
    port = t.raw("POST", "/notifications/channels", {"kind": "webhook", "name": f"e2e-hook-port {RUN}", "target": "https://example.invalid:8443/x"}); check(port.status_code == 400, f"non-443 webhook port {port.status_code}")
    plain = t.raw("POST", "/notifications/channels", {"kind": "webhook", "name": f"e2e-hook-http {RUN}", "target": "http://example.invalid/x"}); check(plain.status_code == 400, f"plain http webhook {plain.status_code}")
    seen = [202]
    for i in range(7):
        r = t.raw("POST", f"/notifications/channels/{c['id']}/test", {}); seen.append(r.status_code)
        if r.status_code != 202: break
    check(409 in seen, f"test rate limit never hit: {seen}")
    last = r.json() if r.status_code != 202 else None
    check(last and last.get("error") == "notification_test_rate_limited", f"limit error {last}")
    return {"webhook_delivery": {"status": dd["status"], "errorCode": dd["errorCode"], "attempts": dd["attempts"]}, "test_statuses": seen}


@scenario("notify/9.1 real failing run -> backup_failed mail + denied webhook; unconfirmed address suppressed")
def failing_run_notification():
    t = F["t"]; fa = t.agents()["fedora-e2e"]
    # channel creation already queues the confirmation mail (no extra test request: the tenant allows only 20 tests/confirmations per hour)
    c = t.call("POST", "/notifications/channels", {"kind": "email", "name": f"e2e-mail {RUN}", "target": MAIL}); S["mail"] = c["id"]
    ghost = f"e2e-notify-ghost-{RUN}@e2e.test"
    g = t.call("POST", "/notifications/channels", {"kind": "email", "name": f"e2e-ghost {RUN}", "target": ghost}); S["ghost"] = g["id"]
    h = t.call("POST", "/notifications/channels", {"kind": "webhook", "name": f"e2e-hook-denied {RUN}", "target": "https://10.10.20.1/e2e-hook"}); S["hook"] = h["id"]
    ms = wait_mail(MAIL, 1, timeout=90); confirm = link(ms[-1], "confirm")
    check(requests.post(f"{BASE}/api/notifications/recipient/confirm", data={"token": token_of(confirm)}, verify=False).status_code == 200, "confirm failed")
    check(chan(t, S["mail"])["recipientVerified"], "channel not verified")
    cfg = copy.deepcopy(F["cfg"]); cfg["database"] = "e2e_missing_db"
    r = t.raw("PUT", "/notifications/policy", POLICY); check(r.status_code == 200, "policy re-save failed")  # fresh ActiveSince before the failure
    before = len(mails(MAIL))
    job = t.call("POST", "/backup-jobs", {"name": f"e2e-authz notify-fail {RUN}", "agentId": fa["agentId"], "overlapPolicy": "Skip", "configuration": cfg,
                                         "retryPolicy": {"maxAttempts": 1}})
    S["fail_job"] = job["id"]
    run = t.call("POST", f"/backup-jobs/{job['id']}/runs", {}); rid = run.get("id", run.get("run", {}).get("id"))
    det = t.wait(f"/backup-runs/{rid}", timeout=240); check(det["status"] == "Failed", f"run did not fail: {det['status']}")
    S["fail_run"] = rid
    end = time.time() + 240; items = []
    while time.time() < end:
        items = [d for d in t.call("GET", "/notifications/deliveries?pageSize=100")["items"] if d["eventType"] == "backup_failed" and job["name"] in (d.get("subjectName") or "")]
        if len(items) >= 2 and all(d["status"] != "pending" or d["errorCode"] for d in items): break
        time.sleep(5)
    by = {d["channelName"]: d for d in items}
    check(f"e2e-mail {RUN}" in by and f"e2e-hook-denied {RUN}" in by, f"missing deliveries: {[(d['channelName'], d['status'], d['errorCode']) for d in items]}")
    mail_d = by[f"e2e-mail {RUN}"]; hook_d = by[f"e2e-hook-denied {RUN}"]
    check(mail_d["status"] == "sent", f"mail delivery {mail_d['status']} {mail_d['errorCode']}")
    got = wait_mail(MAIL, 1, timeout=60, subject=job["name"]); sub = got[0]["Subject"]
    check("failed" in sub.lower(), f"subject {sub}")
    check(all("Confirm" in (m["Subject"] or "") for m in mails(ghost)), "unconfirmed address received an alert, not only the confirmation request")
    check(f"e2e-ghost {RUN}" not in by, "unconfirmed channel got a delivery row")
    check(hook_d["status"] == "failed" and hook_d["errorCode"] == "network_denied", f"hook delivery {hook_d['status']} {hook_d['errorCode']}")
    leaked = [s for s in (ST["fedora"]["pg_password"],) if s in body_of(got[0])]
    check(not leaked, "mail body contains a secret")
    return {"subject": sub, "mail": mail_d["status"], "ghost": "no delivery (unconfirmed address is not deliverable)", "hook": [hook_d["status"], hook_d["errorCode"]], "run": rid}


@scenario("notify/9.1 per-hour cap: events past maxNotificationsPerHour are Suppressed (burst_limited)")
def burst_cap():
    t = F["t"]; fa = t.agents()["fedora-e2e"]
    r = t.raw("PUT", "/notifications/policy", {**POLICY, "maxNotificationsPerHour": 1}); check(r.status_code == 200, f"policy {r.status_code}")
    cfg = copy.deepcopy(F["cfg"]); cfg["database"] = "e2e_missing_db2"
    job = t.call("POST", "/backup-jobs", {"name": f"e2e-authz notify-burst {RUN}", "agentId": fa["agentId"], "overlapPolicy": "Skip", "configuration": cfg, "retryPolicy": {"maxAttempts": 1}})
    rid = t.call("POST", f"/backup-jobs/{job['id']}/runs", {}); rid = rid.get("id", rid.get("run", {}).get("id"))
    det = t.wait(f"/backup-runs/{rid}", timeout=240); check(det["status"] == "Failed", f"run {det['status']}")
    end = time.time() + 200; items = []
    while time.time() < end:
        items = [d for d in t.call("GET", "/notifications/deliveries?pageSize=100")["items"] if d["eventType"] == "backup_failed" and job["name"] in (d.get("subjectName") or "")]
        if items: break
        time.sleep(5)
    check(items, "no delivery rows for the capped event (history should show what was held back)")
    check(all(d["status"] == "suppressed" and d["errorCode"] == "burst_limited" for d in items), f"capped event not suppressed: {[(d['status'], d['errorCode']) for d in items]}")
    before = [m["Subject"] for m in mails(MAIL) if job["name"] in (m["Subject"] or "")]
    check(not before, "mail sent for capped event")
    t.raw("PUT", "/notifications/policy", POLICY)
    S["burst_job"] = job["id"]
    return {"deliveries": [(d["channelName"], d["status"], d["errorCode"]) for d in items]}


@scenario("notify/9.1 SMTP outage: delivery goes Pending with retry and is delivered after recovery")
def smtp_retry():
    t = F["t"]; P = platform()
    good = {"enabled": True, "host": HOST, "port": 3025, "security": "none", "fromAddress": "backup-e2e@e2e.test", "fromName": "E2E Backup"}
    try:
        call(P, "PUT", "/api/platform/notification-transports/email", {**good, "port": 3999})
        before = len(mails(MAIL))
        d = t.call("POST", f"/notifications/channels/{S['mail']}/test", {})["deliveryId"]
        dd = delivery(t, d, timeout=60, until=("pending", "failed", "sent"))
        check(dd["status"] == "pending" and dd["nextAttemptAt"] and dd["errorCode"], f"outage delivery {dd}")
    finally:
        call(P, "PUT", "/api/platform/notification-transports/email", good)
    end = time.time() + 200
    while time.time() < end:
        cur = delivery(t, d, timeout=10, until=("sent", "failed", "pending", "suppressed"))
        if cur["status"] == "sent": break
        time.sleep(5)
    check(cur["status"] == "sent", f"delivery did not recover: {cur}")
    check(len(mails(MAIL)) > before, "no mail after recovery")
    return {"outage": [dd["status"], dd["errorCode"], dd["attempts"]], "after": [cur["status"], cur["attempts"]]}


@scenario("notify/9.1 cleanup: policy off, channels removed")
def cleanup():
    t = F["t"]
    t.raw("PUT", "/notifications/policy", {**POLICY, "enabled": False})
    left = []
    for k in ("mail", "hook", "ghost"):
        if k in S: left.append((k, t.raw("DELETE", f"/notifications/channels/{S[k]}").status_code))
    for j in ("fail_job", "burst_job"):
        if j in S: t.raw("POST", f"/backup-jobs/{S[j]}/disable", {})
    return {"deleted": left}


def main(only=None):
    fixture()
    for fn in (setup, email_channel, unsubscribe_flow, webhook_guard, failing_run_notification, burst_cap, smtp_retry):
        if not only or only in fn.__name__: fn()
    cleanup()
    print("cleanup:", cleanup_synthetic(), teardown())


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else None)
