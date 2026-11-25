import { expect, test, type Page } from '@playwright/test'
import { axeViolations, DESKTOP, expectNoHorizontalScroll, isIsolatedStack, MOBILE, newUser, platformApi, rand, resetIsolatedThrottle, s, signIn, watch } from './helpers'

const dialog = (page: Page) => page.getByRole('dialog')
const row = (page: Page, text: string) => page.getByRole('row').filter({ hasText: text })
const searchUsers = async (page: Page, term: string) => {
  const box = page.getByRole('searchbox', { name: 'Kullanıcı ara' }).or(page.getByLabel('Kullanıcı ara'))
  await box.fill(term)
  await box.press('Enter')
}

/** Click a button that lives in the row's collapsible "İşlemler" menu. */
async function rowAction(page: Page, text: string, name: RegExp) {
  const r = row(page, text)
  await expect(async () => {
    if (!(await r.getByRole('button', { name }).isVisible())) await r.getByText('İşlemler').first().click()
    await expect(r.getByRole('button', { name })).toBeVisible({ timeout: 500 })
  }).toPass()
  await r.getByRole('button', { name }).click()
}

test.beforeAll(() => { resetIsolatedThrottle() })
test.afterAll(() => { resetIsolatedThrottle() })

async function admin(page: Page) {
  await signIn(page, s.accounts.platformAdmin.login, s.accounts.platformAdmin.password)
  await page.waitForURL((u) => u.pathname !== '/login')
  await page.goto('/platform/users')
  await expect(page.getByRole('heading', { level: 1, name: 'Kullanıcılar' })).toBeVisible()
}

test('list: columns, axe, search narrows and empty search shows a message; mobile has no overflow', async ({ page }) => {
  const w = watch(page)
  await admin(page)
  await searchUsers(page, 'e2e.useradmin')
  await expect(row(page, 'e2e.useradmin')).toBeVisible()
  expect(await axeViolations(page)).toEqual([])
  await expect(row(page, 'Platform yöneticisi')).toHaveCount(0)
  await searchUsers(page, `no-such-user-${rand()}`)
  await expect(page.getByText('Aramaya uygun kullanıcı bulunamadı.')).toBeVisible()
  await page.setViewportSize(MOBILE)
  await expectNoHorizontalScroll(page)
  await page.setViewportSize(DESKTOP)
  expect(w.unexpected()).toEqual([])
})

test('desktop 1440px: long unspaced usernames and e-mails wrap without overflowing the table (#287)', async ({ page }) => {
  const platform = await platformApi()
  const longLogin = `e2e.s1.long-identifier-${rand()}-admin`
  await platform.call('POST', '/api/platform/users', {
    username: longLogin,
    email: `${longLogin}@example.test`,
    displayName: `Long ${longLogin}`,
    password: 'E2e-S1-LongUser!2026',
    role: 'UserAdmin',
    tenantIds: [s.tenantId],
  })
  await platform.dispose()
  await page.setViewportSize(DESKTOP)
  await admin(page)
  await searchUsers(page, longLogin)
  await expect(row(page, longLogin)).toBeVisible()
  await expectNoHorizontalScroll(page)
})

test('create: required fields, UserAdmin needs a workspace, duplicate username and short password are refused, then the user is created and forced to change password', async ({ page }) => {
  const login = `e2e-s1-${rand()}`
  const initial = 'E2e-S1-Initial!2026'
  await admin(page)
  await page.getByRole('button', { name: 'Yeni kullanıcı' }).click()
  await expect(dialog(page)).toBeVisible()
  expect(await axeViolations(page)).toEqual([])
  const submit = dialog(page).getByRole('button', { name: 'Kullanıcı oluştur' })
  await expect(submit).toBeDisabled()
  await dialog(page).getByLabel('Kullanıcı adı').fill(login)
  await dialog(page).getByLabel('E-posta').fill('not-an-email')
  await dialog(page).getByLabel('Görünen ad').fill(`S1 ${login}`)
  await dialog(page).getByLabel('Şifre').fill('short')
  await dialog(page).getByLabel(s.tenantName).check()
  await submit.click()
  await expect(dialog(page)).toBeVisible()
  await dialog(page).getByLabel('E-posta').fill(`${login}@example.test`)
  await submit.click()
  await expect(dialog(page).getByRole('alert')).toBeVisible()
  await dialog(page).getByLabel('Şifre').fill(initial)
  await dialog(page).getByLabel('Kullanıcı adı').fill('e2e.useradmin')
  await submit.click()
  await expect(dialog(page).getByRole('alert')).toBeVisible()
  await dialog(page).getByLabel('Kullanıcı adı').fill(login)
  await dialog(page).getByLabel('Şifre').fill(initial)
  await submit.click()
  await expect(dialog(page)).toBeHidden()
  await searchUsers(page, login)
  await expect(row(page, login)).toContainText('Kullanıcı yöneticisi')
  await expect(row(page, login)).toContainText('Etkin')

  const ctx = await page.context().browser()!.newContext({ ignoreHTTPSErrors: true })
  const p2 = await ctx.newPage()
  await signIn(p2, login, initial)
  await expect(p2.getByRole('heading', { name: 'Şifrenizi değiştirin' })).toBeVisible()
  await ctx.close()
})

test('create a PlatformAdmin: no workspace picker, appears as Platform yöneticisi, can open /platform; then disable it', async ({ page, browser }) => {
  const login = `e2e-s1-pa-${rand()}`
  const password = 'E2e-S1-Initial!2026'
  await admin(page)
  await page.getByRole('button', { name: 'Yeni kullanıcı' }).click()
  await dialog(page).getByLabel('Kullanıcı adı').fill(login)
  await dialog(page).getByLabel('E-posta').fill(`${login}@example.test`)
  await dialog(page).getByLabel('Görünen ad').fill(`S1 ${login}`)
  await dialog(page).getByLabel('Şifre').fill(password)
  await dialog(page).getByLabel('Rol').selectOption('PlatformAdmin')
  await expect(dialog(page).getByText('Çalışma alanları (en az birini seçin)')).toHaveCount(0)
  await dialog(page).getByRole('button', { name: 'Kullanıcı oluştur' }).click()
  await expect(dialog(page)).toBeHidden()
  await searchUsers(page, login)
  await expect(row(page, login)).toContainText('Platform yöneticisi')
  await expect(row(page, login)).toContainText('0')

  const ctx = await browser.newContext({ ignoreHTTPSErrors: true })
  const p2 = await ctx.newPage()
  await signIn(p2, login, password)
  await p2.getByLabel('Mevcut şifre').waitFor()
  await p2.getByLabel('Yeni şifre', { exact: true }).fill('E2e-S1-Changed!2026-pa')
  await p2.getByLabel('Yeni şifre (tekrar)').fill('E2e-S1-Changed!2026-pa')
  await p2.getByRole('button', { name: 'Şifreyi Değiştir ve Giriş Yap' }).click()
  await p2.waitForURL((u) => u.pathname !== '/login')
  await p2.goto('/platform/users')
  await expect(p2.getByRole('heading', { level: 1, name: 'Kullanıcılar' })).toBeVisible()
  await ctx.close()

  await rowAction(page, login, /^Devre dışı bırak — /i)
  await dialog(page).getByRole('button', { name: 'Devre dışı bırak' }).click()
  await expect(row(page, login)).toContainText('Devre dışı')
})

test('edit: e-mail and display name changes persist (#251), role membership conflict is rejected, Cancel changes nothing', async ({ page, browser }) => {
  const platform = await platformApi()
  const u = await newUser(platform)
  await platform.dispose()
  await admin(page)
  await searchUsers(page, u.login)
  await row(page, u.login).getByRole('button', { name: /^Düzenle — / }).click()
  await expect(dialog(page).getByRole('heading', { name: 'Kullanıcı düzenle' })).toBeVisible()
  await expect(dialog(page).getByLabel('Şifre')).toHaveCount(0)

  // PlatformAdmin cannot keep existing workspace memberships
  await dialog(page).getByLabel('Rol').selectOption('PlatformAdmin')
  await dialog(page).getByRole('button', { name: 'Kaydet' }).click()
  await expect(dialog(page).getByRole('alert')).toContainText('Platform yöneticisinin çalışma alanı üyeliği olamaz')
  await dialog(page).getByRole('button', { name: /Vazgeç|İptal|Kapat/ }).click()

  await row(page, u.login).getByRole('button', { name: /^Düzenle — / }).click()
  await dialog(page).getByLabel('E-posta').fill(`${u.login}-new@example.test`)
  await dialog(page).getByLabel('Görünen ad').fill(`Renamed ${u.login}`)
  await dialog(page).getByRole('button', { name: 'Kaydet' }).click()
  await expect(dialog(page)).toBeHidden()
  await expect(row(page, u.login)).toContainText(`${u.login}-new@example.test`)
  await expect(row(page, u.login)).toContainText(`Renamed ${u.login}`)
  await page.reload()
  await searchUsers(page, u.login)
  await expect(row(page, u.login)).toContainText(`Renamed ${u.login}`)

  // Edited user sees the new display name on /account
  const ctx = await browser.newContext({ ignoreHTTPSErrors: true })
  const own = await ctx.newPage()
  await signIn(own, u.login, u.password)
  await own.waitForURL(/\/tenants\//)
  await own.goto('/account')
  await expect(own.locator('main').getByText(`Renamed ${u.login}`)).toBeVisible()
  await ctx.close()
})

test('disable ends sessions at once and blocks sign-in; enable restores it', async ({ page, browser }) => {
  const platform = await platformApi()
  const u = await newUser(platform)
  await platform.dispose()
  const ctx = await browser.newContext({ ignoreHTTPSErrors: true })
  const victim = await ctx.newPage()
  await signIn(victim, u.login, u.password)
  await victim.waitForURL(/\/tenants\//)

  await admin(page)
  await searchUsers(page, u.login)
  await rowAction(page, u.login, /^Devre dışı bırak — /i)
  await expect(dialog(page)).toContainText('tüm aktif oturumları anında sonlandırılır')
  await dialog(page).getByLabel('Devre dışı bırakma nedeni').fill('e2e-s1 disable test')
  await dialog(page).getByRole('button', { name: 'Devre dışı bırak' }).click()
  await expect(dialog(page)).toBeHidden()
  await expect(row(page, u.login)).toContainText('Devre dışı')

  await victim.goto('/account')
  await expect(victim).toHaveURL(/\/login/)
  if (isIsolatedStack) {
    await signIn(victim, u.login, u.password)
    await expect(victim.getByRole('alert')).toContainText('Kullanıcı adı veya şifre hatalı')
    await expect(victim).toHaveURL(/\/login$/)
  }

  await expect(page.getByText('devre dışı bırakıldı')).toBeVisible()
  await rowAction(page, u.login, /^Etkinleştir — /i)
  await expect(row(page, u.login)).toContainText('Etkin')
  await signIn(victim, u.login, u.password)
  await expect(victim).toHaveURL(/\/(tenants\/|account$)/)
  await victim.goto(`/tenants/${s.tenantId}`)
  await expect(victim).toHaveURL(new RegExp(`/tenants/${s.tenantId}$`))
  await ctx.close()
})

test('temporary password: sessions end, old password stops working, next sign-in forces a change', async ({ page, browser }) => {
  const platform = await platformApi()
  const u = await newUser(platform)
  await platform.dispose()
  const ctx = await browser.newContext({ ignoreHTTPSErrors: true })
  const victim = await ctx.newPage()
  await signIn(victim, u.login, u.password)
  await victim.waitForURL(/\/tenants\//)

  await admin(page)
  await searchUsers(page, u.login)
  await rowAction(page, u.login, /^Geçici şifre ata — /i)
  await dialog(page).getByLabel('Geçici şifre').fill('short')
  await dialog(page).getByRole('button', { name: 'Geçici şifre ata' }).click()
  await expect(dialog(page).getByRole('alert')).toBeVisible()
  await dialog(page).getByLabel('Geçici şifre').fill('Temp-S1-Passw0rd!2026')
  await dialog(page).getByRole('button', { name: 'Geçici şifre ata' }).click()
  await expect(page.getByText(/geçici şifre atandı/)).toBeVisible()

  await victim.goto('/account')
  await expect(victim).toHaveURL(/\/login/)
  await signIn(victim, u.login, 'Temp-S1-Passw0rd!2026')
  await expect(victim.getByRole('heading', { name: 'Şifrenizi değiştirin' })).toBeVisible()
  await ctx.close()
})

test('last PlatformAdmin cannot be disabled (UI says so, account stays enabled)', async ({ page }) => {
  const platform = await platformApi()
  const items: any[] = []
  for (let p = 1; ; p++) {
    const res = await platform.call('GET', `/api/platform/users?page=${p}&pageSize=100`)
    items.push(...res.items)
    if (items.length >= res.totalCount || res.items.length === 0) break
  }
  const enabledAdmins = items.filter((i: any) => i.role === 'PlatformAdmin' && !i.isDisabled)
  const target = enabledAdmins[0]
  await platform.dispose()
  test.skip(enabledAdmins.length !== 1 || !target, 'another PlatformAdmin exists on the stack; the rule only applies to the last one')

  await admin(page)
  await searchUsers(page, target.displayName)
  const r = row(page, target.displayName)
  await rowAction(page, target.displayName, /^Devre dışı bırak — /i)
  await dialog(page).getByRole('button', { name: 'Devre dışı bırak' }).click()
  await expect(page.getByText(/Son Platform Yöneticisi.*devre dışı bırakılamaz/)).toBeVisible()
  await expect(r).toContainText('Etkin')
})
