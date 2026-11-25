import { expect, test, type Page } from '@playwright/test'
import { Api } from '../../api'
import { axeViolations, DESKTOP, ensureTenant, expectNoHorizontalScroll, MOBILE, platformApi, rand, s, signIn, watch } from './helpers'

const dialog = (page: Page) => page.getByRole('dialog')
const row = (page: Page, text: string) => page.getByRole('row').filter({ hasText: text })

async function admin(page: Page, path: string) {
  await signIn(page, s.accounts.platformAdmin.login, s.accounts.platformAdmin.password)
  await page.waitForURL((u) => u.pathname !== '/login')
  await page.goto(path)
}

test.describe('/platform/quota-policies', () => {
  test('list: default policy is marked, axe, responsive, clean console', async ({ page }) => {
    const w = watch(page)
    await admin(page, '/platform/quota-policies')
    await expect(page.getByRole('heading', { level: 1, name: 'Kota politikaları' })).toBeVisible()
    await expect(row(page, 'Default')).toContainText('Varsayılan')
    expect(await axeViolations(page)).toEqual([])
    await page.setViewportSize(MOBILE)
    await expectNoHorizontalScroll(page)
    await page.setViewportSize(DESKTOP)
    expect(w.unexpected()).toEqual([])
  })

  test('create, validate thresholds and scopes, refuse duplicate key, edit with confirmation, and protect Default policy from disabling', async ({ page }) => {
    const key = `e2e-s1-${rand()}`
    const name = `S1 policy ${key}`
    await admin(page, '/platform/quota-policies')
    await page.getByRole('button', { name: 'Yeni politika' }).click()
    expect(await axeViolations(page)).toEqual([])
    const submit = dialog(page).getByRole('button', { name: /Oluştur|Kaydet/ })
    await dialog(page).getByLabel('Politika anahtarı').fill(key)
    await dialog(page).getByLabel('Politika adı').fill(name)

    // scope validation: empty provider list is rejected inline
    await dialog(page).getByLabel('Depolama kapsamı').selectOption('providerType')
    await dialog(page).getByLabel(/Sağlayıcı Anahtarları/i).fill('   ')
    await submit.click()
    await expect(dialog(page).getByRole('alert')).toContainText('sağlayıcı türü')
    await dialog(page).getByLabel('Depolama kapsamı').selectOption('all')

    // threshold validation: warning >= critical
    await dialog(page).getByLabel('Uyarı Eşiği (%)').fill('95')
    await dialog(page).getByLabel('Kritik Eşik (%)').fill('80')
    await submit.click()
    await expect(dialog(page)).toBeVisible()
    await expect(dialog(page).getByRole('alert').or(dialog(page).locator('[aria-invalid=true]').first())).toBeVisible()
    await dialog(page).getByLabel('Uyarı Eşiği (%)').fill('70')
    await dialog(page).getByLabel('Kritik Eşik (%)').fill('90')
    await submit.click()
    await expect(dialog(page)).toBeHidden()
    await page.getByLabel('Politika ara').fill(name)
    await page.getByLabel('Politika ara').press('Enter')
    await expect(row(page, name)).toBeVisible()

    // duplicate key refused
    await page.getByRole('button', { name: 'Yeni politika' }).click()
    await dialog(page).getByLabel('Politika anahtarı').fill(key)
    await dialog(page).getByLabel('Politika adı').fill(`${name} again`)
    await dialog(page).getByRole('button', { name: /Oluştur|Kaydet/ }).click()
    await expect(dialog(page).getByRole('alert')).toBeVisible()
    await page.keyboard.press('Escape')

    // edit with impact confirmation
    await row(page, name).getByRole('button', { name: 'Düzenle' }).click()
    await expect(dialog(page).getByLabel('Politika anahtarı')).toHaveCount(0)
    await dialog(page).getByLabel('Politika adı').fill(`${name} edited`)
    await dialog(page).getByRole('button', { name: /Kaydet/ }).click()
    await expect(dialog(page)).toContainText('Kota politikası değiştirilsin mi?')
    await dialog(page).getByRole('button', { name: 'Kaydet' }).click()
    await expect(dialog(page)).toBeHidden()
    await page.reload()
    await page.getByLabel('Politika ara').fill(name)
    await page.getByLabel('Politika ara').press('Enter')
    await expect(row(page, `${name} edited`)).toBeVisible()

    // Default policy cannot be disabled
    await page.getByLabel('Politika ara').fill('')
    await page.getByLabel('Politika ara').press('Enter')
    await row(page, 'Default').getByRole('button', { name: 'Düzenle' }).click()
    await dialog(page).getByLabel(/Etkin politika/).uncheck()
    await dialog(page).getByRole('button', { name: 'Kaydet' }).click()
    await dialog(page).getByRole('button', { name: 'Kaydet' }).click()
    await expect(dialog(page).getByRole('alert')).toContainText('Varsayılan kota politikası devre dışı bırakılamaz')
    await page.keyboard.press('Escape')
  })
})

test.describe('/platform/audit', () => {
  test('shows what the admin just did: actor, action, workspace; details expand; axe and responsive', async ({ page }) => {
    const platform = await platformApi()
    const name = `e2e-s1-tenant-${rand()}`
    await ensureTenant(platform, name)
    await platform.dispose()
    const w = watch(page)
    await admin(page, '/platform/audit')
    await expect(page.getByRole('heading', { level: 1, name: 'Denetim kaydı' })).toBeVisible()
    await page.getByRole('combobox').first().fill(name)
    await page.getByRole('option', { name }).click()
    const created = row(page, 'Çalışma alanı oluşturuldu').first()
    await expect(created).toBeVisible()
    await expect(created).toContainText('Platform Admin')
    await expect(created).toContainText(name)
    await created.getByText('Teknik ayrıntılar').click()
    await expect(created).toContainText(/tenant|Tenant|kimliği|action/i)
    expect(await axeViolations(page)).toEqual([])
    await page.setViewportSize(MOBILE)
    await expectNoHorizontalScroll(page)
    expect(w.unexpected()).toEqual([])
  })

  test('tenant filter deep link (?tenantId) narrows the list; unknown tenant shows an empty message; reload keeps the filter', async ({ page }) => {
    await admin(page, `/platform/audit?tenantId=${s.tenantId}`)
    await expect(page.getByRole('heading', { level: 1, name: 'Denetim kaydı' })).toBeVisible()
    await page.reload()
    await expect(page).toHaveURL(new RegExp(`tenantId=${s.tenantId}`))
    await page.goto('/platform/audit?tenantId=11111111-1111-4111-8111-111111111111')
    await expect(page.getByText(/kayıt yok|bulunamadı|Henüz/i).first()).toBeVisible()
  })

  test('failed list request shows an error with retry, not a blank table', async ({ page }) => {
    await admin(page, '/platform/plans')
    let fail = true
    await page.route('**/api/platform/audit**', (r) => fail ? r.fulfill({ status: 500, contentType: 'application/json', body: '{}' }) : r.continue())
    await page.getByRole('link', { name: 'Denetim kaydı' }).click()
    await expect(page.getByRole('alert')).toBeVisible()
    fail = false
    await page.getByRole('button', { name: /Yeniden dene|Tekrar dene/ }).click()
    await expect(page.getByRole('table', { name: 'Denetim kayıtları' })).toBeVisible()
  })

  test('audit and audit/integrity APIs verify event payloads for PlatformAdmin, validate limit, and reject UserAdmin (403)', async () => {
    const adminApi = await platformApi()
    const integrity = await adminApi.call('GET', '/api/platform/audit/integrity?limit=20')
    expect(typeof integrity.signingEnabled).toBe('boolean')
    expect(Array.isArray(integrity.events)).toBe(true)
    expect(integrity.events.length).toBeGreaterThan(0)
    for (const ev of integrity.events) {
      expect(['Unsealed', 'Verified', 'UnknownKey', 'Invalid']).toContain(ev.status)
      expect(typeof ev.canonicalPayloadBase64).toBe('string')
    }
    await expect(adminApi.call('GET', '/api/platform/audit/integrity?limit=0')).rejects.toThrow(/400/)
    await adminApi.dispose()

    const userApi = await Api.signIn(s.accounts.userAdmin)
    await expect(userApi.call('GET', '/api/platform/audit')).rejects.toThrow(/403/)
    await expect(userApi.call('GET', '/api/platform/audit/integrity')).rejects.toThrow(/403/)
    await userApi.dispose()
  })
})

test.describe('platform navigation consistency', () => {
  test('sidebar label matches the page heading and renders one h1 with no console errors (#261)', async ({ page }) => {
    const w = watch(page)
    await admin(page, '/platform')
    for (const label of ['Çalışma alanları', 'Kullanıcılar', 'Planlar', 'Kota politikaları', 'Denetim kaydı']) {
      await page.getByRole('navigation', { name: 'Platform gezinme menüsü' }).getByRole('link', { name: label, exact: true }).click()
      await expect(page.getByRole('heading', { level: 1 }), label).toHaveCount(1)
      await expect(page.getByRole('heading', { level: 1 }), label).toHaveText(label)
    }
    expect(w.unexpected()).toEqual([])
  })
})
