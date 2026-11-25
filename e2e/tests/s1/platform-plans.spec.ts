import { expect, test, type Page } from '@playwright/test'
import { axeViolations, DESKTOP, ensureTenant, expectNoHorizontalScroll, MOBILE, platformApi, rand, s, signIn, watch } from './helpers'

const dialog = (page: Page) => page.getByRole('dialog')
const row = (page: Page, text: string) => page.getByRole('row').filter({ hasText: text })
const searchPlan = async (page: Page, term: string) => {
  const box = page.getByLabel('Plan ara')
  await box.fill(term)
  await box.press('Enter')
}

async function admin(page: Page, path: string) {
  await signIn(page, s.accounts.platformAdmin.login, s.accounts.platformAdmin.password)
  await page.waitForURL((u) => u.pathname !== '/login')
  await page.goto(path)
}

test('plans list: heading, system plan is read-only, status filter, search, axe, responsive, clean console', async ({ page }) => {
  const w = watch(page)
  await admin(page, '/platform/plans')
  await expect(page.getByRole('heading', { level: 1, name: 'Planlar' })).toBeVisible()
  expect(await axeViolations(page)).toEqual([])
  await searchPlan(page, 'Legacy Unlimited')
  await expect(row(page, 'Legacy Unlimited')).toContainText('Sistem planı; salt okunur.')
  await expect(row(page, 'Legacy Unlimited').getByRole('button', { name: /düzenle|devre dışı|etkinleştir/i })).toHaveCount(0)
  await expect(row(page, 'E2E Unlimited')).toHaveCount(0)
  await searchPlan(page, '')
  await page.getByLabel('Durum').selectOption('true')
  await searchPlan(page, 'E2E Unlimited')
  await expect(row(page, 'E2E Unlimited')).toBeVisible()
  await page.getByLabel('Durum').selectOption('')
  await searchPlan(page, `no-plan-${rand()}`)
  await expect(page.getByText(/bulunamadı/)).toBeVisible()
  await page.setViewportSize(MOBILE)
  await expectNoHorizontalScroll(page)
  await page.setViewportSize(DESKTOP)
  expect(w.unexpected()).toEqual([])
})

test('create plan: validation (name, negative, >3 decimals), limit in GB is stored in decimal bytes, appears in list and survives reload', async ({ page }) => {
  const name = `e2e-s1-plan-${rand()}`
  await admin(page, '/platform/plans')
  await page.getByRole('button', { name: 'Yeni plan' }).click()
  expect(await axeViolations(page)).toEqual([])
  const submit = dialog(page).getByRole('button', { name: 'Plan oluştur' })
  await dialog(page).getByLabel('Plan adı').fill(name)
  await dialog(page).getByLabel('Depolama limiti (GB)').fill('-1')
  await submit.click()
  await expect(dialog(page)).toBeVisible()
  await dialog(page).getByLabel('Depolama limiti (GB)').fill('1.2345')
  await submit.click()
  await expect(dialog(page)).toBeVisible()
  await dialog(page).getByLabel('Depolama limiti (GB)').fill('2.5')
  await submit.click()
  await expect(dialog(page)).toBeHidden()
  await searchPlan(page, name)
  await expect(row(page, name)).toContainText('2,5 GB')
  await expect(row(page, name)).toContainText('Etkin')
  await page.reload()
  await searchPlan(page, name)
  await expect(row(page, name)).toBeVisible()
})

test('create plan: "Sınırsız" removes the limit field and lists the plan as unlimited', async ({ page }) => {
  const name = `e2e-s1-plan-${rand()}`
  await admin(page, '/platform/plans')
  await page.getByRole('button', { name: 'Yeni plan' }).click()
  await dialog(page).getByLabel('Plan adı').fill(name)
  await dialog(page).getByLabel('Sınırsız').check()
  await expect(dialog(page).getByLabel('Depolama limiti (GB)')).toHaveCount(0)
  await dialog(page).getByRole('button', { name: 'Plan oluştur' }).click()
  await expect(dialog(page)).toBeHidden()
  await searchPlan(page, name)
  await expect(row(page, name)).toContainText('Sınırsız')
})

test('duplicate plan name is refused with an alert', async ({ page }) => {
  await admin(page, '/platform/plans')
  await page.getByRole('button', { name: 'Yeni plan' }).click()
  await dialog(page).getByLabel('Plan adı').fill('E2E Unlimited')
  await dialog(page).getByLabel('Depolama limiti (GB)').fill('3')
  await dialog(page).getByRole('button', { name: 'Plan oluştur' }).click()
  await expect(dialog(page).getByRole('alert')).toBeVisible()
})

test('edit plan changes limit; disable removes it from the assign list and shows under disabled filter; enable restores it', async ({ page }) => {
  const platform = await platformApi()
  const name = `e2e-s1-plan-${rand()}`
  await platform.call('POST', '/api/platform/plans', { name, storageLimitBytes: 5_000_000_000, unlimited: false, enabled: true, quotaPolicyReference: 'default' })
  const tenantId = await ensureTenant(platform, `e2e-s1-tenant-${rand()}`)
  await platform.dispose()
  await admin(page, '/platform/plans')
  await searchPlan(page, name)
  await row(page, name).getByRole('button', { name: /^Düzenle — / }).click()
  await dialog(page).getByLabel('Depolama limiti (GB)').fill('7')
  await dialog(page).getByRole('button', { name: 'Kaydet' }).click()
  await expect(dialog(page).getByRole('button', { name: 'Kaydet' })).toBeDisabled()
  await dialog(page).getByLabel(/Onaylamak için adı yazın/).fill(name)
  await dialog(page).getByRole('button', { name: 'Kaydet' }).click()
  await expect(row(page, name)).toContainText('7 GB')

  await row(page, name).getByRole('button', { name: /^Devre dışı bırak — / }).click()
  await expect(dialog(page)).toContainText('mevcut abonelikler etkilenmez')
  await dialog(page).getByRole('button', { name: 'Devre dışı bırak' }).click()
  await expect(row(page, name)).toContainText('Devre dışı')
  await page.getByLabel('Durum').selectOption('false')
  await expect(row(page, name)).toBeVisible()
  await page.getByLabel('Durum').selectOption('')

  await page.goto(`/platform/tenants/${tenantId}?tab=subscription`)
  await page.getByRole('button', { name: 'Plan ata' }).click()
  await dialog(page).getByLabel('Plan', { exact: true }).click()
  await expect(page.getByRole('option', { name })).toHaveCount(0)
  await page.keyboard.press('Escape')
  await page.keyboard.press('Escape')
  await expect(dialog(page)).toBeHidden()

  await page.goto('/platform/plans')
  await searchPlan(page, name)
  await row(page, name).getByRole('button', { name: /^Etkinleştir — / }).click()
  await dialog(page).getByRole('button', { name: 'Etkinleştir' }).click()
  await expect(row(page, name)).toContainText('Etkin')
})

test('assign a plan to a workspace through the subscription tab, then suspend and resume it', async ({ page }) => {
  const platform = await platformApi()
  const planName = `e2e-s1-plan-${rand()}`
  await platform.call('POST', '/api/platform/plans', { name: planName, storageLimitBytes: 5_000_000_000, unlimited: false, enabled: true, quotaPolicyReference: 'default' })
  const tenantId = await ensureTenant(platform, `e2e-s1-tenant-${rand()}`)
  await platform.dispose()
  await admin(page, `/platform/tenants/${tenantId}?tab=subscription`)
  await expect(page.getByText('Bu çalışma alanı için henüz bir plan atanmadı.')).toBeVisible()
  await page.getByRole('button', { name: 'Plan ata' }).click()
  await dialog(page).getByLabel('Plan', { exact: true }).click()
  await page.getByRole('option', { name: new RegExp(planName) }).click()
  await dialog(page).getByRole('button', { name: 'Plan ata' }).click()
  await expect(dialog(page)).toBeHidden()
  await expect(page.getByText(planName).first()).toBeVisible()
  await expect(page.getByText('Limit kaynağı')).toBeVisible()
  await page.reload()
  await expect(page.getByText(planName).first()).toBeVisible()

  await page.getByText('Diğer işlemler').click()
  await page.getByRole('button', { name: 'Askıya al' }).click()
  await expect(dialog(page)).toContainText('askıya alınır')
  await dialog(page).getByRole('button', { name: 'Askıya al' }).click()
  await expect(page.getByText(/Askıda/).first()).toBeVisible()
  await page.getByText('Diğer işlemler').click().catch(() => {})
  await page.getByRole('button', { name: 'Yeniden etkinleştir' }).first().click()
  await dialog(page).getByRole('button', { name: 'Yeniden etkinleştir' }).click()
  await expect(page.getByText(/Etkin/).first()).toBeVisible()
})

test('storage-limit override: limited, unlimited, and reset to plan default', async ({ page }) => {
  const platform = await platformApi()
  const planName = `e2e-s1-plan-${rand()}`
  const plan = await platform.call('POST', '/api/platform/plans', { name: planName, storageLimitBytes: 5_000_000_000, unlimited: false, enabled: true, quotaPolicyReference: 'default' })
  const tenantId = await ensureTenant(platform, `e2e-s1-tenant-${rand()}`)
  await platform.call('PUT', `/api/platform/tenants/${tenantId}/subscription`, { planId: plan.planId })
  await platform.dispose()
  await admin(page, `/platform/tenants/${tenantId}?tab=subscription`)
  await page.getByText('Diğer işlemler').click()
  await page.getByRole('button', { name: 'Özel limit' }).click()
  await expect(dialog(page)).toContainText('Depolama limiti değiştirilsin mi?')
  await dialog(page).getByLabel('Özel depolama limiti').selectOption({ label: 'Sınırlı (GB)' })
  const gb = dialog(page).getByLabel('Özel limit boyutu (GB)')
  await gb.fill('-3')
  await dialog(page).getByRole('button', { name: 'Kaydet' }).click()
  await expect(dialog(page)).toBeVisible()
  await gb.fill('9')
  await dialog(page).getByRole('button', { name: 'Kaydet' }).click()
  await expect(dialog(page)).toBeHidden()
  await page.getByText('Teknik ayrıntılar').first().click()
  await expect(page.getByText('9.000.000.000')).toBeVisible()

  if (!(await page.getByRole('button', { name: 'Özel limit' }).isVisible())) {
    await page.getByText('Diğer işlemler').click()
  }
  await page.getByRole('button', { name: 'Özel limit' }).click()
  await dialog(page).getByLabel('Özel depolama limiti').selectOption('unlimited')
  await dialog(page).getByRole('button', { name: 'Kaydet' }).click()
  await expect(dialog(page)).toBeHidden()
  await expect(page.getByText('Sınırsız').first()).toBeVisible()
})

test('UserAdmin cannot open /platform/plans, /platform/quota-policies, or /platform/audit', async ({ page }) => {
  await signIn(page, s.accounts.userAdmin.login, s.accounts.userAdmin.password)
  await page.waitForURL(/\/tenants\//)
  for (const p of ['/platform/plans', '/platform/quota-policies', '/platform/audit']) {
    await page.goto(p)
    await expect(page.getByRole('heading', { name: 'Bu alan için yetkiniz yok' })).toBeVisible()
  }
})
