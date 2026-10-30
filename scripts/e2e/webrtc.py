"""Agent-direct download over WebRTC, end to end in a real browser: a file backup stored on the Fedora agent's local volume is downloaded through the UI
(Artifact -> Download -> Download Backup -> Save file). The browser peer is headless Chromium (browser.py) running the product's own download code against the agent VM's pion peer
over the libvirt bridge; no STUN/TURN is configured for the lab, the host candidates connect directly. The one stub is the native save dialog, which no automation can drive:
`showSaveFilePicker` is replaced by an in-memory sink that records every byte the data channel delivers. The scenario then compares what arrived with the artifact:
byte count == artifact size, SHA-256 of the received bytes == the artifact checksum, and the platform's download session is Completed with the same byte count.
One pass, no retries. Run: python3 webrtc.py"""
import re, api, browser, flows, lib
from lib import ST, scenario

UI = f"https://{api.env['PUBLIC_HOST']}:{api.env.get('FRONTEND_PORT', '5173')}"
# Lab adaptations of the browser peer, nothing else: no STUN (the stock stun.cloudflare.com entry makes this host's ICE gathering take ~40 s, past the product's 15 s limit,
# because its IPv6 path is dead), and Chromium enumerates only the host's physical interface (192.168.1.25) although the agent VM can reach the host only as PUBLIC_HOST
# over virbr0, so the offer's host candidate is re-addressed to PUBLIC_HOST. The data channel, DTLS, SCTP and the download code are the product's.
SINK = """
localStorage.setItem('bp_language', 'en');
const Native = window.RTCPeerConnection;
const localDescription = Object.getOwnPropertyDescriptor(Native.prototype, 'localDescription').get;
window.RTCPeerConnection = function (config, ...rest) {
  const peer = new Native({ ...config, iceServers: [] }, ...rest);
  Object.defineProperty(peer, 'localDescription', { get: () => { const d = localDescription.call(peer); return d && { type: d.type, sdp: d.sdp.replace(/(a=candidate:\\S+ \\d+ udp \\d+ )\\S+( \\d+ typ host)/g, '$1' + __PUBLIC_HOST__ + '$2') } } });
  return peer;
};
window.RTCPeerConnection.prototype = Native.prototype;
window.__dl = { bytes: 0, parts: [], closed: false, aborted: false };
window.showSaveFilePicker = async () => ({ createWritable: async () => ({
  write: async chunk => { window.__dl.parts.push(chunk.slice()); window.__dl.bytes += chunk.byteLength },
  close: async () => { window.__dl.closed = true },
  abort: async () => { window.__dl.aborted = true } }) });
window.__dlSha256 = async () => [...new Uint8Array(await crypto.subtle.digest('SHA-256', await new Blob(window.__dl.parts).arrayBuffer()))].map(b => b.toString(16).padStart(2, '0')).join('');
""".replace("__PUBLIC_HOST__", repr(api.env["PUBLIC_HOST"]))


@scenario("download/agent-direct-webrtc-bytes-equal-artifact-hash")
def webrtc_direct(t):
    fa = t.agents()["fedora-e2e"]
    _, _, made, _ = flows.run_backup(t, fa, "webrtc-direct", flows.file_job_config("e2efiles", [flows.local_dest()], paths=["bin"]))
    art = t.call("GET", f"/backup-artifacts/{made['id']}")
    assert [r["storageProvider"] for r in art["replicas"]] == ["local"], "the artifact must live on the agent's local volume (the agent serves it directly)"
    b = browser.Browser()
    try:
        b.init_script(SINK)
        b.goto(UI + "/login")
        b.type("input[autocomplete=username]", ST["a"]["login"]); b.type("input[type=password]", ST["a"]["password"]); b.click_text("Sign In")
        b.wait("location.pathname !== '/login'", what="the login to finish")
        b.goto(f"{UI}/tenants/{t.id}/artifacts/{art['id']}")
        b.click_text("Download"); b.click_text("Download Backup")
        b.click_text("Save file")
        outcome = b.wait("window.__dl.closed ? 'saved' : window.__dl.aborted ? 'aborted' : false", timeout=300, what="the direct transfer to end")
        assert outcome == "saved", f"the browser peer aborted the download; page says: {b.eval('document.body.innerText')[-400:]!r}"
        b.wait("document.body.innerText.includes('Verified and saved to disk.')", what="the UI to confirm the verified save")
        got = b.eval("window.__dlSha256().then(sha256 => ({bytes: window.__dl.bytes, sha256}))")
        sid = re.search(r"/downloads/([0-9a-f-]{36})", " ".join(b.eval("performance.getEntriesByType('resource').map(r => r.name)")))
        assert sid, "the page never polled a download session"
    finally:
        b.close()
    session = t.call("GET", f"/backup-artifacts/{art['id']}/downloads/{sid.group(1)}")
    assert got["bytes"] == art["sizeBytes"], f"received {got['bytes']} bytes, the artifact has {art['sizeBytes']}"
    assert got["sha256"] == art["artifactChecksum"], f"received SHA-256 {got['sha256'][:16]}, the artifact checksum is {art['artifactChecksum'][:16]}"
    assert (session["state"], session["transferredBytes"]) == ("Completed", art["sizeBytes"]), f"download session {session}"
    return {"artifactId": art["id"], "bytes": got["bytes"], "sha256": got["sha256"][:16], "session": session["state"]}


if __name__ == "__main__":
    webrtc_direct(lib.Tenant("a"))
