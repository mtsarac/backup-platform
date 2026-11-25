import { expect, test } from '@playwright/test'
import {
  axeViolations,
  DESKTOP,
  MOBILE,
  noHorizontalOverflow,
  s,
  seedIsolatedTenant,
  signInAs,
  signInPlatform,
  signInTenant,
  watch,
  type IsolatedTenant,
} from './helpers'

const tenantPaths = [
  'storage',
  'storage/connections/new',
  'settings',
  'settings?tab=channels',
  'settings?tab=deliveries',
  'settings/recovery-key',
  'settings/bandwidth',
  'settings/bandwidth?tab=agents',
  'settings/bandwidth?tab=jobs',
  'settings/bandwidth?tab=profiles',
]

const platformPaths = [
  'platform/storage',
  'platform/storage/connections',
  'platform/notification-transports',
  'platform/agent-settings',
  'platform/quota-policies',
]

let crawlTenant: IsolatedTenant

test.beforeAll(async () => {
  crawlTenant = await seedIsolatedTenant('crawl')
})
test.afterAll(async () => {
  await crawlTenant?.api.dispose()
})

for (const vp of [
  { name: 'desktop', ...DESKTOP },
  { name: 'mobile', ...MOBILE },
]) {
  test.describe(`${vp.name} crawl & a11y`, () => {
    test.use({ viewport: { width: vp.width, height: vp.height } })

    for (const p of tenantPaths) {
      test(`tenant /${p}: loads clean, axe clean, no overflow, survives reload`, async ({ page }) => {
        await signInAs(page, crawlTenant.account)
        const problems = watch(page)
        await page.goto(`/tenants/${crawlTenant.tenantId}/${p}`)
        await expect(page.getByRole('heading').first()).toBeVisible()
        await page.waitForLoadState('networkidle')
        await noHorizontalOverflow(page)
        if (vp.name === 'desktop') expect(await axeViolations(page), `axe on /${p}`).toEqual([])
        await page.reload()
        await expect(page.getByRole('heading').first()).toBeVisible()
        await page.waitForLoadState('networkidle')
        expect(problems).toEqual([])
      })
    }

    for (const p of platformPaths) {
      test(`platform /${p}: loads clean, axe clean, no overflow, survives reload`, async ({ page }) => {
        await signInPlatform(page)
        const problems = watch(page)
        await page.goto(`/${p}`)
        await expect(page.getByRole('heading').first()).toBeVisible()
        await page.waitForLoadState('networkidle')
        await noHorizontalOverflow(page)
        if (vp.name === 'desktop') expect(await axeViolations(page), `axe on /${p}`).toEqual([])
        await page.reload()
        await expect(page.getByRole('heading').first()).toBeVisible()
        await page.waitForLoadState('networkidle')
        expect(problems).toEqual([])
      })
    }
  })
}

test.describe('guards, navigation, keyboard, error recovery and TR/EN consistency (#228 checklist)', () => {
  test('tenant user cannot open platform pages', async ({ page }) => {
    await signInTenant(page)
    for (const p of platformPaths) {
      await page.goto(`/${p}`)
      await expect(page.getByText('Bu alan yalnızca platform yöneticileri içindir.')).toBeVisible()
    }
  })

  test('unauthenticated tenant and platform pages redirect to login', async ({ page }) => {
    for (const p of ['storage', 'settings', 'settings/recovery-key', 'settings/bandwidth']) {
      await page.goto(`/tenants/${s.tenantId}/${p}`)
      await expect(page).toHaveURL(/\/login/)
    }
    for (const p of ['platform/storage', 'platform/storage/connections', 'platform/notification-transports']) {
      await page.goto(`/${p}`)
      await expect(page).toHaveURL(/\/login/)
    }
  })

  test('other tenant id is not accessible on storage and settings', async ({ page }) => {
    await signInTenant(page)
    for (const p of ['storage', 'settings']) {
      await page.goto(`/tenants/00000000-0000-0000-0000-000000000001/${p}`)
      await expect(page.getByText(/bulunamadı|erişim|yetki|not found|forbidden/i).first()).toBeVisible()
    }
  })

  test('legacy /platform/storage-connections redirects to /platform/storage/connections with query preserved', async ({ page }) => {
    await signInPlatform(page)
    await page.goto('/platform/storage-connections?ownership=all')
    await expect(page).toHaveURL(/\/platform\/storage\/connections\?ownership=all$/)
    await expect(page.getByRole('heading', { name: 'Depolama' })).toBeVisible()
  })

  test('browser back and forward preserve route tabs and query tabs cleanly', async ({ page }) => {
    await signInTenant(page)
    const problems = watch(page)
    await page.goto(`/tenants/${s.tenantId}/settings`)
    await expect(page.getByRole('heading', { name: 'Bildirim politikası' })).toBeVisible()

    await page.getByRole('tab', { name: 'Kanallar' }).click()
    await expect(page).toHaveURL(/tab=channels/)
    await page.getByRole('link', { name: 'Kurtarma anahtarı' }).click()
    await expect(page).toHaveURL(/\/settings\/recovery-key$/)
    await page.getByRole('link', { name: 'Yükleme bant genişliği' }).click()
    await expect(page).toHaveURL(/\/settings\/bandwidth$/)

    await page.goBack()
    await expect(page).toHaveURL(/\/settings\/recovery-key$/)
    await page.goBack()
    await expect(page).toHaveURL(/\/settings\?tab=channels$/)
    await expect(page.getByRole('heading', { name: 'Kanallar' })).toBeVisible()
    await page.goForward()
    await expect(page).toHaveURL(/\/settings\/recovery-key$/)
    expect(problems).toEqual([])
  })

  test('keyboard navigation: tablist arrow keys and Escape dismissal on dialogs', async ({ page }) => {
    await signInTenant(page)
    const problems = watch(page)
    await page.goto(`/tenants/${s.tenantId}/settings`)
    const policyTab = page.getByRole('tab', { name: 'Politika' })
    await policyTab.focus()
    await page.keyboard.press('ArrowRight')
    await expect(page.getByRole('tab', { name: 'Kanallar' })).toHaveAttribute('aria-selected', 'true')
    await expect(page.getByRole('heading', { name: 'Kanallar' })).toBeVisible()

    await page.getByRole('button', { name: 'Kanal ekle' }).first().click()
    await expect(page.getByRole('dialog')).toBeVisible()
    expect(await axeViolations(page)).toEqual([])
    await page.keyboard.press('Escape')
    await expect(page.getByRole('dialog')).toBeHidden()
    expect(problems).toEqual([])
  })

  test('mobile navigation menu opens and navigates between S2 screens without overflow', async ({ page }) => {
    await page.setViewportSize(MOBILE)
    await signInTenant(page)
    const problems = watch(page)
    await page.goto(`/tenants/${s.tenantId}/storage`)
    await page.getByRole('button', { name: 'Menüyü aç' }).click()
    await page.getByRole('link', { name: 'Ayarlar', exact: true }).click()
    await expect(page).toHaveURL(new RegExp(`/tenants/${s.tenantId}/settings$`))
    await expect(page.getByRole('heading', { name: 'Ayarlar' })).toBeVisible()
    await noHorizontalOverflow(page)
    expect(problems).toEqual([])
  })

  test('TR/EN language switcher updates S2 tenant and platform screens consistently and persists across reload', async ({ page }) => {
    await signInTenant(page)
    const problems = watch(page)
    await page.goto(`/tenants/${s.tenantId}/storage`)
    await expect(page.getByRole('heading', { name: 'Depolama', exact: true })).toBeVisible()

    await page.getByRole('button', { name: /Dili değiştir/ }).click()
    await expect(page.getByRole('heading', { name: 'Storage', exact: true })).toBeVisible()
    await page.reload()
    await expect(page.getByRole('heading', { name: 'Storage', exact: true })).toBeVisible()

    await page.goto(`/tenants/${s.tenantId}/settings`)
    await expect(page.getByRole('heading', { name: 'Settings', exact: true })).toBeVisible()
    await expect(page.getByRole('tab', { name: 'Channels' })).toBeVisible()
    await expect(page.getByRole('tab', { name: 'Delivery history' }).or(page.getByRole('tab', { name: /Deliver/i }))).toBeVisible()

    await page.getByRole('button', { name: /Change language|Dili değiştir/i }).click()
    await expect(page.getByRole('heading', { name: 'Ayarlar', exact: true })).toBeVisible()
    expect(problems).toEqual([])

    await signInPlatform(page)
    await page.goto('/platform/notification-transports')
    await expect(page.getByRole('heading', { name: 'Bildirim altyapısı' })).toBeVisible()
    await page.getByRole('button', { name: /Dili değiştir/ }).click()
    await expect(page.getByRole('heading', { name: 'Notification infrastructure' })).toBeVisible()
    await page.getByRole('button', { name: /Change language|Dili değiştir/i }).click()
    await expect(page.getByRole('heading', { name: 'Bildirim altyapısı' })).toBeVisible()
  })

  test('session expiry mid-session redirects to login cleanly', async ({ page }) => {
    await signInTenant(page)
    await page.goto(`/tenants/${s.tenantId}/settings`)
    await expect(page.getByRole('heading', { name: 'Ayarlar' })).toBeVisible()
    await page.context().clearCookies()
    await page.reload()
    await expect(page).toHaveURL(/\/login/)
  })

  test('failed network request (500) on /storage renders recoverable error state and retry succeeds', async ({ page }) => {
    await signInTenant(page)
    let failNext = true
    await page.route(`**/api/tenants/${s.tenantId}/storage-connections`, async (route) => {
      if (failNext) {
        await route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ error: 'unexpected' }) })
      } else {
        await route.continue()
      }
    })
    await page.goto(`/tenants/${s.tenantId}/storage`)
    const retry = page.getByRole('button', { name: /Tekrar dene|Yeniden dene/i }).first()
    await expect(retry).toBeVisible()
    failNext = false
    await retry.click()
    await expect(page.getByRole('heading', { name: 'Depolama bağlantıları' })).toBeVisible()
  })
})
