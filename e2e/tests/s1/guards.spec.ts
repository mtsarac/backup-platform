import { expect, test } from '@playwright/test'
import { Api } from '../../api'
import { axeViolations, ensureTenant, expectNoHorizontalScroll, isIsolatedStack, ISOLATED_SKIP, MOBILE, newUser, platformApi, rand, resetIsolatedThrottle, s, signIn, watch } from './helpers'

let tenantB = ''
let tenantBName = ''
let single: Awaited<ReturnType<typeof newUser>>
let multi: Awaited<ReturnType<typeof newUser>>
let orphan: Awaited<ReturnType<typeof newUser>>

test.beforeAll(async () => {
  resetIsolatedThrottle()
  const platform = await platformApi()
  tenantBName = `e2e-s1-tenant-${rand()}`
  tenantB = await ensureTenant(platform, tenantBName)
  single = await newUser(platform)
  multi = await newUser(platform, { tenantIds: [s.tenantId, tenantB] })
  orphan = await newUser(platform, { tenantIds: [tenantB] })
  await platform.call('DELETE', `/api/platform/tenants/${tenantB}/memberships/${orphan.userId}`)
  await platform.dispose()
})
test.afterAll(() => {
  resetIsolatedThrottle()
})

test.describe('route guards', () => {
  test('signed-out visitors are sent to /login from every protected area', async ({ page }) => {
    for (const path of ['/', '/account', '/platform', '/platform/users', '/platform/quota-policies', `/tenants/${s.tenantId}`, `/tenants/${s.tenantId}/storage`]) {
      await page.goto(path)
      await expect(page, path).toHaveURL(/\/login/)
    }
  })

  test('UserAdmin: /platform* shows the permission wall with a way home; no platform link in the tenant UI', async ({ page }) => {
    await signIn(page, single.login, single.password)
    await page.waitForURL(/\/tenants\//)
    await expect(page.getByRole('link', { name: /Platform yönetimi/i })).toHaveCount(0)
    for (const path of ['/platform', '/platform/tenants', '/platform/users', '/platform/plans', '/platform/quota-policies', '/platform/audit']) {
      await page.goto(path)
      await expect(page.getByRole('heading', { name: 'Bu alan için yetkiniz yok' })).toBeVisible()
      await expect(page.getByRole('link', { name: 'Ana sayfaya dön' })).toBeVisible()
    }
    expect(await axeViolations(page)).toEqual([])
    await page.getByRole('link', { name: 'Ana sayfaya dön' }).click()
    await expect(page).toHaveURL(new RegExp(`/tenants/${s.tenantId}`))
  })

  test('UserAdmin: platform API answers 403 (UI wall is not the only guard)', async () => {
    const api = await Api.signIn(single)
    await expect(api.call('GET', '/api/platform/users')).rejects.toThrow(/403/)
    await expect(api.call('GET', '/api/platform/tenants')).rejects.toThrow(/403/)
    await expect(api.call('GET', '/api/platform/plans')).rejects.toThrow(/403/)
    await expect(api.call('GET', '/api/platform/quota-policies')).rejects.toThrow(/403/)
    await api.dispose()
  })

  test('tenant isolation: a member of one tenant cannot open another tenant, in UI or API', async ({ page }) => {
    await signIn(page, single.login, single.password)
    await page.waitForURL(/\/tenants\//)
    await page.goto(`/tenants/${tenantB}`)
    await expect(page.getByText(tenantBName)).toHaveCount(0)
    await expect(page.getByRole('heading', { level: 1, name: 'Sayfa bulunamadı.' })).toBeVisible()
    const api = await Api.signIn(single)
    for (const p of ['dashboard', 'agents', 'storage-connections']) {
      await expect(api.call('GET', `/api/tenants/${tenantB}/${p}`)).rejects.toThrow(/40[34]/)
    }
    await api.dispose()
  })

  test('tenant select lists every membership (2) and links work', async ({ page }) => {
    await signIn(page, multi.login, multi.password)
    await expect(page.getByRole('heading', { level: 1 })).toBeVisible()
    await expect(page).toHaveURL(/\/$/)
    await expect(page.getByRole('link', { name: tenantBName })).toBeVisible()
    await expect(page.getByRole('link', { name: s.tenantName })).toBeVisible()
    expect(await axeViolations(page)).toEqual([])
    await page.setViewportSize(MOBILE)
    await expectNoHorizontalScroll(page)
    await page.getByRole('link', { name: tenantBName }).click()
    await expect(page).toHaveURL(new RegExp(`/tenants/${tenantB}`))
  })

  test('unknown paths and NotFound (#248, #274): h1 + way home in shell, standalone, and empty-GUID routes', async ({ page }) => {
    await signIn(page, single.login, single.password)
    await page.waitForURL(/\/tenants\//)
    await page.goto('/no-such-page')
    await expect(page).toHaveURL(new RegExp(`/tenants/${s.tenantId}$`))

    await page.goto(`/tenants/${s.tenantId}/no-such-page`)
    await expect(page.getByRole('heading', { level: 1, name: 'Sayfa bulunamadı.' })).toBeVisible()
    expect(await axeViolations(page)).toEqual([])

    await page.goto('/tenants/00000000-0000-0000-0000-000000000000')
    await expect(page.getByRole('heading', { level: 1, name: 'Sayfa bulunamadı.' })).toBeVisible()

    await page.goto(`/tenants/${tenantB}`)
    await expect(page.getByRole('heading', { level: 1, name: 'Sayfa bulunamadı.' })).toBeVisible()
    expect(await axeViolations(page)).toEqual([])
    await page.getByRole('link', { name: 'Ana sayfaya dön' }).click()
    await expect(page).toHaveURL(new RegExp(`/tenants/${s.tenantId}$`))
  })

  test('a UserAdmin whose last membership was removed is auto-disabled and cannot sign in (generic error, no account enumeration)', async ({ page }) => {
    test.skip(!isIsolatedStack, ISOLATED_SKIP)
    await signIn(page, orphan.login, orphan.password)
    await expect(page.getByRole('alert')).toContainText('Kullanıcı adı veya şifre hatalı')
    await expect(page).toHaveURL(/\/login$/)
  })

  test('malformed tenant id in the URL does not crash the app', async ({ page }) => {
    await signIn(page, single.login, single.password)
    await page.waitForURL(/\/tenants\//)
    const w = watch(page)
    await page.goto('/tenants/not-a-guid')
    await expect(page.getByRole('heading', { level: 1, name: 'Sayfa bulunamadı.' })).toBeVisible()
    expect(w.problems.filter((p) => p.startsWith('pageerror'))).toEqual([])
  })

  test('PlatformAdmin cannot open a customer workspace or its backup API', async ({ page }) => {
    await signIn(page, s.accounts.platformAdmin.login, s.accounts.platformAdmin.password)
    await expect(page).toHaveURL(/\/platform$/)
    await page.goto(`/tenants/${s.tenantId}`)
    await expect(page.getByRole('heading', { name: 'Sayfa bulunamadı.' })).toBeVisible()
    const platform = await platformApi()
    await expect(platform.call('GET', `/api/tenants/${s.tenantId}/backup-jobs`)).rejects.toThrow(/403/)
    await platform.dispose()
  })

  test('PlatformAdmin reaches /platform; sign-out from there lands on /login; Back stays signed out', async ({ page }) => {
    await signIn(page, s.accounts.platformAdmin.login, s.accounts.platformAdmin.password)
    await page.waitForURL((u) => u.pathname !== '/login')
    await page.goto('/platform')
    await expect(page.getByRole('heading', { name: 'Platform özeti' }).first()).toBeVisible()
    await page.goto('/account')
    await page.getByRole('button', { name: 'Çıkış' }).click()
    await expect(page).toHaveURL(/\/login/)
    await page.goBack()
    await expect(page).toHaveURL(/\/login/)
  })
})
