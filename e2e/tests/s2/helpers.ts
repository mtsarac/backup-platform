import { expect, type Page } from '@playwright/test'
import { state } from '../../state'

export const s = state()
export const rand = () => Math.random().toString(36).slice(2, 7)
export const uniq = (what: string) => `e2e-s2-${what}-${rand()}`

export async function signIn(page: Page, login: string, password: string) {
  await page.goto('/login')
  await page.locator('input[autocomplete="username"], input[type="text"]').first().fill(login)
  await page.locator('input[type="password"]').fill(password)
  await page.locator('button[type="submit"]').click()
  await page.waitForURL((u) => u.pathname !== '/login')
}
export const signInTenant = (p: Page) => signIn(p, s.accounts.userAdmin.login, s.accounts.userAdmin.password)
export const signInPlatform = (p: Page) => signIn(p, s.accounts.platformAdmin.login, s.accounts.platformAdmin.password)

/** Collects console errors, page errors and failed/5xx API responses. */
export function watch(page: Page) {
  const problems: string[] = []
  page.on('console', (m) => { if (m.type() === 'error') problems.push(`console: ${m.text()}`) })
  page.on('pageerror', (e) => problems.push(`pageerror: ${e.message}`))
  page.on('requestfailed', (r) => { if (!r.failure()?.errorText.includes('ABORTED')) problems.push(`requestfailed: ${r.url()} ${r.failure()?.errorText}`) })
  page.on('response', (r) => { if (r.status() >= 400) problems.push(`http ${r.status()} ${r.request().method()} ${r.url()}`) })
  return problems
}

export async function noHorizontalOverflow(page: Page) {
  const o = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)
  expect(o, 'horizontal overflow px').toBeLessThanOrEqual(1)
}

import { Api } from '../../api'
import { execSync } from 'node:child_process'

export const tenantApi = () => Api.signIn(s.accounts.userAdmin)
export const platformApi = () => Api.signIn(s.accounts.platformAdmin)

/** Deletes tenant storage connections whose name starts with `e2e-s2-`. */
export async function cleanConnections() {
  const api = await tenantApi()
  const list = await api.call('GET', `/api/tenants/${s.tenantId}/storage-connections`)
  for (const c of list.items.filter((i: any) => i.name.startsWith('e2e-s2-') || i.name === 'SFTP (192.168.122.1)'))
    await api.call('DELETE', `/api/tenants/${s.tenantId}/storage-connections/${c.id}`, undefined, [200, 204, 404, 409])
  await api.dispose()
}

export const sftpFingerprint = () =>
  execSync(`ssh-keyscan -p 2222 192.168.122.1 2>/dev/null | grep ed25519 | ssh-keygen -lf - | awk '{print $2}'`, { shell: '/bin/bash' }).toString().trim()
