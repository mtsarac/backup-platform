import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { Api, enableAgentEnrollment, ensureTenant, ensureTenantUser, ensureUnlimitedPlan } from '../../api'
import { state } from '../../state'
import { activeAgent, password, signIn, watchErrors, type Active } from './support'

function rows(value: unknown): { id: string; status?: string; configuration?: { engine?: string } }[] {
  if (Array.isArray(value)) return value
  if (value && typeof value === 'object' && 'items' in value && Array.isArray(value.items)) return value.items
  return []
}

/** Screens of the Windows agent flows; detail screens get real ids seeded by the S4 specs. */
async function screens(active: Active) {
  const api = await Api.signIn({ login: active.login, password })
  const base = `/api/tenants/${active.tenantId}`
  const [jobs, runs, restores, instances] = await Promise.all([
    'backup-jobs',
    'backup-runs?status=Succeeded&pageSize=10',
    'restores?pageSize=1',
    `discovery/instances?agentId=${active.agentId}`,
  ].map(path => api.call('GET', `${base}/${path}`)))
  await api.dispose()
  const root = `/tenants/${active.tenantId}`
  const succeededRun = rows(runs)[0]?.id
  const instanceId = rows(instances)[0]?.id
  return [
    `${root}`,
    `${root}/agents`,
    `${root}/agents/${active.agentId}`,
    `${root}/agents/databases`,
    ...(instanceId ? [`${root}/discovery/instances/${instanceId}`] : []),
    `${root}/enrollment`,
    `${root}/first-backup`,
    `${root}/backup-jobs`,
    `${root}/backup-jobs/new`,
    ...(rows(jobs)[0] ? [`${root}/backup-jobs/${rows(jobs)[0].id}`] : []),
    `${root}/backup-runs`,
    ...(succeededRun ? [`${root}/backup-runs/${succeededRun}`, `${root}/backup-runs/${succeededRun}/restore`] : []),
    `${root}/restores`,
    ...(rows(restores)[0] ? [`${root}/restores/${rows(restores)[0].id}`] : []),
    `${root}/backup-shares`,
  ]
}

async function inspect(page: Page, path: string, errors: string[]) {
  await page.goto(path)
  await page.waitForLoadState('networkidle')
  await expect.soft(page.getByRole('main'), path).toBeVisible()
  const violations = (await new AxeBuilder({ page }).analyze()).violations.filter(item => item.impact === 'serious' || item.impact === 'critical')
  expect.soft(violations.map(item => `${item.id}: ${item.nodes[0]?.target}`), `${path} accessibility`).toEqual([])
  expect.soft(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), `${path} horizontal overflow`).toBe(true)
  await page.reload()
  await expect.soft(page, `${path} after reload`).toHaveURL(new RegExp(`${path.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}$`))
  // #288: the discovery screen's rescan answers 429 inside the cooldown; the console error surfaces on whichever screen is open then.
  expect.soft(errors.filter(item => !/429|Too Many Requests/.test(item)), `${path} console and network`).toEqual([])
  errors.length = 0
}

for (const viewport of [{ name: 'desktop', width: 1440, height: 900 }, { name: 'mobile', width: 390, height: 844 }]) {
  test(`Windows agent screens pass the checklist on ${viewport.name}`, async ({ page }) => {
    test.setTimeout(360_000)
    const active = await activeAgent()
    const paths = await screens(active)
    await page.setViewportSize(viewport)
    await signIn(page, active)
    const errors = watchErrors(page)
    for (const path of paths) await inspect(page, path, errors)
  })
}

test('keyboard skip link, back/forward navigation, TR/EN language switch, restore validation and logout work cleanly', async ({ page }) => {
  test.setTimeout(180_000)
  const active = await activeAgent()
  const api = await Api.signIn({ login: active.login, password })
  const runs = await api.call('GET', `/api/tenants/${active.tenantId}/backup-runs?status=Succeeded&pageSize=20`)
  await api.dispose()
  const dbRun = rows(runs).find(item => item.configuration?.engine === 'postgres' || item.configuration?.engine === 'mssql')

  await signIn(page, active)
  const errors = watchErrors(page)
  const root = `/tenants/${active.tenantId}`

  await page.goto(`${root}/backup-jobs`)
  await page.waitForLoadState('networkidle')
  await page.keyboard.press('Tab')
  await expect(page.getByRole('link', { name: 'Ana içeriğe geç' })).toBeFocused()
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/#main$/)

  await page.goto(`${root}/backup-runs`)
  await page.goto(`${root}/restores`)
  await page.goto(`${root}/backup-shares`)
  await page.goBack()
  await expect(page).toHaveURL(new RegExp(`${root}/restores$`))
  await page.goBack()
  await expect(page).toHaveURL(new RegExp(`${root}/backup-runs$`))
  await page.goForward()
  await expect(page).toHaveURL(new RegExp(`${root}/restores$`))

  await page.getByRole('button', { name: /TR \/ EN/ }).click()
  await expect(page.locator('html')).toHaveAttribute('lang', 'en')
  await expect(page.getByRole('link', { name: 'Restores' })).toBeVisible()
  await page.goto(`${root}/agents/databases`)
  await expect(page.getByRole('heading', { name: 'Discovered databases' })).toBeVisible()
  await page.goto(`${root}/backup-shares`)
  await expect(page.getByRole('link', { name: 'Shares' })).toBeVisible()
  await page.getByRole('button', { name: /TR \/ EN/ }).click()
  await expect(page.locator('html')).toHaveAttribute('lang', 'tr')
  await expect(page.getByRole('link', { name: 'Paylaşımlar' })).toBeVisible()

  if (dbRun) {
    await page.goto(`${root}/backup-runs/${dbRun.id}/restore`)
    await page.getByLabel('Yeni Veritabanı Adı').fill('')
    await expect(page.getByRole('button', { name: 'Devam et →' })).toBeDisabled()
  }

  await page.goto('/account')
  await page.getByRole('button', { name: 'Çıkış' }).click()
  await expect(page).toHaveURL(/\/login/)
  await page.goto(`${root}/agents`)
  await expect(page).toHaveURL(/\/login/)
  expect(errors.filter(item => !item.includes('/api/me'))).toEqual([])
})

test('empty tenant shows guided empty states and unknown ids show not-found states', async ({ page }) => {
  const suffix = `${Date.now()}`
  const platform = await Api.signIn(state().accounts.platformAdmin)
  const account = { login: `e2e.s4.empty.${suffix}`, password }
  const tenantId = await ensureTenant(platform, `e2e-s4-empty-${suffix}`)
  const userId = await ensureTenantUser(platform, [tenantId], account)
  await platform.call('POST', `/api/platform/tenants/${tenantId}/memberships`, { userId }, [200, 201, 409])
  await ensureUnlimitedPlan(platform, tenantId, 'E2E Unlimited')
  await enableAgentEnrollment(platform, tenantId)
  await platform.dispose()
  await signIn(page, { tenantId, login: account.login, agentId: '' })
  await page.goto(`/tenants/${tenantId}/agents`)
  await expect(page.getByText('Henüz yedekleme bilgisayarı eklenmedi.')).toBeVisible()
  await page.goto(`/tenants/${tenantId}/backup-jobs`)
  await expect(page.getByText('Henüz yedekleme işi oluşturulmadı.')).toBeVisible()
  await page.goto(`/tenants/${tenantId}/backup-shares`)
  await expect(page.getByText(/Henüz paylaşım yok/)).toBeVisible()
  await page.goto(`/tenants/${tenantId}/backup-jobs/00000000-0000-0000-0000-000000000000`)
  await expect(page.getByText('Yedekleme işi bulunamadı.')).toBeVisible()
  await page.goto(`/tenants/${tenantId}/does-not-exist`)
  await expect(page.getByRole('main')).toBeVisible()
})

test('API failure shows a recoverable error and another tenant or /platform is not reachable', async ({ page }) => {
  const active = await activeAgent()
  await signIn(page, active)
  await page.route('**/api/tenants/*/backup-jobs', route => route.fulfill({ status: 500, contentType: 'application/json', body: '{"error":"internal"}' }))
  await page.goto(`/tenants/${active.tenantId}/backup-jobs`)
  await expect(page.getByRole('alert').first()).toBeVisible()
  await page.unroute('**/api/tenants/*/backup-jobs')
  await page.goto(`/tenants/${state().tenantId}/backup-jobs`)
  await expect(page.getByRole('heading', { name: 'Sayfa bulunamadı.' })).toBeVisible()
  await page.goto('/platform')
  await expect(page.getByRole('heading', { name: 'Bu alan için yetkiniz yok' })).toBeVisible()
})
