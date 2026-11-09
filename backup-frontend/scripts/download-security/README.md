# Download content endpoint: security acceptance run (issue #62)

`run.mjs` checks `GET /api/tenants/{tenantId}/backup-artifacts/{artifactId}/downloads/{sessionId}/content` against a running lab with real Chromium (playwright-core) and cookie-authenticated API clients. It never touches production code or the database; it only calls the public API and loads pages.

The artifact must be API-streamed (S3 or another backend replica, `direct: false`). A private SFTP / agent replica is a direct download where `/content` is only WebRTC signalling, so the script refuses it.

## What it checks

| Test id | Acceptance criterion | How |
| --- | --- | --- |
| `cross-site.<vector>` (9) | 1, 4 | The victim is logged in in a Chromium context. A page on a different **site** (`https://evil.test:<port>`, resolved to 127.0.0.1 by a Chromium-only host rule) is given the content URL and, on a user gesture, tries `fetch` (no-cors and cors, credentials included), `<img>`, `<iframe>`, a GET form, a POST form, top-level navigation, `window.open` and a download anchor. Per vector a fresh session is prepared. Recorded from the browser's own network events: whether the `__Host-BackupPlatform.Auth` cookie was on the request, `Sec-Fetch-Site/Mode/Dest`, status, bytes, and any download event. Pass: no cookie, `cross-site`, not 200, no bytes, and the session read back through the API is still `Ready` with 0 bytes transferred and then downloads fine for its owner (SHA-256 equal). |
| `same-site.positive-control` | 1 (harness validity) | The same vectors from `https://<api host>:<port>` (same site, different port). Must show the cookie being sent, so that "no cookie" above is `SameSite=Strict` at work and not a broken harness. |
| `same-site.residual-risk` | informational | Lists which same-site vectors consumed the session. SameSite is scoped to the site, not the port. |
| `denied.principals` | 2 | Same-tenant other admin, operator, Viewer, other-tenant owner/admin/Viewer (on tenant A's path and on their own tenant's path with A's ids), anonymous. Each sends GET content, POST content (with CSRF token) and GET status. Pass: expected 401/403/404, no octet-stream, no `Content-Disposition`, body under 2 KiB, none of the replica's storage key / target, backend paths, credential markers or any password from the state file in body or headers; the owner's session is untouched and still downloads. |
| `replay.completed` | 3 | Owner downloads (size + SHA-256 checked), then replays GET and POST: 409 / non-200, no bytes, state `Completed`. With `DL_PEER_API` the replay is repeated on the second node. |
| `replay.interrupted-transfer` | 3 | A ~67 MB download is aborted after 256 KiB; the session must end `Cancelled`/`Failed` and the replay returns 409. |
| `replay.after-logout` | 3 | The cookie captured before logout is replayed after logout: 401; the session is still `Ready` for a fresh login. |
| `replay.wrong-binding` | 3 | The session id on another artifact's path, and a random session id: 404. |
| `replay.expired` | 3 | A session prepared at the start of the run is left idle until `SessionLifetime` (15 min) has passed; content is refused (409) and the status is `Failed` / `download_expired`. This makes a run last about 16 minutes. `DL_SKIP_EXPIRY_WAIT=1` skips it and records the test as skipped (a failure). |
| `anchor.download` | 6 | A browser download. With `DL_UI`: login through the UI, artifact page, open the download section, click "Yedeklemeyi İndir" (the app creates the temporary anchor). Without `DL_UI`: prepare through the API and click a same-origin anchor with the `download` attribute in a page of the API origin. The saved file's size and SHA-256 must equal the artifact's and the session must end `Completed` with every byte transferred (Playwright reports no network event for a download-manager request, so the API state is the evidence). |

Limits worth knowing: for the CORS `fetch`, Chromium blocks the response before exposing request headers, so `cookieSent` is `null` there and the verdict rests on the session staying `Ready`. Test sessions cannot be deleted through the API; unused ones expire after 15 minutes by themselves.

## Run

```sh
cd backup-frontend
bun install
export DL_STATE=/path/to/docs/test-environment/state.json   # git-ignored lab credentials, read only
# node 1: API through nginx :5252, UI :5173; the peer is node 2
DL_NODE=bp-api  DL_API=https://192.168.122.1:5252 DL_UI=https://192.168.122.1:5173 DL_PEER_API=https://192.168.122.1:5255 bun scripts/download-security/run.mjs
# node 2 (no UI in front of it)
DL_NODE=bp-api2 DL_API=https://192.168.122.1:5255 DL_PEER_API=https://192.168.122.1:5252 bun scripts/download-security/run.mjs
```

The two runs can go in parallel. Exit code 1 if any non-informational test fails. Results: `results/<DL_NODE>.json` (`meta`, `summary`, one entry per test with its evidence; no passwords or cookie values are written).

| Variable | Default | Meaning |
| --- | --- | --- |
| `DL_API` | `https://192.168.122.1:5252` | API origin under test |
| `DL_UI` | unset | UI origin that proxies to `DL_API`; enables the UI-button download |
| `DL_PEER_API` | unset | Second API node for the cross-node replay |
| `DL_NODE` | API port | Label and result file name |
| `DL_STATE` | `<repo>/docs/test-environment/state.json` | Lab state: `a`, `b`, `roles` accounts and tenant ids |
| `DL_ARTIFACT` | `5e5c54fa-9718-4cc3-b979-ae2f976088ec` | Small (~1.1 MB) S3 artifact of tenant `a` |
| `DL_LARGE_ARTIFACT` | `b957848e-2941-4eda-bc1f-f05e6ad5affd` | ~67 MB S3 artifact for the interrupted transfer |
| `DL_CROSS_HOST` | `evil.test` | Name of the cross-site origin (resolved to loopback inside Chromium only) |
| `DL_CHROMIUM` | `/usr/bin/chromium` | Browser binary |
| `DL_SKIP_EXPIRY_WAIT` | unset | `1` skips the 15-minute expiry wait |
| `DL_OUT` | `scripts/download-security/results/<node>.json` | Output file |

The lab's S3 target (MinIO, compose project `bpe2e-targets`) must be running.
