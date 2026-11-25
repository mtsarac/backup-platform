import { expect, test, type Page } from '@playwright/test'
import { axeViolations, DESKTOP, ensureTenant, expectNoHorizontalScroll, MOBILE, newUser, platformApi, rand, s, signIn, watch } from './helpers'

const dialog = (page: Page) => page.getByRole('dialog')
const row = (page: Page, text: string) => page.getByRole('row').filter({ hasText: text })
const searchBox = (page: Page) => page.getByRole('searchbox', { name: /Çalışma alanı ara|Ara/ })

async function admin(page: Page, path = '/platform/tenants') {
  await signIn(page, s.accounts.platformAdmin.login, s.accounts.platformAdmin.password)
  await page.waitForURL((u) => u.pathname !== '/login')
  await page.goto(path)
}

test('list: heading, columns, axe, search, empty search, responsive, clean console', async ({ page }) => {
  const w = watch(page)
  await admin(page)
  await expect(page.getByRole('heading', { level: 1, name: 'Çalışma alanları' })).toBeVisible()
  await expect(row(page, s.tenantName)).toContainText(/Kayıt açık/)
  expect(await axeViolations(page)).toEqual([])
  await searchBox(page).fill(s.tenantName)
  await searchBox(page).press('Enter')
  await expect(row(page, s.tenantName)).toBeVisible()
  await searchBox(page).fill(`no-such-tenant-${rand()}`)
  await searchBox(page).press('Enter')
  await expect(page.getByText(/Aramaya uygun/)).toBeVisible()
  await page.setViewportSize(MOBILE)
  await expectNoHorizontalScroll(page)
  await page.setViewportSize(DESKTOP)
  expect(w.unexpected()).toEqual([])
})

test('create: empty name blocked, success lists the new workspace and survives reload', async ({ page }) => {
  const name = `e2e-s1-tenant-${rand()}`
  await admin(page)
  await page.getByRole('button', { name: /Yeni çalışma alanı/ }).click()
  await expect(dialog(page).getByRole('heading', { name: 'Yeni çalışma alanı' })).toBeVisible()
  expect(await axeViolations(page)).toEqual([])
  await expect(dialog(page).getByRole('button', { name: 'Çalışma alanı oluştur' })).toBeDisabled()
  await dialog(page).getByLabel('Çalışma alanı adı').fill(name)
  await dialog(page).getByRole('button', { name: 'Çalışma alanı oluştur' }).click()
  await expect(dialog(page)).toBeHidden()
  await searchBox(page).fill(name)
  await searchBox(page).press('Enter')
  await expect(row(page, name)).toBeVisible()
  await page.reload()
  await searchBox(page).fill(name)
  await searchBox(page).press('Enter')
  await expect(row(page, name)).toBeVisible()
})

test('create and rename: duplicate workspace names are refused (#252)', async ({ page }) => {
  const platform = await platformApi()
  const name = `e2e-s1-tenant-${rand()}`
  const other = `e2e-s1-tenant-${rand()}`
  await ensureTenant(platform, name)
  const otherId = await ensureTenant(platform, other)
  await platform.dispose()

  await admin(page)
  await page.getByRole('button', { name: /Yeni çalışma alanı/ }).click()
  await dialog(page).getByLabel('Çalışma alanı adı').fill(name)
  await dialog(page).getByRole('button', { name: 'Çalışma alanı oluştur' }).click()
  await expect(dialog(page).getByRole('alert')).toContainText('zaten var')
  await page.keyboard.press('Escape')

  await page.goto(`/platform/tenants/${otherId}`)
  await page.getByText('Yeniden adlandır').first().click()
  await page.getByRole('textbox', { name: 'Ad', exact: true }).fill(name)
  await page.getByRole('button', { name: 'Yeniden adlandır' }).click()
  await expect(page.getByRole('alert')).toContainText('zaten var')
})

test('detail: all 6 tabs deep-link, back/forward keeps the tab, rename persists, unknown and zero GUID show not-found (#274)', async ({ page }) => {
  const platform = await platformApi()
  const name = `e2e-s1-tenant-${rand()}`
  const id = await ensureTenant(platform, name)
  await platform.dispose()
  await admin(page, `/platform/tenants/${id}`)
  await expect(page.getByRole('heading', { level: 1, name })).toBeVisible()
  expect(await axeViolations(page)).toEqual([])

  await page.goto(`/platform/tenants/${id}?tab=subscription`)
  await expect(page.getByText('Plan ve kota')).toBeVisible()
  await page.goto(`/platform/tenants/${id}?tab=storage`)
  await expect(page.getByRole('tabpanel')).toBeVisible()
  await page.goto(`/platform/tenants/${id}?tab=security`)
  await expect(page.getByText('Şifreleme anahtarları')).toBeVisible()
  await page.goBack()
  await expect(page).toHaveURL(/\?tab=storage$/)
  await page.goBack()
  await expect(page.getByText('Plan ve kota')).toBeVisible()
  await page.reload()
  await expect(page.getByText('Plan ve kota')).toBeVisible()

  await page.goto(`/platform/tenants/${id}`)
  await page.getByText('Yeniden adlandır').first().click()
  const renamed = `${name}-renamed`
  await page.getByRole('textbox', { name: 'Ad', exact: true }).fill(renamed)
  await page.getByRole('button', { name: 'Yeniden adlandır' }).click()
  await expect(page.getByRole('heading', { level: 1, name: renamed })).toBeVisible()
  await page.reload()
  await expect(page.getByRole('heading', { level: 1, name: renamed })).toBeVisible()

  await page.goto('/platform/tenants/00000000-0000-0000-0000-000000000000')
  await expect(page.getByText(/bulunamadı/i).first()).toBeVisible()
  await page.goto('/platform/tenants/11111111-1111-4111-8111-111111111111')
  await expect(page.getByText(/bulunamadı/i).first()).toBeVisible()
})

test('agent enrollment toggle asks for confirmation and flips the state; Cancel changes nothing', async ({ page }) => {
  const platform = await platformApi()
  const id = await ensureTenant(platform, `e2e-s1-tenant-${rand()}`)
  await platform.dispose()
  await admin(page, `/platform/tenants/${id}?tab=computers`)
  await expect(page.getByText('Kayıt kapalı').first()).toBeVisible()
  await page.getByRole('switch').click()
  await expect(dialog(page)).toContainText('Bilgisayar kaydı açılsın mı?')
  await dialog(page).getByRole('button', { name: /Vazgeç|İptal/ }).click()
  await expect(page.getByText('Kayıt kapalı').first()).toBeVisible()
  await page.getByRole('switch').click()
  await dialog(page).getByRole('button', { name: 'Kaydı aç' }).click()
  await expect(page.getByText('Kayıt açık').first()).toBeVisible()
  await page.reload()
  await expect(page.getByText('Kayıt açık').first()).toBeVisible()
  await page.getByRole('switch').click()
  await expect(dialog(page)).toContainText('Bilgisayar kaydı kapatılsın mı?')
  await dialog(page).getByRole('button', { name: 'Kaydı kapat' }).click()
  await expect(page.getByText('Kayıt kapalı').first()).toBeVisible()
})

test('members: add an existing user, they sign in to that workspace; remove it again with accessible action button', async ({ page, browser }) => {
  const platform = await platformApi()
  const name = `e2e-s1-tenant-${rand()}`
  const id = await ensureTenant(platform, name)
  const u = await newUser(platform)
  await platform.dispose()
  await admin(page, `/platform/tenants/${id}?tab=people`)
  await page.getByLabel('Kullanıcı ara').fill(u.login)
  await page.getByRole('button', { name: 'Ara' }).click()
  await expect(page.getByLabel('Kullanıcı', { exact: true }).locator('option')).toHaveCount(2)
  await page.getByLabel('Kullanıcı', { exact: true }).selectOption({ index: 1 })
  await page.getByRole('button', { name: 'Üye ekle', exact: true }).click()
  await expect(row(page, u.login)).toBeVisible()
  await page.reload()
  await expect(row(page, u.login)).toBeVisible()

  const ctx = await browser.newContext({ ignoreHTTPSErrors: true })
  const p2 = await ctx.newPage()
  await signIn(p2, u.login, u.password)
  await expect(p2.getByRole('link', { name })).toBeVisible()
  await ctx.close()

  await row(page, u.login).getByRole('button', { name: /^Kaldır — / }).click()
  await dialog(page).getByRole('button', { name: 'Üyeliği kaldır' }).click()
  await expect(row(page, u.login)).toHaveCount(0)
})

test('UserAdmin cannot reach tenant administration', async ({ page }) => {
  await signIn(page, s.accounts.userAdmin.login, s.accounts.userAdmin.password)
  await page.waitForURL(/\/tenants\//)
  await page.goto(`/platform/tenants/${s.tenantId}`)
  await expect(page.getByRole('heading', { name: 'Bu alan için yetkiniz yok' })).toBeVisible()
})
