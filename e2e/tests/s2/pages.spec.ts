import { expect, test } from '@playwright/test'
import { noHorizontalOverflow, s, signInPlatform, signInTenant, watch } from './helpers'

const tenantPaths = ['storage', 'storage/connections/new', 'settings', 'settings/recovery-key', 'settings/bandwidth']
const platformPaths = ['platform/storage', 'platform/storage/connections', 'platform/notification-transports', 'platform/agent-settings', 'platform/quota-policies']

for (const vp of [{ name: 'desktop', width: 1280, height: 800 }, { name: 'mobile', width: 375, height: 700 }]) {
  test.describe(`${vp.name} crawl`, () => {
    test.use({ viewport: { width: vp.width, height: vp.height } })

    for (const p of tenantPaths) {
      test(`tenant /${p}: loads clean, no overflow, survives reload`, async ({ page }) => {
        test.fail(p === 'storage', 'mtsarac/backup-platform#247: /capacity 404 per connection row')
        await signInTenant(page)
        const problems = watch(page) // after sign-in: the pre-auth /api/me 401 is tracked in #239/#240
        await page.goto(`/tenants/${s.tenantId}/${p}`)
        await expect(page.getByRole('heading').first()).toBeVisible()
        await page.waitForLoadState('networkidle')
        await noHorizontalOverflow(page)
        await page.reload()
        await expect(page.getByRole('heading').first()).toBeVisible()
        await page.waitForLoadState('networkidle')
        expect(problems).toEqual([])
      })
    }
    for (const p of platformPaths) {
      test(`platform /${p}: loads clean, no overflow, survives reload`, async ({ page }) => {
        await signInPlatform(page)
        const problems = watch(page)
        await page.goto(`/${p}`)
        await expect(page.getByRole('heading').first()).toBeVisible()
        await page.waitForLoadState('networkidle')
        await noHorizontalOverflow(page)
        await page.reload()
        await expect(page.getByRole('heading').first()).toBeVisible()
        await page.waitForLoadState('networkidle')
        expect(problems).toEqual([])
      })
    }
  })
}

test('tenant user cannot open platform pages', async ({ page }) => {
  await signInTenant(page)
  for (const p of platformPaths) {
    await page.goto(`/${p}`)
    await expect(page.getByText('Bu alan yalnızca platform yöneticileri içindir.')).toBeVisible()
  }
})

test('unauthenticated tenant pages redirect to login', async ({ page }) => {
  for (const p of tenantPaths) {
    await page.goto(`/tenants/${s.tenantId}/${p}`)
    await expect(page).toHaveURL(/\/login/)
  }
})

test('other tenant id is not accessible', async ({ page }) => {
  await signInTenant(page)
  await page.goto('/tenants/00000000-0000-0000-0000-000000000001/storage')
  await expect(page.getByText(/bulunamadı|erişim|yetki|not found|forbidden/i).first()).toBeVisible()
})
