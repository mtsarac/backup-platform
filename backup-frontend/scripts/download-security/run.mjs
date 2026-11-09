#!/usr/bin/env bun
// Security acceptance run for GET /api/tenants/{t}/backup-artifacts/{a}/downloads/{session}/content (issue #62).
// Real Chromium (playwright-core) plays victim and third-party pages; the API checks use the same cookie-authenticated clients. See README.md.
import { chromium, request as pw } from 'playwright-core'
import https from 'node:https'
import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'

const here = import.meta.dirname
const env = process.env
const API = (env.DL_API ?? 'https://192.168.122.1:5252').replace(/\/$/, '')
const UI = env.DL_UI?.replace(/\/$/, '')
const PEER = env.DL_PEER_API?.replace(/\/$/, '')
const NODE = env.DL_NODE ?? new URL(API).port
const STATE = env.DL_STATE ?? path.resolve(here, '../../../docs/test-environment/state.json')
const OUT = env.DL_OUT ?? path.join(here, `results/${NODE}.json`)
const CHROMIUM = env.DL_CHROMIUM ?? '/usr/bin/chromium'
const ART = env.DL_ARTIFACT ?? '5e5c54fa-9718-4cc3-b979-ae2f976088ec' // API-streamed (S3) replica, ~1.1 MB
const LARGE = env.DL_LARGE_ARTIFACT ?? 'b957848e-2941-4eda-bc1f-f05e6ad5affd' // API-streamed (S3) replica, ~67 MB: large enough that an aborted transfer is still in flight
const CROSS_HOST = env.DL_CROSS_HOST ?? 'evil.test' // mapped to 127.0.0.1 inside Chromium only
const SESSION_LIFETIME_MS = 15 * 60_000 // DownloadService.SessionLifetime
const SKIP_EXPIRY_WAIT = env.DL_SKIP_EXPIRY_WAIT === '1'

const state = JSON.parse(fs.readFileSync(STATE, 'utf8'))
const TENANT = state.a.tenantId
const OTHER_TENANT = state.b.tenantId
const accounts = {
  owner: state.a, 'admin-a': state.roles['admin-a'], 'operator-a': state.roles['operator-a'], 'viewer-a': state.roles['viewer-a'],
  'owner-b': state.b, 'admin-b': state.roles['admin-b'], 'viewer-b': state.roles['viewer-b'],
}
// Every credential-like value of the lab state: none may appear in any denied response.
const SECRETS = []
;(function walk(o, k = '') { for (const [key, v] of Object.entries(o)) typeof v === 'object' && v ? walk(v, key) : typeof v === 'string' && /pass|secret/i.test(key) && v.length >= 8 && SECRETS.push(v) })(state)
const sha256 = (b) => crypto.createHash('sha256').update(b).digest('hex')
const sleep = (ms) => new Promise(r => setTimeout(r, ms))
const within = (p, ms) => Promise.race([p, sleep(ms).then(() => null)]) // opaque / aborted responses can leave body() pending
const tests = []
const test = async (id, criterion, fn, { informational = false } = {}) => {
  const t0 = Date.now()
  try {
    const { pass, evidence } = await fn()
    tests.push({ id, criterion, informational, pass, evidence, ms: Date.now() - t0 })
  } catch (e) {
    tests.push({ id, criterion, informational, pass: false, evidence: { error: String(e?.stack ?? e).slice(0, 600) }, ms: Date.now() - t0 })
  }
  const t = tests.at(-1)
  console.log(`${t.pass ? 'PASS' : t.informational ? 'INFO' : 'FAIL'}  ${id}`)
}

// ---- API clients (cookie jar per actor; the victim's jar is its browser context's, so pages and API share one session) ----
async function login(ctx, api, acct) {
  const token = async () => { const t = await (await ctx.get(`${api}/api/auth/antiforgery`)).json(); return { [t.headerName]: t.requestToken } }
  const r = await ctx.post(`${api}/api/auth/login`, { headers: await token(), data: { loginIdentifier: acct.login, password: acct.password } })
  if (r.status() !== 204) throw new Error(`login ${acct.login} -> ${r.status()}`)
  return { ctx, api, h: await token() }
}
const newClient = async (acct, api = API) => login(await pw.newContext({ ignoreHTTPSErrors: true }), api, acct)
const anonymous = async () => ({ ctx: await pw.newContext({ ignoreHTTPSErrors: true }), api: API, h: {} })
async function call(c, method, p, { json, headers } = {}) {
  const r = await c.ctx.fetch(c.api + p, { method, headers: { ...c.h, ...headers }, data: json, maxRedirects: 0, failOnStatusCode: false })
  const body = await r.body()
  return { status: r.status(), headers: r.headers(), body }
}
const jsonOf = (r) => { try { return JSON.parse(r.body.toString('utf8')) } catch { return null } }
const base = (tenant = TENANT, art = ART) => `/api/tenants/${tenant}/backup-artifacts/${art}`
const content = (sid, tenant, art) => `${base(tenant, art)}/downloads/${sid}/content`

async function prepare(c, art = ART, replicaId) {
  const r = await call(c, 'POST', `${base(TENANT, art)}/downloads`, { json: replicaId ? { replicaId } : {} })
  if (r.status !== 200) throw new Error(`prepare ${r.status} ${r.body.toString().slice(0, 200)}`)
  const p = jsonOf(r)
  if (p.direct) throw new Error('artifact is served direct (WebRTC), not API-streamed; pick an S3 / backend replica')
  return p
}
const status = async (c, sid, art = ART) => jsonOf(await call(c, 'GET', `${base(TENANT, art)}/downloads/${sid}`))
// The owner's legitimate download: 200, exact size and SHA-256.
async function fetchOwned(c, p) {
  const r = await call(c, 'GET', p.downloadUrl)
  return { status: r.status, bytes: r.body.length, sha256: r.status === 200 ? sha256(r.body) : null, ok: r.status === 200 && r.body.length === p.sizeBytes && sha256(r.body) === p.checksum, contentType: r.headers['content-type'], error: r.status === 200 ? undefined : r.body.toString().slice(0, 200) }
}
const denial = (r) => ({ status: r.status, contentType: r.headers['content-type'] ?? null, contentDisposition: r.headers['content-disposition'] ?? null, bodyBytes: r.body.length, body: r.body.toString('utf8').slice(0, 200) })

let LEAK_MARKERS = []
function leaks(r, bytesAllowed = 2048) {
  const hay = r.body.toString('latin1') + '\n' + JSON.stringify(r.headers)
  const found = [...LEAK_MARKERS, ...SECRETS].filter(m => hay.includes(m)).map(m => (SECRETS.includes(m) ? '<lab secret>' : m))
  const bytes = r.status === 200 || r.body.length > bytesAllowed || /octet-stream/.test(r.headers['content-type'] ?? '') || 'content-disposition' in r.headers
  return { found, bytes }
}

// ---- attacker origins: HTTPS pages that run one vector on a user gesture ----
const PAGE = `<!doctype html><meta charset=utf-8><button id=go>go</button><iframe name=sink hidden></iframe><script>
const q = new URLSearchParams(location.search), u = q.get('u')
const form = m => { const f = document.createElement('form'); f.method = m; f.action = u; f.target = 'sink'; document.body.append(f); f.submit() }
const el = (t, p) => { const e = Object.assign(document.createElement(t), p); document.body.append(e) }
const V = {
  'fetch-no-cors': () => fetch(u, { mode: 'no-cors', credentials: 'include' }).catch(() => {}),
  'fetch-cors': () => fetch(u, { mode: 'cors', credentials: 'include' }).catch(() => {}),
  img: () => el('img', { src: u }),
  iframe: () => el('iframe', { src: u }),
  'form-get': () => form('GET'),
  'form-post': () => form('POST'),
  'top-navigation': () => { location.href = u },
  'window-open': () => window.open(u),
  'anchor-download': () => { const a = Object.assign(document.createElement('a'), { href: u, download: 'x' }); document.body.append(a); a.click() },
}
go.onclick = () => V[q.get('v')]()
</script>`
const VECTORS = ['fetch-no-cors', 'fetch-cors', 'img', 'iframe', 'form-get', 'form-post', 'top-navigation', 'window-open', 'anchor-download']

const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'dl-sec-'))
const pem = (n) => path.join(tmp, n)
const sh = Bun.spawnSync(['openssl', 'req', '-x509', '-newkey', 'ec', '-pkeyopt', 'ec_paramgen_curve:prime256v1', '-nodes', '-days', '1', '-subj', '/CN=attacker', '-keyout', pem('k.pem'), '-out', pem('c.pem')])
if (sh.exitCode !== 0) throw new Error('openssl failed: ' + sh.stderr)
const attacker = Bun.serve({ port: 0, hostname: '0.0.0.0', tls: { key: Bun.file(pem('k.pem')), cert: Bun.file(pem('c.pem')) }, fetch: () => new Response(PAGE, { headers: { 'content-type': 'text/html' } }) })
const origins = { 'cross-site': `https://${CROSS_HOST}:${attacker.port}`, 'same-site': `https://${new URL(API).hostname}:${attacker.port}` }

const browser = await chromium.launch({ executablePath: CHROMIUM, args: [`--host-resolver-rules=MAP ${CROSS_HOST} 127.0.0.1`] })
const version = browser.version()
const victimCtx = await browser.newContext({ ignoreHTTPSErrors: true, acceptDownloads: true })
const victim = await login(victimCtx.request, API, accounts.owner).then(c => ({ ...c, browser: victimCtx }))
const detail = jsonOf(await call(victim, 'GET', base()))
const replica = detail.replicas.find(r => r.status === 'Available' && r.verificationState === 'Verified' && r.storageProvider === 's3') ?? detail.replicas[0]
LEAK_MARKERS = [replica.key, replica.storageTarget, '/central-share', '/var/lib/', '/home/e2e-sftp/', 'X-Amz-Signature', 'BEGIN PRIVATE KEY', 'C:\\'].filter(m => m && m.length >= 4 && m !== 'local')
const meta = { node: NODE, api: API, ui: UI ?? null, peer: PEER ?? null, startedAt: new Date().toISOString(), chromium: version, attackerOrigins: origins, artifact: { id: ART, sizeBytes: detail.sizeBytes, sha256: detail.artifactChecksum, replicaProvider: replica.storageProvider }, largeArtifact: LARGE }

// Expiry check is the long pole (15 minutes of real time), so its session is created first.
const expiry = { session: await prepare(victim, ART, replica.id), at: Date.now() }

// ---- 1 + 4: third-party pages in real Chromium ----
const attackResults = []
async function attack(origin, vector) {
  const p = await prepare(victim, ART, replica.id)
  const before = await status(victim, p.sessionId)
  const page = await victimCtx.newPage()
  const seen = []
  const onReq = (r) => r.url().startsWith(API + '/api/') && r.url().includes(p.sessionId) && seen.push(r)
  victimCtx.on('request', onReq)
  const downloads = []
  const onPage = (pg) => pg.on('download', d => downloads.push(d.suggestedFilename()))
  victimCtx.on('page', onPage)
  page.on('download', d => downloads.push(d.suggestedFilename()))
  await page.goto(`${origins[origin]}/?v=${vector}&u=${encodeURIComponent(API + p.downloadUrl)}`)
  await page.click('#go')
  const t0 = Date.now()
  while (!seen.length && Date.now() - t0 < 6000) await sleep(100)
  await sleep(1500)
  victimCtx.off('request', onReq)
  victimCtx.off('page', onPage)
  const requests = []
  for (const r of seen) {
    const h = await within(r.allHeaders().catch(() => ({})), 3000) ?? {}
    const resp = await within(r.response().catch(() => null), 3000)
    const body = resp ? await within(resp.body().catch(() => null), 5000) : null
    requests.push({
      method: r.method(), resourceType: r.resourceType(), failure: r.failure()?.errorText ?? null,
      cookieSent: h['sec-fetch-site'] ? /__Host-BackupPlatform\.Auth=/.test(h.cookie ?? '') : null, // null: the browser blocked the request (CORS) before exposing its headers
      secFetchSite: h['sec-fetch-site'] ?? null, secFetchMode: h['sec-fetch-mode'] ?? null, secFetchDest: h['sec-fetch-dest'] ?? null,
      status: resp?.status() ?? null, contentType: resp?.headers()['content-type'] ?? null, bodyBytes: resp?.headers()['content-length'] != null ? Number(resp.headers()['content-length']) : body?.length ?? null, // header first: for a failed navigation the body is Chromium's own error page
    })
  }
  const after = await status(victim, p.sessionId)
  for (const pg of victimCtx.pages()) await pg.close().catch(() => {})
  // Legitimate owner download of the same session afterwards: proves the page did not burn it (cross-site) or shows that it did (same-site).
  const owner = await fetchOwned(victim, p)
  const res = { origin, vector, sessionId: p.sessionId, before: before.state, after: after.state, afterTransferred: after.transferredBytes, requests, downloadEvents: downloads, ownerDownloadAfter: owner }
  attackResults.push(res)
  console.log(`  ${origin} ${vector}: requests=${requests.length} cookie=${requests.map(q => q.cookieSent)} status=${requests.map(q => q.status)} after=${after.state} (${Date.now() - t0} ms)`)
  return res
}
for (const origin of ['cross-site', 'same-site']) for (const vector of VECTORS) await attack(origin, vector)

for (const vector of VECTORS) {
  await test(`cross-site.${vector}`, '1,4', async () => {
    const a = attackResults.find(r => r.origin === 'cross-site' && r.vector === vector)
    const sent = a.requests.length > 0
    const pass = sent && a.before === 'Ready' &&
      a.requests.every(r => r.cookieSent !== true && (r.secFetchSite === 'cross-site' || (r.cookieSent === null && r.failure && !r.status)) && r.status !== 200 && !(r.bodyBytes > 2048)) &&
      a.after === 'Ready' && a.afterTransferred === 0 && a.downloadEvents.length === 0 && a.ownerDownloadAfter.ok
    return { pass, evidence: a }
  })
}
await test('same-site.positive-control', '1 (harness validity)', async () => {
  const same = attackResults.filter(r => r.origin === 'same-site')
  const withCookie = same.filter(r => r.requests.some(q => q.cookieSent))
  // The cookie CAN reach the API from a same-site page, so the cross-site absence above is the browser's SameSite=Strict at work, not a broken harness.
  return { pass: withCookie.length > 0, evidence: { vectorsWithCookie: withCookie.map(r => r.vector), perVector: same.map(r => ({ vector: r.vector, cookieSent: r.requests.map(q => q.cookieSent), secFetchSite: r.requests.map(q => q.secFetchSite), status: r.requests.map(q => q.status), after: r.after, ownerDownloadAfterStatus: r.ownerDownloadAfter.status })) } }
})
await test('same-site.residual-risk', '1,4 (informational)', async () => {
  const burned = attackResults.filter(r => r.origin === 'same-site' && r.after !== 'Ready').map(r => r.vector)
  return { pass: burned.length === 0, evidence: { vectorsThatConsumedSession: burned, note: 'A script on a same-site origin (any port of the same host) that already knows the unguessable session URL can consume it; SameSite=Strict does not separate ports.' } }
}, { informational: true })

// ---- 2: other principals ----
const owned = await prepare(victim, ART, replica.id)
await test('denied.principals', '2', async () => {
  const actors = {}
  for (const k of ['admin-a', 'operator-a', 'viewer-a', 'owner-b', 'admin-b', 'viewer-b']) actors[k] = await newClient(accounts[k])
  actors.anonymous = await anonymous()
  const cases = [
    ['admin-a (same tenant, other user)', 'admin-a', TENANT, [404]],
    ['operator-a (same tenant)', 'operator-a', TENANT, [403]],
    ['viewer-a (Viewer, same tenant)', 'viewer-a', TENANT, [403]],
    ['owner-b on tenant A path', 'owner-b', TENANT, [403, 404]],
    ['owner-b on own tenant path, A artifact+session', 'owner-b', OTHER_TENANT, [404]],
    ['admin-b on tenant A path', 'admin-b', TENANT, [403, 404]],
    ['viewer-b on own tenant path', 'viewer-b', OTHER_TENANT, [403, 404]],
    ['anonymous', 'anonymous', TENANT, [401, 403, 404]],
  ]
  const rows = []
  let pass = true
  for (const [name, k, tenant, expected] of cases) for (const [method, p] of [['GET', content(owned.sessionId, tenant)], ['POST', content(owned.sessionId, tenant)], ['GET', `${base(tenant)}/downloads/${owned.sessionId}`]]) {
    const r = await call(actors[k], method, p, method === 'POST' ? { json: {} } : {})
    const l = leaks(r)
    const ok = (expected.includes(r.status) || (method === 'POST' && [400, 401, 403, 404].includes(r.status))) && !l.bytes && l.found.length === 0
    pass &&= ok
    rows.push({ actor: name, method, path: p.replace(owned.sessionId, '{sessionId}').replace(TENANT, '{tenantA}').replace(OTHER_TENANT, '{tenantB}'), ok, leakedMarkers: l.found, ...denial(r) })
  }
  const st = await status(victim, owned.sessionId)
  const after = await fetchOwned(victim, owned)
  pass &&= st.state === 'Ready' && st.transferredBytes === 0 && after.ok
  return { pass, evidence: { sessionBeforeOwnerDownload: st, ownerDownloadAfter: after, rows } }
})

// ---- 3: completed, interrupted, logged-out, wrong-artifact, expired ----
await test('replay.completed', '3', async () => {
  const p = await prepare(victim, ART, replica.id)
  const first = await fetchOwned(victim, p)
  const done = await status(victim, p.sessionId)
  const again = await call(victim, 'GET', p.downloadUrl)
  const post = await call(victim, 'POST', p.downloadUrl, { json: {} })
  const result = { first, statusAfter: done, replayGet: denial(again), replayPost: denial(post) }
  if (PEER) {
    const peer = await newClient(accounts.owner, PEER)
    const r = await call(peer, 'GET', p.downloadUrl)
    result.replayOnPeerNode = { api: PEER, ...denial(r) }
    result.peerOk = r.status === 409 && !leaks(r).bytes
  }
  const pass = first.ok && done.state === 'Completed' && done.transferredBytes === p.sizeBytes && again.status === 409 && !leaks(again).bytes && post.status !== 200 && !leaks(post).bytes && (!PEER || result.peerOk)
  return { pass, evidence: result }
})
await test('replay.interrupted-transfer', '3', async () => {
  const big = jsonOf(await call(victim, 'GET', base(TENANT, LARGE)))
  const p = await prepare(victim, LARGE, big.replicas.find(r => r.storageProvider === 's3' && r.status === 'Available').id)
  const cookie = (await victimCtx.cookies(API)).map(c => `${c.name}=${c.value}`).join('; ')
  const got = await new Promise((resolve, reject) => {
    const req = https.get(API + p.downloadUrl, { rejectUnauthorized: false, headers: { cookie } }, (resp) => {
      let n = 0
      resp.on('data', (c) => { n += c.length; if (n >= 256 * 1024) { req.destroy(); resolve({ status: resp.statusCode, bytesBeforeAbort: n }) } })
      resp.on('end', () => resolve({ status: resp.statusCode, bytesBeforeAbort: n, finished: true }))
    })
    req.on('error', () => {})
    setTimeout(() => reject(new Error('no data within 60 s')), 60_000)
  })
  let s
  for (let i = 0; i < 150; i++) { s = await status(victim, p.sessionId, LARGE); if (['Completed', 'Failed', 'Cancelled'].includes(s.state)) break; await sleep(1000) }
  const replay = await call(victim, 'GET', p.downloadUrl)
  return { pass: !got.finished && ['Failed', 'Cancelled'].includes(s.state) && s.transferredBytes < p.sizeBytes && replay.status === 409 && !leaks(replay).bytes, evidence: { artifact: LARGE, sizeBytes: p.sizeBytes, aborted: got, finalState: s, replay: denial(replay) } }
})
await test('replay.after-logout', '3', async () => {
  const c = await newClient(accounts.owner)
  const p = await prepare(c, ART, replica.id)
  const cookie = (await c.ctx.storageState()).cookies.map(x => `${x.name}=${x.value}`).join('; ')
  const out = await call(c, 'POST', '/api/auth/logout')
  const stolen = await call(await anonymous(), 'GET', p.downloadUrl, { headers: { cookie } })
  const st = await status(victim, p.sessionId)
  const fresh = await newClient(accounts.owner)
  const again = await fetchOwned(fresh, p)
  return { pass: out.status === 204 && stolen.status === 401 && !leaks(stolen).bytes && st.state === 'Ready' && again.ok, evidence: { logout: out.status, replayWithRevokedCookie: denial(stolen), sessionAfter: st.state, ownerDownloadWithNewLogin: again } }
})
await test('replay.wrong-binding', '3', async () => {
  const p = await prepare(victim, ART, replica.id)
  const otherArt = ART === LARGE ? '00000000-0000-0000-0000-000000000001' : LARGE
  const r = await call(victim, 'GET', content(p.sessionId, TENANT, otherArt))
  const ghost = await call(victim, 'GET', content(crypto.randomUUID(), TENANT, ART))
  const st = await status(victim, p.sessionId)
  return { pass: r.status === 404 && ghost.status === 404 && !leaks(r).bytes && st.state === 'Ready', evidence: { sessionOnOtherArtifactPath: denial(r), unknownSession: denial(ghost), sessionAfter: st.state } }
})
if (SKIP_EXPIRY_WAIT) tests.push({ id: 'replay.expired', criterion: '3', informational: false, pass: false, skipped: true, evidence: { note: 'DL_SKIP_EXPIRY_WAIT=1' } })
else await test('replay.expired', '3', async () => {
  const wait = expiry.at + SESSION_LIFETIME_MS + 20_000 - Date.now()
  if (wait > 0) { console.log(`waiting ${Math.ceil(wait / 1000)} s for the idle session to expire`); await sleep(wait) }
  const r = await call(victim, 'GET', expiry.session.downloadUrl)
  const st = await status(victim, expiry.session.sessionId)
  return { pass: r.status === 409 && !leaks(r).bytes && st.state === 'Failed' && st.failureCode === 'download_expired', evidence: { ageSeconds: Math.round((Date.now() - expiry.at) / 1000), content: denial(r), status: st } }
})

// ---- 6: normal anchor download in the browser, size + SHA-256 ----
await test('anchor.download', '6', async () => {
  const ctx = await browser.newContext({ ignoreHTTPSErrors: true, acceptDownloads: true })
  const page = await ctx.newPage()
  let sid // the session the page prepared; Playwright reports no network event for a download-manager request, so the API state is the evidence
  page.on('response', async r => { if (r.request().method() === 'POST' && r.url().endsWith('/downloads') && r.ok()) sid = (await r.json()).sessionId })
  let sessionFromUi
  if (UI) {
    await page.goto(`${UI}/login`)
    await page.getByLabel('Kullanıcı adı').fill(accounts.owner.login)
    await page.getByLabel('Şifre').fill(accounts.owner.password)
    await page.getByRole('button', { name: /Giriş Yap/ }).click()
    await page.waitForURL(u => !u.pathname.startsWith('/login'), { timeout: 20_000 })
    await page.goto(`${UI}/tenants/${TENANT}/artifacts/${ART}`)
    await page.getByRole('button', { name: /^(İndir|Download)$/ }).click() // opens the download section
    const button = page.getByRole('button', { name: /^(Yedeklemeyi İndir|Download Backup)$/ })
    await button.waitFor({ timeout: 30_000 })
    var [download] = await Promise.all([page.waitForEvent('download', { timeout: 60_000 }), button.click()])
  } else {
    // No UI on this node: do what the UI does (prepare, then click a temporary same-origin anchor with the download attribute) in a page of the API origin.
    const c = await login(ctx.request, API, accounts.owner)
    const p = await prepare(c, ART, replica.id)
    sid = p.sessionId
    await page.goto(`${API}/api/auth/antiforgery`)
    var [download] = await Promise.all([page.waitForEvent('download', { timeout: 60_000 }), page.evaluate(([href, name]) => { const a = Object.assign(document.createElement('a'), { href, download: name }); document.body.append(a); a.click(); a.remove() }, [API + p.downloadUrl, p.filename])])
  }
  const file = path.join(tmp, 'download.bin')
  await download.saveAs(file)
  const bytes = fs.readFileSync(file)
  const failure = await download.failure()
  const st = sid ? await status(victim, sid) : null
  await ctx.close()
  const pass = !failure && bytes.length === detail.sizeBytes && sha256(bytes) === detail.artifactChecksum && st?.state === 'Completed' && st.transferredBytes === detail.sizeBytes
  return { pass, evidence: { mode: UI ? 'ui-button' : 'injected-anchor', suggestedFilename: download.suggestedFilename(), downloadFailure: failure, bytes: bytes.length, expectedBytes: detail.sizeBytes, sha256: sha256(bytes), expectedSha256: detail.artifactChecksum, sessionId: sid, sessionState: st } }
})

await browser.close()
attacker.stop(true)
fs.rmSync(tmp, { recursive: true, force: true })
const failed = tests.filter(t => !t.pass && !t.informational)
const out = { meta: { ...meta, finishedAt: new Date().toISOString() }, summary: { total: tests.length, passed: tests.filter(t => t.pass).length, failed: failed.map(t => t.id), informationalNotPassing: tests.filter(t => !t.pass && t.informational).map(t => t.id) }, tests }
fs.mkdirSync(path.dirname(OUT), { recursive: true })
fs.writeFileSync(OUT, JSON.stringify(out, null, 2) + '\n')
console.log(`\n${out.summary.passed}/${out.summary.total} passed on node ${NODE}; wrote ${OUT}`)
process.exit(failed.length ? 1 : 0)
