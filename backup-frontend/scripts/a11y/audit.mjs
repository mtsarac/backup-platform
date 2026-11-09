#!/usr/bin/env bun
// Chromium + axe-core audit of the tenant pages: dashboard, jobs, wizard, artifacts, agent detail.
// Writes JSON with axe violations and the size of every interactive element (see README.md).
import { chromium } from 'playwright-core'
import fs from 'node:fs'
import path from 'node:path'
import { createRequire } from 'node:module'

const here = import.meta.dirname
const env = process.env
const BASE = (env.A11Y_BASE_URL ?? 'http://localhost:5173').replace(/\/$/, '')
const STATE = env.A11Y_STATE ?? path.resolve(here, '../../../docs/test-environment/state.json')
const OUT = env.A11Y_OUT ?? path.join(here, 'results/audit.json')
const CHROMIUM = env.A11Y_CHROMIUM ?? '/usr/bin/chromium'
const ROLE = env.A11Y_ROLE ?? 'a'
const AXE = createRequire(import.meta.url).resolve('axe-core/axe.min.js')
const AXE_TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa', 'best-practice']
const TARGET = 44 // design target in CSS px
const WCAG_MIN = 24 // WCAG 2.5.8 AA minimum, used by the spacing exemption

const VIEWPORTS = {
  desktop: { viewport: { width: 1366, height: 900 } },
  mobile: { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true, deviceScaleFactor: 2 },
}

const state = JSON.parse(fs.readFileSync(STATE, 'utf8'))
const account = state[ROLE]
const tenant = account.tenantId

/** Runs in the page: every interactive element with its box, label and inline-in-sentence flag. */
function measureInPage() {
  const SELECTOR = 'a[href], button, input:not([type=hidden]), select, textarea, summary, [role=button], [role=link], [role=checkbox], [role=radio], [role=switch], [role=tab], [role=menuitem], [role=option], [role=combobox], [tabindex]:not([tabindex^="-"])'
  const css = (el) => {
    const parts = []
    for (let n = el; n && n.nodeType === 1 && parts.length < 4; n = n.parentElement) {
      if (n.id) { parts.unshift(`#${CSS.escape(n.id)}`); break }
      const i = n.parentElement ? [...n.parentElement.children].indexOf(n) + 1 : 1
      parts.unshift(`${n.localName}:nth-child(${i})`)
    }
    return parts.join(' > ')
  }
  const label = (el) => (el.getAttribute('aria-label') ?? el.labels?.[0]?.innerText ?? el.innerText ?? el.value ?? el.title ?? '').replace(/\s+/g, ' ').trim().slice(0, 60)
  const box = (el) => { const r = el.getBoundingClientRect(); return { x: r.x + scrollX, y: r.y + scrollY, w: r.width, h: r.height } }
  const out = []
  for (const el of document.querySelectorAll(SELECTOR)) {
    if (el.closest('[inert], [aria-hidden=true]')) continue
    const cs = getComputedStyle(el)
    if (cs.display === 'none' || cs.visibility === 'hidden') continue
    const own = box(el)
    if (own.w === 0 && own.h === 0) continue
    // A checkbox/radio is activated by its label too, so the label box counts as the target.
    const labels = el.labels ? [...el.labels].map(box).filter(b => b.w > 0) : []
    const best = [own, ...labels].reduce((a, b) => (b.w * b.h > a.w * a.h ? b : a))
    let inline = false
    if (cs.display === 'inline' && el.localName === 'a') {
      let block = el.parentElement
      while (block && getComputedStyle(block).display === 'inline') block = block.parentElement
      // Prose only: text written directly in the block (not inside another control) counts, separators like "·" do not.
      const prose = [...(block?.childNodes ?? [])].filter(n => n.nodeType === 3).map(n => n.textContent).join('').replace(/[^\p{L}]/gu, '')
      inline = prose.length >= 20
    }
    out.push({
      selector: css(el), tag: el.localName, role: el.getAttribute('role') ?? el.type ?? null, label: label(el),
      ...best, ownW: own.w, ownH: own.h, disabled: el.matches(':disabled, [aria-disabled=true]'), inline,
      srOnly: own.w <= 1 || own.h <= 1,
    })
  }
  return out
}

/** Classify undersized targets: 'inline' | 'disabled' | 'sr-only' | 'spacing' (WCAG 2.5.8) | null. Needs all targets of the page. */
function classify(targets, coarse) {
  const small = (t) => t.w < TARGET || t.h < TARGET
  const circles = targets.filter(t => !t.srOnly).map(t => ({ t, cx: t.x + t.w / 2, cy: t.y + t.h / 2 }))
  const hitsBox = (c, o) => { const dx = Math.max(o.x - c.cx, 0, c.cx - (o.x + o.w)); const dy = Math.max(o.y - c.cy, 0, c.cy - (o.y + o.h)); return Math.hypot(dx, dy) < WCAG_MIN / 2 }
  for (const t of targets) {
    t.size = `${Math.round(t.w)}x${Math.round(t.h)}`
    if (!small(t)) { t.status = 'pass'; continue }
    if (t.srOnly) { t.status = 'exempt'; t.exemption = 'sr-only: not rendered as a pointer target (skip link or hidden input revealed on focus only)'; continue }
    if (t.disabled) { t.status = 'exempt'; t.exemption = 'disabled: inactive control (WCAG 2.5.8 inactive-target exception)'; continue }
    if (t.inline) { t.status = 'exempt'; t.exemption = 'inline: link inside a sentence (WCAG 2.5.8 inline exception)'; continue }
    t.status = 'fail'
  }
  // Spacing exemption: a target of at least 24x24 passes WCAG 2.5.8 AA; a smaller one needs its 24px circle clear of every other target.
  for (const t of targets.filter(t => t.status === 'fail')) {
    if (!coarse && t.w >= WCAG_MIN && t.h >= WCAG_MIN) { t.status = 'exempt'; t.exemption = `fine-pointer: ${t.size} px meets the WCAG 2.5.8 AA 24x24 minimum; coarse (touch) pointers get the 44 px target through pointer-coarse variants`; t.tier = 'wcag-aa-only'; continue }
    const me = circles.find(c => c.t === t)
    if (coarse) continue // touch: no spacing exception, 44 px or nothing
    const clash = circles.some(o => o.t !== t && (hitsBox(me, o.t) || (small(o.t) && Math.hypot(o.cx - me.cx, o.cy - me.cy) < WCAG_MIN)))
    if (!clash) { t.status = 'exempt'; t.exemption = 'spacing: 24 px circle around the target does not touch any other target (WCAG 2.5.8 spacing exception)' }
  }
}

async function login(context) {
  const page = await context.newPage()
  await page.goto(`${BASE}/login`)
  await page.getByLabel('Kullanıcı adı').fill(account.login)
  await page.getByLabel('Şifre').fill(account.password)
  await page.getByRole('button', { name: /Giriş Yap/ }).click()
  await page.waitForURL(u => !u.pathname.startsWith('/login'), { timeout: 20_000 })
  const agents = await page.evaluate(async (t) => (await fetch(`/api/tenants/${t}/agents`)).json(), tenant)
  const agent = agents.find(a => a.connectivity === 'online') ?? agents[0]
  await page.close()
  return agent.agentId
}

/** One page load, audited once per state; each state's `run` builds on the previous one (wizard steps, opened menus). */
async function auditPage(context, spec) {
  const page = await context.newPage()
  await page.goto(`${BASE}${spec.url}`)
  await page.waitForLoadState('networkidle')
  await page.waitForTimeout(800)
  const out = []
  for (const state of spec.states ?? [{ name: '' }]) {
    if (state.run) { await state.run(page); await page.waitForTimeout(500) }
    out.push(await auditState(page, spec.name + (state.name ? `:${state.name}` : ''), spec.url))
  }
  await page.close()
  return out
}

async function auditState(page, name, url) {
  await page.addScriptTag({ path: AXE })
  const axe = await page.evaluate((tags) => window.axe.run(document, { runOnly: { type: 'tag', values: tags }, resultTypes: ['violations', 'incomplete'] }), AXE_TAGS)
  const targets = await page.evaluate(measureInPage)
  const coarse = await page.evaluate(() => matchMedia('(pointer: coarse)').matches)
  classify(targets, coarse)
  const slim = (v) => ({ id: v.id, impact: v.impact, help: v.help, nodes: v.nodes.map(n => ({ target: n.target, summary: n.failureSummary?.split('\n').slice(0, 3).join(' ').trim() })) })
  return {
    page: name, url, coarsePointer: coarse,
    axe: { violations: axe.violations.map(slim), incomplete: axe.incomplete.map(slim) },
    targets: { total: targets.length, pass: targets.filter(t => t.status === 'pass').length, exempt: targets.filter(t => t.status === 'exempt').length, fail: targets.filter(t => t.status === 'fail').length, elements: targets.filter(t => t.status !== 'pass') },
  }
}

const next = (page) => page.getByRole('button', { name: 'İleri' }).click()
/** The wizard is walked to the review step with the minimum valid input; nothing is saved. */
const wizardStates = [
  { name: 'source' },
  { name: 'storage', run: async (page) => {
    await page.getByLabel(/İş adı/).fill('a11y-audit')
    await page.locator('main select').first().selectOption({ index: 1 })
    await page.getByLabel(/SQL Server ana bilgisayarı/).fill('localhost')
    await page.getByLabel('Veritabanı', { exact: true }).fill('audit')
    await next(page)
  } },
  { name: 'schedule', run: next },
  { name: 'protection', run: next },
  { name: 'review', run: next },
]
const openMobileNav = { name: 'nav-open', run: (page) => page.getByRole('button', { name: 'Menüyü aç' }).click() }

const browser = await chromium.launch({ executablePath: CHROMIUM, headless: true })
const results = []
try {
  for (const [vpName, vp] of Object.entries(VIEWPORTS)) {
    const context = await browser.newContext({ ...vp, ignoreHTTPSErrors: true })
    const agentId = await login(context)
    const t = `/tenants/${tenant}`
    const mobileNav = vp.isMobile ? [openMobileNav] : []
    const pages = [
      { name: 'dashboard', url: t, states: [{ name: '' }, { name: 'details-open', run: (page) => page.evaluate(() => document.querySelectorAll('details').forEach(d => { d.open = true })) }] },
      { name: 'dashboard', url: t, states: mobileNav },
      { name: 'jobs', url: `${t}/backup-jobs` },
      { name: 'wizard', url: `${t}/backup-jobs/new`, states: wizardStates },
      { name: 'artifacts', url: `${t}/artifacts` },
      { name: 'agent-detail', url: `${t}/agents/${agentId}` },
    ]
    for (const spec of pages) for (const r of await auditPage(context, spec)) results.push({ viewport: vpName, ...r })
    await context.close()
  }
} finally { await browser.close() }

const serious = results.flatMap(r => r.axe.violations.filter(v => v.impact === 'serious' || v.impact === 'critical').map(v => ({ viewport: r.viewport, page: r.page, id: v.id, impact: v.impact, nodes: v.nodes.length })))
const summary = {
  generatedAt: new Date().toISOString(), base: BASE, targetPx: TARGET, wcagMinPx: WCAG_MIN, tags: AXE_TAGS,
  seriousOrCriticalAxe: serious, undersizedWithoutExemption: results.reduce((n, r) => n + r.targets.fail, 0),
}
fs.mkdirSync(path.dirname(OUT), { recursive: true })
fs.writeFileSync(OUT, JSON.stringify({ summary, results }, null, 2))
for (const r of results) console.log(`${r.viewport.padEnd(7)} ${r.page.padEnd(22)} axe violations ${r.axe.violations.length} (serious+ ${r.axe.violations.filter(v => ['serious', 'critical'].includes(v.impact)).length}) | targets ${r.targets.total}: pass ${r.targets.pass} exempt ${r.targets.exempt} FAIL ${r.targets.fail}`)
console.log(JSON.stringify(summary))
process.exit(serious.length || summary.undersizedWithoutExemption ? 1 : 0)
