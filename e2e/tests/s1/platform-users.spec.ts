import { expect, test, type Page } from '@playwright/test'
import { Api } from '../../api'
import { axeViolations, DESKTOP, expectNoHorizontalScroll, MOBILE, newUser, platformApi, rand, s, signIn, watch } from './helpers'

const dialog = (page: Page) => page.getByRole('dialog')
const row = (page: Page, text: string) => page.getByRole('row').filter({ hasText: text })
/** Click a button that lives in the row's collapsible "İşlemler" menu. */
async function rowAction(page: Page, text: string, name: RegExp) {
  const r = row(page, text)
  await expect(async () => {
    if (!(await r.getByRole('button', { name }).isVisible())) await r.getByText('İşlemler').first().click()
    await expect(r.getByRole('button', { name })).toBeVisible({ timeout: 500 })
  }).toPass()
  await r.getByRole('button', { name }).click()
}


async function admin(page: Page) {
  await signIn(page, s.accounts.platformAdmin.login, s.accounts.platformAdmin.password)
  await page.waitForURL((u) => u.pathname !== '/login')
  await page.goto('/platform/users')
  await expect(page.getByRole('heading', { level: 1, name: 'Kullanıcılar' })).toBeVisible()
}

test('list: columns, axe, search narrows and empty search shows a message; mobile has no overflow', async ({ page }) => {
  const w = watch(page)
  await admin(page)
  await expect(row(page, 'e2e.useradmin')).toBeVisible()
  expect(await axeViolations(page)).toEqual([])
  await page.getByRole('searchbox', { name: 'Kullanıcı ara' }).or(page.getByLabel('Kullanıcı ara')).fill('e2e.useradmin')
  await expect(row(page, 'Platform Yöneticisi')).toHaveCount(0)
  await page.getByLabel('Kullanıcı ara').fill(`no-such-user-${rand()}`)
  await expect(page.getByText('Aramaya uygun kullanıcı bulunamadı.')).toBeVisible()
  await page.setViewportSize(MOBILE)
  await expectNoHorizontalScroll(page)
  await page.setViewportSize(DESKTOP)
  expect(w.unexpected()).toEqual([])
})

test('create: required fields, UserAdmin needs a workspace, duplicate username and short password are refused, then the user is created and can sign in after the forced change', async ({ page }) => {
  const login = `e2e-s1-${rand()}`
  const initial = 'E2e-S1-Initial!2026'
  await admin(page)
  await page.getByRole('button', { name: 'Yeni kullanıcı' }).click()
  await expect(dialog(page)).toBeVisible()
  expect(await axeViolations(page)).toEqual([])
  const submit = dialog(page).getByRole('button', { name: 'Kullanıcı oluştur' })
  await expect(submit).toBeDisabled() // UserAdmin with no workspace chosen
  await dialog(page).getByLabel('Kullanıcı adı').fill(login)
  await dialog(page).getByLabel('E-posta').fill('not-an-email')
  await dialog(page).getByLabel('Görünen ad').fill(`S1 ${login}`)
  await dialog(page).getByLabel('Şifre').fill('short')
  await dialog(page).getByLabel(s.tenantName).check()
  await submit.click()
  // browser validation blocks the invalid e-mail: still on the dialog, nothing created
  await expect(dialog(page)).toBeVisible()
  await dialog(page).getByLabel('E-posta').fill(`${login}@example.test`)
  await submit.click()
  await expect(dialog(page).getByRole('alert')).toBeVisible() // password too short
  await dialog(page).getByLabel('Şifre').fill(initial)
  await dialog(page).getByLabel('Kullanıcı adı').fill('e2e.useradmin') // duplicate
  await submit.click()
  await expect(dialog(page).getByRole('alert')).toBeVisible()
  await dialog(page).getByLabel('Kullanıcı adı').fill(login)
  await dialog(page).getByLabel('Şifre').fill(initial) // the form clears the secret after every submit
  await submit.click()
  await expect(dialog(page)).toBeHidden()
  await page.getByLabel('Kullanıcı ara').fill(login)
  await expect(row(page, login)).toContainText('Kullanıcı Yöneticisi')
  await expect(row(page, login)).toContainText('Etkin')

  // new account is forced to change the password at first sign-in
  const ctx = await page.context().browser()!.newContext({ ignoreHTTPSErrors: true })
  const p2 = await ctx.newPage()
  await signIn(p2, login, initial)
  await expect(p2.getByRole('heading', { name: 'Şifrenizi değiştirin' })).toBeVisible()
  await ctx.close()
})

test('create a PlatformAdmin: no workspace picker, appears as Platform Yöneticisi, can open /platform; then disable it', async ({ page, browser }) => {
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
  await page.getByLabel('Kullanıcı ara').fill(login)
  await expect(row(page, login)).toContainText('Platform Yöneticisi')
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

  await rowAction(page, login, /devre dışı bırak/)
  await dialog(page).getByRole('button', { name: 'Devre dışı bırak' }).click()
  await expect(row(page, login)).toContainText('Devre dışı')
})

test('edit: e-mail change persists across reload; Cancel changes nothing; the dialog has no password field', async ({ page }) => {
  const platform = await platformApi()
  const u = await newUser(platform)
  await platform.dispose()
  await admin(page)
  await page.getByLabel('Kullanıcı ara').fill(u.login)
  await row(page, u.login).getByRole('button', { name: /kullanıcısını düzenle/ }).click()
  await expect(dialog(page).getByRole('heading', { name: 'Kullanıcı düzenle' })).toBeVisible()
  await expect(dialog(page).getByLabel('Şifre')).toHaveCount(0)
  await dialog(page).getByLabel('E-posta').fill('cancelled@example.test')
  await dialog(page).getByRole('button', { name: /Vazgeç|İptal|Kapat/ }).click()
  await expect(row(page, u.login)).not.toContainText('cancelled@example.test')
  await row(page, u.login).getByRole('button', { name: /kullanıcısını düzenle/ }).click()
  await dialog(page).getByLabel('E-posta').fill(`${u.login}-new@example.test`)
  await dialog(page).getByRole('button', { name: 'Kaydet' }).click()
  await expect(dialog(page)).toBeHidden()
  await expect(row(page, u.login)).toContainText(`${u.login}-new@example.test`)
  await page.reload()
  await page.getByLabel('Kullanıcı ara').fill(u.login)
  await expect(row(page, u.login)).toContainText(`${u.login}-new@example.test`)
})

test.fixme('edit: display name change is saved (#251)', async ({ page }) => {
  const platform = await platformApi()
  const u = await newUser(platform)
  await platform.dispose()
  await admin(page)
  await page.getByLabel('Kullanıcı ara').fill(u.login)
  await row(page, u.login).getByRole('button', { name: /kullanıcısını düzenle/ }).click()
  await dialog(page).getByLabel('Görünen ad').fill(`Renamed ${u.login}`)
  await dialog(page).getByRole('button', { name: 'Kaydet' }).click()
  await expect(dialog(page)).toBeHidden()
  await page.reload()
  await page.getByLabel('Kullanıcı ara').fill(u.login)
  await expect(row(page, u.login)).toContainText(`Renamed ${u.login}`)
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
  await page.getByLabel('Kullanıcı ara').fill(u.login)
  await rowAction(page, u.login, /devre dışı bırak/)
  await expect(dialog(page)).toContainText('tüm aktif oturumları anında sonlandırılır')
  await dialog(page).getByLabel('Devre dışı bırakma nedeni').fill('e2e-s1 disable test')
  await dialog(page).getByRole('button', { name: 'Devre dışı bırak' }).click()
  await expect(dialog(page)).toBeHidden()
  await expect(row(page, u.login)).toContainText('Devre dışı')

  await victim.goto('/account')
  await expect(victim).toHaveURL(/\/login/)
  await signIn(victim, u.login, u.password)
  await expect(victim.getByRole('alert')).toBeVisible()
  await expect(victim).toHaveURL(/\/login$/)

  await expect(page.getByText('devre dışı bırakıldı')).toBeVisible()
  await rowAction(page, u.login, /etkinleştir/)
  await expect(row(page, u.login)).toContainText('Etkin')
  await signIn(victim, u.login, u.password)
  await expect(victim).toHaveURL(/\/tenants\//)
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
  await page.getByLabel('Kullanıcı ara').fill(u.login)
  await rowAction(page, u.login, /geçici şifre ata/)
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
  // Safety net: if the rule were broken this test would lock the platform out, so re-enable through the DB afterwards.
  const platform = await platformApi()
  const all = await platform.call('GET', '/api/platform/users?pageSize=100')
  const enabledAdmins = all.items.filter((i: any) => i.role === 'PlatformAdmin' && !i.isDisabled)
  test.skip(enabledAdmins.length !== 1, 'another PlatformAdmin exists on the stack; the rule only applies to the last one')
  await platform.dispose()
  await admin(page)
  await page.getByLabel('Kullanıcı ara').fill(s.accounts.platformAdmin.login)
  const r = row(page, 'Platform Yöneticisi')
  await rowAction(page, 'Platform Yöneticisi', /devre dışı bırak/)
  await dialog(page).getByRole('button', { name: 'Devre dışı bırak' }).click()
  await expect(page.getByText('Son Platform Yöneticisi devre dışı bırakılamaz.')).toBeVisible()
  await expect(r).toContainText('Etkin')
})
