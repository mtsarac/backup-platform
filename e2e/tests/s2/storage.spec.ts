import { expect, test, type Page } from '@playwright/test'
import {
  axeViolations,
  cleanConnections,
  s,
  seedIsolatedTenant,
  sftpFingerprint,
  signInAs,
  signInTenant,
  uniq,
  watch,
  type IsolatedTenant,
} from './helpers'

const base = `/tenants/${s.tenantId}/storage`
const hasGoogleDriveCreds = Boolean(process.env.GOOGLE_CLIENT_ID && process.env.GOOGLE_CLIENT_SECRET)

test.beforeAll(async () => {
  await cleanConnections()
})
test.afterAll(async () => {
  await cleanConnections()
})

async function startWizard(page: Page, provider: RegExp, storageBase = base) {
  await page.goto(`${storageBase}/connections/new`)
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

async function fillS3(
  page: Page,
  over: { endpoint?: string; bucket?: string; region?: string; accessKey?: string; secretKey?: string } = {},
) {
  await page.getByLabel('HTTPS uç noktası').fill(over.endpoint ?? 'https://192.168.122.1:9100')
  await page.getByLabel('Kova').fill(over.bucket ?? 's2bucket')
  await page.getByLabel('İmzalama bölgesi').fill(over.region ?? 'us-east-1')
  await page.getByLabel('Erişim anahtarı').fill(over.accessKey ?? 's2minio')
  await page.getByLabel('Gizli anahtar').fill(over.secretKey ?? 'S2-minio-pass-1')
}

async function rename(page: Page, name: string) {
  await page.getByRole('button', { name: 'Adı değiştir' }).click()
  await page.getByRole('textbox', { name: 'Ad', exact: true }).fill(name)
}

test.describe('storage wizard validation & feature flags', () => {
  test('provider step: next is blocked until chosen; cancel returns to list; SMB is hidden when smb flag is off', async ({ page }) => {
    await signInTenant(page)
    const problems = watch(page)
    await page.goto(`${base}/connections/new`)
    await expect(page.getByRole('button', { name: 'İleri' })).toBeDisabled()
    await expect(page.getByRole('radio', { name: /SMB/i })).toHaveCount(0)
    await expect(page.getByRole('radio', { name: /Merkezi yönetilen depolama/ })).toBeDisabled()
    await page.getByRole('link', { name: 'Vazgeç' }).click()
    await expect(page).toHaveURL(new RegExp(`${base}$`))
    expect(problems).toEqual([])
  })

  test('SFTP: required fields, auth-method toggle (password vs private-key), malformed fingerprint and out-of-range port', async ({ page }) => {
    await signInTenant(page)
    const problems = watch(page, [{ status: 400, path: /\/storage-connections$/ }])
    await startWizard(page, /^SFTP/)
    await expect(page.getByRole('button', { name: 'İleri' })).toBeDisabled()

    await page.getByLabel('Giriş yöntemi').selectOption('private-key')
    await expect(page.getByLabel(/SSH özel anahtarı|Özel anahtar/i).first()).toBeVisible()
    await page.getByLabel('Giriş yöntemi').selectOption('password')

    await fillSftp(page)
    await expect(page.getByRole('button', { name: 'İleri' })).toBeEnabled()
    await page.getByLabel('Sunucu adresi').fill('')
    await expect(page.getByRole('button', { name: 'İleri' })).toBeDisabled()

    await fillSftp(page, { fingerprint: 'not-a-fingerprint' })
    await page.getByRole('button', { name: 'İleri' }).click()
    await page.getByRole('button', { name: 'Bağlantıyı oluştur ve test et' }).click()
    await expect(page.getByRole('alert').first()).toBeVisible()
    await page.getByRole('button', { name: 'Geri' }).click()

    await fillSftp(page, { port: '70000' })
    const next = page.getByRole('button', { name: 'İleri' })
    if (await next.isEnabled()) {
      await next.click()
      await page.getByRole('button', { name: 'Bağlantıyı oluştur ve test et' }).click()
      await expect(page.getByRole('alert').first()).toBeVisible()
    }
    expect(problems).toEqual([])
  })

  test('S3: required fields block next; non-HTTPS endpoint and invalid bucket are rejected and wipe typed secrets', async ({ page }) => {
    await signInTenant(page)
    const problems = watch(page, [{ status: 400, path: /\/storage-connections$/ }])
    await startWizard(page, /^S3/)
    await expect(page.getByRole('button', { name: 'İleri' })).toBeDisabled()
    await expect(page.getByLabel(/Object Lock|WORM|Session token/i)).toHaveCount(0)

    await fillS3(page, { endpoint: 'http://192.168.122.1:9000' })
    await page.getByRole('button', { name: 'İleri' }).click()
    await page.getByRole('button', { name: 'Bağlantıyı oluştur ve test et' }).click()
    await expect(page.getByRole('alert').first()).toContainText(/Güvenlik nedeniyle giriş bilgileri temizlendi/i)
    await page.getByRole('button', { name: 'Geri' }).click()
    await expect(page.getByRole('button', { name: 'İleri' })).toBeDisabled()

    await fillS3(page, { bucket: 'Bad_Bucket!' })
    await page.getByRole('button', { name: 'İleri' }).click()
    await page.getByRole('button', { name: 'Bağlantıyı oluştur ve test et' }).click()
    await expect(page.getByRole('alert').first()).toBeVisible()
    expect(problems).toEqual([])
  })

  test('deep links ?provider=sftp, ?provider=s3 and ?provider=local open the details step directly on cold load (#284)', async ({ page }) => {
    await signInTenant(page)
    await page.goto(`${base}/connections/new?provider=sftp`)
    await expect(page.getByRole('textbox', { name: 'Sunucu adresi' })).toBeVisible()
    await page.goto(`${base}/connections/new?provider=s3`)
    await expect(page.getByLabel('HTTPS uç noktası')).toBeVisible()
    await page.goto(`${base}/connections/new?provider=local`)
    await expect(page.getByLabel('Disk adı')).toBeVisible()
  })
})

test.describe('Local provider lifecycle', () => {
  test('create, detail, edit volume & name, reload persistence, disable/enable, delete', async ({ page }) => {
    test.setTimeout(45_000)
    const name = uniq('local')
    const renamed = `${name}-r`
    await signInTenant(page)
    const problems = watch(page)

    await startWizard(page, /^Yerel disk/)
    await page.getByLabel('Disk adı').fill('backup-vol-1')
    await page.getByRole('button', { name: 'İleri' }).click()
    await rename(page, name)
    await page.getByRole('button', { name: 'Bağlantıyı oluştur ve test et' }).click()
    await expect(page.getByText('Bağlantı kaydedildi')).toBeVisible()
    await page.getByRole('link', { name: 'Depolamaya dön' }).click()
    await expect(page.getByRole('link', { name })).toBeVisible()

    await page.getByRole('link', { name }).click()
    await page.getByRole('tab', { name: 'Yapılandırma' }).click()
    await expect(page.getByText('backup-vol-1')).toBeVisible()
    await page.getByRole('button', { name: `Düzenle — ${name}` }).click()
    const edit = page.getByRole('dialog')
    await edit.getByLabel('Ad', { exact: true }).fill(renamed)
    await edit.getByLabel('Disk adı').fill('backup-vol-2')
    await edit.getByRole('button', { name: 'Kaydet' }).click()
    await expect(edit).toBeHidden()
    await page.reload()
    await page.getByRole('tab', { name: 'Yapılandırma' }).click()
    await expect(page.getByRole('heading', { name: renamed })).toBeVisible()
    await expect(page.getByText('backup-vol-2')).toBeVisible()

    await page.getByRole('tab', { name: 'Yönetim' }).click()
    await page.getByRole('button', { name: `Devre dışı bırak — ${renamed}` }).click()
    await page.getByRole('dialog').getByRole('button', { name: 'Devre dışı bırak' }).click()
    await expect(page.getByText(/devre dışı/i).first()).toBeVisible()
    await page.getByRole('button', { name: `Etkinleştir — ${renamed}` }).click()
    await page.getByRole('dialog').getByRole('button', { name: 'Etkinleştir' }).click()
    await expect(page.getByRole('button', { name: `Devre dışı bırak — ${renamed}` })).toBeVisible()

    await page.getByRole('button', { name: `Sil — ${renamed}` }).click()
    await page.getByRole('dialog').getByRole('textbox').fill(renamed)
    await page.getByRole('dialog').getByRole('button', { name: 'Bağlantıyı sil' }).click()
    await expect(page).toHaveURL(new RegExp(`${base}$`))
    await expect(page.getByText(renamed)).toHaveCount(0)
    expect(problems).toEqual([])
  })
})

test.describe('SFTP connection lifecycle (sftp target on :2222)', () => {
  test('create, list, detail a11y, rename, secret rotation/clear/re-set, disable, enable, delete', async ({ page }) => {
    test.setTimeout(60_000)
    const name = uniq('sftp')
    const renamed = `${name}-r`
    await signInTenant(page)
    const problems = watch(page)

    await startWizard(page, /^SFTP/)
    await fillSftp(page)
    await page.getByRole('button', { name: 'İleri' }).click()
    await rename(page, name)
    await page.getByRole('button', { name: 'Bağlantıyı oluştur ve test et' }).click()
    await expect(page.getByText(/Bağlantı kaydedildi|Bağlantı çalışıyor/).first()).toBeVisible()
    await page.getByRole('link', { name: 'Depolamaya dön' }).click()
    await expect(page.getByRole('link', { name })).toBeVisible()

    await page.getByRole('link', { name }).click()
    await expect(page.getByRole('heading', { name })).toBeVisible()
    expect(await axeViolations(page)).toEqual([])

    await page.getByRole('tab', { name: 'Yapılandırma' }).click()
    await page.getByRole('button', { name: `Düzenle — ${name}` }).click()
    await page.getByRole('dialog').getByLabel('Ad', { exact: true }).fill(renamed)
    await page.getByRole('dialog').getByRole('button', { name: 'Kaydet' }).click()
    await expect(page.getByRole('heading', { name: renamed })).toBeVisible()

    await page.getByRole('tab', { name: 'Kimlik bilgisi' }).click()
    await page.getByRole('button', { name: `Kimlik bilgisini yenile — ${renamed}` }).click()
    await page.getByRole('dialog').getByLabel('Parola', { exact: true }).fill('S2-sftp-pass1')
    await page.getByRole('dialog').getByRole('button', { name: 'Kaydet' }).click()
    await expect(page.getByRole('dialog')).toBeHidden()

    await page.getByRole('button', { name: `Kimlik bilgisini temizle — ${renamed}` }).click()
    await page.getByRole('dialog').getByRole('button', { name: 'Kimlik bilgisini temizle' }).click()
    await expect(page.getByRole('button', { name: `Kimlik bilgisi ayarla — ${renamed}` })).toBeVisible()

    await page.getByRole('button', { name: `Kimlik bilgisi ayarla — ${renamed}` }).click()
    await page.getByRole('dialog').getByLabel('Parola', { exact: true }).fill('S2-sftp-pass1')
    await page.getByRole('dialog').getByRole('button', { name: 'Kaydet' }).click()
    await expect(page.getByRole('button', { name: `Kimlik bilgisini yenile — ${renamed}` })).toBeVisible()

    await page.getByRole('tab', { name: 'Yönetim' }).click()
    await page.getByRole('button', { name: `Devre dışı bırak — ${renamed}` }).click()
    await page.getByRole('dialog').getByRole('button', { name: 'Devre dışı bırak' }).click()
    await expect(page.getByText(/Bu hedef.*devre dışı/)).toBeVisible()
    await page.getByRole('button', { name: `Etkinleştir — ${renamed}` }).click()
    await page.getByRole('dialog').getByRole('button', { name: 'Etkinleştir' }).click()
    await expect(page.getByRole('button', { name: `Devre dışı bırak — ${renamed}` })).toBeVisible()

    const detailUrl = new URL(page.url()).pathname
    await page.getByRole('button', { name: `Sil — ${renamed}` }).click()
    await page.getByRole('dialog').getByRole('textbox').fill(renamed)
    await page.getByRole('dialog').getByRole('button', { name: 'Bağlantıyı sil' }).click()
    await expect(page).toHaveURL(new RegExp(`${base}$`))
    await expect(page.getByText(renamed)).toHaveCount(0)
    await page.goto(detailUrl)
    await expect(page.getByText(/bulunamadı|erişilemiyor/).first()).toBeVisible()
    expect(problems).toEqual([])
  })
})

test.describe('S3 connection lifecycle (MinIO target on :9100)', () => {
  test('create against MinIO, detail, capacity unmeasurable state, edit region & name, rotate/clear/set credentials, disable/enable, delete', async ({ page }) => {
    test.setTimeout(60_000)
    const name = uniq('s3')
    const renamed = `${name}-r`
    await signInTenant(page)
    const problems = watch(page)

    await startWizard(page, /^S3/)
    await fillS3(page)
    await page.getByRole('button', { name: 'İleri' }).click()
    await rename(page, name)
    await page.getByRole('button', { name: 'Bağlantıyı oluştur ve test et' }).click()
    await expect(page.getByText(/Bağlantı kaydedildi|Bağlantı çalışıyor/).first()).toBeVisible()
    await page.getByRole('link', { name: 'Depolamaya dön' }).click()
    await expect(page.getByRole('link', { name })).toBeVisible()

    await page.getByRole('link', { name }).click()
    await expect(page.getByRole('heading', { name })).toBeVisible()
    await expect(page.getByRole('button', { name: 'Kapasiteyi sorgula' })).toBeDisabled()
    await expect(page.getByText('Kapasite ölçülemiyor')).toBeVisible()

    await page.getByRole('tab', { name: 'Yapılandırma' }).click()
    await expect(page.getByText('s2bucket')).toBeVisible()
    await expect(page.getByText('us-east-1')).toBeVisible()
    await page.getByRole('button', { name: `Düzenle — ${name}` }).click()
    const edit = page.getByRole('dialog')
    await edit.getByLabel('Ad', { exact: true }).fill(renamed)
    await edit.getByLabel('İmzalama bölgesi').fill('eu-central-1')
    await edit.getByRole('button', { name: 'Kaydet' }).click()
    await expect(edit).toBeHidden()
    await page.reload()
    await page.getByRole('tab', { name: 'Yapılandırma' }).click()
    await expect(page.getByRole('heading', { name: renamed })).toBeVisible()
    await expect(page.getByText('eu-central-1')).toBeVisible()

    await page.getByRole('tab', { name: 'Kimlik bilgisi' }).click()
    await page.getByRole('button', { name: `Kimlik bilgisini yenile — ${renamed}` }).click()
    const secDialog = page.getByRole('dialog')
    await expect(secDialog.getByRole('button', { name: 'Kaydet' })).toBeDisabled()
    await secDialog.getByLabel('Erişim anahtarı').fill('s2minio')
    await expect(secDialog.getByRole('button', { name: 'Kaydet' })).toBeDisabled()
    await secDialog.getByLabel('Gizli anahtar').fill('S2-minio-pass-1')
    await secDialog.getByRole('button', { name: 'Kaydet' }).click()
    await expect(secDialog).toBeHidden()

    await page.getByRole('button', { name: `Kimlik bilgisini temizle — ${renamed}` }).click()
    await page.getByRole('dialog').getByRole('button', { name: 'Kimlik bilgisini temizle' }).click()
    await expect(page.getByRole('button', { name: `Kimlik bilgisi ayarla — ${renamed}` })).toBeVisible()

    await page.getByRole('button', { name: `Kimlik bilgisi ayarla — ${renamed}` }).click()
    await page.getByRole('dialog').getByLabel('Erişim anahtarı').fill('s2minio')
    await page.getByRole('dialog').getByLabel('Gizli anahtar').fill('S2-minio-pass-1')
    await page.getByRole('dialog').getByRole('button', { name: 'Kaydet' }).click()
    await expect(page.getByRole('button', { name: `Kimlik bilgisini yenile — ${renamed}` })).toBeVisible()

    await page.getByRole('tab', { name: 'Yönetim' }).click()
    await page.getByRole('button', { name: `Devre dışı bırak — ${renamed}` }).click()
    await page.getByRole('dialog').getByRole('button', { name: 'Devre dışı bırak' }).click()
    await expect(page.getByText(/Bu hedef.*devre dışı/)).toBeVisible()
    await page.getByRole('button', { name: `Etkinleştir — ${renamed}` }).click()
    await page.getByRole('dialog').getByRole('button', { name: 'Etkinleştir' }).click()
    await expect(page.getByRole('button', { name: `Devre dışı bırak — ${renamed}` })).toBeVisible()

    await page.getByRole('button', { name: `Sil — ${renamed}` }).click()
    await page.getByRole('dialog').getByRole('textbox').fill(renamed)
    await page.getByRole('dialog').getByRole('button', { name: 'Bağlantıyı sil' }).click()
    await expect(page).toHaveURL(new RegExp(`${base}$`))
    await expect(page.getByText(renamed)).toHaveCount(0)
    expect(problems).toEqual([])
  })
})

test.describe('Google Drive wizard, unconfigured OAuth state & real OAuth flow', () => {
  test('saves connection, surfaces OAuth configuration error cleanly without crashing, shows reauthorize state on detail, edits name and deletes', async ({ page }) => {
    test.skip(hasGoogleDriveCreds, 'Runs only when Google Drive OAuth client credentials are not configured.')
    test.setTimeout(45_000)
    const name = uniq('gdrive')
    const renamed = `${name}-r`
    await signInTenant(page)
    const problems = watch(page, [
      { status: 409, path: /\/google-drive\/authorize-url$/ },
      { status: 503, path: /\/google-drive\/authorize-url$/ },
      { status: 400, path: /\/google-drive\/authorize-url$/ },
    ])

    await page.goto(`${base}/connections/new`)
    await page.getByRole('radio', { name: /^Google Drive/ }).check()
    await page.getByRole('button', { name: 'İleri' }).click()
    await expect(page.getByText(/Google parolanızı burada girmeyin/)).toBeVisible()
    await rename(page, name)

    // Close popup immediately if Development RedirectUri causes /authorize-url to return 200 with empty client_id
    page.context().on('page', (popup) => { void popup.close().catch(() => {}) })
    await page.getByRole('button', { name: 'Kaydet ve Google ile bağlan' }).click()

    await expect(
      page.getByText('Google yetkilendirmesi başlatılamadı').or(page.getByText('Google hesabı henüz bağlanmadı')),
    ).toBeVisible({ timeout: 10_000 })
    await page.getByRole('link', { name: 'Depolamaya dön' }).click()
    await expect(page.getByRole('link', { name })).toBeVisible()

    await page.getByRole('link', { name }).click()
    await expect(page.getByText('Google hesabının yeniden yetkilendirilmesi gerekiyor')).toBeVisible()
    await expect(page.getByRole('button', { name: 'Yeniden yetkilendir' })).toBeVisible()

    await page.getByRole('tab', { name: 'Yapılandırma' }).click()
    await page.getByRole('button', { name: `Düzenle — ${name}` }).click()
    await page.getByRole('dialog').getByLabel('Ad', { exact: true }).fill(renamed)
    await page.getByRole('dialog').getByRole('button', { name: 'Kaydet' }).click()
    await expect(page.getByRole('heading', { name: renamed })).toBeVisible()

    await page.getByRole('tab', { name: 'Yönetim' }).click()
    await page.getByRole('button', { name: `Sil — ${renamed}` }).click()
    await page.getByRole('dialog').getByRole('textbox').fill(renamed)
    await page.getByRole('dialog').getByRole('button', { name: 'Bağlantıyı sil' }).click()
    await expect(page).toHaveURL(new RegExp(`${base}$`))
    expect(problems).toEqual([])
  })

  test('Google Drive OAuth consent redirect and real connection verification (#237)', async ({ page }) => {
    test.fixme(!hasGoogleDriveCreds, 'Google Drive OAuth client credentials (GOOGLE_CLIENT_ID / GOOGLE_CLIENT_SECRET) are not configured in this environment (#237)')
    const name = uniq('gdrive-oauth')
    await signInTenant(page)
    const problems = watch(page)

    await page.goto(`${base}/connections/new`)
    await page.getByRole('radio', { name: /^Google Drive/ }).check()
    await page.getByRole('button', { name: 'İleri' }).click()
    await rename(page, name)

    const popupPromise = page.waitForEvent('popup')
    await page.getByRole('button', { name: 'Kaydet ve Google ile bağlan' }).click()
    const popup = await popupPromise
    await expect(popup).toHaveURL(/^https:\/\/accounts\.google\.com\/o\/oauth2\/v2\/auth\?.*code_challenge_method=S256/)
    await popup.close()
    expect(problems).toEqual([])
  })
})

test.describe('Quota view & empty/unprobed states', () => {
  let finiteTenant: IsolatedTenant

  test.beforeAll(async () => {
    finiteTenant = await seedIsolatedTenant('quota', { limitBytes: 10 * 1024 * 1024 * 1024 })
  })
  test.afterAll(async () => {
    await finiteTenant?.api.dispose()
  })

  test('storage page displays total usage, finite plan quota limit, quota meter, default cards and empty-state CTA', async ({ page }) => {
    await signInAs(page, finiteTenant.account)
    const problems = watch(page)
    await page.goto(`/tenants/${finiteTenant.tenantId}/storage`)
    await expect(page.getByRole('heading', { name: 'Depolama kullanımı' })).toBeVisible()
    await expect(page.getByText(/Toplam boyut:.*Kota:.*10/)).toBeVisible()
    await expect(page.getByRole('progressbar')).toBeVisible()
    await expect(page.getByRole('article', { name: 'Yerel disk' })).toContainText('Varsayılan')

    // Backend re-provisions DefaultStorageConnection.All on every GET, so mock zero items to exercise the empty-state CTA
    await page.route(`**/api/tenants/${finiteTenant.tenantId}/storage-connections`, (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [] }) }),
    )
    await page.reload()
    await expect(page.getByText('Henüz depolama bağlantısı yok.')).toBeVisible()
    await expect(page.getByRole('link', { name: 'Depolama bağlantısı ekle' })).toBeVisible()
    expect(problems).toEqual([])
  })

  test('unmeasured connection on /storage emits no /capacity 404 or console error (#247 regression)', async ({ page }) => {
    const fresh = await seedIsolatedTenant('bug247')
    try {
      await signInAs(page, fresh.account)
      const problems = watch(page)
      await page.goto(`/tenants/${fresh.tenantId}/storage`)
      await expect(page.getByRole('heading', { name: 'Depolama bağlantıları' })).toBeVisible()
      await page.waitForLoadState('networkidle')
      expect(problems).toEqual([])
    } finally {
      await fresh.api.dispose()
    }
  })
})
