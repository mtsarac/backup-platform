#!/usr/bin/env node
// Language-switcher contrast audit in real Chromium (playwright-core + axe-core).
//
//   bun scripts/a11y/contrast-audit.mjs --base-url http://localhost:5199 --label after
//
// Env for the authenticated surfaces (login page needs nothing):
//   A11Y_STATE_FILE  JSON with { a: { login, password, tenantId } } (lab state.json shape), or
//   A11Y_LOGIN / A11Y_PASSWORD / A11Y_TENANT_ID
// Output: results/contrast.json, one entry per --label; exits 1 if any measured ratio < 4.5.
import { readFileSync, writeFileSync, existsSync, mkdirSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { chromium } from 'playwright-core'
import axe from 'axe-core'

const here = dirname(fileURLToPath(import.meta.url))
const args = Object.fromEntries(process.argv.slice(2).reduce((acc, a, i, all) => (a.startsWith('--') ? [...acc, [a.slice(2), all[i + 1]]] : acc), []))
const baseUrl = (args['base-url'] ?? 'http://localhost:5173').replace(/\/$/, '')
const label = args.label ?? 'after'
const outFile = resolve(args.out ?? `${here}/results/contrast.json`)
const MIN_RATIO = 4.5
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

/** Runs in the page: computed-style contrast of each switcher label, independent of axe. */
function measureInPage(selector) {
  const ctx = document.createElement('canvas').getContext('2d', { willReadFrequently: true })
  ctx.canvas.width = ctx.canvas.height = 1
  const rgba = (css) => { // any CSS colour (rgb, color-mix, oklch...) -> [r,g,b,a] 0-255/0-1
    ctx.clearRect(0, 0, 1, 1); ctx.fillStyle = '#000'; ctx.fillStyle = css; ctx.fillRect(0, 0, 1, 1)
    const d = ctx.getImageData(0, 0, 1, 1).data
    // premultiplied rounding is negligible; alpha from the pixel
    const a = d[3] / 255
    return [a ? d[0] / a : 0, a ? d[1] / a : 0, a ? d[2] / a : 0, a]
  }
  const over = (top, bottom) => { const a = top[3] + bottom[3] * (1 - top[3]); return a === 0 ? [0, 0, 0, 0] : [0, 1, 2].map((i) => (top[i] * top[3] + bottom[i] * bottom[3] * (1 - top[3])) / a).concat(a) }
  const lum = ([r, g, b]) => { const f = (v) => { v /= 255; return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4 }; return 0.2126 * f(r) + 0.7152 * f(g) + 0.0722 * f(b) }
  const ratio = (a, b) => { const [l1, l2] = [lum(a), lum(b)].sort((x, y) => y - x); return (l1 + 0.05) / (l2 + 0.05) }
  const rgbStr = (c) => `rgb(${c.slice(0, 3).map(Math.round).join(',')})`

  const button = document.querySelector(selector)
  if (!button) return null
  const bgOf = (el) => { // composite ancestor backgrounds, bottom = canvas (white if nothing opaque)
    const layers = []; let hasImage = false
    for (let n = el; n; n = n.parentElement) {
      const cs = getComputedStyle(n)
      if (cs.backgroundImage !== 'none') hasImage = true
      layers.push(rgba(cs.backgroundColor))
      if (rgba(cs.backgroundColor)[3] === 1) break
    }
    let acc = rgba(getComputedStyle(document.documentElement).backgroundColor)
    acc = over(acc, [255, 255, 255, 1])
    for (const l of layers.reverse()) acc = over(l, acc)
    return { color: acc, hasImage }
  }
  const spans = [...button.querySelectorAll(':scope > span')]
  return spans.map((span) => {
    const cs = getComputedStyle(span)
    let opacity = 1
    for (let n = span; n; n = n.parentElement) opacity *= Number(getComputedStyle(n).opacity)
    const fg = rgba(cs.color)
    const { color: bg, hasImage } = bgOf(span)
    const effective = over([fg[0], fg[1], fg[2], fg[3] * opacity], bg)
    return {
      text: span.textContent,
      ariaCurrent: span.getAttribute('aria-current'),
      color: cs.color, opacity: Number(opacity.toFixed(3)),
      fontWeight: cs.fontWeight, textDecorationLine: cs.textDecorationLine, fontSize: cs.fontSize,
      foreground: rgbStr(effective), background: rgbStr(bg), backgroundHasImage: hasImage,
      ratio: Number(ratio(effective, bg).toFixed(2)),
    }
  })
}

async function runAxe(page, tags) {
  await page.addScriptTag({ content: axe.source })
  return page.evaluate(async ({ selector, tags }) => {
    document.querySelector(selector).setAttribute('data-a11y-switcher', '')
    const pick = (n) => ({ target: n.target, ...(n.any?.[0]?.data ? { fgColor: n.any[0].data.fgColor, bgColor: n.any[0].data.bgColor, contrastRatio: n.any[0].data.contrastRatio } : {}) })
    const switcherRun = await window.axe.run({ include: [['[data-a11y-switcher]']] }, { runOnly: ['color-contrast'], resultTypes: ['violations', 'passes', 'incomplete'] })
    const page = await window.axe.run(document, { runOnly: { type: 'tag', values: tags }, resultTypes: ['violations'] })
    return {
      switcherContrast: {
        violations: switcherRun.violations.flatMap((r) => r.nodes.map(pick)),
        passes: switcherRun.passes.flatMap((r) => r.nodes.map(pick)),
        incomplete: switcherRun.incomplete.flatMap((r) => r.nodes.map(pick)),
      },
      pageViolations: page.violations.map((v) => ({ id: v.id, impact: v.impact, nodes: v.nodes.length, firstTarget: v.nodes[0]?.target })),
    }
  }, { selector: SWITCHER, tags })
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

for (const surface of surfaces) for (const cond of CONDITIONS) for (const lang of LANGUAGES) {
  const context = await newContext(cond, lang, surface.auth ? storageState : undefined)
  const page = await context.newPage()
  await page.goto(`${baseUrl}${surface.path}`)
  await page.locator(SWITCHER).first().waitFor({ timeout: 20000 })
  await page.evaluate(() => document.fonts.ready)
  const labels = await page.evaluate(measureInPage, SWITCHER)
  const box = await page.locator(SWITCHER).first().boundingBox()
  const entry = {
    surface: surface.name, condition: cond.name, language: lang,
    forcedColorsMatches: await page.evaluate(() => matchMedia('(forced-colors: active)').matches),
    rootClasses: await page.evaluate(() => document.documentElement.className),
    buttonSize: box && { width: Math.round(box.width), height: Math.round(box.height) },
    labels,
  }
  // axe does not model forced-colors system colours; its numbers there are recorded but not relied on.
  entry.axe = await runAxe(page, ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'])
  entry.axeReliable = !cond.forced
  entry.minRatio = Math.min(...labels.map((l) => l.ratio))
  entry.pass = entry.minRatio >= MIN_RATIO && (cond.forced || entry.axe.switcherContrast.violations.length === 0)
  if (!entry.pass) failed = true
  results.push(entry)
  await context.close()
}
await browser.close()

const previous = existsSync(outFile) ? JSON.parse(readFileSync(outFile, 'utf8')) : {}
previous[label] = { baseUrl, measuredAt: new Date().toISOString(), minRequiredRatio: MIN_RATIO, results }
mkdirSync(dirname(outFile), { recursive: true })
writeFileSync(outFile, `${JSON.stringify(previous, null, 2)}\n`)

for (const r of results) console.log(`${r.pass ? 'PASS' : 'FAIL'} ${r.surface.padEnd(13)} ${r.condition.padEnd(20)} ${r.language} ${r.labels.map((l) => `${l.text.trim()}:${l.ratio}`).join(' ')}`)
console.log(`${label}: ${results.length} measurements -> ${outFile}`)
process.exit(failed ? 1 : 0)
