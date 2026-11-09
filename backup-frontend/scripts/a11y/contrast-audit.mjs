#!/usr/bin/env node
// Language-switcher contrast audit in real Chromium (playwright-core + axe-core).
//
//   bun scripts/a11y/contrast-audit.mjs --base-url http://localhost:5173 --label after
//
// Measured per surface x condition x language, in three states: default, mouse hover, keyboard focus (Tab).
//   - text: computed colour (with inherited opacity) composited over the real ancestor background vs that background,
//     for the active label, the inactive label and the separator; threshold 4.5:1;
//   - focus indicator: computed outline colour vs the background it is drawn on; threshold 3:1 (WCAG 1.4.11 / 2.4.11);
//   - axe-core `color-contrast` on the switcher (not trusted under forced-colors, see README).
// All colour maths lives in lib/color.cjs; every channel must be 0..255 and every ratio 1..21, otherwise the
// run fails and nothing is written (see validateDeep below).
//
// Env for the authenticated surfaces (login page needs nothing):
//   A11Y_STATE_FILE  JSON with { a: { login, password, tenantId } } (lab state.json shape), or
//   A11Y_LOGIN / A11Y_PASSWORD / A11Y_TENANT_ID
// Output: results/contrast.json, one entry per --label; exits 1 if any measurement fails or is invalid.
import { readFileSync, writeFileSync, existsSync, mkdirSync } from 'node:fs'
import { createRequire } from 'node:module'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { chromium } from 'playwright-core'
import axe from 'axe-core'

const here = dirname(fileURLToPath(import.meta.url))
const COLOR_LIB = resolve(here, 'lib/color.cjs')
const { validateDeep } = createRequire(import.meta.url)(COLOR_LIB)
const args = Object.fromEntries(process.argv.slice(2).reduce((acc, a, i, all) => (a.startsWith('--') ? [...acc, [a.slice(2), all[i + 1]]] : acc), []))
const baseUrl = (args['base-url'] ?? 'http://localhost:5173').replace(/\/$/, '')
const label = args.label ?? 'after'
const outFile = resolve(args.out ?? `${here}/results/contrast.json`)
const MIN_RATIO = 4.5
const MIN_FOCUS_RATIO = 3
const SWITCHER = 'button[aria-label^="Dili değiştir"], button[aria-label^="Change language"]'

const creds = (() => {
  if (process.env.A11Y_LOGIN) return { login: process.env.A11Y_LOGIN, password: process.env.A11Y_PASSWORD, tenantId: process.env.A11Y_TENANT_ID }
  if (process.env.A11Y_STATE_FILE) return JSON.parse(readFileSync(process.env.A11Y_STATE_FILE, 'utf8')).a
  return null
})()

// theme x forced-colors; hc = .hc class (A11y toolbar), dark = .dark class
const CONDITIONS = [
  { name: 'light', dark: false, hc: false, forced: false },
  { name: 'dark', dark: true, hc: false, forced: false },
  { name: 'light-high-contrast', dark: false, hc: true, forced: false },
  { name: 'dark-high-contrast', dark: true, hc: true, forced: false },
  { name: 'forced-colors-light', dark: false, hc: false, forced: true },
  { name: 'forced-colors-dark', dark: true, hc: false, forced: true },
]
const LANGUAGES = ['tr', 'en']

/** Runs in the page: computed-style contrast of each switcher label in the current (default / hover / focus) state. */
function measureLabels(selector) {
  const C = window.__a11yColor
  const button = document.querySelector(selector)
  if (!button) return null
  const buttonCs = getComputedStyle(button)
  return {
    state: { hover: button.matches(':hover'), focus: button.matches(':focus'), focusVisible: button.matches(':focus-visible') },
    buttonBackground: C.rgbString(C.backgroundOf(button).color),
    buttonColor: buttonCs.color,
    labels: [...button.querySelectorAll(':scope > span')].map((span) => {
      const cs = getComputedStyle(span)
      const opacity = C.inheritedOpacity(span)
      const { color: bg, hasImage } = C.backgroundOf(span)
      const effective = C.over(C.withOpacity(C.parse(cs.color), opacity), bg)
      return {
        text: span.textContent, ariaCurrent: span.getAttribute('aria-current'),
        color: cs.color, opacity: Number(opacity.toFixed(3)),
        fontWeight: cs.fontWeight, textDecorationLine: cs.textDecorationLine, fontSize: cs.fontSize,
        foreground: C.rgbString(effective), background: C.rgbString(bg), backgroundHasImage: hasImage,
        ratio: C.ratio(effective, bg),
      }
    }),
  }
}

/** Runs in the page: the focus outline against the background it is drawn on (the area around the button). */
function measureFocusIndicator(selector) {
  const C = window.__a11yColor
  const button = document.querySelector(selector)
  const cs = getComputedStyle(button)
  const width = parseFloat(cs.outlineWidth)
  const visible = cs.outlineStyle !== 'none' && width > 0
  const { color: around } = C.backgroundOf(button.parentElement)
  const outline = C.parse(cs.outlineColor)
  const drawn = C.over(outline, around)
  return {
    outlineStyle: cs.outlineStyle, outlineWidth: cs.outlineWidth, outlineOffset: cs.outlineOffset, outlineColor: cs.outlineColor,
    visible, indicator: C.rgbString(drawn), adjacentBackground: C.rgbString(around),
    // the button's own fill, for reference: the 2 px offset gap keeps the ring off it
    buttonBackground: C.rgbString(C.backgroundOf(button).color),
    ratio: C.ratio(drawn, around),
  }
}

async function axeSwitcher(page) {
  await page.addScriptTag({ content: axe.source })
  return page.evaluate(async (selector) => {
    document.querySelector(selector).setAttribute('data-a11y-switcher', '')
    const pick = (n) => ({ target: n.target, ...(n.any?.[0]?.data ? { fgColor: n.any[0].data.fgColor, bgColor: n.any[0].data.bgColor, contrastRatio: n.any[0].data.contrastRatio } : {}) })
    const run = await window.axe.run({ include: [['[data-a11y-switcher]']] }, { runOnly: ['color-contrast'], resultTypes: ['violations', 'passes', 'incomplete'] })
    return {
      violations: run.violations.flatMap((r) => r.nodes.map(pick)),
      passes: run.passes.flatMap((r) => r.nodes.map(pick)),
      incomplete: run.incomplete.flatMap((r) => r.nodes.map(pick)),
    }
  }, SWITCHER)
}

async function axePage(page, tags) {
  return page.evaluate(async (tags) => {
    const run = await window.axe.run(document, { runOnly: { type: 'tag', values: tags }, resultTypes: ['violations'] })
    return run.violations.map((v) => ({ id: v.id, impact: v.impact, nodes: v.nodes.length, firstTarget: v.nodes[0]?.target }))
  }, tags)
}

const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH ?? '/usr/bin/chromium', headless: true })
const results = []
let failed = false

async function newContext(cond, lang, storageState) {
  const context = await browser.newContext({
    ignoreHTTPSErrors: true, viewport: { width: 1280, height: 800 }, storageState,
    colorScheme: cond.dark ? 'dark' : 'light', forcedColors: cond.forced ? 'active' : 'none',
  })
  await context.addInitScript(({ cond, lang }) => {
    localStorage.setItem('bp_language', lang)
    localStorage.setItem('backup-theme', cond.dark ? 'dark' : 'light')
    localStorage.setItem('backup-high-contrast', cond.hc ? '1' : '0')
  }, { cond, lang })
  await context.addInitScript({ path: COLOR_LIB })
  return context
}

// One login, reused as storage state for every authenticated condition.
let storageState
if (creds) {
  const context = await newContext(CONDITIONS[0], 'tr')
  const page = await context.newPage()
  await page.goto(`${baseUrl}/login`)
  await page.locator('input[autocomplete="username"]').fill(creds.login)
  await page.locator('input[type="password"]').fill(creds.password)
  await page.keyboard.press('Enter')
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 20000 })
  storageState = await context.storageState()
  await context.close()
}

const surfaces = [{ name: 'login', path: '/login', auth: false }]
if (creds) {
  surfaces.push({ name: 'tenant-select', path: '/', auth: true })
  if (creds.tenantId) surfaces.push({ name: 'dashboard', path: `/tenants/${creds.tenantId}/`, auth: true })
}

/** Keyboard-focus the switcher the way a user does (Tab), so :focus-visible matches. */
async function tabToSwitcher(page) {
  await page.mouse.move(0, 0)
  await page.evaluate(() => { document.activeElement?.blur?.() })
  for (let i = 0; i < 80; i++) {
    await page.keyboard.press('Tab')
    if (await page.evaluate((s) => document.activeElement?.matches(s), SWITCHER)) return true
  }
  return false
}

for (const surface of surfaces) for (const cond of CONDITIONS) for (const lang of LANGUAGES) {
  const context = await newContext(cond, lang, surface.auth ? storageState : undefined)
  const page = await context.newPage()
  await page.goto(`${baseUrl}${surface.path}`)
  await page.locator(SWITCHER).first().waitFor({ timeout: 20000 })
  await page.evaluate(() => document.fonts.ready)
  const box = await page.locator(SWITCHER).first().boundingBox()
  const entry = {
    surface: surface.name, condition: cond.name, language: lang,
    forcedColorsMatches: await page.evaluate(() => matchMedia('(forced-colors: active)').matches),
    rootClasses: await page.evaluate(() => document.documentElement.className),
    buttonSize: box && { width: Math.round(box.width), height: Math.round(box.height) },
  }

  // default state: mouse parked away, nothing focused
  await page.mouse.move(0, 0)
  const base = await page.evaluate(measureLabels, SWITCHER)
  entry.labels = base.labels
  entry.axe = { switcherContrast: await axeSwitcher(page), pageViolations: await axePage(page, ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']) }
  entry.axeReliable = !cond.forced

  // hover (desktop pointer); wait out the 150 ms colour transition
  await page.locator(SWITCHER).first().hover()
  await page.waitForTimeout(400)
  const hover = await page.evaluate(measureLabels, SWITCHER)
  entry.hover = { ...hover, axe: await axeSwitcher(page) }

  // keyboard focus via Tab, pointer away
  const reached = await tabToSwitcher(page)
  await page.waitForTimeout(400)
  const focus = reached ? await page.evaluate(measureLabels, SWITCHER) : null
  entry.focus = reached ? { ...focus, indicator: await page.evaluate(measureFocusIndicator, SWITCHER), axe: await axeSwitcher(page) } : { reachedByTab: false }

  const textRatios = [...entry.labels, ...entry.hover.labels, ...(entry.focus.labels ?? [])].map((l) => l.ratio)
  entry.minRatio = Math.min(...textRatios)
  entry.minRatioByState = { default: Math.min(...entry.labels.map((l) => l.ratio)), hover: Math.min(...entry.hover.labels.map((l) => l.ratio)), focus: entry.focus.labels ? Math.min(...entry.focus.labels.map((l) => l.ratio)) : null }
  entry.focusIndicatorRatio = entry.focus.indicator?.ratio ?? null
  const axeClean = cond.forced || [entry.axe.switcherContrast, entry.hover.axe, entry.focus.axe].every((a) => !a || a.violations.length === 0)
  entry.pass = Boolean(reached) && entry.minRatio >= MIN_RATIO && entry.focus.indicator.visible && entry.focusIndicatorRatio >= MIN_FOCUS_RATIO && axeClean
  if (!entry.pass) failed = true
  results.push(entry)
  await context.close()
}
await browser.close()

const problems = validateDeep(results)
if (problems.length) {
  console.error(`INVALID MEASUREMENTS (${problems.length}), results not written:\n${problems.slice(0, 20).join('\n')}`)
  process.exit(1)
}

const previous = existsSync(outFile) ? JSON.parse(readFileSync(outFile, 'utf8')) : {}
previous[label] = { baseUrl, measuredAt: new Date().toISOString(), minRequiredRatio: MIN_RATIO, minRequiredFocusIndicatorRatio: MIN_FOCUS_RATIO, results }
mkdirSync(dirname(outFile), { recursive: true })
writeFileSync(outFile, `${JSON.stringify(previous, null, 2)}\n`)

for (const r of results) console.log(`${r.pass ? 'PASS' : 'FAIL'} ${r.surface.padEnd(13)} ${r.condition.padEnd(20)} ${r.language} default ${r.minRatioByState.default} hover ${r.minRatioByState.hover} focus ${r.minRatioByState.focus} ring ${r.focusIndicatorRatio} | ${r.labels.map((l) => `${l.text.trim()}:${l.ratio}`).join(' ')}`)
console.log(`${label}: ${results.length} measurements -> ${outFile}`)
process.exit(failed ? 1 : 0)
