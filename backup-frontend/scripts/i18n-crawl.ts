/**
 * Browser language audit. Signs in, visits the main pages in Turkish and English, saves a screenshot
 * of each and prints visible words that belong to the other language.
 *
 *   BASE=http://127.0.0.1:5199 LOGIN=admin PASSWORD=... bun scripts/i18n-crawl.ts [outDir]
 */
import { chromium } from 'playwright-core'
import { mkdirSync } from 'node:fs'
import { sourceCatalog } from '../src/lib/i18n/source-catalog'

const base = process.env.BASE ?? 'http://127.0.0.1:5199'
const out = process.argv[2] ?? 'i18n-shots'
mkdirSync(out, { recursive: true })
const words = (text: string) => text.toLowerCase().match(/[a-zçğıöşü]{4,}/g) ?? []
const trWords = new Set<string>(), enWords = new Set<string>()
for (const entry of Object.values(sourceCatalog)) { words(entry.tr).forEach(w => trWords.add(w)); words(entry.en).forEach(w => enWords.add(w)) }

const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH ?? '/usr/bin/chromium', args: ['--no-sandbox'] })
const findings: string[] = []
for (const lang of ['tr', 'en'] as const) {
  const context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1360, height: 900 } })
  await context.addInitScript(l => localStorage.setItem('bp_language', l), lang)
  const page = await context.newPage()
  await page.goto(`${base}/login`)
  await page.getByLabel(/kullanıcı|username|login|e-posta|email/i).first().fill(process.env.LOGIN ?? 'admin')
  await page.locator('input[type=password]').first().fill(process.env.PASSWORD ?? 'PlatformAdmin123!')
  await page.locator('button[type=submit]').first().click()
  await page.waitForURL(u => !u.pathname.startsWith('/login'))
  await page.waitForTimeout(1500)
  const tenants: { tenantId?: string; id?: string }[] = (await (await page.request.get(`${base}/api/platform/tenants?pageSize=5`)).json().catch(() => ({ items: [] }))).items ?? []
  const tenant = tenants[0]?.tenantId ?? tenants[0]?.id
  const paths = ['/', '/account/security', '/account/sessions', '/account/passkeys', '/platform/tenants', '/platform/users', '/platform/plans', '/platform/storage-connections', '/platform/quota-policies', '/platform/notification-transports', '/platform/agent-settings', '/platform/audit', '/platform/settings']
  if (tenant) for (const p of ['', '/agents', '/enrollment', '/backup-jobs', '/backup-jobs/new', '/backup-runs', '/restores', '/storage', '/storage/connections/new', '/shares', '/backup-shares', '/bandwidth', '/recovery-key', '/first-backup', '/settings']) paths.push(`/tenants/${tenant}${p}`)
  for (const path of paths) {
    await page.goto(`${base}${path}`).catch(() => undefined)
    await page.waitForLoadState('load').catch(() => undefined)
    await page.waitForTimeout(400)
    const shot = `${out}/${lang}-${path.replace(/[^a-z0-9]+/gi, '_') || 'home'}.png`
    await page.screenshot({ path: shot, fullPage: true, timeout: 8000 }).catch(() => page.screenshot({ path: shot, timeout: 8000 }).catch(() => undefined))
    const text = await page.evaluate(() => {
      const parts: string[] = [document.body.innerText]
      for (const el of document.querySelectorAll('[aria-label],[title],[placeholder],[alt]')) for (const a of ['aria-label', 'title', 'placeholder', 'alt']) { const v = el.getAttribute(a); if (v) parts.push(v) }
      return parts.join('\n')
    })
    const other = lang === 'tr' ? enWords : trWords, own = lang === 'tr' ? trWords : enWords
    const strange = [...new Set(words(text).filter(w => other.has(w) && !own.has(w)))]
    const raw = [...new Set(text.match(/\b[a-z]+[A-Z][A-Za-z]+\b|\b[a-z]+_[a-z_]+\b/g) ?? [])]
    if (strange.length || raw.length) findings.push(`${lang} ${path}: foreign=[${strange.join(', ')}] raw=[${raw.join(', ')}]`)
  }
  await context.close()
}
await browser.close()
console.log(findings.join('\n') || 'no findings')
