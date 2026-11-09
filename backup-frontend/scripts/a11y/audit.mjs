#!/usr/bin/env bun
// Chromium + axe-core audit of the tenant pages: dashboard, jobs, wizard, artifacts, agent detail,
// in light, dark, high-contrast and forced-colors conditions at a desktop and a mobile viewport.
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
const SHOTS = env.A11Y_SHOTS // optional directory: <dir>/<condition>/<viewport>-<page>.png

// Same switches the app reads: `backup-theme` (dark class), `backup-high-contrast` (.hc root class); forced-colors is Chromium emulation.
const CONDITIONS = {
  light: { dark: false, hc: false, forced: false },
  dark: { dark: true, hc: false, forced: false },
  'high-contrast': { dark: false, hc: true, forced: false },
  'high-contrast-dark': { dark: true, hc: true, forced: false },
  'forced-colors': { dark: false, hc: false, forced: true },
  'forced-colors-dark': { dark: true, hc: false, forced: true },
}
const CONDITION_NAMES = (env.A11Y_CONDITIONS ?? Object.keys(CONDITIONS).join(',')).split(',').map(c => c.trim()).filter(Boolean)
for (const c of CONDITION_NAMES) if (!CONDITIONS[c]) throw new Error(`unknown condition ${c}; known: ${Object.keys(CONDITIONS)}`)
// axe does not model forced-colors system colours, so colour-contrast is off there and visibleInForcedColors() measures computed styles instead.
const AXE_DISABLED_IN_FORCED = ['color-contrast']

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

/** Runs in the page under forced-colors: text and controls must stay distinguishable using computed (system-colour) styles. */
function visibleInForcedColors() {
  const ctx = document.createElement('canvas').getContext('2d', { willReadFrequently: true })
  ctx.canvas.width = ctx.canvas.height = 1
  const rgba = (css) => { ctx.clearRect(0, 0, 1, 1); ctx.fillStyle = '#000'; ctx.fillStyle = css; ctx.fillRect(0, 0, 1, 1); const d = ctx.getImageData(0, 0, 1, 1).data; const a = d[3] / 255; return [a ? d[0] / a : 0, a ? d[1] / a : 0, a ? d[2] / a : 0, a] }
  const over = (t, b) => { const a = t[3] + b[3] * (1 - t[3]); return a === 0 ? [0, 0, 0, 0] : [0, 1, 2].map(i => (t[i] * t[3] + b[i] * b[3] * (1 - t[3])) / a).concat(a) }
  const lum = ([r, g, b]) => { const f = (v) => { v /= 255; return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4 }; return 0.2126 * f(r) + 0.7152 * f(g) + 0.0722 * f(b) }
  const ratio = (a, b) => { const [x, y] = [lum(a), lum(b)].sort((p, q) => q - p); return (x + 0.05) / (y + 0.05) }
  const bgOf = (el) => { const layers = []; for (let n = el; n; n = n.parentElement) { const c = rgba(getComputedStyle(n).backgroundColor); layers.push(c); if (c[3] === 1) break } let acc = over(rgba(getComputedStyle(document.documentElement).backgroundColor), [255, 255, 255, 1]); for (const l of layers.reverse()) acc = over(l, acc); return acc }
  const css = (el) => (el.id ? `#${el.id}` : `${el.localName}${el.className && typeof el.className === 'string' ? '.' + el.className.trim().split(/\s+/).slice(0, 2).join('.') : ''}`)
  const findings = []
  let texts = 0
  const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT)
  const seen = new Set()
  for (let n = walker.nextNode(); n; n = walker.nextNode()) {
    const el = n.parentElement
    if (!el || seen.has(el) || !n.textContent.trim() || el.closest('script,style,noscript,[hidden],[aria-hidden=true]')) continue
    seen.add(el)
    const cs = getComputedStyle(el); const r = el.getBoundingClientRect()
    if (cs.display === 'none' || cs.visibility === 'hidden' || r.width <= 1 || r.height <= 1 || el.matches(':disabled, [aria-disabled=true]')) continue
    let opacity = 1; for (let m = el; m; m = m.parentElement) opacity *= Number(getComputedStyle(m).opacity)
    const bg = bgOf(el); const fg = rgba(cs.color)
    const rr = ratio(over([fg[0], fg[1], fg[2], fg[3] * opacity], bg), bg)
    texts++
    if (rr < 3) findings.push({ kind: 'text-indistinguishable', element: css(el), text: n.textContent.trim().slice(0, 40), color: cs.color, background: `rgb(${bg.slice(0, 3).map(Math.round)})`, opacity: Number(opacity.toFixed(2)), ratio: Number(rr.toFixed(2)) })
  }
  const controls = [...document.querySelectorAll('button, input:not([type=hidden]), select, textarea, summary, [role=button], [role=checkbox], [role=radio], [role=switch], [role=tab]')]
    .filter(el => { const r = el.getBoundingClientRect(); const cs = getComputedStyle(el); return r.width > 1 && r.height > 1 && cs.display !== 'none' && cs.visibility !== 'hidden' && !el.matches(':disabled') && !el.closest('[aria-hidden=true], [inert]') })
  let focusChecked = 0
  for (const el of controls) {
    const cs = getComputedStyle(el)
    const edge = (side) => parseFloat(cs[`border${side}Width`]) > 0 && cs[`border${side}Style`] !== 'none' && rgba(cs[`border${side}Color`])[3] > 0
    const bareText = !['input', 'select', 'textarea'].includes(el.localName) && !['checkbox', 'radio', 'switch'].includes(el.getAttribute('role') ?? '')
    // A control with no border of its own is acceptable only if it is plain text-like (ghost button, summary): it keeps visible text.
    if (!(edge('Top') || edge('Right') || edge('Bottom') || edge('Left')) && !bareText && !(el.localName === 'input' && ['checkbox', 'radio'].includes(el.type)))
      findings.push({ kind: 'control-without-border', element: css(el), label: (el.getAttribute('aria-label') ?? el.innerText ?? '').slice(0, 40) })
    el.focus({ preventScroll: true })
    if (document.activeElement === el) {
      focusChecked++
      const f = getComputedStyle(el)
      const outlined = f.outlineStyle !== 'none' && parseFloat(f.outlineWidth) > 0 && rgba(f.outlineColor)[3] > 0
      const ring = /\d/.test(f.boxShadow) && f.boxShadow !== 'none' // box-shadow rings are removed by forced colors; outline must exist
      if (!outlined) findings.push({ kind: 'focus-indicator-missing', element: css(el), label: (el.getAttribute('aria-label') ?? el.innerText ?? '').slice(0, 40), boxShadowOnly: ring })
    }
    el.blur()
  }
  return { textElements: texts, controls: controls.length, focusChecked, findings }
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
async function auditPage(context, spec, cond) {
  const page = await context.newPage()
  await page.goto(`${BASE}${spec.url}`)
  await page.waitForLoadState('networkidle')
  await page.waitForTimeout(800)
  const out = []
  for (const state of spec.states ?? [{ name: '' }]) {
    if (state.run) { await state.run(page); await page.waitForTimeout(500) }
    const name = spec.name + (state.name ? `:${state.name}` : '')
    out.push(await auditState(page, name, spec.url, cond))
    if (SHOTS && cond.shotDir) {
      fs.mkdirSync(cond.shotDir, { recursive: true })
      await page.screenshot({ path: path.join(cond.shotDir, `${cond.viewport}-${name.replace(/:/g, '-')}.png`), fullPage: true })
    }
  }
  await page.close()
  return out
}

async function auditState(page, name, url, cond) {
  await page.addScriptTag({ path: AXE })
  const disabled = cond.forced ? AXE_DISABLED_IN_FORCED : []
  const axe = await page.evaluate(({ tags, disabled }) => window.axe.run(document, { runOnly: { type: 'tag', values: tags }, rules: Object.fromEntries(disabled.map(id => [id, { enabled: false }])), resultTypes: ['violations', 'incomplete'] }), { tags: AXE_TAGS, disabled })
  // A keypress first: after a mouse click (wizard steps) Chromium would not match :focus-visible for programmatic focus.
  if (cond.forced) await page.keyboard.press('Shift')
  const forcedColors = cond.forced ? await page.evaluate(visibleInForcedColors) : undefined
  const forcedActive = await page.evaluate(() => matchMedia('(forced-colors: active)').matches)
  const targets = await page.evaluate(measureInPage)
  const coarse = await page.evaluate(() => matchMedia('(pointer: coarse)').matches)
  classify(targets, coarse)
  const slim = (v) => ({ id: v.id, impact: v.impact, help: v.help, nodes: v.nodes.map(n => ({ target: n.target, summary: n.failureSummary?.split('\n').slice(0, 3).join(' ').trim() })) })
  return {
    page: name, url, coarsePointer: coarse, forcedColorsActive: forcedActive, axeDisabledRules: disabled,
    ...(forcedColors && { forcedColorsVisibility: forcedColors }),
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
const pageSpecs = (t, agentId, vp) => [
  { name: 'dashboard', url: t, states: [{ name: '' }, { name: 'details-open', run: (page) => page.evaluate(() => document.querySelectorAll('details').forEach(d => { d.open = true })) }] },
  { name: 'dashboard', url: t, states: vp.isMobile ? [openMobileNav] : [] },
  { name: 'jobs', url: `${t}/backup-jobs` },
  { name: 'wizard', url: `${t}/backup-jobs/new`, states: wizardStates },
  { name: 'artifacts', url: `${t}/artifacts` },
  { name: 'agent-detail', url: `${t}/agents/${agentId}` },
]
try {
  for (const [vpName, vp] of Object.entries(VIEWPORTS)) {
    // One login per viewport; every condition reuses the session.
    const loginContext = await browser.newContext({ ...vp, ignoreHTTPSErrors: true })
    const agentId = await login(loginContext)
    const storageState = await loginContext.storageState()
    await loginContext.close()
    for (const condName of CONDITION_NAMES) {
      const c = CONDITIONS[condName]
      const context = await browser.newContext({ ...vp, ignoreHTTPSErrors: true, storageState, colorScheme: c.dark ? 'dark' : 'light', forcedColors: c.forced ? 'active' : 'none' })
      await context.addInitScript(({ c }) => {
        localStorage.setItem('bp_language', 'tr')
        localStorage.setItem('backup-theme', c.dark ? 'dark' : 'light')
        localStorage.setItem('backup-high-contrast', c.hc ? '1' : '0')
      }, { c })
      const cond = { ...c, viewport: vpName, shotDir: SHOTS && path.join(SHOTS, condName) }
      for (const spec of pageSpecs(`/tenants/${tenant}`, agentId, vp)) for (const r of await auditPage(context, spec, cond)) results.push({ condition: condName, viewport: vpName, ...r })
      await context.close()
    }
  }
} finally { await browser.close() }

const serious = results.flatMap(r => r.axe.violations.filter(v => v.impact === 'serious' || v.impact === 'critical').map(v => ({ condition: r.condition, viewport: r.viewport, page: r.page, id: v.id, impact: v.impact, nodes: v.nodes.length })))
const summary = {
  generatedAt: new Date().toISOString(), base: BASE, targetPx: TARGET, wcagMinPx: WCAG_MIN, tags: AXE_TAGS,
  conditions: CONDITION_NAMES, axeDisabledInForcedColors: AXE_DISABLED_IN_FORCED,
  seriousOrCriticalAxe: serious, undersizedWithoutExemption: results.reduce((n, r) => n + r.targets.fail, 0),
  forcedColorsFindings: results.flatMap(r => (r.forcedColorsVisibility?.findings ?? []).map(f => ({ condition: r.condition, viewport: r.viewport, page: r.page, ...f }))),
  perCondition: Object.fromEntries(CONDITION_NAMES.map(cn => [cn, Object.fromEntries(Object.keys(VIEWPORTS).map(v => { const rs = results.filter(r => r.condition === cn && r.viewport === v); return [v, { states: rs.length, axeSeriousOrCritical: rs.reduce((n, r) => n + r.axe.violations.filter(x => ['serious', 'critical'].includes(x.impact)).length, 0), axeOther: rs.reduce((n, r) => n + r.axe.violations.filter(x => !['serious', 'critical'].includes(x.impact)).length, 0), undersized: rs.reduce((n, r) => n + r.targets.fail, 0), forcedVisibility: rs.reduce((n, r) => n + (r.forcedColorsVisibility?.findings.length ?? 0), 0) }] }))])),
}
fs.mkdirSync(path.dirname(OUT), { recursive: true })
fs.writeFileSync(OUT, JSON.stringify({ summary, results }, null, 2))
for (const r of results) console.log(`${r.condition.padEnd(18)} ${r.viewport.padEnd(7)} ${r.page.padEnd(22)} axe violations ${r.axe.violations.length} (serious+ ${r.axe.violations.filter(v => ['serious', 'critical'].includes(v.impact)).length}) forced-vis ${r.forcedColorsVisibility?.findings.length ?? '-'} | targets ${r.targets.total}: pass ${r.targets.pass} exempt ${r.targets.exempt} FAIL ${r.targets.fail}`)
console.log(JSON.stringify(summary))
process.exit(serious.length || summary.undersizedWithoutExemption || summary.forcedColorsFindings.length ? 1 : 0)
