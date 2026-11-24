import { expect, type Page } from '@playwright/test'
import { account } from './fixture'

/** Collects console errors/warnings, failed requests and 4xx/5xx responses; `expectClean` asserts none (minus `allow` URL patterns). */
export function watch(page: Page) {
  const problems: string[] = []
  page.on('console', m => { if (['error', 'warning'].includes(m.type())) problems.push(`console.${m.type()}: ${m.text()} @ ${m.location().url}`) })
  page.on('pageerror', e => problems.push(`pageerror: ${e.message}`))
  page.on('requestfailed', r => { if (r.failure()?.errorText !== 'net::ERR_ABORTED') problems.push(`requestfailed: ${r.method()} ${r.url()} ${r.failure()?.errorText}`) })
  page.on('response', r => { if (r.status() >= 400) problems.push(`http ${r.status()}: ${r.request().method()} ${r.url()}`) })
  return { problems, expectClean(allow: RegExp[] = []) { expect(problems.filter(p => !allow.some(a => a.test(p)))).toEqual([]) }, reset() { problems.length = 0 } }
}

export async function signIn(page: Page, login = account.login, password = account.password) {
  await page.goto('/login')
  await page.locator('input[type="text"]').first().fill(login)
  await page.locator('input[type="password"]').fill(password)
  await page.locator('button[type="submit"]').click()
  await page.waitForURL(u => u.pathname !== '/login')
}

export const tenantPath = (tenantId: string, p = '') => `/tenants/${tenantId}${p ? `/${p}` : ''}`
