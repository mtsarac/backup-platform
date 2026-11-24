import { expect, test, type Page } from '@playwright/test'
import { cleanConnections, noHorizontalOverflow, s, sftpFingerprint, signInTenant, uniq, watch } from './helpers'

const base = `/tenants/${s.tenantId}/storage`
test.beforeAll(cleanConnections)
test.afterAll(cleanConnections)

async function startWizard(page: Page, provider: RegExp) {
  await page.goto(`${base}/connections/new`)
  await page.getByRole('radio', { name: provider }).check()
  await page.getByRole('button', { name: 'İleri' }).click()
}
async function fillSftp(page: Page, over: { port?: string; fingerprint?: string } = {}) {
  await page.getByLabel('Sunucu adresi').fill('192.168.122.1')
  await page.getByLabel('Bağlantı noktası').fill(over.port ?? '2222')
  await page.getByLabel('Kullanıcı adı').fill('s2user')
  await page.getByLabel('Yedeklerin saklanacağı klasör').fill('/upload')
  await page.getByLabel('Sunucunun güvenlik parmak izi (SHA256)').fill(over.fingerprint ?? sftpFingerprint())
  await page.getByLabel('Parola', { exact: true }).fill('S2-sftp-pass1')
}
async function rename(page: Page, name: string) {
  await page.getByRole('button', { name: 'Adı değiştir' }).click()
  await page.getByRole('textbox', { name: 'Ad', exact: true }).fill(name)
}

test.describe('storage wizard validation', () => {
  test('provider step: next is blocked until a provider is chosen; cancel returns to the list', async ({ page }) => {
    await signInTenant(page)
    await page.goto(`${base}/connections/new`)
    await expect(page.getByRole('button', { name: 'İleri' })).toBeDisabled()
    await page.getByRole('link', { name: 'Vazgeç' }).click()
    await expect(page).toHaveURL(new RegExp(`${base}$`))
  })

  test('central provider is listed but cannot be chosen by a tenant', async ({ page }) => {
    await signInTenant(page)
    await page.goto(`${base}/connections/new`)
    await expect(page.getByRole('radio', { name: /Merkezi yönetilen depolama/ })).toBeDisabled()
  })

  test('SFTP: required fields block the next step, and typing clears them', async ({ page }) => {
    await signInTenant(page)
    await startWizard(page, /^SFTP/)
    await expect(page.getByRole('button', { name: 'İleri' })).toBeDisabled()
    await fillSftp(page)
    await expect(page.getByRole('button', { name: 'İleri' })).toBeEnabled()
    await page.getByLabel('Sunucu adresi').fill('')
    await expect(page.getByRole('button', { name: 'İleri' })).toBeDisabled()
  })

  test('SFTP: malformed fingerprint is rejected by the server and the form stays editable', async ({ page }) => {
    await signInTenant(page)
    await startWizard(page, /^SFTP/)
    await fillSftp(page, { fingerprint: 'not-a-fingerprint' })
    await page.getByRole('button', { name: 'İleri' }).click()
    await page.getByRole('button', { name: 'Bağlantıyı oluştur ve test et' }).click()
    await expect(page.getByRole('alert').first()).toBeVisible()
    await expect(page.getByRole('button', { name: /Bağlantıyı oluştur/ }).or(page.getByRole('button', { name: 'Geri' })).first()).toBeVisible()
  })

  test('SFTP: out-of-range port is rejected', async ({ page }) => {
    await signInTenant(page)
    await startWizard(page, /^SFTP/)
    await fillSftp(page, { port: '70000' })
    const next = page.getByRole('button', { name: 'İleri' })
    if (await next.isEnabled()) {
      await next.click()
      await page.getByRole('button', { name: 'Bağlantıyı oluştur ve test et' }).click()
      await expect(page.getByRole('alert').first()).toBeVisible()
    }
  })

  test('S3: required fields block the next step', async ({ page }) => {
    await signInTenant(page)
    await page.goto(`${base}/connections/new`)
    await page.getByRole('radio', { name: /^S3/ }).check()
    await page.getByRole('button', { name: 'İleri' }).click()
    await expect(page.getByRole('button', { name: 'İleri' })).toBeDisabled()
  })

  test('deep link ?provider=sftp opens the details step', async ({ page }) => {
    await signInTenant(page)
    await page.goto(`${base}/connections/new?provider=sftp`)
    await expect(page.getByLabel('Sunucu adresi')).toBeVisible()
  })
})

test.describe('SFTP connection lifecycle (real sftp target on :2222)', () => {
  test('create, list, detail, rename, disable, enable, delete', async ({ page }) => {
    test.setTimeout(45_000)
    const name = uniq('sftp')
    const renamed = `${name}-r`
    await signInTenant(page)
    const problems = watch(page) // after sign-in: the pre-auth /api/me 401 is tracked in #239/#240
    await startWizard(page, /^SFTP/)
    await fillSftp(page)
    await page.getByRole('button', { name: 'İleri' }).click()
    await rename(page, name)
    await page.getByRole('button', { name: 'Bağlantıyı oluştur ve test et' }).click()
    await expect(page.getByText('Bağlantı kaydedildi')).toBeVisible()
    await page.getByRole('link', { name: 'Depolamaya dön' }).click()
    await expect(page).toHaveURL(new RegExp(`${base}$`))
    await expect(page.getByText(name)).toBeVisible()

    await page.getByRole('link', { name }).click()
    await expect(page).toHaveURL(/storage\/connections\/[0-9a-f-]+$/)
    await expect(page.getByRole('heading', { name })).toBeVisible()
    await page.reload()
    await expect(page.getByRole('heading', { name })).toBeVisible()

    await page.getByRole('tab', { name: 'Yapılandırma' }).click()
    await page.getByRole('button', { name: 'Düzenle' }).first().click()
    await page.getByRole('dialog').getByLabel('Ad', { exact: true }).fill(renamed)
    await page.getByRole('dialog').getByRole('button', { name: 'Kaydet' }).click()
    await expect(page.getByRole('heading', { name: renamed })).toBeVisible()

    await page.getByRole('tab', { name: 'Yönetim' }).click()
    await page.getByRole('button', { name: 'Devre dışı bırak' }).click()
    await page.getByRole('dialog').getByRole('button', { name: 'Devre dışı bırak' }).click()
    await expect(page.getByText('Bu hedef şu an devre dışı.').or(page.getByText('Bu hedef devre dışı')).first()).toBeVisible()
    await page.getByRole('button', { name: 'Etkinleştir' }).click()
    await page.getByRole('dialog').getByRole('button', { name: 'Etkinleştir' }).click()
    await expect(page.getByRole('dialog')).toBeHidden()
    await expect(page.getByRole('button', { name: 'Devre dışı bırak' })).toBeVisible()

    const detailUrl = new URL(page.url()).pathname
    await page.getByRole('button', { name: /bağlantısı: sil$/ }).click()
    await page.getByRole('dialog').getByRole('textbox').fill(renamed)
    await page.getByRole('dialog').getByRole('button', { name: 'Bağlantıyı sil' }).click()
    await expect(page).toHaveURL(new RegExp(`${base}$`))
    await expect(page.getByText(renamed)).toHaveCount(0)
    await page.goto(detailUrl)
    await expect(page.getByText(/bulunamadı|erişilemiyor/).first()).toBeVisible()
    expect(problems.filter((p) => !p.includes('404') && !p.includes('/capacity'))).toEqual([])
  })
})

test('storage list on mobile has no horizontal overflow', async ({ page }) => {
  await page.setViewportSize({ width: 375, height: 700 })
  await signInTenant(page)
  await page.goto(base)
  await expect(page.getByRole('heading', { name: 'Depolama' }).first()).toBeVisible()
  await noHorizontalOverflow(page)
})
