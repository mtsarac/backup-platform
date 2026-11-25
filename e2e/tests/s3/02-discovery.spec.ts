import { expect, test, type Page } from '@playwright/test'
import { type Api } from '../../api'
import { ensureAgent, seedS3 } from './fixture'
import { PG, seedSourceDatabase } from './pg'
import { signIn, tenantPath, watch } from './ui'

let tenantId: string
let tenant: Api
test.describe.configure({ mode: 'serial', timeout: 120_000 })
test.beforeAll(async () => {
  ;({ tenantId, tenant } = await seedS3())
  await ensureAgent(tenant, tenantId)
  seedSourceDatabase()
})
test.afterAll(async () => { await tenant.dispose() })

async function openInstance(page: Page) {
  await expect(async () => {
    await page.goto(tenantPath(tenantId, 'agents/databases'))
    const rescan = page.getByRole('button', { name: 'Yeniden tara' }).first()
    if (await rescan.isVisible({ timeout: 1500 }).catch(() => false)) await rescan.click().catch(() => {})
    await expect(page.getByRole('link', { name: /PostgreSQL/ }).first()).toBeVisible({ timeout: 5000 })
  }).toPass({ timeout: 60_000, intervals: [3000] })
  await page.getByRole('link', { name: /PostgreSQL/ }).first().click()
  await expect(page).toHaveURL(/discovery\/instances\//)
}

async function saveCredential(page: Page, user: string, password: string, encrypt: boolean, waitThrottle = true) {
  if (waitThrottle) await page.waitForTimeout(6000) // manual rescans are throttled to one per 5 s (#264)
  const change = page.getByText('Giriş bilgilerini değiştir', { exact: true })
  if (await change.isVisible()) await change.click()
  await page.getByLabel('Veritabanı kullanıcı adı').fill(user)
  await page.getByLabel('Veritabanı parolası').fill(password)
  const box = page.getByLabel('Bağlantıyı şifrele')
  encrypt ? await box.check() : await box.uncheck()
  await page.getByRole('button', { name: 'Kaydet ve doğrula' }).click()
}

test('wrong password shows a clear credential error', async ({ page }) => {
  const w = watch(page)
  await signIn(page); w.reset()
  await openInstance(page)
  await saveCredential(page, PG.user, 'definitely-wrong', false)
  await expect(page.getByText('Kimlik doğrulaması başarısız.')).toBeVisible({ timeout: 60_000 })
  await expect(page.getByText('Giriş bilgilerini kontrol edip yeniden kaydedin.')).toBeVisible()
  w.expectClean()
})

test('encryption requested but the server has no TLS explains what to do', async ({ page }) => {
  await signIn(page)
  await openInstance(page)
  await saveCredential(page, PG.user, PG.password, true)
  await expect(page.getByText('Şifreli bağlantı kurulamadı')).toBeVisible({ timeout: 60_000 })
  await expect(page.getByText(/TLS ekleyin ya da/)).toBeVisible()
})

test('rapid credential correction within 5 s re-verifies instead of stalling on rescan_too_soon (#264)', async ({ page }) => {
  await signIn(page)
  await openInstance(page)
  await saveCredential(page, PG.user, PG.password, true)
  await expect(page.getByText('Şifreli bağlantı kurulamadı')).toBeVisible({ timeout: 60_000 })
  await saveCredential(page, PG.user, PG.password, false, false)
  await expect(page.getByText('Kimlik doğrulandı')).toBeVisible({ timeout: 60_000 })
})

test('valid credential verifies and lists databases; system databases are not selectable', async ({ page }) => {
  const w = watch(page)
  await signIn(page); w.reset()
  await openInstance(page)
  await saveCredential(page, PG.user, PG.password, false)
  await expect(page.getByText('Kimlik doğrulandı')).toBeVisible({ timeout: 60_000 })
  await expect(page.getByRole('row', { name: new RegExp(PG.database) })).toContainText('Yedeklenebilir')
  await expect(page.getByRole('row', { name: /^postgres/ })).toContainText('Sistem veritabanı')
  await expect(page.getByRole('row', { name: /template0/ }).getByRole('checkbox')).toBeDisabled()
  w.expectClean()
})
