"""A headless Chromium driven over the DevTools pipe (stdlib only; needs /usr/bin/chromium). Just enough for UI-driven e2e scenarios: navigate, type, click, run JS, wait.
The lab certificate is self-signed (--ignore-certificate-errors) and WebRTC must offer real host addresses, so the browser peer can reach the agent VM across the libvirt
bridge (mDNS-obfuscated host candidates cannot be resolved there)."""
import fcntl, json, os, queue, shutil, subprocess, tempfile, threading, time

CHROMIUM = shutil.which("chromium") or "/usr/bin/chromium"


class Browser:
    def __init__(self):
        self.profile = tempfile.mkdtemp(prefix="e2e-chromium-")
        to_child, self._out = os.pipe(); self._in, from_child = os.pipe()
        # Chromium reads commands on fd 3 and writes replies on fd 4; park the child's ends high first so the dup2 targets cannot clobber them.
        c_in, c_out = fcntl.fcntl(to_child, fcntl.F_DUPFD, 20), fcntl.fcntl(from_child, fcntl.F_DUPFD, 20)
        def child():
            os.dup2(c_in, 3); os.dup2(c_out, 4)
        self.proc = subprocess.Popen([CHROMIUM, "--headless=new", "--remote-debugging-pipe", "--ignore-certificate-errors", "--disable-features=WebRtcHideLocalIpsWithMdns", "--force-webrtc-ip-handling-policy=default",
                                      f"--user-data-dir={self.profile}", "--no-first-run", "--disable-gpu", "--no-default-browser-check", "about:blank"],
                                     close_fds=False, preexec_fn=child, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        for fd in (to_child, from_child, c_in, c_out): os.close(fd)
        self._seq = 0; self._lock = threading.Lock(); self._replies = {}
        threading.Thread(target=self._read, daemon=True).start()
        target = self.call("Target.createTarget", {"url": "about:blank"})["targetId"]
        self.session = self.call("Target.attachToTarget", {"targetId": target, "flatten": True})["sessionId"]
        for domain in ("Page", "Runtime"): self.call(f"{domain}.enable", session=self.session)

    def _read(self):
        buf = b""
        with os.fdopen(self._in, "rb", buffering=0) as stream:
            while chunk := stream.read(65536):
                buf += chunk
                while b"\0" in buf:
                    raw, buf = buf.split(b"\0", 1)
                    msg = json.loads(raw)
                    if "id" in msg:
                        with self._lock: self._replies[msg["id"]] = msg

    def call(self, method, params=None, session=None, timeout=60):
        with self._lock: self._seq += 1; mid = self._seq
        msg = {"id": mid, "method": method, "params": params or {}}
        if session: msg["sessionId"] = session
        os.write(self._out, json.dumps(msg).encode() + b"\0")
        end = time.time() + timeout
        while time.time() < end:
            with self._lock: reply = self._replies.pop(mid, None)
            if reply:
                if "error" in reply: raise RuntimeError(f"{method}: {reply['error']}")
                return reply["result"]
            time.sleep(0.02)
        raise TimeoutError(method)

    def init_script(self, source):
        """Runs in every new document before the page's own scripts."""
        self.call("Page.addScriptToEvaluateOnNewDocument", {"source": source}, session=self.session)

    def goto(self, url):
        self.call("Page.navigate", {"url": url}, session=self.session)

    def eval(self, expression, timeout=60):
        """Evaluates a JS expression (promises are awaited) and returns its JSON value."""
        r = self.call("Runtime.evaluate", {"expression": expression, "awaitPromise": True, "returnByValue": True}, session=self.session, timeout=timeout)
        if "exceptionDetails" in r: raise RuntimeError("page script failed: " + json.dumps(r["exceptionDetails"])[:400])
        return r["result"].get("value")

    def wait(self, expression, timeout=30, what=None):
        """Polls a JS expression until it is truthy and returns it; a timeout names what was awaited."""
        end = time.time() + timeout
        while time.time() < end:
            try:
                value = self.eval(expression, timeout=10)
                if value: return value
            except (RuntimeError, TimeoutError): pass  # the page is still loading
            time.sleep(0.25)
        raise TimeoutError(f"timed out waiting for {what or expression}")

    def type(self, selector, text):
        """Focuses the element and inserts text as typed input (fires the events a framework listens to)."""
        self.wait(f"(() => {{ const e = document.querySelector({json.dumps(selector)}); if (!e) return false; e.focus(); return document.activeElement === e }})()", what=selector)
        self.call("Input.insertText", {"text": text}, session=self.session)

    def click_text(self, text, selector="button", timeout=30):
        """Clicks the enabled element matching `selector` whose text is exactly `text`."""
        self.wait(f"(() => {{ const el = [...document.querySelectorAll({json.dumps(selector)})].find(e => e.textContent.trim() === {json.dumps(text)} && !e.disabled); if (!el) return false; el.click(); return true }})()",
                  timeout=timeout, what=f"enabled {selector} '{text}'")

    def close(self):
        self.proc.terminate()
        try: self.proc.wait(10)
        except subprocess.TimeoutExpired: self.proc.kill()
        shutil.rmtree(self.profile, ignore_errors=True)
