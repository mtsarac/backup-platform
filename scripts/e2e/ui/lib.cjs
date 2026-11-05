// Shared helpers for the UiChromium real-browser checks (playwright + axe-core are installed globally under ~/.bun).
const G = process.env.HOME + '/.bun/install/global/node_modules/';
const { chromium } = require(G + 'playwright');
const fs = require('fs'), path = require('path');
const ROOT = path.resolve(__dirname, '../../..');
const ST = JSON.parse(fs.readFileSync(ROOT + '/docs/test-environment/state.json', 'utf8'));
const OUT = ROOT + '/docs/test-environment/run-20261105/screenshots-ui';
const BASE = 'https://192.168.122.1:5173';
const API = 'https://192.168.122.1:5252';
const RES = ROOT + '/docs/test-environment/run-20261105/ui-results.jsonl';
fs.mkdirSync(OUT, { recursive: true });

async function launch(opts = {}) {
  const browser = await chromium.launch({ executablePath: '/usr/bin/chromium', headless: true, args: ['--ignore-certificate-errors', '--no-sandbox'] });
  return browser;
}
async function newCtx(browser, o = {}) {
  return browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1440, height: 900 }, ...o });
}
/** creds: {login,password} read from state.json key: 'a' | 'b' | roles name */
function creds(key) { const c = ST.roles[key] || ST[key]; return { login: c.login, password: c.password }; }
async function login(page, key, lang) {
  const c = creds(key);
  await page.goto(BASE + '/login', { waitUntil: 'domcontentloaded' });
  const user = page.locator('input[name="login"], input[type="email"], input[autocomplete="username"], input[type="text"]').first();
  await user.fill(c.login);
  await page.locator('input[type="password"]').fill(c.password);
  await page.locator('button[type="submit"]').click();
  await page.waitForURL(u => /\/(tenants|platform)\b/.test(u.pathname), { timeout: 20000 });
  return page.url();
}
// Refuse to capture any page with a filled password field.
async function shot(page, name, o = {}) { if (await page.locator('input[type=password]').evaluateAll(es => es.some(e => e.value))) throw new Error('Screenshot refused: password field is filled'); const f = `${OUT}/${name}.png`; await page.screenshot({ path: f, mask: [page.locator('input[type=password]')], ...o }); return name + '.png'; }
function rec(id, name, ok, detail) { const row = { id, name, result: ok === null ? 'INFO' : ok ? 'PASS' : 'FAIL', at: new Date().toISOString(), ...detail }; fs.appendFileSync(RES, JSON.stringify(row) + '\n'); console.log(JSON.stringify(row)); }
module.exports = { launch, newCtx, login, shot, rec, ST, ROOT, OUT, BASE, API, creds, G };
/** Compact view of the interactive surface of the main area (for exploring/asserting steps). */
async function dump(page, sel = 'main') {
  return page.evaluate(s => {
    const root = document.querySelector(s) || document.body;
    const name = e => (e.getAttribute('aria-label') || (e.labels && e.labels[0] && e.labels[0].innerText) || e.innerText || e.placeholder || e.name || '').trim().replace(/\s+/g, ' ').slice(0, 50);
    const vis = e => !!(e.offsetWidth || e.offsetHeight);
    return {
      h: [...root.querySelectorAll('h1,h2,h3')].filter(vis).map(e => e.innerText.trim().slice(0, 60)),
      fields: [...root.querySelectorAll('input,select,textarea')].filter(vis).map(e => `${e.tagName.toLowerCase()}${e.type && e.type !== 'text' ? ':' + e.type : ''}[${name(e)}]=${e.type === 'password' ? '***' : (e.value || '').slice(0, 30)}${e.checked ? ' ✓' : ''}${e.disabled ? ' (dis)' : ''}`),
      buttons: [...root.querySelectorAll('button,[role=button],[role=tab],[role=radio],[role=combobox]')].filter(vis).map(e => `${e.getAttribute('role') || 'btn'}:${name(e)}${e.disabled ? '(dis)' : ''}`),
    };
  }, sel);
}
module.exports.dump = dump;
const { execFileSync } = require('child_process');
/** Host PostgreSQL container (127.0.0.1:5432) via docker exec; password is read from state.json and never printed. */
function hostPg(sql, db = 'postgres') {
  const c = ST.host.pg;
  return execFileSync('docker', ['exec', '-e', 'PGPASSWORD=' + c.password, 'postgres', 'psql', '-h', '127.0.0.1', '-U', c.user, '-d', db, '-tA', '-v', 'ON_ERROR_STOP=1', '-c', sql], { encoding: 'utf8' }).trim();
}
const PGQ = "select count(*)||'|'||md5(string_agg(name||payload, ',' order by id)) from items";
/** Authenticated API call as a tenant owner (cookie session + antiforgery handled by the browser context request). */
async function api(ctx, method, path, body) {
  const r = await ctx.request.fetch(API + path, { method, data: body, ignoreHTTPSErrors: true, headers: { 'Idempotency-Key': require('crypto').randomUUID() } });
  return { status: r.status(), json: await r.json().catch(() => null) };
}
Object.assign(module.exports, { hostPg, PGQ, api });
function envAdmin() {
  const e = Object.fromEntries(fs.readFileSync(ROOT + '/.env.e2e', 'utf8').split('\n').filter(l => l.includes('=') && !l.startsWith('#')).map(l => [l.slice(0, l.indexOf('=')), l.slice(l.indexOf('=') + 1)]));
  return { login: e.BOOTSTRAP_ADMIN_LOGIN, password: e.BOOTSTRAP_ADMIN_PASSWORD };
}
/** Form login with explicit credentials ({login,password}); returns the post-login URL. */
async function loginAs(page, c) {
  await page.goto(BASE + '/login', { waitUntil: 'domcontentloaded' });
  await page.locator('input').nth(0).fill(c.login);
  await page.locator('input[type="password"]').fill(c.password);
  await page.locator('button[type="submit"]').click();
  await page.waitForURL(u => /\/(tenants|platform)\b/.test(u.pathname), { timeout: 20000 });
  return page.url();
}
/** GET JSON through the logged-in browser context. */
async function get(ctx, path) { const r = await ctx.request.get(API + path, { ignoreHTTPSErrors: true }); return r.status() === 200 ? r.json() : { __status: r.status() }; }
Object.assign(module.exports, { envAdmin, loginAs, get });
